// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// Which messages get an Undo button, which merely get said, and which have to persist.
///
/// The original rule here was "the banner for undoable things, the bottom bar for
/// everything else", and it was written because a highlighted bar with an Undo button on
/// every option change was reported as overkill. That reasoning is intact and these tests
/// still enforce it; what changed on 2026-09-23 is that the bottom bar stopped being a
/// place anything could survive. It is a progress meter, overwritten by the next scan, and
/// that is where the app was putting its error messages and its confirmations.
///
/// So the quiet half moved to the toast WITHOUT its Undo button, which is the thing that
/// was actually complained about. The split being asserted now is three ways:
///
///   toast + Undo   something happened and you can take it back
///   toast, quiet   something happened and you cannot
///   notice region  something went wrong, or a run finished — stays until dismissed
///
/// Tested rather than eyeballed because the first attempt at the original split silently
/// did not apply: a text substitution failed to match and the check counted the wrong
/// thing.
/// </summary>
public class NoticeRoutingTests
{
    [Fact]
    public void Using_a_template_is_said_quietly_with_no_undo()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.UseTemplate(fixture.ViewModel.Templates[0]);

        fixture.ViewModel.ActionNotice!.ShouldContain(fixture.ViewModel.Templates[0].Name);
        fixture.ViewModel.NoticeHasUndo.ShouldBeFalse("choosing a template is an option change");
    }

    [Fact]
    public void Leaving_a_template_is_said_quietly_but_is_still_said()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.UseTemplate(fixture.ViewModel.Templates[0]);

        fixture.ViewModel.WriteChanged = true;

        // Still SAID: dropping the template can change what Apply does in ways the
        // controls cannot show. Just said without a button.
        fixture.ViewModel.ActionNotice!.ShouldContain("Stopped using");
        fixture.ViewModel.NoticeHasUndo.ShouldBeFalse();
    }

    [Fact]
    public void Saving_a_template_is_said_quietly_with_no_undo()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.SaveCurrentAsTemplate("My fix").ShouldBeNull();

        fixture.ViewModel.ActionNotice!.ShouldContain("My fix");
        fixture.ViewModel.NoticeHasUndo.ShouldBeFalse();
    }

    /// <summary>A real list action still gets the button, because it has a real Undo.</summary>
    [Fact]
    public async Task Adding_files_still_offers_the_undo()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        string dropped = fixture.CreateFile("dropped.jpg");
        await fixture.ViewModel.AddDroppedAsync([dropped], TestContext.Current.CancellationToken);

        fixture.ViewModel.ActionNotice.ShouldNotBeNull();
        fixture.ViewModel.NoticeHasUndo.ShouldBeTrue("a drop can be undone, so it earns the button");
    }

    /// <summary>
    /// The ordering trap. Setting the notice is what raises the Undo button's visibility,
    /// and every site used to set the notice BEFORE the undo it belongs to - which would
    /// have evaluated the button against the previous action's way back.
    /// </summary>
    [Fact]
    public async Task A_quiet_notice_after_an_undoable_one_does_not_inherit_its_undo()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        string dropped = fixture.CreateFile("dropped.jpg");
        await fixture.ViewModel.AddDroppedAsync([dropped], TestContext.Current.CancellationToken);
        fixture.ViewModel.NoticeHasUndo.ShouldBeTrue();

        fixture.ViewModel.Confirm("Copied the path to dropped.jpg.");

        fixture.ViewModel.ActionNotice.ShouldBe("Copied the path to dropped.jpg.");
        fixture.ViewModel.NoticeHasUndo.ShouldBeFalse("the drop's Undo must not attach to this sentence");
    }

    /// <summary>
    /// And it goes away once somebody moves on to configuring the run. It never expiring
    /// is the other half of why the banner looked like it was reacting to option changes:
    /// it was simply still there from the last drop.
    /// </summary>
    [Fact]
    public async Task The_undo_offer_clears_when_an_option_is_changed()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        string dropped = fixture.CreateFile("dropped.jpg");
        await fixture.ViewModel.AddDroppedAsync([dropped], TestContext.Current.CancellationToken);
        fixture.ViewModel.NoticeHasUndo.ShouldBeTrue();

        fixture.ViewModel.WriteChanged = true;

        fixture.ViewModel.ActionNotice.ShouldBeNull("carrying on is accepting the list");
        fixture.ViewModel.NoticeHasUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task Starting_over_still_offers_the_undo()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.StartOver();

        fixture.ViewModel.ActionNotice.ShouldNotBeNull();
        fixture.ViewModel.NoticeHasUndo.ShouldBeTrue("start over can be undone");
    }

    /// <summary>
    /// An error has to outlive the gesture after it. This is the one that was worst before:
    /// "Could not open x.jpg" went to the progress meter, so the next scan wrote a file
    /// count over the only report that something had failed.
    /// </summary>
    [Fact]
    public async Task A_problem_persists_and_the_footer_does_not_carry_it()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.ReportProblem("Could not open a.jpg: it is being used by another process.");

        fixture.ViewModel.ShowsProblemNotice.ShouldBeTrue();

        // The gesture that used to erase it: a scan writing progress to the footer.
        await fixture.LoadAsync("a.jpg", "b.jpg");

        fixture.ViewModel.ShowsProblemNotice.ShouldBeTrue("a scan must not erase a failure");
        fixture.ViewModel.ProblemNotice!.ShouldContain("another process");

        fixture.ViewModel.DismissProblemNotice();
        fixture.ViewModel.ShowsProblemNotice.ShouldBeFalse();
    }

    /// <summary>
    /// Both are events, and this one is newer: a run report is a summary that may already
    /// have been read, and a failure that has just happened has not been.
    /// </summary>
    [Fact]
    public void A_problem_outranks_a_run_report()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.RunNotice = "Done. 2 changed, 0 failed, 0 skipped.";
        fixture.ViewModel.ShowsRunNotice.ShouldBeTrue();

        fixture.ViewModel.ReportProblem("Could not copy b.jpg.");

        fixture.ViewModel.ShowsProblemNotice.ShouldBeTrue();
        fixture.ViewModel.ShowsRunNotice.ShouldBeFalse("the newer event takes the region");
        fixture.ViewModel.QueuedNoticeCount.ShouldBe(1);
    }

    /// <summary>
    /// The footer's whole contract after this change, asserted directly: progress, and
    /// nothing that anybody needs to still be there a moment later.
    /// </summary>
    [Fact]
    public async Task The_footer_only_ever_reports_progress()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.jpg");

        fixture.ViewModel.ProgressStatus.ShouldContain("Read 2 files from");

        // None of these may touch it.
        fixture.ViewModel.Confirm("Copied the path to a.jpg.");
        fixture.ViewModel.ReportProblem("Could not open b.jpg.");
        fixture.ViewModel.RunNotice = "Done. 0 changed, 0 failed, 0 skipped.";

        fixture.ViewModel.ProgressStatus.ShouldContain("Read 2 files from", Case.Sensitive);
    }
}
