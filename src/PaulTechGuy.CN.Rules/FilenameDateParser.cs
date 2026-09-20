// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Rules;

/// <summary>Why a filename yielded no date.</summary>
public enum FilenameMatchOutcome
{
    Matched,

    /// <summary>Nothing matched, or everything that matched failed the plausibility gate.</summary>
    NoMatch,

    /// <summary>
    /// More than one plausible date in the name. Reported rather than guessed: silently
    /// picking one is how a photo library gets scrambled.
    /// </summary>
    Ambiguous,
}

/// <summary>The result of parsing one filename.</summary>
public readonly record struct FilenameParseResult(
    FilenameMatchOutcome Outcome,
    FilenameDateMatch Match)
{
    public static FilenameParseResult None => new(FilenameMatchOutcome.NoMatch, default);

    public bool Success => this.Outcome == FilenameMatchOutcome.Matched;
}

/// <summary>
/// Recovers a capture date from a filename.
///
/// Two gates sit between a regex match and a result, and both exist because a plausible-looking
/// wrong date is worse than no date: plausibility rejects nonsense, and the ambiguity check
/// refuses to choose when a name contains two credible dates.
/// </summary>
public sealed class FilenameDateParser
{
    private readonly ConcurrentDictionary<string, Regex> _compiled = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<FilenamePattern> _patterns;
    private readonly int _minYear;
    private readonly int _maxYear;

    public FilenameDateParser(
        IEnumerable<FilenamePattern>? patterns = null,
        int minYear = 1970,
        int? maxYear = null)
    {
        this._patterns = (patterns ?? BuiltInPatterns.All)
            .Where(p => p.IsEnabled)
            .OrderBy(p => p.Order)
            .ToArray();

        this._minYear = minYear;

        // Next year, not this one: a camera whose clock runs fast, or a photo taken abroad
        // across the date line, should not be rejected on 31 December.
        this._maxYear = maxYear ?? DateTime.UtcNow.Year + 1;
    }

    /// <summary>Parses using every enabled pattern, in order.</summary>
    public FilenameParseResult Parse(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        foreach (FilenamePattern pattern in this._patterns)
        {
            string subject = Subject(path, pattern.Scope);
            if (subject.Length == 0)
            {
                continue;
            }

            Regex regex = this._compiled.GetOrAdd(pattern.Id, _ => PatternCompiler.Compile(pattern));

            Match m;
            try
            {
                m = regex.Match(subject);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological user pattern. Skip it rather than failing the whole scan.
                continue;
            }

            if (!m.Success || !this.TryBuild(pattern, m, out FilenameDateMatch built))
            {
                continue;
            }

            // Ambiguity: is there a SECOND plausible date elsewhere in the same name?
            if (this.HasFurtherMatch(subject, built))
            {
                return new FilenameParseResult(FilenameMatchOutcome.Ambiguous, built);
            }

            return new FilenameParseResult(FilenameMatchOutcome.Matched, built);
        }

        return FilenameParseResult.None;
    }

    /// <summary>Parses with one named pattern, which is what DateSource.FromFileName asks for.</summary>
    public FilenameParseResult ParseWith(string path, string patternId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        FilenamePattern? pattern = this._patterns.FirstOrDefault(p => p.Id == patternId)
            ?? BuiltInPatterns.ById(patternId);

        if (pattern is null)
        {
            return FilenameParseResult.None;
        }

        string subject = Subject(path, pattern.Scope);
        Regex regex = this._compiled.GetOrAdd(pattern.Id, _ => PatternCompiler.Compile(pattern));

        try
        {
            Match m = regex.Match(subject);
            return m.Success && this.TryBuild(pattern, m, out FilenameDateMatch built)
                ? new FilenameParseResult(FilenameMatchOutcome.Matched, built)
                : FilenameParseResult.None;
        }
        catch (RegexMatchTimeoutException)
        {
            return FilenameParseResult.None;
        }
    }

    private static string Subject(string path, PatternScope scope) => scope switch
    {
        PatternScope.FileName => Path.GetFileName(path),
        PatternScope.ParentFolderName => Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
        _ => Path.GetFileNameWithoutExtension(path),
    };

