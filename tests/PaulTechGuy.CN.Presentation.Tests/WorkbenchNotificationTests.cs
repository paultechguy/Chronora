// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// The bug class that kept reaching the user, closed mechanically.
///
/// Three separate faults were the same mistake: a computed property whose value depends on
/// state that changed, with no PropertyChanged raised for it. The control bound to it then
/// shows whatever was true at startup, forever. It cannot be spotted by reading the code -
/// nothing is missing at the call site, something is missing from a list somewhere else -
/// and it compiles, runs and looks fine.
///
/// So rather than testing the three known instances, these assert the invariant: after any
/// action, every property whose VALUE moved must also have been announced. A new computed
/// property added tomorrow is covered without anyone remembering to cover it.
/// </summary>
public class WorkbenchNotificationTests
{
    /// <summary>
    /// The one that shipped: choosing an intent changed IsPhotoMode, ShowsFileDates,
    /// ShowsAdvancedFields and NeedsExifTool, and only some of them said so.
    /// </summary>
    [Theory]
    [InlineData(WorkIntent.FileDates)]
    [InlineData(WorkIntent.PhotoDates)]
    [InlineData(WorkIntent.Custom)]
    public void Choosing_an_intent_announces_everything_it_changes(WorkIntent intent)
    {
        using var fixture = new WorkbenchFixture();
        using var watcher = new NotificationWatcher(fixture.ViewModel);

        fixture.ViewModel.ChooseIntent(intent);

        watcher.SilentChanges().ShouldBeEmpty();
    }

    /// <summary>Moving between intents, which is how the checkbox lists change shape.</summary>
    [Fact]
    public void Switching_between_intents_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);

        watcher.SilentChanges().ShouldBeEmpty();
    }

    /// <summary>
    /// Ticking a target by hand reconciles the intent to Custom and can flip whether
    /// ExifTool is needed, so it has to announce both.
    /// </summary>
    [Fact]
    public void Ticking_a_target_by_hand_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.WriteTaken = true;

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public void Unticking_a_target_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.Custom);

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.WriteTaken = false;

        watcher.SilentChanges().ShouldBeEmpty();
    }

    /// <summary>
    /// Changing the source changes which input is shown, and can change whether metadata
    /// is read at all - "copy from another date" may point at a photo field.
    /// </summary>
    [Theory]
    [InlineData(SourceChoice.PickADate)]
    [InlineData(SourceChoice.ShiftBy)]
    [InlineData(SourceChoice.FromAnotherDate)]
    [InlineData(SourceChoice.FromFileName)]
    public void Changing_the_source_announces_everything_it_changes(SourceChoice source)
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.Source = source;

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public void Changing_the_copy_from_field_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.Source = SourceChoice.FromAnotherDate;

        using var watcher = new NotificationWatcher(fixture.ViewModel);

        // Reading a photo field needs ExifTool even though only file dates are written.
        fixture.ViewModel.CopyFromField = Domain.DateField.ExifDateTimeOriginal;

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public async Task Adding_files_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        _ = fixture.CreateFile("a.jpg");
        using var watcher = new NotificationWatcher(fixture.ViewModel);

        await fixture.ViewModel.AddFolderAsync(fixture.Files, Domain.ScanFilter.Default, TestContext.Current.CancellationToken);

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public async Task Clearing_the_list_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg", "b.jpg");

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.ClearList();

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public async Task Starting_over_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.Custom);
        fixture.ViewModel.Sort = SortChoice.BiggestChange;
        fixture.ViewModel.ShowOnlyChanging = true;
        await fixture.LoadAsync("a.jpg");

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.StartOver();

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public async Task Undoing_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg");
        fixture.ViewModel.ClearList();

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.UndoLastAction();

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public async Task Dropping_files_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        string dropped = fixture.CreateFile("dropped.jpg");
        using var watcher = new NotificationWatcher(fixture.ViewModel);

        await fixture.ViewModel.AddDroppedAsync([dropped], TestContext.Current.CancellationToken);

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public async Task Filtering_and_sorting_announce_everything_they_change()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg", "b.jpg");

        using var watcher = new NotificationWatcher(fixture.ViewModel);

        fixture.ViewModel.ShowOnlyChanging = true;
        fixture.ViewModel.ShowOnlyProblems = true;
        fixture.ViewModel.Sort = SortChoice.BiggestChange;

        watcher.SilentChanges().ShouldBeEmpty();
    }

    /// <summary>
    /// Using a template moves the checkboxes, the source, the intent label and the
    /// template notice all at once - the largest single state change in the app, and so
    /// the most likely place for the missing-notification bug to reappear.
    /// </summary>
    [Fact]
    public void Using_a_template_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.UseTemplate(fixture.ViewModel.Templates[0]);

        watcher.SilentChanges().ShouldBeEmpty();
    }

    /// <summary>And stepping back out of one, which is triggered by an ordinary edit.</summary>
    [Fact]
    public void Leaving_a_template_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.UseTemplate(fixture.ViewModel.Templates[0]);

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.WriteAccessed = true;

        watcher.SilentChanges().ShouldBeEmpty();
        fixture.ViewModel.ActiveTemplate.ShouldBeNull("an edit takes the template out of charge");
    }

    /// <summary>
    /// A guard on the guard. If the watcher cannot detect a deliberately unannounced
    /// change then every test above is passing for the wrong reason.
    /// </summary>
    [Fact]
    public void The_watcher_actually_detects_a_silent_change()
    {
        using var fixture = new WorkbenchFixture();
        using var watcher = new NotificationWatcher(fixture.ViewModel);

        // Reaches past the property setter, exactly as a missing notification would.
        typeof(WorkbenchViewModel)
            .GetField("<Intent>k__BackingField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(fixture.ViewModel, WorkIntent.PhotoDates);

        watcher.SilentChanges().ShouldContain("Intent");
    }
}
