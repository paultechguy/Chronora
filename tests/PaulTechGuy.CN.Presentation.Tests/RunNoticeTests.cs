// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// The run report, and the region that now ranks it against the two standing conditions.
///
/// The report used to be written to the footer, and the line after the one that wrote it
/// is `await this.RescanAsync()` - which re-enters AddFolderAsync and overwrites the footer
/// twice more, all inside the same await chain, before control returns to the UI. So this
/// was never a message that COULD be missed: it was destroyed on every run by the app
/// itself, and a run that half failed reported "Read 2 files from C:\Photos."
/// </summary>
public class RunNoticeTests
{
    private static async Task<WorkbenchFixture> ReadyToApplyAsync()
    {
        var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.txt");

        fixture.ViewModel.WriteCreated = true;
        fixture.ViewModel.WriteModified = true;
        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2024, 3, 15, 0, 0, 0, DateTimeOffset.Now.Offset);
        fixture.ViewModel.AbsoluteTime = new TimeSpan(14, 25, 0);
        fixture.ViewModel.Recompute();

        return fixture;
    }

    /// <summary>
    /// The one that matters. Before this, the assertion below could be written against the
    /// footer and it would fail, naming the rescan's sentence instead.
    /// </summary>
    [Fact]
    public async Task A_run_report_survives_the_rescan_that_follows_it()
    {
        using WorkbenchFixture fixture = await ReadyToApplyAsync();

        await fixture.ViewModel.ApplyAsync();

        fixture.ViewModel.RunNotice.ShouldNotBeNull("the run report was destroyed by the rescan");
        fixture.ViewModel.RunNotice.ShouldContain("2 changed");
        fixture.ViewModel.ShowsRunNotice.ShouldBeTrue();

        // And the footer is back to describing the rescan, which is its job and the reason
        // it could never have kept this.
        fixture.ViewModel.ProgressStatus.ShouldContain("Read 2 files from");
    }

    [Fact]
    public async Task A_clean_run_is_not_flagged_as_bad()
    {
        using WorkbenchFixture fixture = await ReadyToApplyAsync();

        await fixture.ViewModel.ApplyAsync();

        fixture.ViewModel.RunNoticeIsBad.ShouldBeFalse("nothing failed");
    }

    [Fact]
    public async Task The_report_stays_until_it_is_dismissed()
    {
        using WorkbenchFixture fixture = await ReadyToApplyAsync();

        await fixture.ViewModel.ApplyAsync();
        fixture.ViewModel.ShowsRunNotice.ShouldBeTrue();

        // The kind of incidental gesture that used to wipe it out.
        fixture.ViewModel.WriteChanged = true;
        fixture.ViewModel.Recompute();

        fixture.ViewModel.ShowsRunNotice.ShouldBeTrue("only the reader retires a run report");

        fixture.ViewModel.DismissRunNotice();

        fixture.ViewModel.ShowsRunNotice.ShouldBeFalse();
        fixture.ViewModel.RunNotice.ShouldBeNull();
    }

    /// <summary>
    /// A notice that persists until dismissed has to be retired by whatever makes it
    /// untrue, or the previous run's counts sit over the top of the one now writing.
    /// </summary>
    [Fact]
    public async Task A_new_run_clears_the_last_ones_report()
    {
        using WorkbenchFixture fixture = await ReadyToApplyAsync();

        await fixture.ViewModel.ApplyAsync();
        fixture.ViewModel.RunNotice.ShouldNotBeNull();

        List<bool> seen = [];

        fixture.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.RunNotice))
            {
                seen.Add(fixture.ViewModel.RunNotice is not null);
            }
        };

        fixture.ViewModel.AbsoluteTime = new TimeSpan(9, 0, 0);
        fixture.ViewModel.Recompute();
        await fixture.ViewModel.ApplyAsync();

        seen.ShouldContain(false, "the previous report must be cleared as the new run starts");
        fixture.ViewModel.RunNotice.ShouldNotBeNull("and replaced by this run's own");
    }

    /// <summary>
    /// The ranking, which is the point of a region rather than a stack. A run report
    /// describes something that has just happened and will never be said again; the other
    /// two describe conditions that are still true and will be true in a minute.
    /// </summary>
    [Fact]
    public async Task A_run_report_outranks_a_standing_condition()
    {
        using WorkbenchFixture fixture = await ReadyToApplyAsync();

        // No ExifTool in this temp folder, so asking for photo dates raises that condition.
        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);
        fixture.ViewModel.Recompute();

        Assert.SkipUnless(fixture.ViewModel.NeedsExifTool, "this test needs the ExifTool condition to be up");

        fixture.ViewModel.ShowsExifToolNotice.ShouldBeTrue("nothing outranks it yet");
        fixture.ViewModel.HasQueuedNotices.ShouldBeFalse("it is the only one open");

        fixture.ViewModel.RunNotice = "Done. 0 changed, 5 failed, 0 skipped.";

        fixture.ViewModel.ShowsRunNotice.ShouldBeTrue("the event wins");
        fixture.ViewModel.ShowsExifToolNotice.ShouldBeFalse("and the condition steps behind it");
        fixture.ViewModel.QueuedNoticeCount.ShouldBe(1);
        fixture.ViewModel.QueuedNoticeLabel.ShouldBe("1 more notice");
    }

    /// <summary>
    /// One region that hides the rest is otherwise indistinguishable from one region with
    /// nothing else to say, and the hidden one is often what explains the visible one.
    /// </summary>
    [Fact]
    public void Nothing_open_means_nothing_queued()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ShowsRunNotice.ShouldBeFalse();
        fixture.ViewModel.QueuedNoticeCount.ShouldBe(0);
        fixture.ViewModel.HasQueuedNotices.ShouldBeFalse();
    }
}
