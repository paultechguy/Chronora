// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Metadata;

public sealed partial class MetadataWriter
{
    /// <summary>
    /// Removes the chosen categories of personal detail from one file, in place.
    ///
    /// No backup copy is taken, and that is a product decision rather than an omission: the
    /// run is irreversible by design and says so before it starts. A copy would also be the
    /// one thing on disk still holding the details the user asked to remove, and on any
    /// failure path it would be left behind beside their photo.
    ///
    /// The result here is ExifTool's exit status and nothing more. Whether the details are
    /// actually GONE is decided by the caller re-reading the file: ExifTool runs with -m, so a
    /// minor problem is a warning on a successful write (measured: every PNG IPTC write says
    /// "Creating non-standard IPTC in PNG"), and a tag that cannot be deleted - an unwritable
    /// maker-note field - is silently left. Only a read-back can tell those apart.
    /// </summary>
    public async Task<MetadataWriteResult> StripAsync(
        IExifToolSession session,
        string path,
        IReadOnlyCollection<PrivacyCategory> categories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(categories);

        if (categories.Count == 0)
        {
            return new MetadataWriteResult(path, Succeeded: true, WriteDestination.Embedded, null, null);
        }

        // Same two flags as a date write, for the same reasons: in place keeps Created, the ACL
        // and alternate streams; -P is belt and braces for Modified.
        var arguments = new List<string> { "-overwrite_original_in_place", "-P" };
        arguments.AddRange(PrivacyTagCatalog.DeleteArgumentsFor(categories));
        arguments.Add(path);

        ExifToolResult result = await session
            .ExecuteAsync(arguments, timeout: TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            this._logger.LogDebug("ExifTool warned while removing details from {Path}: {Warnings}", path, result.StandardError.Trim());
        }

        return new MetadataWriteResult(
            path,
            result.Succeeded,
            WriteDestination.Embedded,
            BackupPath: null,
            result.Succeeded ? null : Describe(result));
    }
}
