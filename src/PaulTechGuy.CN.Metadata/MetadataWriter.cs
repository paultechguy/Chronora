// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Metadata;

/// <summary>Where a file's metadata is written.</summary>
public enum WriteDestination
{
    /// <summary>Into the file itself.</summary>
    Embedded,

    /// <summary>Into a neighbouring .xmp, leaving the original bytes untouched.</summary>
    Sidecar,
}

/// <summary>How a metadata write went for one file.</summary>
/// <param name="Path">The file that was asked for, not the sidecar that may have been written.</param>
/// <param name="Succeeded">Whether the tags landed.</param>
/// <param name="Destination">Where they landed.</param>
/// <param name="BackupPath">The copy taken first, if any, so it can be offered for deletion later.</param>
/// <param name="Detail">What went wrong, in the user's language.</param>
public sealed record MetadataWriteResult(
    string Path,
    bool Succeeded,
    WriteDestination Destination,
    string? BackupPath,
    string? Detail);

/// <summary>What one file needs written.</summary>
/// <param name="Path">The file.</param>
/// <param name="Kind">Decides embedded or sidecar.</param>
/// <param name="Assignments">The tags, already expanded to include offsets and sub-seconds.</param>
public sealed record MetadataWriteRequest(
    string Path,
    MediaKind Kind,
    IReadOnlyList<TagAssignment> Assignments);

/// <summary>
/// Writes date tags back to files.
///
/// Three decisions here are load-bearing and none of them is the obvious default.
///
/// ExifTool is asked for -overwrite_original_in_place, not -overwrite_original. The
/// default writes a temp file and renames it over the original, which gives the file a new
/// Created time and drops its ACLs and alternate streams - so the very timestamp the user
/// came here to set would be quietly replaced by the tool setting it.
///
/// One file per command, never a batch. A batch reports one status for the lot, and this
/// app's entire promise is that you know exactly what happened to each file. The
/// stay-open process means the cost is a pipe round trip rather than a process start.
///
/// The backup is Chronora's own copy, taken before the write. ExifTool's own _original
/// backup is mutually exclusive with writing in place, and in place is the more important
/// of the two, so the copy is made here instead.
/// </summary>
public sealed class MetadataWriter(ILogger<MetadataWriter>? logger = null)
{
    private readonly ILogger<MetadataWriter> _logger = logger ?? NullLogger<MetadataWriter>.Instance;

