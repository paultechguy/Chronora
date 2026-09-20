// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Metadata;

/// <summary>What one file's metadata read produced.</summary>
/// <param name="Path">The file, as ExifTool echoed it back.</param>
/// <param name="Values">Every date field that was asked for and found.</param>
/// <param name="QuickTimeReadAsUtc">
/// Which way the QuickTime timezone question was decided for this file. Recorded because
/// the inference can be wrong and the user is entitled to see which way it went.
/// </param>
public sealed record FileMetadata(
    string Path,
    FrozenDictionary<DateField, MetadataValue> Values,
    bool QuickTimeReadAsUtc);

/// <summary>
/// Reads date tags out of files, in batches, and turns them into domain values.
///
/// Two things here are easy to get wrong and silent when you do.
///
/// The offset lives in a separate tag. An EXIF date tag has no room for a time zone, so
/// DateTimeOriginal and OffsetTimeOriginal have to be read together and recombined, or
/// every photo appears to have been taken in whatever zone this PC happens to be in.
///
/// QuickTime dates are specified as UTC and very often are not. Half the cameras in the
/// world write local time into an atom the standard says is UTC, so the flag cannot be a
/// constant - applied uniformly it shifts half a library the wrong way. It is inferred per
/// file, against the EXIF date when the file has one.
/// </summary>
public sealed class MetadataReader(ILogger<MetadataReader>? logger = null)
{
    /// <summary>
    /// How many files go in one ExifTool command.
    ///
    /// Reads are safe to batch because the JSON carries SourceFile on every record, so a
    /// result is always attributable. Writes are not, which is why they are one per
    /// command.
    /// </summary>
    public const int BatchSize = 200;

    private readonly ILogger<MetadataReader> _logger = logger ?? NullLogger<MetadataReader>.Instance;

    /// <summary>
    /// Every tag that has to be requested to fill in the date fields, including the offset
    /// and sub-second companions that give the dates their real meaning.
    /// </summary>
    internal static IReadOnlyList<string> TagsToRequest { get; } = BuildTagList();

    /// <summary>
    /// Reads one batch. The caller chunks; this does one round trip.
    /// </summary>
    /// <param name="session">A running ExifTool.</param>
    /// <param name="paths">Files to read. Should be no more than <see cref="BatchSize" />.</param>
    /// <param name="localZone">
    /// The zone a naive date is understood to be in when the file does not say. Injected
    /// rather than read from the machine so the behaviour is testable and so a future
    /// per-run zone override has somewhere to go.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<IReadOnlyList<FileMetadata>> ReadAsync(
        IExifToolSession session,
        IReadOnlyList<string> paths,
        TimeZoneInfo localZone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(localZone);

        if (paths.Count == 0)
        {
            return [];
        }

        var arguments = new List<string>(TagsToRequest.Count + paths.Count + 2) { "-j" };

        // Structured dates rather than ExifTool's display formatting. Without this a tag
        // can come back prettied up, and a prettied value is not what is on disk - which
        // matters because Raw is what a revert writes back.
        arguments.AddRange(TagsToRequest.Select(tag => "-" + tag));
        arguments.AddRange(paths);

        ExifToolResult result = await session.ExecuteAsync(arguments, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            // Per-file warnings are normal in any real library - a truncated JPEG, an
            // unknown maker note - and are not a reason to lose the whole batch.
            this._logger.LogDebug("ExifTool reported while reading: {Errors}", result.StandardError.Trim());
        }

