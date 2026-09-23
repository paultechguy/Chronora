// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Journal;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// What "Undo last run" is allowed to reach.
///
/// It sits beside Apply because the moment somebody wants it is the moment straight after
/// the run they regret. That only holds if it means THAT run. Enabled from whatever the
/// journal happens to hold, it came up live on a fresh launch and offered to revert work
/// from days earlier - under a caption that says "last run", next to the button that had
/// just done nothing at all.
/// </summary>
public class UndoScopeTests
{
    /// <summary>Puts a completed run in the journal without going near the disk-writing path.</summary>
    private static long RecordFinishedRun(WorkbenchFixture fixture)
    {
        var header = new RunHeader(
            RunKind.Apply,
            "test",
            ExifToolVersion: null,
            TimeZoneInfo.Local.Id,
            "{}",
            []);

        long runId = fixture.Journal.BeginRun(header, DateTimeOffset.UtcNow);
        fixture.Journal.CompleteRun(runId, RunStatus.Completed, DateTimeOffset.UtcNow);

        return runId;
    }

    [Fact]
    public void A_run_from_a_previous_session_does_not_arm_undo()
    {
        using var fixture = new WorkbenchFixture();

        _ = RecordFinishedRun(fixture);
        fixture.ViewModel.RefreshHistory();

        fixture.ViewModel.History.ShouldNotBeEmpty("the journal really does hold a revertible run");

        fixture.ViewModel.CanUndo.ShouldBeFalse(
            "this session has applied nothing, so there is no 'last run' to undo");
    }

    /// <summary>
    /// The older run is not lost and is not meant to be reached from this button. History
    /// lists it with what it did and when, and puts Undo on the row it undoes.
    /// </summary>
    [Fact]
    public void The_older_run_is_still_offered_by_history()
    {
        using var fixture = new WorkbenchFixture();

        long runId = RecordFinishedRun(fixture);
        fixture.ViewModel.RefreshHistory();

        fixture.ViewModel.HasHistory.ShouldBeTrue();
        fixture.ViewModel.IsHistoryEmpty.ShouldBeFalse();
        fixture.ViewModel.HistoryRows.Any(r => r.RunId == runId).ShouldBeTrue();
    }

    [Fact]
    public async Task Undo_says_where_the_older_runs_are()
    {
        using var fixture = new WorkbenchFixture();

        _ = RecordFinishedRun(fixture);
        fixture.ViewModel.RefreshHistory();

        await fixture.ViewModel.UndoLastAsync();

        // The toast, not the footer. A refusal answers a click that just happened, and the
        // footer is a progress meter now - the next scan writes a file count over it.
        fixture.ViewModel.ActionNotice.ShouldNotBeNull();
        fixture.ViewModel.ActionNotice!.ShouldContain("History");
        fixture.ViewModel.NoticeHasUndo.ShouldBeFalse("there was nothing to undo, so nothing to offer back");
    }
}
