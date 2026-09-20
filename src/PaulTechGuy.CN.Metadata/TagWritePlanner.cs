// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Metadata;

/// <summary>One tag assignment, as it will be handed to ExifTool.</summary>
/// <param name="Tag">The group-qualified tag name.</param>
/// <param name="Value">The value, or empty to delete the tag.</param>
public readonly record struct TagAssignment(string Tag, string Value)
{
    /// <summary>An empty assignment is how ExifTool is told to remove a tag.</summary>
    public bool IsDeletion => this.Value.Length == 0;

    public string ToArgument() => $"-{this.Tag}={this.Value}";
}

/// <summary>
/// Turns a date target into the set of tags that actually have to be written.
///
/// A date field is almost never one tag, and getting this wrong is silent. Three facts drive
/// everything here, and all three contradict what the API looks like it does:
///
/// 1. ExifTool DISCARDS a time zone written into an EXIF date tag. Handing it
///    "2024:03:15 14:25:30+01:00" stores the time and drops the offset, with no error. The
///    offset has to be written as its own OffsetTime* tag or it is simply lost.
///
/// 2. Writing a new date does NOT clear an existing SubSecTime* or OffsetTime*. A photo
///    taken at 14:25:30.847+09:00 and "corrected" to 2024-03-15 14:25 keeps the .847 and the
///    +09:00, describing a moment that never existed - and Chronora's own next scan reads
///    that back and shows the user something they never asked for.
///
/// 3. -AllDates cannot carry offsets either. The feature was requested upstream and
///    declined, because AllDates means "these tags share one formatted value" and a zone
///    breaks that premise.
/// </summary>
public static class TagWritePlanner
{
    /// <summary>ExifTool's date format. Colons in the date part, which is not a typo.</summary>
    private const string DateFormat = "yyyy:MM:dd HH:mm:ss";

    /// <summary>
    /// Every tag that must be written for one field to end up correct and self-consistent.
    /// </summary>
    /// <param name="field">The date field being set.</param>
    /// <param name="value">The value, including the offset that will be split out.</param>
    /// <param name="precision">
    /// Whether the source actually knew sub-seconds. When it did not, the existing
    /// sub-second tag is DELETED rather than left behind to contradict the new time.
    /// </param>
    /// <param name="writeOffset">
    /// Whether to record the UTC offset. Off means the existing offset tag is cleared, so
    /// the file does not keep claiming a zone the new value was not expressed in.
    /// </param>
    public static IReadOnlyList<TagAssignment> Plan(
        DateField field,
        DateTimeOffset value,
        DatePrecision precision,
        bool writeOffset = true)
    {
        DateFieldSpec spec = DateFieldCatalog.Get(field);

        if (spec.Genre != FieldGenre.Metadata || spec.Tag is null)
        {
            throw new ArgumentException($"{field} is not a metadata field.", nameof(field));
        }

        var assignments = new List<TagAssignment>(3);

        // XMP carries the offset inside the value itself, so it is a single ISO-8601 write
        // and none of the companion-tag machinery applies.
        if (spec.CarriesOwnOffset)
        {
            assignments.Add(new TagAssignment(spec.Tag, value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)));
            return assignments;
        }

        // The naive wall-clock reading, with no zone in it - because the tag has no room
        // for one and ExifTool would drop it silently.
        assignments.Add(new TagAssignment(spec.Tag, value.ToString(DateFormat, CultureInfo.InvariantCulture)));

        if (spec.OffsetTag is not null)
        {
            assignments.Add(writeOffset
                ? new TagAssignment(spec.OffsetTag, FormatOffset(value.Offset))

                // Cleared, not skipped. Leaving a stale offset behind is worse than having
                // none: it makes the file assert a zone the new time was never in.
                : new TagAssignment(spec.OffsetTag, string.Empty));
        }

        if (spec.SubSecondTag is not null)
        {
            bool hasSubSeconds = precision == DatePrecision.Millisecond && value.Millisecond != 0;

            assignments.Add(hasSubSeconds
                ? new TagAssignment(spec.SubSecondTag, value.Millisecond.ToString("000", CultureInfo.InvariantCulture))
                : new TagAssignment(spec.SubSecondTag, string.Empty));
        }

        return assignments;
    }

    /// <summary>
    /// Restores a tag to exactly what was there before, including deleting it when it was
    /// not there at all.
    ///
    /// Reverting "absent" has to DELETE rather than blank, and that cannot be inferred from
    /// the recorded value being null, because a tag can exist and be empty.
    /// </summary>
    public static TagAssignment PlanRestore(string tag, bool wasPresent, string? raw) =>
        wasPresent
            ? new TagAssignment(tag, raw ?? string.Empty)
            : new TagAssignment(tag, string.Empty);

    /// <summary>
    /// EXIF 2.31 offsets are "+HH:MM", including for UTC, which is "+00:00" rather than "Z".
    /// </summary>
    internal static string FormatOffset(TimeSpan offset)
    {
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan magnitude = offset.Duration();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{magnitude.Hours:00}:{magnitude.Minutes:00}");
    }

    /// <summary>
    /// Whether QuickTime dates in this file should be read as UTC.
    ///
    /// -api QuickTimeUTC cannot be a blanket constant. The atoms are specified as UTC, but a
    /// great many cameras and phones write local time into them regardless. Applying the
    /// flag uniformly shifts half a library by the UTC offset in the wrong direction, so it
    /// is inferred per file and the inference is recorded and shown.
    /// </summary>
    /// <param name="quickTimeValue">The value as read without the flag.</param>
    /// <param name="corroborating">
    /// A date from elsewhere in the same file - usually EXIF DateTimeOriginal - which is
    /// known to be local time.
    /// </param>
    /// <param name="localOffset">The offset the corroborating value is expressed in.</param>
    public static bool ShouldTreatQuickTimeAsUtc(
        DateTimeOffset quickTimeValue,
        DateTimeOffset? corroborating,
        TimeSpan localOffset)
    {
        // With nothing to compare against, the specification is the better guess: the atom
        // is DEFINED as UTC, so honour that rather than assuming the camera was wrong.
        if (corroborating is not { } reference)
        {
            return true;
        }

        // If the two already agree, the QuickTime value is in the same frame as the known
        // local time, which means the camera wrote local time and the flag must stay off.
        TimeSpan asWritten = (quickTimeValue - reference).Duration();

        // Re-reading the same digits as UTC moves the instant by the local offset, and the
        // sign is easy to get backwards: digits interpreted in a -07:00 zone land seven
        // hours LATER than the same digits interpreted as UTC, so recovering the UTC
        // reading means adding the (negative) offset.
        TimeSpan asUtc = (quickTimeValue + localOffset - reference).Duration();

        var tolerance = TimeSpan.FromMinutes(2);

        if (asWritten <= tolerance)
        {
            return false;
        }

        if (asUtc <= tolerance)
        {
            return true;
        }

        // Neither reading reconciles them. Prefer whichever is closer rather than picking
        // a side, and the caller surfaces the disagreement as a suspicious row.
        return asUtc < asWritten;
    }
}
