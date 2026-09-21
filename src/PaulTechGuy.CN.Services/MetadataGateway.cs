// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Metadata;

namespace PaulTechGuy.CN.Services;

/// <summary>
/// The one place ExifTool is actually run from.
///
/// It owns the process. Starting one takes about a second of Perl boot, so a run keeps a
/// single stay-open session alive and hands it to the reader and the writer in turn; the
/// session is discarded only when the configured install changes underneath it.
///
/// Everything here degrades rather than fails. "No ExifTool" is a supported state, not an
/// error: every file-date feature keeps working and only the metadata surfaces report why
/// they cannot. So a read with no engine returns nothing, and a write with no engine
/// returns a refusal with a sentence, and neither throws.
/// </summary>
public sealed class MetadataGateway(
    ExifToolService exifTool,
    MetadataReader reader,
    MetadataWriter writer,
    ILogger<MetadataGateway>? logger = null) : IMetadataWriteGateway, IAsyncDisposable
{
    private readonly ExifToolService _exifTool = exifTool;
    private readonly MetadataReader _reader = reader;
    private readonly MetadataWriter _writer = writer;
    private readonly ILogger<MetadataGateway> _logger = logger ?? NullLogger<MetadataGateway>.Instance;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    private ExifToolSession? _session;
    private string? _sessionPath;
    private bool _disposed;

    /// <summary>
    /// The zone a naive date is read as. Settable so a run can override it and so tests do
    /// not depend on where the build machine is.
    /// </summary>
    public TimeZoneInfo LocalZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>Whether metadata work can happen at all right now.</summary>
    public bool Available => this._exifTool.Status.Available;

    /// <summary>
    /// Whether a file is worth asking ExifTool about.
    ///
    /// A cloud placeholder is excluded because reading its metadata means downloading the
    /// whole file. Filesystem dates need no such thing, which is why the simple path can
    /// never accidentally pull 200 GB out of OneDrive.
    /// </summary>
    public static bool CanRead(ScannedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return !file.IsDirectory
            && file.Kind != MediaKind.Other
            && !file.Traits.HasFlag(FileTraits.CloudDehydrated);
    }

    /// <summary>
    /// Reads metadata for as many of these files as make sense, in batches.
    /// </summary>
    /// <param name="files">The scanned files. Ones with no metadata to read are skipped here.</param>
    /// <param name="progress">Files read so far, for the status line.</param>
    /// <param name="cancellationToken">Stops between batches.</param>
    public async Task<IReadOnlyDictionary<string, FileMetadata>> ReadAsync(
        IReadOnlyList<ScannedFile> files,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var results = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);

        if (!this.Available)
        {
            return results;
        }

        string[] paths = [.. files.Where(CanRead).Select(f => f.FullPath)];

        if (paths.Length == 0)
        {
            return results;
        }

        ExifToolSession? session = await this.GetSessionAsync(cancellationToken).ConfigureAwait(false);

        if (session is null)
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
                IReadOnlyList<FileMetadata> read = await this._reader
                    .ReadAsync(session, batch, this.LocalZone, cancellationToken)
                    .ConfigureAwait(false);

                foreach (FileMetadata file in read)
                {
                    results[file.Path] = file;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The process went away mid-run. The rest of the scan is still useful, and
                // the files with no metadata read simply show none.
                this._logger.LogWarning(ex, "ExifTool stopped responding during a read; the remaining files have no metadata.");
                break;
            }

            done += batch.Length;
            progress?.Report(done);
        }

        return results;
    }

    /// <summary>
    /// Reads one file's date tags. Used by undo, which has to ask what the file holds now
    /// before deciding whether putting it back would overwrite somebody else's change.
    /// </summary>
    public async Task<FileMetadata?> ReadOneAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!this.Available)
        {
            return null;
        }

        ExifToolSession? session = await this.GetSessionAsync(cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return null;
        }

        try
        {
            IReadOnlyList<FileMetadata> read = await this._reader
                .ReadAsync(session, [path], this.LocalZone, cancellationToken)
                .ConfigureAwait(false);

            return read.Count > 0 ? read[0] : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            this._logger.LogWarning(ex, "Could not read the current metadata of {Path}.", path);
            return null;
        }
    }

    /// <summary>
    /// Writes one file's tags. One file per call, because a batch reports a single status
    /// and this app's promise is that you know what happened to each file.
    /// </summary>
    public async Task<MetadataWriteResult> WriteAsync(
        MetadataWriteRequest request,
        bool keepBackup = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.Available)
        {
            return new MetadataWriteResult(
                request.Path,
                Succeeded: false,
                MetadataWriter.DestinationFor(request.Kind),
                null,
                "ExifTool is not available, so the photo date could not be written.");
        }

        ExifToolSession? session = await this.GetSessionAsync(cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return new MetadataWriteResult(
                request.Path,
                Succeeded: false,
                MetadataWriter.DestinationFor(request.Kind),
                null,
                "ExifTool would not start, so the photo date could not be written.");
        }

        try
        {
            return await this._writer.WriteAsync(session, request, keepBackup, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            this._logger.LogWarning(ex, "ExifTool stopped responding while writing {Path}.", request.Path);

            // The session is wedged, so the next call starts a fresh one rather than
            // reporting the same failure for every remaining file.
            await this.DiscardSessionAsync().ConfigureAwait(false);

            return new MetadataWriteResult(
                request.Path,
                Succeeded: false,
                MetadataWriter.DestinationFor(request.Kind),
                null,
                "ExifTool stopped responding while writing this file.");
        }
    }

    /// <summary>
    /// The running session, started on first use and reused after that.
    ///
    /// The configured install is checked each time: a machine-wide copy can be upgraded or
    /// uninstalled between one batch and the next, and holding a handle to a process whose
    /// executable has been replaced is how a run ends up half-done with no explanation.
    /// </summary>
    private async Task<ExifToolSession?> GetSessionAsync(CancellationToken cancellationToken)
    {
        string? path = this._exifTool.Status.Install?.ExecutablePath;

        if (path is null)
        {
            return null;
        }

        await this._sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(this._disposed, this);

            if (this._session is not null && string.Equals(this._sessionPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return this._session;
            }

            if (this._session is not null)
            {
                await this._session.DisposeAsync().ConfigureAwait(false);
                this._session = null;
            }

            try
            {
                this._session = ExifToolSession.Start(path, this._logger);
                this._sessionPath = path;

                this._logger.LogInformation(
                    "Started ExifTool from {Path} as pid {Pid}.", path, this._session.ProcessId);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                this._logger.LogWarning(ex, "Could not start ExifTool from {Path}.", path);
                this._session = null;
                this._sessionPath = null;
            }

            return this._session;
        }
        finally
        {
            _ = this._sessionLock.Release();
        }
    }

    private async Task DiscardSessionAsync()
    {
        await this._sessionLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (this._session is not null)
            {
                await this._session.DisposeAsync().ConfigureAwait(false);
                this._session = null;
                this._sessionPath = null;
            }
        }
        finally
        {
            _ = this._sessionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;

        if (this._session is not null)
        {
            await this._session.DisposeAsync().ConfigureAwait(false);
            this._session = null;
        }

        this._sessionLock.Dispose();
    }
}
