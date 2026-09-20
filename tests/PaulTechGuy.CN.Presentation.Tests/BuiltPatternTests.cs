// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Rules;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// A pattern built by clicking the numbers in a filename, driving a real run.
///
/// The chip builder is only worth anything if what it produces reaches the evaluator, and
/// that link is easy to get subtly wrong: the view model and the evaluator have to be
/// holding the same parser, or registering a pattern succeeds, the preview looks right,
/// and every row says "no match".
/// </summary>
public class BuiltPatternTests
{
    private static string Tokens(string name, params (int Chip, ChipRole Role)[] roles)
    {
        var chips = new FilenameChipBuilder(name);

        foreach ((int chip, ChipRole role) in roles)
        {
            chips.Assign(chip, role).ShouldBeTrue($"chip {chip} should accept {role}");
        }

        return chips.ToTokens();
    }

    [Fact]
    public async Task A_built_pattern_dates_the_files_it_was_built_from()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("trip-2011-06-02.jpg", "trip-2014-11-30.jpg");

        // "trip-" "2011" "-" "06" "-" "02"
        string tokens = Tokens(
            "trip-2011-06-02",
            (1, ChipRole.Year),
            (3, ChipRole.Month),
            (5, ChipRole.Day));

        fixture.ViewModel.UseFilenamePattern(tokens, DatePrecision.Day);

        fixture.ViewModel.Source.ShouldBe(SourceChoice.FromFileName);
        fixture.ViewModel.HasCustomPattern.ShouldBeTrue();
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(2, "both names fit the pattern");
    }

    /// <summary>
    /// The count is the whole reassurance the builder offers. "Matches 1 of 1,284" is what
    /// stops somebody applying a pattern that only ever fitted the file they built it from.
    /// </summary>
    [Fact]
    public async Task The_preview_counts_what_would_actually_match()
    {
        using var fixture = new WorkbenchFixture();

        await fixture.LoadAsync("trip-2011-06-02.jpg", "trip-2014-11-30.jpg", "DSC00123.jpg");

        string tokens = Tokens(
            "trip-2011-06-02",
            (1, ChipRole.Year),
            (3, ChipRole.Month),
            (5, ChipRole.Day));

        FilenamePatternPreview preview = fixture.ViewModel.PreviewFilenamePattern(tokens, DatePrecision.Day);

        preview.Total.ShouldBe(3);
        preview.Matched.ShouldBe(2, "DSC00123 is not that shape");
        preview.Samples.Count.ShouldBe(2);
    }

    /// <summary>
    /// Previewing must not change anything. It is a question, asked while a dialog is
    /// open, and answering it by quietly repointing the run would be the worst possible
    /// time to do so.
    /// </summary>
    [Fact]
    public async Task Previewing_a_pattern_changes_nothing()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("trip-2011-06-02.jpg");

        SourceChoice before = fixture.ViewModel.Source;

        _ = fixture.ViewModel.PreviewFilenamePattern("{yyyy}", DatePrecision.Day);

        fixture.ViewModel.Source.ShouldBe(before);
        fixture.ViewModel.HasCustomPattern.ShouldBeFalse();
    }

    [Fact]
    public async Task Forgetting_the_pattern_goes_back_to_the_built_in_ones()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        // A name the built-ins DO recognise, so the difference is visible either way.
        await fixture.LoadAsync("IMG_20240315_142530.jpg");

        fixture.ViewModel.UseFilenamePattern("{yyyy}", DatePrecision.Day);
        fixture.ViewModel.HasCustomPattern.ShouldBeTrue();

        fixture.ViewModel.ForgetFilenamePattern();

        fixture.ViewModel.HasCustomPattern.ShouldBeFalse();
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(1, "the built-in pattern should take over again");
    }
}
