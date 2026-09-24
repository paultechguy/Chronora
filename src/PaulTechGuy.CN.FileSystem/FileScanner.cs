// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.FileSystem;

/// <summary>
/// Turns a folder and a filter into the sealed snapshot the evaluator runs against.
///
/// Everything the preview will ever need about a file is established here, once. The
/// evaluator never touches the disk again, which is what makes a live preview possible at
/// 50,000 files.
/// </summary>
public sealed class FileScanner(
    FileTimeWriter? reader = null,
    VolumeProbe? volumes = null,
    ILogger<FileScanner>? logger = null)
{
    private static readonly char[] Separators = ['\\', '/'];

    private static readonly FrozenDictionary<DateField, MetadataValue> NoMetadata =
        new Dictionary<DateField, MetadataValue>().ToFrozenDictionary();

    private readonly FileTimeWriter _reader = reader ?? new FileTimeWriter();
    private readonly VolumeProbe _volumes = volumes ?? new VolumeProbe();
    private readonly ILogger<FileScanner> _logger = logger ?? NullLogger<FileScanner>.Instance;

    /// <summary>
    /// Walks a root and yields one entry per matching file or folder.
    ///
    /// Streamed rather than returned as a list so the grid can start filling immediately;
    /// metadata arrives later, on a separate pass, because ExifTool is orders of magnitude
    /// slower than reading four timestamps.
    /// </summary>
    public async IAsyncEnumerable<ScannedFile> ScanAsync(
        string root,
        ScanFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(filter);

        VolumeCapabilities volume = this._volumes.For(root);

        // The root folder itself is separate from its contents, which FileTouch got right
        // and is easy to miss: "stamp the folder" and "stamp what is in the folder" are
        // different requests.
        if (filter.IncludeRootDirectory && Directory.Exists(root))
        {
            ScannedFile? entry = this.Describe(root, isDirectory: true, volume);
            if (entry is not null)
            {
                yield return entry;
            }
        }

        foreach (string path in this.Enumerate(root, filter, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool isDirectory = Directory.Exists(path);

            // IncludeDirectories was honoured in exactly one case - when folders were the
            // ONLY thing asked for - and ignored otherwise. Enumerate hands back folders
            // as well as files whenever files are wanted, and nothing here checked, so
            // every subfolder of a dropped tree arrived as a row of its own and took the
            // run's dates along with the photos in it. The parameter has always been
            // documented as "whether folders are collected"; now it is.
            if (isDirectory && !filter.IncludeDirectories)
            {
                continue;
            }

            ScannedFile? entry = this.Describe(path, isDirectory, volume);
            if (entry is not null)
            {
                yield return entry;
            }

            // Yields the thread periodically so a scan of a large tree does not starve the
            // UI's dispatcher while the channel drains.
            await Task.Yield();
        }
    }

    /// <summary>
    /// Scans a mixed list of folders and individual files, as a drop from Explorer hands
    /// them over. Windows gives no guarantee the items are all one kind, so neither does
    /// this: each is classified and handled on its own.
    /// </summary>
    public async IAsyncEnumerable<ScannedFile> ScanPathsAsync(
        IReadOnlyList<string> paths,
        ScanFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(filter);

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                await foreach (ScannedFile file in this.ScanAsync(path, filter, cancellationToken))
                {
                    yield return file;
                }

                continue;
            }

            if (!File.Exists(path))
            {
                this._logger.LogWarning("Dropped item {Path} is neither a file nor a folder.", path);
                continue;
            }

            // A file named explicitly is included whatever the pattern filter says: the
            // user pointed at this one, which is a stronger signal than a wildcard.
            ScannedFile? entry = this.Describe(path, isDirectory: false, this._volumes.For(path));
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    private IEnumerable<string> Enumerate(string root, ScanFilter filter, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = filter.Recurse,
            IgnoreInaccessible = true,

            // This was FileAttributes.None, which overrides .NET's own default and meant
            // every scan collected hidden and system files with nothing on screen saying
            // so. It is now the caller's choice, and the caller's default is to leave them
            // alone - somebody dropping a folder means the files they can see in it.
            AttributesToSkip = filter.IncludeHidden
                ? FileAttributes.None
                : FileAttributes.Hidden | FileAttributes.System,

            ReturnSpecialDirectories = false,
        };

        bool directoriesOnly = filter.IncludeDirectories && !filter.IncludeFiles;

        // "*" and an empty list both mean everything; testing one pattern is cheaper than
        // testing none by special case in the hot path.
        IReadOnlyList<string> patterns = NamePatterns.IsEverything(filter.Patterns) ? NamePatterns.Everything : filter.Patterns;

        // Include folders only means anything below the root. With recursion off every
        // file is IN the root, and the pane is inert rather than a way to empty the list.
        bool folderGate = filter.Recurse && !NamePatterns.IsEverything(filter.IncludeFolders);

        // Entries from one folder arrive together, so the ancestor test is answered once
        // per folder rather than once per file.
        string? lastDirectory = null;
        bool lastDirectoryIncluded = false;

        bool InIncludedFolder(ref FileSystemEntry entry)
        {
            ReadOnlySpan<char> directory = entry.Directory;

            if (lastDirectory is null || !directory.SequenceEqual(lastDirectory))
            {
                lastDirectory = directory.ToString();
                lastDirectoryIncluded = IsWithinIncludedFolders(RelativeDirectory(ref entry), filter.IncludeFolders);
            }

            return lastDirectoryIncluded;
        }

        // ONE walk. This used to build an enumerable per pattern and de-duplicate across
        // them, which cost nothing while the list was always "*" and would have walked a
        // NAS five times over for five patterns. Each entry is visited once now, so the
        // de-duplication went with it.
        IEnumerable<string> matches;
        try
        {
            matches = new FileSystemEnumerable<string>(
                root,
                static (ref FileSystemEntry entry) => entry.ToFullPath(),
                options)
            {
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                    ShouldRecurse(entry.FileName, entry.Attributes, filter),

                // MatchesSimpleExpression, not Win32: EnumerationOptions.MatchType
                // defaults to Simple, so this is the matcher the app has always used.
                // Win32 would quietly change what IMG_????.CR2 means.
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.IsDirectory)
                    {
                        // Folder rows answer to the FOLDER patterns only. The file
                        // patterns used to be tested against folder names too, so
                        // "*.jpg" quietly meant "and no folder rows"; that was never a
                        // choice anybody made.
                        //
                        // The root's "always in" is for its FILES. A folder sitting in the
                        // root is in only by matching itself - without the IsEmpty check,
                        // 2020 and Archive both came back as rows under "2019*".
                        return filter.IncludeDirectories
                            && !NamePatterns.MatchesAny(filter.ExcludeFolders, entry.FileName)
                            && (!folderGate
                                || NamePatterns.MatchesAny(filter.IncludeFolders, entry.FileName)
                                || (!RelativeDirectory(ref entry).IsEmpty && InIncludedFolder(ref entry)));
                    }

                    return !directoriesOnly
                        && NamePatterns.MatchesAny(patterns, entry.FileName)
                        && !NamePatterns.MatchesAny(filter.ExcludeFiles, entry.FileName)
                        && (!folderGate || InIncludedFolder(ref entry));
                },
            };
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            this._logger.LogWarning(ex, "Could not enumerate {Root}.", root);
            yield break;
        }

        foreach (string path in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    /// <summary>
    /// Whether the walk goes into a folder. Pulled out of the predicate so a test can show
    /// an excluded folder is PRUNED rather than walked and then filtered - the two look the
    /// same from the files that come back, and only one of them saves the time.
    /// </summary>
    /// <remarks>
    /// The junction guard is first, and the reason the enumerable is hand-built rather than
    /// a call to Directory.EnumerateFileSystemEntries.
    ///
    /// RecurseSubdirectories follows reparse points: measured 2026-09-22, a junction inside
    /// a dropped folder handed back a file from outside it, and a junction that points at
    /// one of its own ancestors walks for ever. "I dropped this folder" cannot reasonably
    /// mean "and everywhere its links point".
    ///
    /// Adding ReparsePoint to AttributesToSkip DOES stop the descent - also measured - and
    /// is the wrong tool, because that flag applies to files as well as folders, and a
    /// dehydrated OneDrive file is a reparse point. A photo-date tool that silently skipped
    /// somebody's cloud photos to guard against junctions would be a poor trade. This is
    /// only consulted for directories, so files are untouched.
    ///
    /// The dropped root is never tested: the walk starts inside it. Dropping a folder that
    /// is named like an excluded one means that folder.
    /// </remarks>
    internal static bool ShouldRecurse(ReadOnlySpan<char> name, FileAttributes attributes, ScanFilter filter) =>
        !attributes.HasFlag(FileAttributes.ReparsePoint)
        && !NamePatterns.MatchesAny(filter.ExcludeFolders, name);

    /// <summary>
    /// Whether any folder in a path relative to the root matches an include pattern -
    /// "subtree follows", so 2019\January is in because 2019 is. An empty path is the root
    /// itself, whose files are always in.
    /// </summary>
    internal static bool IsWithinIncludedFolders(ReadOnlySpan<char> relativeDirectory, IReadOnlyList<string> include)
    {
        relativeDirectory = relativeDirectory.Trim(Separators);

        if (relativeDirectory.IsEmpty)
        {
            return true;
        }

        foreach (Range segment in relativeDirectory.SplitAny(Separators))
        {
            if (NamePatterns.MatchesAny(include, relativeDirectory[segment]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The entry's folder relative to the root it was reached from.
    ///
    /// Sliced by RootDirectory rather than by the root string the caller passed, because
    /// they differ: RootDirectory has a trailing separator trimmed, except at a drive root
    /// where "C:\" keeps its own. Slicing by the caller's string was off by one - and threw
    /// for a file in a root given with a trailing slash. Measured 2026-09-23.
    /// </summary>
    private static ReadOnlySpan<char> RelativeDirectory(ref FileSystemEntry entry) =>
        entry.Directory.Length <= entry.RootDirectory.Length
            ? []
            : entry.Directory[entry.RootDirectory.Length..];

    private ScannedFile? Describe(string path, bool isDirectory, VolumeCapabilities volume)
    {
        if (!this._reader.TryRead(path, isDirectory, out TimestampSet times, out FileAttributes attributes))
        {
            return null;
        }

        FileTraits traits = FileTraits.None;

        if (isDirectory)
        {
            traits |= FileTraits.Directory;
        }

        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            traits |= FileTraits.ReadOnly;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            traits |= FileTraits.ReparsePoint;
        }

        // A cloud placeholder. Filesystem work still succeeds thanks to
        // FILE_FLAG_OPEN_NO_RECALL; metadata would pull the whole file down.
        const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

        if (attributes.HasFlag(FileAttributes.Offline)
            || attributes.HasFlag(RecallOnOpen)
            || attributes.HasFlag(RecallOnDataAccess))
        {
            traits |= FileTraits.CloudDehydrated;
        }

        if (!volume.SupportsChangeTime)
        {
            traits |= FileTraits.NoChangeTimeSupport;
        }

        if (LongPath.IsLong(path))
        {
            traits |= FileTraits.LongPath;
        }

        long length = 0;
        if (!isDirectory)
        {
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Size is used to price a rewrite, not to decide correctness, so an
                // unreadable length costs an estimate rather than the row.
                this._logger.LogDebug(ex, "Could not read the size of {Path}.", path);
            }
        }

        return new ScannedFile(
            LongPath.ToDisplay(path),
            length,
            isDirectory ? MediaKind.Other : KindOf(path),
            attributes,
            times,
            NoMetadata,
            traits);
    }

    /// <summary>
    /// Classifies by extension, which is what decides whether metadata may be written in
    /// place, via a sidecar, or not at all.
    /// </summary>
    public static MediaKind KindOf(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".JPG" or ".JPEG" or ".JPE" => MediaKind.Jpeg,
            ".HEIC" or ".HEIF" or ".HIF" => MediaKind.Heic,
            ".PNG" => MediaKind.Png,
            ".TIF" or ".TIFF" => MediaKind.Tiff,
            ".DNG" => MediaKind.Dng,
            ".CR2" or ".CR3" or ".NEF" or ".NRW" or ".ARW" or ".SR2" or ".ORF"
                or ".RW2" or ".RAF" or ".PEF" or ".SRW" => MediaKind.RawProprietary,
            ".MP4" or ".MOV" or ".M4V" or ".AVI" or ".MTS" or ".M2TS" or ".3GP" => MediaKind.Video,
            _ => MediaKind.Other,
        };
}
