// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Rules;

/// <summary>What happened when a wall-clock time was converted to an instant.</summary>
public enum ClockConversion
{
    Ok,

    /// <summary>The time does not exist: it falls in a spring-forward gap.</summary>
    Invalid,

    /// <summary>The time happens twice: it falls in a fall-back overlap.</summary>
    Ambiguous,
}

/// <summary>How to resolve a local time that is invalid or ambiguous.</summary>
public enum DstPolicy
{
    /// <summary>Report it and let the user decide. The default, because guessing is silent.</summary>
    Report,

    /// <summary>Invalid times move forward past the gap; ambiguous times take the first (DST) reading.</summary>
    PreferFirst,

    /// <summary>Invalid times move forward past the gap; ambiguous times take the second (standard) reading.</summary>
    PreferSecond,
}

/// <summary>
/// The single place wall-clock time becomes an instant, and back.
///
/// It exists as one surface because the BCL's defaults are wrong for this app in both
/// directions: ConvertTimeToUtc throws on a time in a spring-forward gap, and silently picks
/// standard time for one in a fall-back overlap. A twenty-year photo library hits both dozens
/// of times, and a bulk tool that guesses is a bulk tool that is quietly wrong.
/// </summary>
/// <param name="Zone">The zone wall-clock values are interpreted in.</param>
/// <param name="Policy">What to do when a value is invalid or ambiguous.</param>
public sealed record ClockContext(TimeZoneInfo Zone, DstPolicy Policy = DstPolicy.Report)
{
    public static ClockContext Local => new(TimeZoneInfo.Local);

    /// <summary>
    /// Interprets a naive wall-clock reading as an instant in this zone.
    /// </summary>
    public ClockConversion ToInstant(DateTime wallClock, out DateTimeOffset instant)
    {
        DateTime unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

        if (this.Zone.IsInvalidTime(unspecified))
        {
            // The reading names a moment that never happened. Moving forward by the gap is
            // the only interpretation that keeps the day and the ordering intact.
            TimeSpan gap = this.GapAt(unspecified);
            instant = new DateTimeOffset(unspecified.Add(gap), this.Zone.GetUtcOffset(unspecified.Add(gap)));
            return ClockConversion.Invalid;
        }

        if (this.Zone.IsAmbiguousTime(unspecified))
        {
            TimeSpan[] offsets = this.Zone.GetAmbiguousTimeOffsets(unspecified);

            // Offsets come back unordered; the larger one is the daylight reading.
            TimeSpan chosen = this.Policy == DstPolicy.PreferSecond
                ? offsets.Min()
                : offsets.Max();

            instant = new DateTimeOffset(unspecified, chosen);
            return ClockConversion.Ambiguous;
        }

        instant = new DateTimeOffset(unspecified, this.Zone.GetUtcOffset(unspecified));
        return ClockConversion.Ok;
    }

    /// <summary>The wall-clock reading of an instant, in this zone.</summary>
    public DateTime ToWallClock(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, this.Zone).DateTime;

    /// <summary>
    /// Shifts a value, honouring what the shift was meant to mean.
    ///
    /// Wall-clock: the displayed reading moves by the delta, and the underlying instant moves
    /// by however much that turns out to require. Instant: the reverse.
    /// </summary>
    public DateTimeOffset Shift(DateTimeOffset value, TimeSpan delta, ShiftBasis basis)
    {
        if (basis == ShiftBasis.Instant)
        {
            return value + delta;
        }

        DateTime shifted = this.ToWallClock(value).Add(delta);
        _ = this.ToInstant(shifted, out DateTimeOffset instant);
        return instant;
    }

    /// <summary>
    /// Reinterprets a reading as having been taken in a different zone: the wall-clock digits
    /// stay, the instant moves. This is what "the camera was still set to New York" means.
    /// </summary>
    public static DateTimeOffset ReinterpretZone(DateTimeOffset value, TimeZoneInfo from, TimeZoneInfo to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        DateTime wall = TimeZoneInfo.ConvertTime(value, from).DateTime;
        DateTime unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);

        TimeSpan offset = to.IsInvalidTime(unspecified)
            ? to.GetUtcOffset(unspecified.AddHours(1))
            : to.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset);
    }

    /// <summary>
    /// How wide the spring-forward gap is. Almost always an hour, but Lord Howe Island uses
    /// thirty minutes and some historical transitions were stranger still, so it is measured
    /// rather than assumed.
    /// </summary>
    private TimeSpan GapAt(DateTime unspecified)
    {
        TimeSpan before = this.Zone.GetUtcOffset(unspecified.AddDays(-1));
        TimeSpan after = this.Zone.GetUtcOffset(unspecified.AddDays(1));

        TimeSpan gap = after - before;
        return gap > TimeSpan.Zero ? gap : TimeSpan.FromHours(1);
    }
}
