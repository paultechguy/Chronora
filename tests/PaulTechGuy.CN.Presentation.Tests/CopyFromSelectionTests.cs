// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// Choosing which date to copy FROM.
///
/// Reported as "what does 'another date on the file' mean?" - and the honest answer was
/// that it did not mean anything usable, because there was no control to answer it with.
/// The option showed no picker and silently fell back to Modified, so on the simple path
/// it usually copied Modified onto Modified and did nothing at all.
/// </summary>
public class CopyFromSelectionTests
{
    [Fact]
    public void Every_date_worth_reading_is_offered()
    {
        using var fixture = new WorkbenchFixture();

        IReadOnlyList<DateField> offered = [.. fixture.ViewModel.CopyFromOptions.Select(o => o.Field)];

        // Both genres. Reading a photo date to write onto the file dates is the product's
        // whole differentiator, and the mode only ever limits what may be WRITTEN.
        offered.ShouldContain(DateField.ExifDateTimeOriginal);
        offered.ShouldContain(DateField.QuickTimeCreateDate);
        offered.ShouldContain(DateField.FileCreated);
        offered.ShouldContain(DateField.FileModified);
    }

    [Fact]
    public void The_offered_dates_all_have_a_readable_name()
    {
        using var fixture = new WorkbenchFixture();

        foreach (DateFieldSpec spec in fixture.ViewModel.CopyFromOptions)
        {
            spec.DisplayName.ShouldNotBeNullOrWhiteSpace();
            spec.DisplayName.ShouldNotBe(spec.Field.ToString(), "the list should read as English, not as enum names");
        }
    }

    /// <summary>The control and the value have to agree in both directions.</summary>
    [Fact]
    public void The_choice_round_trips_through_the_index()
    {
        using var fixture = new WorkbenchFixture();

        for (int i = 0; i < fixture.ViewModel.CopyFromOptions.Count; i++)
        {
            fixture.ViewModel.CopyFromIndex = i;

            fixture.ViewModel.CopyFromField.ShouldBe(fixture.ViewModel.CopyFromOptions[i].Field);
            fixture.ViewModel.CopyFromIndex.ShouldBe(i);
        }
    }

    /// <summary>Setting the field directly moves the control, which is what a template does.</summary>
    [Fact]
    public void Setting_the_field_moves_the_control()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.CopyFromField = DateField.ExifDateTimeOriginal;

        fixture.ViewModel.CopyFromIndex.ShouldBe(
            fixture.ViewModel.CopyFromOptions.Select((o, i) => (o, i)).First(x => x.o.Field == DateField.ExifDateTimeOriginal).i);
    }

    /// <summary>The picker only appears for the option that needs it.</summary>
    [Fact]
    public void The_picker_is_shown_only_for_this_source()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.Source = SourceChoice.PickADate;
        fixture.ViewModel.NeedsCopyFromInput.ShouldBeFalse();

        fixture.ViewModel.Source = SourceChoice.FromAnotherDate;
        fixture.ViewModel.NeedsCopyFromInput.ShouldBeTrue();
    }

    /// <summary>
    /// Choosing a photo date here is what raises the ExifTool prompt from the simple
    /// file-dates path, which is intended rather than incidental.
    /// </summary>
    [Fact]
    public void Choosing_a_photo_date_to_read_asks_for_exiftool()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.Source = SourceChoice.FromAnotherDate;

        fixture.ViewModel.CopyFromField = DateField.FileModified;
        fixture.ViewModel.NeedsExifTool.ShouldBeFalse();

        fixture.ViewModel.CopyFromField = DateField.QuickTimeCreateDate;
        fixture.ViewModel.NeedsExifTool.ShouldBeTrue();
    }
}
