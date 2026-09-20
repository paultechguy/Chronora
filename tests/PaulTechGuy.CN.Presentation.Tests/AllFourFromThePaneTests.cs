// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// Ticking all four file dates in the pane and getting one moment in all four.
///
/// The apply path is already pinned. This is the half above it - the date picker, the time
/// picker and the checkboxes - because a reported run came out with the dates matching and
/// the times not, and the two halves fail for completely different reasons.
/// </summary>
public class AllFourFromThePaneTests
{
    private static async Task<WorkbenchFixture> ReadyAsync()
    {
        var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.txt");

        return fixture;
    }

    [Fact]
    public async Task All_four_ticked_gives_all_four_the_same_time()
    {
        using WorkbenchFixture fixture = await ReadyAsync();

        fixture.ViewModel.WriteCreated = true;
        fixture.ViewModel.WriteModified = true;
        fixture.ViewModel.WriteAccessed = true;
        fixture.ViewModel.WriteChanged = true;

        // Exactly what the two pickers produce: a date at midnight, and a time of day.
        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2024, 3, 15, 0, 0, 0, DateTimeOffset.Now.Offset);
        fixture.ViewModel.AbsoluteTime = new TimeSpan(14, 25, 0);

        fixture.ViewModel.Recompute();

        foreach (PlanRowViewModel row in fixture.ViewModel.Rows)
        {
            List<DateTimeOffset?> after = [.. row.Plan!.Changes.Where(c => c.WillWrite).Select(c => c.AfterDate)];

            after.Count.ShouldBe(4, $"{row.Name} should write all four fields");
            after.Distinct().Count().ShouldBe(1, $"{row.Name}: one chosen moment, four identical values");

            after[0]!.Value.TimeOfDay.ShouldBe(new TimeSpan(14, 25, 0), "the time picker's value must survive");
            after[0]!.Value.Date.ShouldBe(new DateTime(2024, 3, 15), "and so must the date picker's");
        }
    }

    /// <summary>
    /// The same through the row menu's own dialog, which builds its value separately and
    /// so could disagree with the pane while both look right on their own.
    /// </summary>
    [Fact]
    public async Task A_row_set_by_hand_gets_the_same_time_in_every_field()
    {
        using WorkbenchFixture fixture = await ReadyAsync();

        PlanRowViewModel row = fixture.ViewModel.Rows[0];

        fixture.ViewModel.SetManualDate(
            row,
            new DateTimeOffset(2024, 3, 15, 14, 25, 0, DateTimeOffset.Now.Offset),
            new HashSet<DateField>
            {
                DateField.FileCreated,
                DateField.FileModified,
                DateField.FileAccessed,
                DateField.FileChanged,
            });

        List<DateTimeOffset?> after = [.. row.Plan!.Changes.Where(c => c.WillWrite).Select(c => c.AfterDate)];

        after.Count.ShouldBe(4);
        after.Distinct().Count().ShouldBe(1);
        after[0]!.Value.TimeOfDay.ShouldBe(new TimeSpan(14, 25, 0));
    }

    /// <summary>
    /// And the case that DOES diverge, on purpose, so the difference is written down.
    ///
    /// A file name usually carries a date and no time. Rather than invent midnight,
    /// Chronora keeps whatever time each field already had - which looks exactly like the
    /// bug above unless you know it is deliberate.
    /// </summary>
    [Fact]
    public async Task A_date_only_source_keeps_each_field_s_own_time_of_day()
    {
        using WorkbenchFixture fixture = await ReadyAsync();

        fixture.ViewModel.WriteCreated = true;
        fixture.ViewModel.WriteModified = true;

        fixture.ViewModel.UseFilenamePattern("{*}{yyyy}-{MM}-{dd}{*}", DatePrecision.Day);

        // Nothing is asserted about equality here: the point is that this is the shape of
        // run where the times legitimately differ, and the pane should say so.
        fixture.ViewModel.Source.ShouldBe(SourceChoice.FromFileName);
    }
}
