// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Rules;
using Shouldly;

namespace PaulTechGuy.CN.Rules.Tests;

/// <summary>
/// Building a pattern by clicking the numbers in a filename.
///
/// The case that decides whether this is useful at all is a camera name, where the whole
/// date is one run of digits: 20240315 has to become year, then month, then day by
/// clicking three times. If a run could only be named as a whole, the feature would work
/// on exactly the tidy filenames the built-ins already handle.
/// </summary>
public class FilenameChipTests
{
    private static FilenameChipBuilder Named(string name) => new(name);

    [Fact]
    public void A_filename_splits_into_runs_of_digits_and_everything_else()
    {
        FilenameChipBuilder chips = Named("IMG_20240315_142530");

        chips.Chips.Select(c => c.Text).ShouldBe(["IMG_", "20240315", "_", "142530"]);
        chips.Chips.Select(c => c.IsDigits).ShouldBe([false, true, false, true]);
    }

    [Fact]
    public void A_role_narrower_than_the_run_splits_it()
    {
        FilenameChipBuilder chips = Named("20240315");

        chips.Assign(0, ChipRole.Year).ShouldBeTrue();

        chips.Chips.Select(c => c.Text).ShouldBe(["2024", "0315"]);

        chips.Assign(1, ChipRole.Month).ShouldBeTrue();
        chips.Assign(2, ChipRole.Day).ShouldBeTrue();

        chips.ToTokens().ShouldBe("{yyyy}{MM}{dd}");
        chips.Problem.ShouldBeNull();
    }

    [Fact]
    public void A_run_too_short_for_the_role_is_refused()
    {
        FilenameChipBuilder chips = Named("IMG_24");

        chips.Assign(1, ChipRole.Year).ShouldBeFalse("a four-digit year does not fit in two digits");
        chips.Assign(1, ChipRole.ShortYear).ShouldBeTrue();
    }

    [Fact]
    public void Clearing_a_role_puts_the_run_back_together()
    {
        FilenameChipBuilder chips = Named("20240315");

        _ = chips.Assign(0, ChipRole.Year);
        chips.Clear(0);

        chips.Chips.Select(c => c.Text).ShouldBe(["20240315"], "a cleared split must heal");
    }

    /// <summary>
    /// Unassigned digits have to be matched by width. The obvious {#} is one-or-more and
    /// greedy, so it would swallow the month and day that follow a named year.
    /// </summary>
    [Fact]
    public void Digits_left_unnamed_are_matched_by_width()
    {
        FilenameChipBuilder chips = Named("20240315");

        _ = chips.Assign(0, ChipRole.Year);

        chips.ToTokens().ShouldBe("{yyyy}{#4}");

        var regex = new Regex(PatternCompiler.TokensToRegex(chips.ToTokens()));

        regex.Match("20240315").Groups["y"].Value.ShouldBe("2024");
    }

    [Fact]
    public void The_pattern_it_builds_actually_matches_the_file_it_was_built_from()
    {
        FilenameChipBuilder chips = Named("Holiday-2011-06-02-at-14.25");

        _ = chips.Assign(1, ChipRole.Year);
        _ = chips.Assign(3, ChipRole.Month);
        _ = chips.Assign(5, ChipRole.Day);
        _ = chips.Assign(7, ChipRole.Hour);
        _ = chips.Assign(9, ChipRole.Minute);

        var regex = new Regex(PatternCompiler.TokensToRegex(chips.ToTokens()));
        Match match = regex.Match("Holiday-2011-06-02-at-14.25");

        match.Success.ShouldBeTrue();
        match.Groups["y"].Value.ShouldBe("2011");
        match.Groups["M"].Value.ShouldBe("06");
        match.Groups["d"].Value.ShouldBe("02");
        match.Groups["H"].Value.ShouldBe("14");
        match.Groups["m"].Value.ShouldBe("25");

        chips.Precision.ShouldBe(DatePrecision.Minute);
    }

    /// <summary>
    /// A day and a month with no year is not a date, and defaulting the year silently
    /// would file four thousand holiday photos under this one.
    /// </summary>
    [Theory]
    [InlineData(ChipRole.Month, "year")]
    [InlineData(ChipRole.Day, "year")]
    public void A_pattern_without_a_year_is_refused(ChipRole role, string expected)
    {
        FilenameChipBuilder chips = Named("x-01-02");

        _ = chips.Assign(1, role);

        chips.Problem.ShouldNotBeNull();
        chips.Problem!.ShouldContain(expected, Case.Insensitive);
    }

    [Fact]
    public void Seconds_without_minutes_are_refused()
    {
        FilenameChipBuilder chips = Named("2011-06-02-30");

        _ = chips.Assign(1, ChipRole.Year);
        _ = chips.Assign(3, ChipRole.Month);
        _ = chips.Assign(5, ChipRole.Day);
        _ = chips.Assign(7, ChipRole.Second);

        chips.Problem.ShouldNotBeNull();
    }

    /// <summary>
    /// A date with no time is day precision, which the evaluator treats differently: it
    /// must set the date and leave the existing time of day alone rather than zero it.
    /// </summary>
    [Fact]
    public void A_date_with_no_time_is_day_precision()
    {
        FilenameChipBuilder chips = Named("2011-06-02");

        _ = chips.Assign(0, ChipRole.Year);
        _ = chips.Assign(2, ChipRole.Month);
        _ = chips.Assign(4, ChipRole.Day);

        chips.Problem.ShouldBeNull();
        chips.Precision.ShouldBe(DatePrecision.Day);
    }

    /// <summary>
    /// Literal text between the numbers must stay literal. A filename full of dots and
    /// brackets would otherwise become regex by accident.
    /// </summary>
    [Fact]
    public void Punctuation_in_the_name_is_not_treated_as_regex()
    {
        FilenameChipBuilder chips = Named("shot(1).2011.06.02");

        _ = chips.Assign(3, ChipRole.Year);
        _ = chips.Assign(5, ChipRole.Month);
        _ = chips.Assign(7, ChipRole.Day);

        var regex = new Regex(PatternCompiler.TokensToRegex(chips.ToTokens()));

        regex.IsMatch("shot(1).2011.06.02").ShouldBeTrue();
        regex.IsMatch("shotX1X_2011_06_02").ShouldBeFalse("the punctuation was literal, not wildcards");
    }
}
