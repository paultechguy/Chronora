// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Rules.Tests;

public class FilenameDateParserTests
{
    private static readonly FilenameDateParser Parser = new(maxYear: 2027);

    [Theory]
    [InlineData("IMG_20240315_142530.jpg", "android-camera", "2024-03-15 14:25:30")]
    [InlineData("VID_20240315_142530.mp4", "android-video", "2024-03-15 14:25:30")]
    [InlineData("PXL_20240315_142530123.jpg", "pixel", "2024-03-15 14:25:30.123")]
    [InlineData("signal-2024-03-15-142530.jpg", "signal", "2024-03-15 14:25:30")]
    [InlineData("photo_2024-03-15_14-25-30.jpg", "telegram", "2024-03-15 14:25:30")]
    [InlineData("Screenshot 2024-03-15 142530.png", "screenshot-windows", "2024-03-15 14:25:30")]
    [InlineData("Screenshot_20240315-142530.png", "screenshot-android", "2024-03-15 14:25:30")]
    [InlineData("2024-03-15 14.25.30.jpg", "iphone-import", "2024-03-15 14:25:30")]
    [InlineData("2024_0315_142530_001.MP4", "dashcam", "2024-03-15 14:25:30")]
    public void A_known_camera_filename_yields_its_date(string fileName, string expectedPattern, string expected)
    {
        FilenameParseResult result = Parser.Parse(fileName);

        result.Success.ShouldBeTrue($"'{fileName}' should have matched a built-in pattern");
        result.Match.PatternId.ShouldBe(expectedPattern);
        result.Match.Value.ShouldBe(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_macos_screenshot_resolves_its_afternoon_meridiem()
    {
        FilenameParseResult result = Parser.Parse("Screen Shot 2024-03-15 at 2.25.30 PM.png");

        result.Success.ShouldBeTrue();
        result.Match.Value.Hour.ShouldBe(14);
    }

    [Fact]
    public void A_macos_screenshot_before_noon_keeps_its_morning_hour()
    {
        FilenameParseResult result = Parser.Parse("Screen Shot 2024-03-15 at 9.05.00 AM.png");

        result.Success.ShouldBeTrue();
        result.Match.Value.Hour.ShouldBe(9);
    }

    [Fact]
    public void Midnight_in_twelve_hour_form_is_zero_not_twelve()
    {
        FilenameParseResult result = Parser.Parse("Screen Shot 2024-03-15 at 12.30.00 AM.png");

        result.Success.ShouldBeTrue();
        result.Match.Value.Hour.ShouldBe(0);
    }

    /// <summary>
    /// The counter after WA is not a clock. Reading it as one would invent a time of day,
    /// and the whole point of tracking precision is that the evaluator then leaves the
    /// existing time alone rather than flattening everything to midnight.
    /// </summary>
    [Theory]
    [InlineData("IMG-20240315-WA0001.jpg")]
    [InlineData("VID-20240315-WA0042.mp4")]
    public void A_whatsapp_name_yields_a_date_but_never_a_time(string fileName)
    {
        FilenameParseResult result = Parser.Parse(fileName);

        result.Success.ShouldBeTrue();
        result.Match.Precision.ShouldBe(DatePrecision.Day);
        result.Match.Value.TimeOfDay.ShouldBe(TimeSpan.Zero);
        result.Match.Value.Date.ShouldBe(new DateTime(2024, 3, 15));
    }

    [Theory]
    [InlineData("IMG_1234.jpg")]              // 1234 is not a year
    [InlineData("DSC00123.JPG")]
    [InlineData("P1000123.JPG")]
    [InlineData("GX010123.MP4")]
    [InlineData("100_0234.JPG")]
    [InlineData("20250230_120000.jpg")]       // February 30th
    [InlineData("20241332_120000.jpg")]       // month 13
    [InlineData("20240315_250000.jpg")]       // hour 25
    [InlineData("9999999999.jpg")]            // ten digits, but the year 2286
    [InlineData("Screenshot (3).png")]
    [InlineData("notes.txt")]
    public void A_filename_without_a_plausible_date_yields_nothing(string fileName) =>
        Parser.Parse(fileName).Success.ShouldBeFalse($"'{fileName}' should not have produced a date");

    [Fact]
    public void A_unix_timestamp_is_read_as_an_instant()
    {
        FilenameParseResult result = Parser.Parse("1710512730.jpg");

        result.Success.ShouldBeTrue();
        result.Match.Value.Year.ShouldBe(2024);
    }

    /// <summary>
    /// The compact date pattern would match inside almost every other pattern here and also
    /// matches serial numbers, so it ships disabled. Enabling it must be a deliberate act.
    /// </summary>
    [Fact]
    public void The_compact_date_pattern_is_off_by_default()
    {
        BuiltInPatterns.ById("date-compact")!.IsEnabled.ShouldBeFalse();

        Parser.Parse("20240315.jpg").Success.ShouldBeFalse();
    }

    [Fact]
    public void A_disabled_pattern_can_still_be_requested_by_name()
    {
        FilenameParseResult result = Parser.ParseWith("20240315.jpg", "date-compact");

        result.Success.ShouldBeTrue();
        result.Match.Value.Date.ShouldBe(new DateTime(2024, 3, 15));
    }

    /// <summary>
    /// Two credible dates in one name. Picking one silently is how a library gets scrambled,
    /// so the row goes to the user instead.
    /// </summary>
    [Fact]
    public void Two_plausible_dates_in_one_name_are_reported_rather_than_guessed()
    {
        FilenameParseResult result = Parser.Parse("IMG_20240315_142530 copy of 2019-01-02.jpg");

        result.Outcome.ShouldBe(FilenameMatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_specific_pattern_wins_over_a_general_one()
    {
        // Both android-camera and iso-basic can match this; order decides, and the specific
        // one must win or the time would be discarded.
        FilenameParseResult result = Parser.Parse("IMG_20240315_142530.jpg");

        result.Match.PatternId.ShouldBe("android-camera");
        result.Match.Precision.ShouldBe(DatePrecision.Second);
    }

    [Fact]
    public void A_year_beyond_the_allowed_range_is_rejected()
    {
        var strict = new FilenameDateParser(minYear: 2000, maxYear: 2020);

        strict.Parse("IMG_20240315_142530.jpg").Success.ShouldBeFalse();
    }

    [Fact]
    public void An_iso_name_carries_its_offset()
    {
        FilenameParseResult result = Parser.Parse("2024-03-15T14:25:30+02:00.jpg");

        result.Success.ShouldBeTrue();
        result.Match.Offset.ShouldBe(TimeSpan.FromHours(2));
    }
}

public class PatternCompilerTests
{
    [Theory]
    [InlineData("{yyyy}", @"(?<y>\d{4})")]
    [InlineData("{MM}", @"(?<M>\d{2})")]
    [InlineData("{unix}", @"(?<unix>\d{10})")]
    public void A_token_compiles_to_its_named_group(string tokens, string expected) =>
        PatternCompiler.TokensToRegex(tokens).ShouldBe(expected);

    /// <summary>
    /// Literal text is escaped, so a user typing a filename fragment containing regex
    /// metacharacters gets what they meant rather than a pattern that matches everything.
    /// </summary>
    [Fact]
    public void Literal_text_around_tokens_is_escaped()
    {
        string regex = PatternCompiler.TokensToRegex("IMG.{yyyy}");

        regex.ShouldContain(@"IMG\.");
    }

    [Fact]
    public void An_unknown_token_fails_loudly()
    {
        Should.Throw<FormatException>(() => PatternCompiler.TokensToRegex("{nope}"));
    }

    [Fact]
    public void An_unclosed_token_fails_loudly()
    {
        Should.Throw<FormatException>(() => PatternCompiler.TokensToRegex("IMG_{yyyy"));
    }

    [Fact]
    public void Every_built_in_pattern_compiles()
    {
        foreach (FilenamePattern pattern in BuiltInPatterns.All)
        {
            Should.NotThrow(() => PatternCompiler.Compile(pattern), $"pattern '{pattern.Id}' should compile");
        }
    }

    [Fact]
    public void Built_in_pattern_ids_are_unique()
    {
        BuiltInPatterns.All.Select(p => p.Id).Distinct(StringComparer.Ordinal)
            .Count().ShouldBe(BuiltInPatterns.All.Count);
    }
}
