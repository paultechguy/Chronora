// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaulTechGuy.CN.Services;

/// <summary>
/// The published release list, as it appears on disk.
///
/// Its own shape rather than the domain's, for the same reason the ExifTool manifest has
/// one: this is a file format that every future version of Chronora has to keep reading,
/// and it is served from a static site where nothing can migrate it.
/// </summary>
/// <param name="Version">The newest release, as a three-part version.</param>
/// <param name="Url">Where a person goes to get it. A page, not a binary.</param>
/// <param name="Notes">One line on what changed. Optional.</param>
/// <param name="PublishedUtc">When it went out. Optional.</param>
public sealed record ReleaseManifest(
    string Version,
    string Url,
    string? Notes,
    DateTimeOffset? PublishedUtc);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReleaseManifest))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext;

/// <summary>What a check concluded.</summary>
public enum UpdateOutcome
{
    /// <summary>Nothing newer is published.</summary>
    UpToDate,

    /// <summary>A newer release exists.</summary>
    UpdateAvailable,

    /// <summary>The question could not be answered. Not the same as "no".</summary>
    CouldNotCheck,
}

/// <summary>
/// The result of one check, including why it failed when it did.
/// </summary>
/// <param name="Outcome">What was concluded.</param>
/// <param name="Latest">The published version, when one was read.</param>
/// <param name="Url">Where to get it.</param>
/// <param name="Notes">What changed.</param>
/// <param name="Problem">Plain words for a person, when the check could not be made.</param>
public sealed record UpdateStatus(
    UpdateOutcome Outcome,
    Version? Latest,
    string? Url,
    string? Notes,
    string? Problem)
{
    public static UpdateStatus Failed(string problem) =>
        new(UpdateOutcome.CouldNotCheck, null, null, null, problem);
}

/// <summary>
/// Asks whether a newer Chronora has been published.
///
/// User-initiated only. Nothing calls this on a timer or at startup: the app's network
/// policy is two narrow things a person asked for, and a background call home is neither.
/// The About window has a button, and that is the entire trigger.
///
/// It never downloads or installs anything. A newer version resolves to a link the person
/// clicks, which keeps the whole feature to one GET of a small JSON file and leaves the
/// decision where it belongs.
/// </summary>
public sealed class UpdateChecker(HttpClient http, ILogger<UpdateChecker>? logger = null)
{
    /// <summary>
    /// Served from the repository's own docs folder, the GitHub Pages site that already
    /// carries the ExifTool manifest - so announcing a release is a commit, and a release
    /// that turns out to be broken can be unannounced the same way.
    /// </summary>
    public const string DefaultUrl = "https://paultechguy.github.io/Chronora/version.json";

    private readonly HttpClient _http = http;
    private readonly ILogger<UpdateChecker> _logger = logger ?? NullLogger<UpdateChecker>.Instance;

    /// <summary>
    /// Reads the release manifest and compares it with the running version.
    ///
    /// Every failure resolves to <see cref="UpdateOutcome.CouldNotCheck" /> with something
    /// a person can read. That distinction earns its place: reporting "you are up to date"
    /// when the site was unreachable is a lie that keeps somebody on a version with a bug
    /// that has already been fixed.
    /// </summary>
    public async Task<UpdateStatus> CheckAsync(
        Version current,
        string? url = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);

        string endpoint = url ?? DefaultUrl;

        try
        {
            // A short timeout of its own. The shared client allows five minutes because it
            // also pulls a 12 MB download; nobody should wait that long to be told about a
            // version number.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            string json = await this._http.GetStringAsync(endpoint, timeout.Token).ConfigureAwait(false);

            ReleaseManifest? manifest =
                JsonSerializer.Deserialize(json, ReleaseJsonContext.Default.ReleaseManifest);

            if (manifest is null || !Version.TryParse(manifest.Version, out Version? latest))
            {
                this._logger.LogWarning("The release manifest at {Url} could not be understood.", endpoint);

                return UpdateStatus.Failed("The list of releases could not be read.");
            }

            // Build and revision are ignored on purpose: a release is identified by its
            // three-part version, and 0.1.0 built twice is still 0.1.0.
            var running = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
            var published = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));

            this._logger.LogInformation(
                "Update check: running {Running}, published {Published}.",
                running,
                published);

            return published > running
                ? new UpdateStatus(UpdateOutcome.UpdateAvailable, published, manifest.Url, manifest.Notes, null)
                : new UpdateStatus(UpdateOutcome.UpToDate, published, manifest.Url, manifest.Notes, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return UpdateStatus.Failed("Checking for updates took too long.");
        }
        catch (HttpRequestException ex)
        {
            this._logger.LogWarning(ex, "Could not reach {Url}.", endpoint);

            return UpdateStatus.Failed("Chronora could not reach the list of releases.");
        }
        catch (JsonException ex)
        {
            this._logger.LogWarning(ex, "The release manifest at {Url} is not valid JSON.", endpoint);

            return UpdateStatus.Failed("The list of releases could not be read.");
        }
    }
}