    /// <summary>
    /// Re-scans the text outside the accepted match. A second plausible date means the name
    /// is genuinely ambiguous and the row goes to the user rather than to a guess.
    /// </summary>
    private bool HasFurtherMatch(string subject, in FilenameDateMatch accepted)
    {
        int end = accepted.MatchStart + accepted.MatchLength;

        foreach (string remainder in (string[])[subject[..accepted.MatchStart], end < subject.Length ? subject[end..] : string.Empty])
        {
            if (remainder.Length < 8)
            {
                continue;
            }

            foreach (FilenamePattern pattern in this._patterns)
            {
                Regex regex = this._compiled.GetOrAdd(pattern.Id, _ => PatternCompiler.Compile(pattern));

                try
                {
                    Match m = regex.Match(remainder);
                    if (m.Success && this.TryBuild(pattern, m, out _))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Treated as no further match; the primary result still stands.
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The plausibility gate. Rejects IMG_1234 read as the year 1234, 20250230, hour 25, and
    /// a ten-digit serial number read as a Unix timestamp in the year 2286.
    /// </summary>
    private bool TryBuild(FilenamePattern pattern, Match m, out FilenameDateMatch result)
    {
        result = default;

        if (TryGroup(m, "unixms", out long unixMs))
        {
            DateTimeOffset value = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
            if (!this.YearInRange(value.Year))
            {
                return false;
            }

            result = new FilenameDateMatch(pattern.Id, value.UtcDateTime, DatePrecision.Millisecond, TimeSpan.Zero, m.Index, m.Length);
            return true;
        }

        if (TryGroup(m, "unix", out long unixSeconds))
        {
            DateTimeOffset value = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            if (!this.YearInRange(value.Year))
            {
                return false;
            }

            result = new FilenameDateMatch(pattern.Id, value.UtcDateTime, DatePrecision.Second, TimeSpan.Zero, m.Index, m.Length);
            return true;
        }

        if (!TryGroup(m, "y", out long year))
        {
            if (!TryGroup(m, "yy", out long shortYear))
            {
                return false;
            }

            // The usual pivot: 69 and below is this century.
            year = shortYear <= 69 ? 2000 + shortYear : 1900 + shortYear;
        }

        if (!this.YearInRange((int)year)
            || !TryGroup(m, "M", out long month)
            || month is < 1 or > 12
            || !TryGroup(m, "d", out long day))
        {
            return false;
        }

        if (day < 1 || day > DateTime.DaysInMonth((int)year, (int)month))
        {
            return false;
        }

        long hour = 0;
        long minute = 0;
        long second = 0;
        long milli = 0;
        DatePrecision precision = DatePrecision.Day;

        if (TryGroup(m, "H", out hour))
        {
            precision = DatePrecision.Minute;
        }
        else if (TryGroup(m, "h", out long hour12))
        {
            hour = Normalize12Hour(hour12, m.Groups["t"].Success ? m.Groups["t"].Value : null);
            precision = DatePrecision.Minute;
        }

        if (hour > 23)
        {
            return false;
        }

        if (TryGroup(m, "m", out minute) && minute > 59)
        {
            return false;
        }

        if (TryGroup(m, "s", out second))
        {
            // A leap second is clamped rather than rejected: the filename is still usable.
            second = Math.Min(second, 59);
            precision = DatePrecision.Second;
        }

        if (TryGroup(m, "f", out milli))
        {
            precision = DatePrecision.Millisecond;
        }

        // A pattern that declares itself day-only never reports a finer precision, even if
        // the regex happened to capture digits that look like a time. WhatsApp is the case:
        // the counter after WA is not a clock.
        if (pattern.Precision == DatePrecision.Day)
        {
            precision = DatePrecision.Day;
            hour = minute = second = milli = 0;
        }

        var parsed = new DateTime((int)year, (int)month, (int)day, (int)hour, (int)minute, (int)second, (int)milli, DateTimeKind.Unspecified);

        result = new FilenameDateMatch(pattern.Id, parsed, precision, ParseOffset(m), m.Index, m.Length);
        return true;
    }

    private static long Normalize12Hour(long hour12, string? meridiem)
    {
        long hour = hour12 % 12;
        bool pm = meridiem is not null && meridiem.Replace(".", string.Empty, StringComparison.Ordinal)
            .StartsWith("p", StringComparison.OrdinalIgnoreCase);

        return pm ? hour + 12 : hour;
    }

    private static TimeSpan? ParseOffset(Match m)
    {
        if (!m.Groups["z"].Success)
        {
            return null;
        }

        string z = m.Groups["z"].Value;
        if (z.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.Zero;
        }

        string normalized = z.Contains(':', StringComparison.Ordinal) ? z : $"{z[..3]}:{z[3..]}";

        return TimeSpan.TryParse(normalized.TrimStart('+'), CultureInfo.InvariantCulture, out TimeSpan parsed)
            ? (z[0] == '-' ? -parsed : parsed)
            : null;
    }

    private static bool TryGroup(Match m, string name, out long value)
    {
        Group g = m.Groups[name];
        if (!g.Success)
        {
            value = 0;
            return false;
        }

        return long.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private bool YearInRange(int year) => year >= this._minYear && year <= this._maxYear;
}
