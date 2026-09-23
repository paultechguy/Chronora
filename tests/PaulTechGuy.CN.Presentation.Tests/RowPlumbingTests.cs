// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// How rows attach to the view model and how they come off again.
///
/// Both of these were found by reading rather than by anything going wrong, which is why
/// they are pinned here: neither produces a visible symptom on the list sizes anybody has
/// actually used, and both get worse in exactly the direction the app is built to go.
/// </summary>
public class RowPlumbingTests
{
    /// <summary>Counts how many times the summary was rebuilt.</summary>
    private sealed class SummaryCounter : IDisposable
    {
        private readonly WorkbenchViewModel _source;

        public SummaryCounter(WorkbenchViewModel source)
        {
            this._source = source;
            source.PropertyChanged += this.OnChanged;
        }

        public int Rebuilds { get; private set; }

        private void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.Summary))
            {
                this.Rebuilds++;
            }
        }

        public void Dispose() => this._source.PropertyChanged -= this.OnChanged;
    }

    /// <summary>
    /// How many handlers are attached to a row's PropertyChanged.
    ///
    /// Reflection, because the leak this pins has no behavioural symptom to catch it by.
    /// The handler rebuilds the summary, the summary is a record, and rebuilding it from a
    /// list that has not moved produces an EQUAL record - which the observable property
    /// then declines to raise. So a leaked handler runs, changes nothing, and is invisible
    /// from outside. The first version of this test counted summary rebuilds and passed
    /// against the unfixed code, which is worse than having no test.
    ///
    /// Counting the subscribers asserts what the fix actually claims: rows detach when they
    /// leave the list. ObservableObject declares PropertyChanged as a field-like event, so
    /// the backing field carries the invocation list.
    /// </summary>
    private static int HandlersOn(PlanRowViewModel row)
    {
        FieldInfo field = typeof(ObservableObject)
            .GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ObservableObject no longer backs PropertyChanged with a field of that name.");

        return ((PropertyChangedEventHandler?)field.GetValue(row))?.GetInvocationList().Length ?? 0;
    }

    /// <summary>
    /// A row that has left the list stops talking to the view model.
    ///
    /// The subscription was a lambda, which cannot be unsubscribed, and `_allRows.Clear()`
    /// happened in five places without detaching anything — so every row ever loaded stayed
    /// wired up for the life of the window. The leak is the dull half. The sharp half is
    /// this: a discarded row could still rebuild the summary of the list that replaced it.
    /// </summary>
    [Fact]
    public async Task A_row_that_leaves_the_list_is_detached()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.jpg");

        PlanRowViewModel discarded = fixture.ViewModel.Rows[0];
        HandlersOn(discarded).ShouldBe(1, "a loaded row is watched");

        fixture.ViewModel.ClearList();
        fixture.ViewModel.Rows.ShouldBeEmpty();

        HandlersOn(discarded).ShouldBe(0, "a row that has left the list is still wired to the view model");
    }

    /// <summary>The same for the other four ways the list gets emptied.</summary>
    [Fact]
    public async Task Every_way_of_emptying_the_list_detaches()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.jpg");
        PlanRowViewModel viaStartOver = fixture.ViewModel.Rows[0];
        fixture.ViewModel.StartOver();
        HandlersOn(viaStartOver).ShouldBe(0, "StartOver");

        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("c.jpg");
        PlanRowViewModel viaRescan = fixture.ViewModel.Rows[0];
        await fixture.ViewModel.RescanWithOptionsAsync();
        HandlersOn(viaRescan).ShouldBe(0, "the rescan rebuilds the list from the roots");

        string dropped = fixture.CreateFile("dropped.jpg");
        await fixture.ViewModel.AddDroppedAsync([dropped], TestContext.Current.CancellationToken);
        PlanRowViewModel viaReplace = fixture.ViewModel.Rows.First(r => r.Name != "dropped.jpg");
        fixture.ViewModel.ReplaceWithDrop();
        HandlersOn(viaReplace).ShouldBe(0, "ReplaceWithDrop keeps only what the drop brought");
    }

    /// <summary>
    /// And the rows an undo puts back are wired up again, which is the other half of the
    /// same change: detaching on the way out is only safe if coming back re-attaches.
    /// </summary>
    [Fact]
    public async Task A_row_restored_by_undo_moves_the_summary_again()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.jpg");

        fixture.ViewModel.ClearList();
        fixture.ViewModel.UndoLastAction();

        fixture.ViewModel.Rows.Count.ShouldBe(2, "the undo should have put them back");

        HandlersOn(fixture.ViewModel.Rows[0]).ShouldBe(1, "a restored row must be watched again");
        HandlersOn(fixture.ViewModel.Rows[1]).ShouldBe(1);
    }

    /// <summary>
    /// Restoring twice must not count twice.
    ///
    /// TrackRow detaches before it attaches for this reason: a row that went out and came
    /// back and went out and came back would otherwise carry a handler per round trip, and
    /// every checkbox would rebuild the summary as many times as its row had been undone.
    /// </summary>
    [Fact]
    public async Task A_row_that_has_been_restored_twice_is_still_watched_once()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.jpg");

        for (int i = 0; i < 3; i++)
        {
            fixture.ViewModel.ClearList();
            fixture.ViewModel.UndoLastAction();
        }

        fixture.ViewModel.Rows.Count.ShouldBe(2);

        HandlersOn(fixture.ViewModel.Rows[0]).ShouldBe(1, "three round trips left three handlers on the row");
    }

    /// <summary>
    /// The storm. Each row's change rebuilt the whole summary, which walks every loaded row
    /// AND rebuilds the recipe — so ticking N rows did N of those. Survivable only because
    /// nobody had clicked it on a list big enough to feel it, on a grid built for 50,000.
    /// </summary>
    [Theory]
    [InlineData("all")]
    [InlineData("none")]
    [InlineData("only")]
    public async Task A_bulk_selection_rebuilds_the_summary_once(string which)
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.jpg", "c.jpg", "d.jpg", "e.jpg", "f.jpg");
        fixture.ViewModel.Rows.Count.ShouldBe(6);

        // Start from the opposite of what the command is about to do, so every row actually
        // changes and the storm would be at full strength.
        fixture.ViewModel.SelectNone();

        using var counter = new SummaryCounter(fixture.ViewModel);

        switch (which)
        {
            case "all":
                fixture.ViewModel.SelectAllShown();
                break;

            case "none":
                fixture.ViewModel.SelectAllShown();
                counter.Rebuilds.ShouldBe(1);
                fixture.ViewModel.SelectNone();
                break;

            default:
                fixture.ViewModel.SelectOnly(fixture.ViewModel.Rows[3]);
                break;
        }

        int expected = which == "none" ? 2 : 1;

        counter.Rebuilds.ShouldBe(expected, "one refresh per command, not one per row");
    }

    /// <summary>
    /// Suppressing the per-row storm must not suppress the answer. The refresh is in a
    /// finally for this: a summary left describing the selection as it was before would be
    /// a worse bug than the one being fixed, because the Apply button quotes it.
    /// </summary>
    [Fact]
    public async Task A_bulk_selection_still_leaves_the_summary_correct()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.jpg", "c.jpg");

        fixture.ViewModel.SelectNone();
        fixture.ViewModel.Summary.FilesIncluded.ShouldBe(0);

        fixture.ViewModel.SelectAllShown();
        fixture.ViewModel.Summary.FilesIncluded.ShouldBe(3);

        fixture.ViewModel.SelectOnly(fixture.ViewModel.Rows[1]);
        fixture.ViewModel.Summary.FilesIncluded.ShouldBe(1);
    }
}
