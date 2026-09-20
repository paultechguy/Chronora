// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaulTechGuy.CN.Metadata;

/// <summary>
/// Which ExifTool Chronora offers to install, and how to know it arrived intact.
///
/// This is fetched rather than compiled in, and that is the whole point. A hash baked into
/// an unsigned, manually published binary goes stale the first time Phil Harvey ships a
/// release: the URL 404s or serves different bytes, the check fails, and every new user
/// from that day sees something indistinguishable from a security compromise. A manifest on
/// the project's own pages can be corrected in a commit.
/// </summary>
/// <param name="Version">The version that will be installed.</param>
/// <param name="Url">Where the archive comes from.</param>
/// <param name="Sha256">The expected hash of the archive, hex, case-insensitive.</param>
/// <param name="SizeBytes">Download size, so the consent pane can state it honestly.</param>
public sealed record ExifToolManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes)
{
    /// <summary>Human-readable size for the consent pane, which should not say "12582912".</summary>
    public string SizeText => this.SizeBytes <= 0
        ? "unknown size"
        : $"{this.SizeBytes / (1024.0 * 1024.0):N0} MB";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExifToolManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;

/// <summary>Where the manifest is read from.</summary>
public sealed class ExifToolManifestSource(HttpClient http, ILogger<ExifToolManifestSource>? logger = null)
{
    /// <summary>
    /// Published from the repository's own docs folder, which is already a GitHub Pages
    /// site. Updating the pinned version is then a commit rather than a release.
    /// </summary>
    public const string DefaultUrl = "https://paultechguy.github.io/Chronora/exiftool.json";

    private readonly HttpClient _http = http;
    private readonly ILogger<ExifToolManifestSource> _logger = logger ?? NullLogger<ExifToolManifestSource>.Instance;

    /// <summary>
    /// Reads the manifest. Returns null rather than throwing: an unreachable manifest is a
    /// situation the consent pane has to explain anyway, alongside the other ways a
    /// download can fail on a managed network.
    /// </summary>
    public async Task<ExifToolManifest?> FetchAsync(string? url = null, CancellationToken cancellationToken = default)
    {
        string source = url ?? DefaultUrl;

        try
        {
            ExifToolManifest? manifest = await this._http
                .GetFromJsonAsync(source, ManifestJsonContext.Default.ExifToolManifest, cancellationToken)
                .ConfigureAwait(false);

            if (manifest is null || !IsUsable(manifest))
            {
                this._logger.LogWarning("The ExifTool manifest at {Url} is missing required fields.", source);
                return null;
            }

            this._logger.LogInformation("ExifTool manifest offers version {Version}.", manifest.Version);
            return manifest;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            this._logger.LogWarning(ex, "Could not read the ExifTool manifest from {Url}.", source);
            return null;
        }
    }

    /// <summary>
    /// A manifest missing any of these is worse than none at all: it would let the app
    /// download something it cannot verify.
    /// </summary>
    internal static bool IsUsable(ExifToolManifest manifest) =>
        !string.IsNullOrWhiteSpace(manifest.Version)
        && Uri.TryCreate(manifest.Url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && manifest.Sha256.Length == 64
        && manifest.Sha256.All(Uri.IsHexDigit);
}
