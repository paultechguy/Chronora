// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Metadata;

/// <summary>What the privacy read found in one file.</summary>
/// <param name="Path">The file, as ExifTool echoed it back.</param>
/// <param name="Findings">Tag names per category; never values.</param>
public sealed record FilePrivacy(string Path, PrivacyFindings Findings);

/// <summary>One tag, as the metadata viewer shows it.</summary>
/// <param name="Group">The family-1 group: IFD0, GPS, XMP-dc, PNG…</param>
/// <param name="Name">The tag name.</param>
/// <param name="Value">ExifTool's display value. Binary data arrives as its own placeholder text.</param>
public sealed record MetadataTag(string Group, string Name, string Value);

public sealed partial class MetadataReader
{
    /// <summary>
    /// Which personal details each file carries. Same batching and same SourceFile keying as
    /// the date read; a separate pass because it is only paid for while the Private details
    /// intent is chosen.
    ///
    /// Values are looked at only to decide presence and are never logged or kept.
    /// </summary>
    public async Task<IReadOnlyList<FilePrivacy>> ReadPrivacyAsync(
        IExifToolSession session,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return [];
        }

        var arguments = new List<string>(PrivacyTagCatalog.ReadArguments.Count + paths.Count + 1) { "-j" };
        arguments.AddRange(PrivacyTagCatalog.ReadArguments);
        arguments.AddRange(paths);

        ExifToolResult result = await session.ExecuteAsync(arguments, cancellationToken: cancellationToken).ConfigureAwait(false);

        return this.ParsePrivacy(result.StandardOutput);
    }

    /// <summary>
    /// Every tag in one file, for the viewer. <c>-a</c> keeps duplicates across groups and
    /// <c>-u</c> includes unknown tags, because "show all" that quietly omits things is the
    /// kind of promise a privacy feature cannot make. No <c>-b</c>: binary values come back as
    /// ExifTool's "(Binary data N bytes…)" text, which is what a person wants to see anyway.
    /// </summary>
    public async Task<IReadOnlyList<MetadataTag>> ReadAllAsync(
        IExifToolSession session,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(path);

        ExifToolResult result = await session
            .ExecuteAsync(["-j", "-a", "-u", path], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return this.ParseAll(result.StandardOutput);
    }

    internal IReadOnlyList<FilePrivacy> ParsePrivacy(string json)
    {
        var files = new List<FilePrivacy>();

        foreach (JsonElement record in this.Records(json))
        {
            string path = record.TryGetProperty("SourceFile", out JsonElement source) ? source.GetString() ?? string.Empty : string.Empty;

            if (path.Length == 0)
            {
                continue;
            }

            var found = new Dictionary<PrivacyCategory, List<string>>();
            bool embeddedVideo = false;

            // EnumerateObject rather than TryGetProperty: with -a the same key can appear
            // twice, and TryGetProperty would only ever see the last.
            foreach (JsonProperty property in record.EnumerateObject())
            {
                if (PrivacyTagCatalog.IsEmbeddedVideo(property.Name, ValueOf(property.Value)))
                {
                    embeddedVideo = true;
                    continue;
                }

                if (PrivacyTagCatalog.CategoryOf(property.Name) is not { } category)
                {
                    continue;
                }

                if (!found.TryGetValue(category, out List<string>? tags))
                {
                    tags = [];
                    found[category] = tags;
                }

                if (!tags.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(property.Name);
                }
            }

            files.Add(new FilePrivacy(
                path,
                new PrivacyFindings(
                    found.ToFrozenDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value),
                    embeddedVideo)));
        }

        return files;
    }

    internal IReadOnlyList<MetadataTag> ParseAll(string json)
    {
        var tags = new List<MetadataTag>();

        foreach (JsonElement record in this.Records(json))
        {
            foreach (JsonProperty property in record.EnumerateObject())
            {
                if (property.Name == "SourceFile")
                {
                    continue;
                }

                int colon = property.Name.LastIndexOf(':');
                string group = colon < 0 ? string.Empty : property.Name[..colon];
                string name = colon < 0 ? property.Name : property.Name[(colon + 1)..];

                tags.Add(new MetadataTag(group, name, ValueOf(property.Value) ?? string.Empty));
            }

            // One file was asked for; a second record would be somebody else's tags.
            break;
        }

        return tags;
    }

    /// <summary>The top-level records, or nothing when the output is not a JSON array.</summary>
    private List<JsonElement> Records(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Array
                ? [.. document.RootElement.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object).Select(r => r.Clone())]
                : [];
        }
        catch (JsonException ex)
        {
            this._logger.LogWarning(ex, "ExifTool returned output that is not JSON; treating the batch as unreadable.");
            return [];
        }
    }

    private static string? ValueOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };
}
