// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// The deck across the top of the window: what is loaded, what the run will do, what comes
/// out.
///
/// These tests exist because the deck promotes numbers that used to be captions. A caption
/// that is slightly off is a blemish; a headline figure that disagrees with the button
/// underneath it, or a count that promises four rows and produces nine, is a reason not to
/// trust the preview - and the preview is the product.
/// </summary>
public class CommandDeckTests
{
    private static async Task<WorkbenchFixture> ChangingFolderAsync()
    {
        var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.txt", "b.txt", "c.txt", "d.txt");

        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2019, 1, 2, 3, 4, 0, TimeSpan.Zero);
        fixture.ViewModel.Recompute();

        return fixture;
    }

    /// <summary>
    /// A folder that really does produce problems, so the counting tests are pinning a
    /// number rather than agreeing that nought equals nought.
    ///
    /// The fixture records no ExifTool consent, so the engine is unavailable and a photo
    /// date cannot be written. That is the app's commonest blocked field by a wide margin
    /// and it needs no fake.
    /// </summary>
    private static async Task<WorkbenchFixture> ProblemFolderAsync()
    {
        var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);

        await fixture.LoadAsync("a.jpg", "b.jpg", "c.jpg");

        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2019, 1, 2, 3, 4, 0, TimeSpan.Zero);
        fixture.ViewModel.Recompute();

        return fixture;
    }

    /// <summary>
    /// The count on the toggle has to be the number of rows the toggle produces.
    ///
    /// FilesBlocked and FilesSuspicious are independent tallies and a file that is both is
    /// counted in both, so neither of them is the size of this set. Labelling the control
    /// from either would have it say four and then show nine.
    /// </summary>
    [Fact]
    public async Task The_problem_count_is_exactly_what_the_problem_filter_shows()
    {
        using WorkbenchFixture fixture = await ProblemFolderAsync();

        fixture.ViewModel.Summary.FilesWithProblems.ShouldBeGreaterThan(
            0,
            "otherwise this test agrees that nought equals nought and pins nothing");

        fixture.ViewModel.ShowOnlyProblems = true;

        fixture.ViewModel.Rows.Count.ShouldBe(
            fixture.ViewModel.Summary.FilesWithProblems,
            "the label on the filter is drawn from this count, so it has to be the filter's own cardinality");
    }

    /// <summary>The union can never exceed the two tallies added together, and usually is smaller.</summary>
    [Fact]
    public async Task The_problem_count_never_double_counts()
    {
        using WorkbenchFixture fixture = await ProblemFolderAsync();

        ChangeSummary summary = fixture.ViewModel.Summary;

        summary.FilesWithProblems.ShouldBeLessThanOrEqualTo(summary.FilesBlocked + summary.FilesSuspicious);
        summary.FilesWithProblems.ShouldBeGreaterThanOrEqualTo(Math.Max(summary.FilesBlocked, summary.FilesSuspicious));
    }

    /// <summary>
    /// The headline is the number Apply acts on, not the number of files that could change.
    ///
    /// Those differ the moment a row is unticked, and the old summary headline reported the
    /// second one - so unticking rows left the top of the window and the bottom of the
    /// window stating different sizes for the same run.
    /// </summary>
    [Fact]
    public async Task The_headline_counts_what_apply_counts()
    {
        using WorkbenchFixture fixture = await ChangingFolderAsync();

        fixture.ViewModel.Summary.FilesChanging.ShouldBe(4);

        fixture.ViewModel.Rows[0].IsIncluded = false;
        fixture.ViewModel.RefreshSummary();

        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(3);
        fixture.ViewModel.Summary.FilesChanging.ShouldBe(4, "unticking does not change what COULD change");

        fixture.ViewModel.ResultHeadline.ShouldContain("3 of 4");
        fixture.ViewModel.Summary.ApplyLabel.ShouldContain("3 of 4");
    }

    /// <summary>
    /// A count of zero greys its toggle - unless that toggle is the one holding the view
    /// empty, in which case greying it locks the user inside a list of nothing with the way
    /// out disabled. Apply rescans on success, so this is the state a successful run lands
    /// in, not a corner case.
    /// </summary>
    [Fact]
    public async Task A_filter_showing_nothing_can_still_be_switched_off()
    {
        var fixture = new WorkbenchFixture();

        using (fixture)
        {
            fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
            await fixture.LoadAsync("a.txt", "b.txt");

            // No date chosen, so nothing changes.
            fixture.ViewModel.Summary.FilesChanging.ShouldBe(0);
            fixture.ViewModel.CanFilterChanging.ShouldBeFalse("nothing to filter down to");

            fixture.ViewModel.ShowOnlyChanging = true;

            fixture.ViewModel.CanFilterChanging.ShouldBeTrue(
                "it is on, and the only control that can turn it off must not be disabled");
        }
    }

    /// <summary>
    /// The message under an empty list has to name the filter that emptied it.
    ///
    /// It was hardcoded to the type filter, so with an empty filter box it rendered
    /// "Nothing matches ." followed by an instruction to clear that box. The path that
    /// produces it is the common one: a successful run rescans, every row becomes
    /// unchanged, and the app announced it had lost the files it had just written.
    /// </summary>
    [Fact]
    public async Task The_empty_view_names_the_filter_that_emptied_it()
    {
        var fixture = new WorkbenchFixture();

        using (fixture)
        {
            fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
            await fixture.LoadAsync("a.txt", "b.txt");

            fixture.ViewModel.ShowOnlyChanging = true;

            fixture.ViewModel.IsFilteredToNothing.ShouldBeTrue();

            string note = fixture.ViewModel.FilteredToNothingNote;

            note.ShouldContain("will change");
            note.ShouldContain("2 files hidden");
            note.ShouldNotContain("Nothing matches .", Case.Sensitive);
        }
    }

    /// <summary>
    /// A template can carry a rule the pane cannot show, so the deck quotes the template
    /// rather than paraphrasing controls that would state the run wrongly.
    /// </summary>
    [Fact]
    public async Task The_rule_segment_defers_to_an_active_template()
    {
        using WorkbenchFixture fixture = await ChangingFolderAsync();

        fixture.ViewModel.IsRuleFromPane.ShouldBeTrue();
        fixture.ViewModel.IsRuleFromTemplate.ShouldBeFalse();
        fixture.ViewModel.RuleIntentLine.ShouldBe("File dates");
        fixture.ViewModel.RuleTargetLine.ShouldContain("Created");
    }

    /// <summary>
    /// The edit link appears and disappears with the rule it offers to edit.
    ///
    /// A lone link floating in a card that has just said "Nothing chosen yet" looks like
    /// the card failed to load - and the pane it would jump to is showing that same
    /// question at full size a few inches away, so there is nothing to shortcut to.
    /// </summary>
    [Fact]
    public async Task The_edit_link_is_gone_until_there_is_a_rule()
    {
        var fixture = new WorkbenchFixture();

        using (fixture)
        {
            await fixture.LoadAsync("a.txt");

            fixture.ViewModel.RuleIntentLine.ShouldBe("Nothing chosen yet");
            fixture.ViewModel.HasRuleToEdit.ShouldBeFalse();

            fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

            fixture.ViewModel.RuleIntentLine.ShouldBe("File dates");
            fixture.ViewModel.HasRuleToEdit.ShouldBeTrue();

            fixture.ViewModel.StartOver();

            fixture.ViewModel.RuleIntentLine.ShouldBe("Nothing chosen yet");
            fixture.ViewModel.HasRuleToEdit.ShouldBeFalse(
                "the link has to move with the line it sits beside, not merely agree with it");
        }
    }

    /// <summary>The source segment counts folders separately from files, and says neither when empty.</summary>
    [Fact]
    public async Task The_source_segment_states_what_is_loaded()
    {
        var fixture = new WorkbenchFixture();

        using (fixture)
        {
            fixture.ViewModel.SourceHeadline.ShouldBe("No files yet");

            await fixture.LoadAsync("a.txt", "b.txt", "c.txt");

            fixture.ViewModel.SourceHeadline.ShouldContain("3 files");
            fixture.ViewModel.SourceHeadline.ShouldContain("1 folder");
        }
    }
}
