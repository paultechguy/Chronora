// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Metadata;

namespace PaulTechGuy.CN.Services;

public sealed partial class MetadataGateway
{
    /// <summary>
    /// Which personal details each file carries, in batches, keyed by path. Only asked for
    /// while the Private details intent is chosen.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, PrivacyFindings>> ReadPrivacyAsync(
        IReadOnlyList<ScannedFile> files,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var results = new Dictionary<string, PrivacyFindings>(StringComparer.OrdinalIgnoreCase);

        if (!this.Available)
        {
            return results;
        }

        string[] paths = [.. files.Where(CanRead).Select(f => f.FullPath)];

        if (paths.Length == 0 || await this.GetSessionAsync(cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return results;
        }

        int done = 0;

        for (int i = 0; i < paths.Length; i += MetadataReader.BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] batch = [.. paths.Skip(i).Take(MetadataReader.BatchSize)];

            try
            {
                foreach (FilePrivacy file in await this._reader.ReadPrivacyAsync(session, batch, cancellationToken).ConfigureAwait(false))
                {
                    results[WindowsPath(file.Path)] = file.Findings;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                this._logger.LogWarning(ex, "ExifTool stopped responding during the privacy read; the remaining files were not read.");
                break;
            }

            done += batch.Length;
            progress?.Report(done);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<PrivacyFindings?> ReadPrivacyOneAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!this.Available || await this.GetSessionAsync(cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return null;
        }

        try
        {
            IReadOnlyList<FilePrivacy> read = await this._reader.ReadPrivacyAsync(session, [path], cancellationToken).ConfigureAwait(false);
            return read.Count > 0 ? read[0].Findings : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            this._logger.LogWarning(ex, "Could not re-read the personal details of {Path}.", path);
            return null;
        }
    }

    /// <summary>Every tag in one file, for the viewer. Null when ExifTool is not there.</summary>
    public async Task<IReadOnlyList<MetadataTag>?> ReadAllAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!this.Available || await this.GetSessionAsync(cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return null;
        }

        try
        {
            return await this._reader.ReadAllAsync(session, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            this._logger.LogWarning(ex, "Could not read the metadata of {Path}.", path);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<MetadataWriteResult> StripAsync(
        string path,
        IReadOnlyCollection<PrivacyCategory> categories,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(categories);

        if (!this.Available || await this.GetSessionAsync(cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return Failed(path, "ExifTool is not available, so the details were not removed.");
        }

        try
        {
            return await this._writer.StripAsync(session, path, categories, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The second arm is the session's own five-minute ceiling, which surfaces as a
            // cancellation nobody asked for. Either way the session is wedged - ExifTool may
            // still be mid-file - so the next call gets a fresh one, and the caller's re-read
            // says what actually happened to this file.
            this._logger.LogWarning(ex, "ExifTool did not finish removing details from {Path}.", path);
            await this.DiscardSessionAsync().ConfigureAwait(false);

            return Failed(path, "ExifTool did not finish with this file, so the details may not have been removed.");
        }
    }

    /// <summary>
    /// ExifTool echoes SourceFile with forward slashes ("C:/Photos/a.jpg") however the path
    /// was passed in - measured 2026-09-27 - while every row is keyed by the Windows form. An
    /// unnormalised key matches no row, silently: the privacy read left every row "reading…"
    /// for ever, and the date read's results had the same mismatch.
    /// </summary>
    private static string WindowsPath(string exifToolPath) => exifToolPath.Replace('/', '\\');

    private static MetadataWriteResult Failed(string path, string detail) =>
        new(path, Succeeded: false, WriteDestination.Embedded, BackupPath: null, detail);
}
