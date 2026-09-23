// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaulTechGuy.CN.Metadata;

/// <summary>How an install attempt ended.</summary>
/// <param name="Succeeded">Whether ExifTool is now in place and runnable.</param>
/// <param name="Install">What was installed, ready to record as the consent.</param>
/// <param name="Detail">Plain language, already fit to show.</param>
public sealed record InstallResult(bool Succeeded, ExifToolInstall? Install, string Detail)
{
    public static InstallResult Failed(string detail) => new(false, null, detail);
}

/// <summary>
/// Puts a verified copy of ExifTool into Chronora's own folder.
///
/// The download and the unpacking are separate on purpose: the unpacking is where all the
/// ways this can go quietly wrong live, and keeping it free of network code means it can be
/// tested against a synthetic archive rather than only against the internet.
/// </summary>
public sealed class ExifToolInstaller(HttpClient http, ILogger<ExifToolInstaller>? logger = null)
{
    private readonly HttpClient _http = http;
    private readonly ILogger<ExifToolInstaller> _logger = logger ?? NullLogger<ExifToolInstaller>.Instance;

    /// <summary>
    /// How long the download may sit with no bytes arriving before it is abandoned.
    ///
    /// A stall clock, not a speed limit. ExifTool is a large file and a slow link is not a
    /// fault, so every chunk that arrives pushes this back — only a connection that has
    /// stopped saying anything at all runs it down. An overall cap on the whole download
    /// cannot tell a stalled transfer from a slow one, which is why this is not one.
    ///
    /// Settable so a test can prove the behaviour in milliseconds rather than by waiting a
    /// minute, the same bargain <see cref="ExifToolManifestSource.LocalDirectory"/> makes.
    /// </summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Downloads, verifies and installs. Nothing is moved into place until the hash
    /// matches, so a failed or tampered download leaves the previous state untouched.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        ExifToolManifest manifest,
        string dataDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        string scratch = Path.Combine(Path.GetTempPath(), "chronora-exiftool", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(scratch);

        try
        {
            string archive = Path.Combine(scratch, "exiftool.zip");

            try
            {
                await this.DownloadAsync(manifest, archive, progress, cancellationToken).ConfigureAwait(false);
            }
            // This filter has to come first. TaskCanceledException derives from
            // OperationCanceledException, so the general catch below would otherwise
            // swallow a cancellation and report it as a network fault — telling somebody
            // who has just pressed Cancel themselves that their proxy might be to blame.
            // Stopping is an answer, not a failure, and it leaves here as one.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                this._logger.LogInformation("The ExifTool download was cancelled.");
                throw;
            }
            catch (TimeoutException ex)
            {
                this._logger.LogWarning(ex, "The ExifTool download from {Url} stalled.", manifest.Url);

                return InstallResult.Failed(
                    $"The download from {manifest.Url} stopped sending data and was abandoned. {ex.Message} "
                    + "You can try again, or install ExifTool yourself and point Chronora at it instead.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                this._logger.LogWarning(ex, "Could not download ExifTool from {Url}.", manifest.Url);

                return InstallResult.Failed(
                    $"The download from {manifest.Url} did not complete. {ex.Message} "
                    + "On a managed network this is often a proxy or a policy; you can install ExifTool "
                    + "yourself and point Chronora at it instead.");
            }

            // Checked before the hash, because these two failures need different sentences.
            //
            // A host that answers with a web page instead of the file is a real and current
            // hazard: exiftool.org now hands its downloads to SourceForge, whose ordinary
            // /download links serve an HTML interstitial. That arrives here as a perfectly
            // successful 200 whose bytes are not an archive - and reported as a checksum
            // mismatch it reads as "someone tampered with your download", which is alarming
            // and wrong. It is a dead link, and it should say so.
            if (!await LooksLikeZipAsync(archive, cancellationToken).ConfigureAwait(false))
            {
                this._logger.LogWarning("The download from {Url} was not a zip archive.", manifest.Url);

                return InstallResult.Failed(
                    $"What came back from {manifest.Url} was a web page rather than the ExifTool download, so "
                    + "nothing was installed. The link has most likely moved. You can install ExifTool yourself "
                    + "and point Chronora at it instead.");
            }

            string actual = await ExifToolValidator.ComputeSha256Async(archive, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                this._logger.LogWarning(
                    "ExifTool download hash mismatch. Expected {Expected}, got {Actual}.", manifest.Sha256, actual);

                return InstallResult.Failed(
                    "The download did not match the expected checksum, so it was discarded and nothing was "
                    + $"installed.\n\nExpected  {manifest.Sha256}\nGot       {actual}");
            }

            string target = Path.Combine(dataDirectory, "exiftool");

            return Unpack(archive, target, manifest, this._logger);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    /// <summary>
    /// Whether the downloaded file starts with a local-file-header signature.
    ///
    /// "PK\x03\x04" is the first four bytes of every non-empty zip. This is not a security
    /// check - the hash is - it is there so a dead link produces "the link has moved"
    /// rather than a checksum mismatch that sounds like an attack.
    /// </summary>
    internal static async Task<bool> LooksLikeZipAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);

        byte[] magic = new byte[4];
        int read = await stream.ReadAtLeastAsync(magic, 4, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

        return read == 4 && magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04;
    }

    private async Task DownloadAsync(
        ExifToolManifest manifest,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // A second token, linked to the user's so Cancel still reaches every read, but with
        // a clock on it that the arrival of data keeps pushing back. Which of the two fired
        // is recovered below by asking the user's token, since by the time the exception
        // arrives the linked one says only that somebody cancelled.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(this.StallTimeout);

        try
        {
            // The clock covers the headers too. A host that accepts the connection and then
            // never answers is the same hang as one that stops mid-body, and it is the one
            // that used to leave the progress pane up for as long as HttpClient allowed.
            using HttpResponseMessage response = await this._http
                .GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);

            _ = response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength ?? (manifest.SizeBytes > 0 ? manifest.SizeBytes : null);

            await using Stream source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using FileStream target = File.Create(destination);

            byte[] buffer = new byte[81920];
            long written = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                // Any progress buys another full window, which is what keeps this a stall
                // clock rather than a time limit on a large file over a thin link.
                stall.CancelAfter(this.StallTimeout);

                if (total is > 0)
                {
                    progress?.Report(100.0 * written / total.Value);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The user's token is quiet, so it was the stall clock. Reported as its own
            // kind rather than as a cancellation, because the caller has to tell the two
            // apart and an OperationCanceledException from here would read as "the user
            // stopped it" when nobody did.
            throw new TimeoutException(
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"No data arrived for {this.StallTimeout.TotalSeconds:N0} seconds."));
        }
    }

