// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
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

    private IEnumerable<string> Enumerate(string root, ScanFilter filter, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = filter.Recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.None,
            ReturnSpecialDirectories = false,
        };

        // Patterns are entered semicolon-separated, which is the form FileTouch used and
        // which people already expect from Explorer-adjacent tools.
        IReadOnlyList<string> patterns = filter.Patterns.Count > 0 ? filter.Patterns : ["*"];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string pattern in patterns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IEnumerable<string> matches;
            try
            {
                matches = filter.IncludeDirectories && !filter.IncludeFiles
                    ? Directory.EnumerateDirectories(root, pattern, options)
                    : Directory.EnumerateFileSystemEntries(root, pattern, options);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
            {
                this._logger.LogWarning(ex, "Could not enumerate {Root} with pattern {Pattern}.", root, pattern);
                continue;
            }

            foreach (string path in matches)
            {
                // Several patterns can match the same file; the user asked for the file
                // once, so they get it once.
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

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
