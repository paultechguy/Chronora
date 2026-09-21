// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// One file answering the run's two questions - which date, and which fields - for itself.
///
/// The risk worth testing is leakage in either direction: an override that quietly changes
/// the rest of the run, or a run change that silently discards an override somebody set by
/// hand. Both would make the preview a liar about the file they cared enough to single out.
/// </summary>
public class RowOverrideTests
{
    private static async Task<WorkbenchFixture> LoadedAsync()
    {
        var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.txt");

        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2019, 1, 2, 0, 0, 0, TimeSpan.Zero);
        fixture.ViewModel.Recompute();

        return fixture;
    }

    [Fact]
    public async Task An_overridden_row_uses_its_own_date_and_leaves_the_others_alone()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        PlanRowViewModel first = fixture.ViewModel.Rows[0];
        PlanRowViewModel second = fixture.ViewModel.Rows[1];

        var mine = new DateTimeOffset(2001, 5, 6, 7, 8, 0, TimeSpan.Zero);
        fixture.ViewModel.SetManualDate(first, mine, new HashSet<DateField> { DateField.FileModified });

        first.Plan!.Changes
            .Where(c => c.WillWrite)
            .ShouldAllBe(c => c.AfterDate == mine, "the row should write its own date");

        second.Plan!.Changes
            .Where(c => c.WillWrite)
            .ShouldAllBe(c => c.AfterDate != mine, "no other row should have moved");
    }

    [Fact]
    public async Task An_override_can_write_fields_the_run_does_not()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        PlanRowViewModel row = fixture.ViewModel.Rows[0];

        // The run writes Created and Modified; this file alone also gets Changed.
        fixture.ViewModel.SetManualDate(
            row,
            new DateTimeOffset(2001, 5, 6, 7, 8, 0, TimeSpan.Zero),
            new HashSet<DateField> { DateField.FileChanged });

        List<DateField?> written =
        [
            .. row.Plan!.Changes
                .Where(c => c.WillWrite)
                .Select(c => (c.Target as ChangeTarget.Field)?.Which),
        ];

        written.ShouldBe([DateField.FileChanged], "only the field the override named should be written");
    }

    /// <summary>
    /// Clearing every override at once, and being able to take that back.
    ///
    /// An override is visible only as a marker on its own row, so several of them in a long
    /// list can only be found by scrolling - reported from use as "I set one and there is no
    /// way back". The count is part of the contract: it is the only place the number of
    /// by-hand rows appears anywhere in the app.
    /// </summary>
    [Fact]
    public async Task Every_override_can_be_removed_at_once_and_put_back()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        var mine = new DateTimeOffset(2001, 5, 6, 7, 8, 0, TimeSpan.Zero);

        foreach (PlanRowViewModel row in fixture.ViewModel.Rows)
        {
            fixture.ViewModel.SetManualDate(row, mine, new HashSet<DateField> { DateField.FileCreated });
        }

        int count = fixture.ViewModel.Rows.Count;
        count.ShouldBeGreaterThan(1, "the point of this is more than one");
        fixture.ViewModel.RowsSetByHand.ShouldBe(count);

        fixture.ViewModel.ClearAllManualDates();

        fixture.ViewModel.RowsSetByHand.ShouldBe(0);
        fixture.ViewModel.Rows.ShouldAllBe(r => !r.HasManualDate);
        fixture.ViewModel.ActionNotice.ShouldNotBeNull("a bulk change has to be takeable back");

        fixture.ViewModel.UndoLastAction();

        fixture.ViewModel.RowsSetByHand.ShouldBe(count);
        fixture.ViewModel.Rows.ShouldAllBe(r => r.ManualDate == mine);
    }

    /// <summary>With nothing set by hand it does nothing at all, and says nothing.</summary>
    [Fact]
    public async Task Removing_every_override_when_there_are_none_is_silent()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        fixture.ViewModel.RowsSetByHand.ShouldBe(0);

        fixture.ViewModel.ClearAllManualDates();

        fixture.ViewModel.ActionNotice.ShouldBeNull("nothing happened, so nothing is announced");
    }

    [Fact]
    public async Task Clearing_an_override_hands_the_row_back_to_the_run()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        PlanRowViewModel row = fixture.ViewModel.Rows[0];

        fixture.ViewModel.SetManualDate(
            row,
            new DateTimeOffset(2001, 5, 6, 7, 8, 0, TimeSpan.Zero),
            new HashSet<DateField> { DateField.FileChanged });

        row.HasManualDate.ShouldBeTrue();

        fixture.ViewModel.SetManualDate(row, null);

        row.HasManualDate.ShouldBeFalse();
        row.ManualTargets.ShouldBeNull("the fields must go back with the date, not linger");
    }

    /// <summary>
    /// A text file has no Taken date and never will, so offering one produces a tick the
    /// recipe silently drops - which is the failure the preview exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_field_the_file_cannot_carry_is_not_offered()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        PlanRowViewModel text = fixture.ViewModel.Rows.Single(r => r.Name == "b.txt");
        PlanRowViewModel image = fixture.ViewModel.Rows.Single(r => r.Name == "a.jpg");

        WorkbenchViewModel.CanTarget(text, DateField.ExifDateTimeOriginal).ShouldBeFalse();
        WorkbenchViewModel.CanTarget(image, DateField.ExifDateTimeOriginal).ShouldBeTrue();
        WorkbenchViewModel.CanTarget(text, DateField.FileCreated).ShouldBeTrue();
    }

    /// <summary>
    /// The dialog opens describing what would happen anyway, so a glance confirms it rather
    /// than a fresh set of decisions arriving on top of the ones already made.
    /// </summary>
    [Fact]
    public async Task The_dialog_starts_from_what_the_run_would_do()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        IReadOnlySet<DateField> seeded =
            fixture.ViewModel.DefaultTargetsFor(fixture.ViewModel.Rows.Single(r => r.Name == "b.txt"));

        seeded.ShouldBe(
            new HashSet<DateField> { DateField.FileCreated, DateField.FileModified },
            ignoreOrder: true);
    }
}