    /// <summary>
    /// Extracts the archive and arranges it the way the app expects.
    ///
    /// Two details about the Windows distribution matter and are easy to get wrong. The
    /// executable ships as "exiftool(-k).exe", where the -k means "pause before exiting" -
    /// run under that name it waits for a keypress that never comes, so it must be renamed.
    /// And it is useless without the exiftool_files folder beside it, which carries the
    /// whole Perl runtime; an exe on its own starts and dies immediately.
    /// </summary>
    internal static InstallResult Unpack(string archivePath, string targetDirectory, ExifToolManifest manifest, ILogger logger)
    {
        string staging = targetDirectory + ".new";

        TryDelete(staging);
        _ = Directory.CreateDirectory(staging);

        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            logger.LogWarning(ex, "Could not extract the ExifTool archive.");
            return InstallResult.Failed($"The download could not be unpacked: {ex.Message}");
        }

        // Distributions have varied between a flat layout and a single wrapper folder, so
        // the exe is found rather than assumed.
        string? exe = Directory
            .EnumerateFiles(staging, "exiftool*.exe", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();

        if (exe is null)
        {
            TryDelete(staging);
            return InstallResult.Failed("The download did not contain an ExifTool executable.");
        }

        string root = Path.GetDirectoryName(exe)!;
        string final = Path.Combine(root, "exiftool.exe");

        if (!string.Equals(exe, final, StringComparison.OrdinalIgnoreCase))
        {
            // "exiftool(-k).exe" waits for a keypress on exit. Under the stay-open protocol
            // that is a process which never answers, which looks exactly like antivirus
            // having eaten it.
            File.Move(exe, final, overwrite: true);
        }

        if (!Directory.Exists(Path.Combine(root, "exiftool_files")))
        {
            TryDelete(staging);
            return InstallResult.Failed(
                "The download was missing its exiftool_files folder, which carries the runtime ExifTool "
                + "needs. Without it the program starts and exits immediately.");
        }

        // Swap only once the staged copy is known good, so a failure never leaves a
        // half-installed folder where a working one used to be.
        TryDelete(targetDirectory);

        try
        {
            Directory.Move(root, targetDirectory);
        }
        catch (IOException ex)
        {
            TryDelete(staging);
            logger.LogWarning(ex, "Could not move the staged ExifTool into place.");
            return InstallResult.Failed($"The unpacked files could not be moved into place: {ex.Message}");
        }

        if (!string.Equals(staging, targetDirectory, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(staging);
        }

        string installed = Path.Combine(targetDirectory, "exiftool.exe");

        logger.LogInformation("Installed ExifTool {Version} at {Path}.", manifest.Version, installed);

        return new InstallResult(
            true,
            new ExifToolInstall(
                installed,
                InstallOwnership.Managed,
                manifest.Version,

                // The hash recorded is the EXECUTABLE's, not the archive's: it is the
                // executable that later goes missing or gets altered by antivirus, and it
                // is what the validator can re-check on every connect.
                ExifToolValidator.ComputeSha256Async(installed, CancellationToken.None).GetAwaiter().GetResult(),
                manifest.Url,
                DateTimeOffset.UtcNow),
            $"ExifTool {manifest.Version} installed.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers in a temp folder are not worth failing an install over.
        }
    }
}