        return this.Parse(result.StandardOutput, localZone);
    }

    /// <summary>
    /// Turns ExifTool's JSON array into one entry per file.
    ///
    /// Parsing failures cost the batch nothing: a file whose record cannot be understood
    /// comes back with no metadata, which is the same state as a file that has none, and
    /// the app already handles that everywhere.
    /// </summary>
    internal IReadOnlyList<FileMetadata> Parse(string json, TimeZoneInfo localZone)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            this._logger.LogWarning(ex, "ExifTool returned output that is not JSON; treating the batch as unreadable.");
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var files = new List<FileMetadata>(document.RootElement.GetArrayLength());

            foreach (JsonElement record in document.RootElement.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string path = record.TryGetProperty("SourceFile", out JsonElement source)
                    ? source.GetString() ?? string.Empty
                    : string.Empty;

                if (path.Length == 0)
                {
                    continue;
                }

                files.Add(ReadOne(path, record, localZone));
            }

            return files;
        }
    }

    private static FileMetadata ReadOne(string path, JsonElement record, TimeZoneInfo localZone)
    {
        var values = new Dictionary<DateField, MetadataValue>();

        // EXIF first, because the QuickTime inference needs it as its reference point.
        foreach (DateFieldSpec spec in DateFieldCatalog.All
                     .Where(s => s.Genre == FieldGenre.Metadata && !IsQuickTime(s.Field)))
        {
            if (ReadField(record, spec, localZone) is { } value)
            {
                values[spec.Field] = value;
            }
        }

        DateTimeOffset? corroborating = values.TryGetValue(DateField.ExifDateTimeOriginal, out MetadataValue taken)
            ? taken.Parsed
            : null;

        bool readAsUtc = false;

        foreach (DateFieldSpec spec in DateFieldCatalog.All
                     .Where(s => s.Genre == FieldGenre.Metadata && IsQuickTime(s.Field)))
        {
            string? raw = RawOf(record, spec.Tag!);

            if (raw is null)
            {
                continue;
            }

            if (!TryParseNaive(raw, out DateTime naive, out int milliseconds))
            {
                // Present but unreadable. Kept byte-exact anyway: a revert has to be able
                // to put the junk back exactly as it found it.
                values[spec.Field] = new MetadataValue(Present: true, raw, Parsed: null);
                continue;
            }

            TimeSpan localOffset = localZone.GetUtcOffset(naive);
            var asLocal = new DateTimeOffset(naive.AddMilliseconds(milliseconds), localOffset);

            readAsUtc = TagWritePlanner.ShouldTreatQuickTimeAsUtc(asLocal, corroborating, localOffset);

            values[spec.Field] = new MetadataValue(
                Present: true,
                raw,
                readAsUtc ? new DateTimeOffset(naive.AddMilliseconds(milliseconds), TimeSpan.Zero) : asLocal);
        }

        return new FileMetadata(path, values.ToFrozenDictionary(), readAsUtc);
    }

    /// <summary>
    /// One non-QuickTime field, recombined with its offset and sub-second companions.
    /// </summary>
    private static MetadataValue? ReadField(JsonElement record, DateFieldSpec spec, TimeZoneInfo localZone)
    {
        string? raw = RawOf(record, spec.Tag!);

        if (raw is null)
        {
            return null;
        }

        // XMP carries its offset inside the value, so it round-trips through the standard
        // ISO parse without any companion tags.
        if (spec.CarriesOwnOffset)
        {
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset iso)
                ? new MetadataValue(Present: true, raw, iso)
                : new MetadataValue(Present: true, raw, Parsed: null);
        }

        if (!TryParseNaive(raw, out DateTime naive, out int milliseconds))
        {
            // "0000:00:00 00:00:00" lands here, and so do the genuinely malformed values
            // real cameras write. Present, preserved, not pretended to be a date.
            return new MetadataValue(Present: true, raw, Parsed: null);
        }

        // A sub-second tag may add precision the date tag cannot hold.
        if (milliseconds == 0 && spec.SubSecondTag is not null && RawOf(record, spec.SubSecondTag) is { } subSecond)
        {
            milliseconds = ParseSubSecond(subSecond);
        }

        TimeSpan offset = spec.OffsetTag is not null && RawOf(record, spec.OffsetTag) is { } offsetText
            && TryParseOffset(offsetText, out TimeSpan declared)
                ? declared

                // No offset tag means the file does not say, and the convention for a naive
                // EXIF date is the local zone. Guessing UTC instead would shift every
                // undated-zone photo in the library by the machine's offset.
                : localZone.GetUtcOffset(naive);

        return new MetadataValue(
            Present: true,
            raw,
            new DateTimeOffset(naive.AddMilliseconds(milliseconds), offset));
    }

    /// <summary>
    /// The tag's value as a string, whatever JSON type ExifTool chose for it.
    ///
    /// ExifTool emits a bare number for some tags, and asking a JSON number for its string
    /// throws, which would lose the whole file over a tag that parsed fine.
    /// </summary>
    private static string? RawOf(JsonElement record, string tag)
    {
        if (!record.TryGetProperty(tag, out JsonElement element))
        {
            // With -G1 the key is group-qualified, but a tag that exists in only one group
            // can come back unqualified, so the bare name is worth a second look.
            int colon = tag.LastIndexOf(':');

            if (colon < 0 || !record.TryGetProperty(tag[(colon + 1)..], out element))
            {
                return null;
            }
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null => null,
            _ => element.GetRawText(),
        };
    }

    /// <summary>
    /// ExifTool's date format: "2024:03:15 14:25:30", optionally with a fraction and
    /// optionally with an offset the caller may or may not want.
    /// </summary>
    internal static bool TryParseNaive(string raw, out DateTime value, out int milliseconds)
    {
        value = default;
        milliseconds = 0;

        string text = raw.Trim();

        if (text.Length < 19)
        {
            return false;
        }

        // A trailing offset is stripped here and read from the offset tag instead, which is
        // where it is authoritative. Both are usually present and agree; when they do not,
        // the dedicated tag is the one EXIF 2.31 defines.
        string head = text[..19];
        string tail = text[19..];

        if (!DateTime.TryParseExact(
                head, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            // Some writers use dashes. Accepted on read, never produced on write.
            if (!DateTime.TryParseExact(
                    head, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
            {
                return false;
            }
        }

        if (tail.StartsWith('.'))
        {
            int digits = 1;
            while (digits < tail.Length && char.IsAsciiDigit(tail[digits]))
            {
                digits++;
            }

            milliseconds = ParseSubSecond(tail[1..digits]);
        }

        return true;
    }

    /// <summary>
    /// A sub-second tag is a FRACTION, not a count: "8" means .8 seconds, not 8 ms. Reading
    /// it as an integer makes every such photo 792 ms early.
    /// </summary>
    internal static int ParseSubSecond(string text)
    {
        string digits = new([.. text.Trim().TakeWhile(char.IsAsciiDigit)]);

        if (digits.Length == 0)
        {
            return 0;
        }

        // Normalise to three digits, which is all a DateTime can hold.
        digits = digits.Length >= 3 ? digits[..3] : digits.PadRight(3, '0');

        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    /// <summary>EXIF 2.31 offsets are "+HH:MM". "Z" appears in the wild and means UTC.</summary>
    internal static bool TryParseOffset(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        string trimmed = text.Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed is "Z" or "z")
        {
            return true;
        }

        if (trimmed[0] is not ('+' or '-'))
        {
            return false;
        }

        if (!TimeSpan.TryParseExact(trimmed[1..], @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan magnitude))
        {
            return false;
        }

        offset = trimmed[0] == '-' ? -magnitude : magnitude;
        return true;
    }

    private static bool IsQuickTime(DateField field) =>
        field is DateField.QuickTimeCreateDate or DateField.QuickTimeModifyDate;

    private static List<string> BuildTagList()
    {
        var tags = new List<string>();

        foreach (DateFieldSpec spec in DateFieldCatalog.All.Where(s => s.Genre == FieldGenre.Metadata))
        {
            if (spec.Tag is not null)
            {
                tags.Add(spec.Tag);
            }

            if (spec.OffsetTag is not null)
            {
                tags.Add(spec.OffsetTag);
            }

            if (spec.SubSecondTag is not null)
            {
                tags.Add(spec.SubSecondTag);
            }
        }

        return tags;
    }
}