    /// <summary>
    /// Writes one file. Sequential by design: ExifTool's stay-open protocol has a single
    /// pair of pipes and no request ids, so two commands in flight would interleave.
    /// </summary>
    /// <param name="session">A running ExifTool.</param>
    /// <param name="request">What to write.</param>
    /// <param name="keepBackup">
    /// Take a copy first. On by default for anything that rewrites bytes, because the undo
    /// journal restores field VALUES and cannot repair a container a write corrupted.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<MetadataWriteResult> WriteAsync(
        IExifToolSession session,
        MetadataWriteRequest request,
        bool keepBackup = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Assignments.Count == 0)
        {
            return new MetadataWriteResult(request.Path, Succeeded: true, WriteDestination.Embedded, null, null);
        }

        WriteDestination destination = DestinationFor(request.Kind);

        return destination == WriteDestination.Sidecar
            ? await WriteSidecarAsync(session, request, cancellationToken).ConfigureAwait(false)
            : await this.WriteEmbeddedAsync(session, request, keepBackup, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MetadataWriteResult> WriteEmbeddedAsync(
        IExifToolSession session,
        MetadataWriteRequest request,
        bool keepBackup,
        CancellationToken cancellationToken)
    {
        string? backup = null;

        if (keepBackup)
        {
            try
            {
                backup = request.Path + "_original";
                File.Copy(request.Path, backup, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Refuse rather than proceed. The backup is the only thing standing between
                // a bad write and an irreplaceable photo, so "could not make one" is a
                // reason to stop, not a warning to log and carry on past.
                this._logger.LogWarning(ex, "Could not back up {Path} before writing metadata.", request.Path);

                return new MetadataWriteResult(
                    request.Path,
                    Succeeded: false,
                    WriteDestination.Embedded,
                    null,
                    "Chronora could not make a backup copy first, so it left this file alone. "
                    + "Check there is free space on the drive.");
            }
        }

        var arguments = new List<string>(request.Assignments.Count + 3)
        {
            // Rewrites the original file rather than renaming a temp over it. Costs an
            // extra copy; keeps Created, the ACL and any alternate streams.
            "-overwrite_original_in_place",

            // Belt and braces only. ExifTool rewrites the file either way, so this is not
            // what protects the timestamps - the apply order is. It costs nothing and
            // helps if a write ever happens outside that path.
            "-P",
        };

        arguments.AddRange(request.Assignments.Select(a => a.ToArgument()));
        arguments.Add(request.Path);

        ExifToolResult result = await session
            .ExecuteAsync(arguments, timeout: TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            return new MetadataWriteResult(request.Path, Succeeded: true, WriteDestination.Embedded, backup, null);
        }

        // The write failed, so the copy is the file as it should still be. It is only
        // removed on success, never here.
        return new MetadataWriteResult(
            request.Path,
            Succeeded: false,
            WriteDestination.Embedded,
            backup,
            Describe(result));
    }

    /// <summary>
    /// Proprietary RAW gets a sidecar rather than an embedded write.
    ///
    /// These are undocumented formats that only their vendor fully understands, and the
    /// file is the negative - there is no reprinting it. A sidecar is what Lightroom and
    /// Bridge write, so it is also what other software reads.
    /// </summary>
    private static async Task<MetadataWriteResult> WriteSidecarAsync(
        IExifToolSession session,
        MetadataWriteRequest request,
        CancellationToken cancellationToken)
    {
        string sidecar = SidecarPathFor(request.Path);

        var arguments = new List<string>(request.Assignments.Count + 3);

        if (!File.Exists(sidecar))
        {
            // -o creates the sidecar; without it ExifTool refuses to write to a file that
            // is not there.
            arguments.Add("-o");
            arguments.Add(sidecar);
        }
        else
        {
            arguments.Add("-overwrite_original_in_place");
        }

        // The tags are rewritten into the XMP namespace, because that is the only one an
        // .xmp file has. An EXIF tag name would be silently dropped.
        arguments.AddRange(request.Assignments.Select(a => ToXmp(a).ToArgument()));
        arguments.Add(File.Exists(sidecar) ? sidecar : request.Path);

        ExifToolResult result = await session
            .ExecuteAsync(arguments, timeout: TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);

        return new MetadataWriteResult(
            request.Path,
            result.Succeeded,
            WriteDestination.Sidecar,
            BackupPath: null,
            result.Succeeded ? null : Describe(result));
    }

    /// <summary>
    /// Proprietary RAW is written to a sidecar; everything else is written in place.
    ///
    /// DNG is the exception among raw formats and deliberately so: it is an open, published
    /// specification designed to be written to, which is the entire reason it exists.
    /// </summary>
    public static WriteDestination DestinationFor(MediaKind kind) =>
        kind == MediaKind.RawProprietary ? WriteDestination.Sidecar : WriteDestination.Embedded;

    /// <summary>
    /// The sidecar path, following the convention Lightroom and Bridge use: the extension
    /// is replaced, not appended.
    ///
    /// That convention has a trap. A camera shooting RAW+JPEG produces IMG_1234.CR2 and
    /// IMG_1234.JPG, which map to the same IMG_1234.xmp - so the caller has to check for
    /// the collision before writing, which <see cref="FindSidecarCollisions" /> does.
    /// </summary>
    public static string SidecarPathFor(string path) => Path.ChangeExtension(path, ".xmp");

    /// <summary>
    /// Files in the same run that would write to the same sidecar.
    ///
    /// RAW+JPEG is a normal camera mode, so this is not a corner case, and the failure is
    /// invisible: one file's dates would end up describing the other. Reported rather than
    /// resolved, because which of the two should own the sidecar is the user's call.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> FindSidecarCollisions(
        IReadOnlyList<MetadataWriteRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var bySidecar = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (MetadataWriteRequest request in requests.Where(r => DestinationFor(r.Kind) == WriteDestination.Sidecar))
        {
            string sidecar = SidecarPathFor(request.Path);

            if (!bySidecar.TryGetValue(sidecar, out List<string>? sources))
            {
                sources = [];
                bySidecar[sidecar] = sources;
            }

            sources.Add(request.Path);
        }

        return bySidecar
            .Where(pair => pair.Value.Count > 1)
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves an assignment into the XMP namespace, which is the only one a sidecar has.
    ///
    /// The offset and sub-second companions do not survive the move and do not need to:
    /// XMP dates are ISO-8601 and carry both inside the value.
    /// </summary>
    internal static TagAssignment ToXmp(TagAssignment assignment)
    {
        int colon = assignment.Tag.LastIndexOf(':');
        string bare = colon < 0 ? assignment.Tag : assignment.Tag[(colon + 1)..];

        string tag = bare switch
        {
            "DateTimeOriginal" => "XMP-photoshop:DateCreated",
            "CreateDate" => "XMP-xmp:CreateDate",
            "ModifyDate" => "XMP-xmp:ModifyDate",
            _ => "XMP:" + bare,
        };

        return new TagAssignment(tag, assignment.Value);
    }

    /// <summary>
    /// ExifTool's complaint, cleaned up enough to show someone.
    ///
    /// Its warnings are prefixed and repeated per file; the user needs the sentence, not
    /// the transcript.
    /// </summary>
    private static string Describe(ExifToolResult result)
    {
        string[] lines = [.. result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Warning", StringComparison.OrdinalIgnoreCase))];

        return lines.Length > 0
            ? string.Join(" ", lines)
            : $"ExifTool could not write this file (status {result.Status}).";
    }
}
