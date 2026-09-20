// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaulTechGuy.CN.Metadata;

/// <summary>The consent record as it is stored, which is deliberately readable by a person.</summary>
/// <param name="ExecutablePath">Where it is.</param>
/// <param name="Ownership">Managed by Chronora, or the user's own.</param>
/// <param name="Version">What was agreed to.</param>
/// <param name="Sha256">Only meaningful for a managed copy.</param>
/// <param name="SourceUrl">Where a managed copy came from, so Repair can fetch it again.</param>
/// <param name="ConsentedUtc">When.</param>
/// <param name="Route">Downloaded, or pointed at an existing install.</param>
public sealed record ConsentRecord(
    string ExecutablePath,
    InstallOwnership Ownership,
    string? Version,
    string? Sha256,
    string? SourceUrl,
    DateTimeOffset ConsentedUtc,
    string Route);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConsentRecord))]
internal sealed partial class ConsentJsonContext : JsonSerializerContext;

/// <summary>
/// Everything about getting hold of ExifTool, in one place.
///
/// The order matters and is the whole design: probe what is already on the machine BEFORE
/// asking for anything. Someone who installed ExifTool through winget last year should
/// never be shown a download prompt, because silent success is the best consent flow there
/// is and asking for something they already have reads as the app not paying attention.
/// </summary>
public sealed class ExifToolService(
    ExifToolLocator locator,
    ExifToolValidator validator,
    ExifToolManifestSource manifests,
    ExifToolInstaller installer,
    ILogger<ExifToolService>? logger = null)
{
    private readonly ExifToolLocator _locator = locator;
    private readonly ExifToolValidator _validator = validator;
    private readonly ExifToolManifestSource _manifests = manifests;
    private readonly ExifToolInstaller _installer = installer;
    private readonly ILogger<ExifToolService> _logger = logger ?? NullLogger<ExifToolService>.Instance;

    /// <summary>The state everything else reads. Never stale: revalidated on every call.</summary>
    public EngineStatus Status { get; private set; } = EngineStatus.NotConfigured;

    /// <summary>
    /// Works out where things stand, at startup and after any change.
    ///
    /// A recorded install is a starting point, never a promise. A machine-wide copy can be
    /// upgraded, uninstalled or quarantined between sessions, so it is checked rather than
    /// trusted.
    /// </summary>
    public async Task<EngineStatus> RefreshAsync(string dataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        ConsentRecord? recorded = ReadConsent(dataDirectory, this._logger);

        if (recorded is not null)
        {
            var install = new ExifToolInstall(
                recorded.ExecutablePath,
                recorded.Ownership,
                recorded.Version,
                recorded.Sha256,
                recorded.SourceUrl,
                recorded.ConsentedUtc);

            this.Status = await this._validator.ValidateAsync(install, cancellationToken).ConfigureAwait(false);
            return this.Status;
        }

        this.Status = EngineStatus.NotConfigured;
        return this.Status;
    }

    /// <summary>
    /// What is already installed on this machine, found before anything is asked of the
    /// user. Empty means there is genuinely nothing to adopt.
    /// </summary>
    public IReadOnlyList<ExifToolCandidate> FindExisting() => this._locator.FindAll();

    /// <summary>What a download would fetch, so the consent pane can state it precisely.</summary>
    public Task<ExifToolManifest?> GetOfferAsync(CancellationToken cancellationToken = default) =>
        this._manifests.FetchAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// Adopts a copy the user already has. Recorded as External, which means Chronora will
    /// not hash-check it and will not silently replace it if it disappears - both because
    /// its lifecycle belongs to them.
    /// </summary>
    public async Task<EngineStatus> UseExistingAsync(
        string executablePath,
        string dataDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);

        var install = new ExifToolInstall(
            executablePath,
            InstallOwnership.External,
            Version: null,
            Sha256: null,
            SourceUrl: null,
            DateTimeOffset.UtcNow);

        EngineStatus status = await this._validator.ValidateAsync(install, cancellationToken).ConfigureAwait(false);

        if (status.Available)
        {
            WriteConsent(dataDirectory, Record(status.Install!, "pointed at an existing install"), this._logger);
        }

        this.Status = status;
        return status;
    }

    /// <summary>
    /// Downloads and installs a copy Chronora owns. Only ever reached from an explicit
    /// choice: nothing here runs on startup or in the background.
    /// </summary>
    public async Task<EngineStatus> InstallAsync(
        string dataDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ExifToolManifest? manifest = await this.GetOfferAsync(cancellationToken).ConfigureAwait(false);

        if (manifest is null)
        {
            this.Status = new EngineStatus(
                false,
                this.Status.Install,
                EngineFault.NotConfigured,
                "Chronora could not reach the list of available ExifTool versions. If this machine is behind "
                + "a proxy, you can install ExifTool yourself and point Chronora at it instead.",
                new HashSet<string>());

            return this.Status;
        }

        InstallResult result = await this._installer
            .InstallAsync(manifest, dataDirectory, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            this.Status = new EngineStatus(
                false, this.Status.Install, EngineFault.NotConfigured, result.Detail, new HashSet<string>());

            return this.Status;
        }

        EngineStatus status = await this._validator.ValidateAsync(result.Install, cancellationToken).ConfigureAwait(false);

        if (status.Available)
        {
            WriteConsent(dataDirectory, Record(status.Install!, "downloaded by Chronora"), this._logger);
        }

        this.Status = status;
        return status;
    }

    /// <summary>
    /// Puts back a managed copy that has gone missing or been altered.
    ///
    /// Restoring the version already agreed to is not a new decision, so this is one click
    /// rather than the whole consent pane again. It is only ever offered for a copy
    /// Chronora installed; an external one that vanished was the user's, and replacing it
    /// unasked would be installing software they did not choose.
    /// </summary>
    public async Task<EngineStatus> RepairAsync(
        string dataDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!this.Status.CanRepair)
        {
            return this.Status;
        }

        this._logger.LogInformation("Repairing the managed ExifTool install.");
        return await this.InstallAsync(dataDirectory, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets the consent, and deletes the copy if Chronora put it there. A copy the user
    /// installed themselves is left alone: removing it was never Chronora's to do.
    /// </summary>
    public void Remove(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        if (this.Status.Install is { Ownership: InstallOwnership.Managed } managed)
        {
            string? folder = Path.GetDirectoryName(managed.ExecutablePath);

            try
            {
                if (folder is not null && Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                this._logger.LogWarning(ex, "Could not delete the managed ExifTool folder.");
            }
        }

        try
        {
            string path = ConsentPath(dataDirectory);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this._logger.LogWarning(ex, "Could not delete the ExifTool consent record.");
        }

        this.Status = EngineStatus.NotConfigured;
    }

    private static ConsentRecord Record(ExifToolInstall install, string route) =>
        new(install.ExecutablePath, install.Ownership, install.Version, install.Sha256, install.SourceUrl, install.ConsentedUtc, route);

    internal static string ConsentPath(string dataDirectory) => Path.Combine(dataDirectory, "exiftool-consent.json");

    internal static ConsentRecord? ReadConsent(string dataDirectory, ILogger logger)
    {
        string path = ConsentPath(dataDirectory);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, ConsentJsonContext.Default.ConsentRecord);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged record is treated as no record: the worst outcome is being asked
            // once more, which beats refusing to start.
            logger.LogWarning(ex, "Could not read the ExifTool consent record; treating it as absent.");
            return null;
        }
    }

    internal static void WriteConsent(string dataDirectory, ConsentRecord record, ILogger logger)
    {
        string path = ConsentPath(dataDirectory);

        try
        {
            _ = Directory.CreateDirectory(dataDirectory);

            // Written to a neighbour and swapped, so a crash mid-write cannot leave a
            // truncated record that reads as "never consented".
            string temp = path + ".tmp";

            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, record, ConsentJsonContext.Default.ConsentRecord);
            }

            File.Move(temp, path, overwrite: true);

            logger.LogInformation(
                "Recorded ExifTool consent: {Version} at {Path} ({Route}).",
                record.Version, record.ExecutablePath, record.Route);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not record the ExifTool consent.");
        }
    }
}
