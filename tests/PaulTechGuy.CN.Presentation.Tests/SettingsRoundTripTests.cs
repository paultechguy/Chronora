// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Repositories;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// What survives closing the app, and what deliberately does not.
///
/// The second half is the one worth a test. Restoring the chosen date would mean the app
/// opens with a plan already loaded and starts proposing to write it the moment files
/// arrive - which is the exact failure the "no plan until asked" work removed once
/// already, and it would come straight back through the settings file.
/// </summary>
public class SettingsRoundTripTests
{
    [Fact]
    public async Task The_shape_of_the_last_run_comes_back()
    {
        using var fixture = new WorkbenchFixture();
        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);
        fixture.ViewModel.WriteChanged = true;
        fixture.ViewModel.Source = SourceChoice.ShiftBy;
        fixture.ViewModel.ShiftHours = -5;
        fixture.ViewModel.Sort = SortChoice.BiggestChange;
        fixture.ViewModel.CopyFromField = DateField.ExifDateTimeOriginal;
        fixture.ViewModel.ShowOnlyProblems = true;

        // Read back rather than asserted as PhotoDates: the intent is DERIVED from the
        // ticked boxes, and ticking Accessed on top of a preset genuinely makes it Custom.
        // What matters here is that whatever it became survives the round trip.
        WorkIntent ended = fixture.ViewModel.Intent;

        var saved = new AppSettings();
        fixture.ViewModel.CaptureSettings(saved);

        using var next = new WorkbenchFixture();
        next.ViewModel.ApplySettings(saved);

        next.ViewModel.Intent.ShouldBe(ended);
        next.ViewModel.WriteChanged.ShouldBeTrue();
        next.ViewModel.WriteTaken.ShouldBeTrue();
        next.ViewModel.Source.ShouldBe(SourceChoice.ShiftBy);
        next.ViewModel.ShiftHours.ShouldBe(-5);
        next.ViewModel.Sort.ShouldBe(SortChoice.BiggestChange);
        next.ViewModel.CopyFromField.ShouldBe(DateField.ExifDateTimeOriginal);
        next.ViewModel.ShowOnlyProblems.ShouldBeTrue();
    }

    /// <summary>
    /// Choosing an intent sets the write targets as a side effect, so a restore that
    /// applied them in the other order would quietly undo every box ticked afterwards.
    /// </summary>
    [Fact]
    public void Ticks_made_after_choosing_an_intent_outlast_it()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);
        fixture.ViewModel.WriteCreated = true;

        var saved = new AppSettings();
        fixture.ViewModel.CaptureSettings(saved);

        using var next = new WorkbenchFixture();
        next.ViewModel.ApplySettings(saved);

        next.ViewModel.WriteCreated.ShouldBeTrue("the intent must not reassert its own defaults");
        next.ViewModel.WriteTaken.ShouldBeTrue();
    }

    [Fact]
    public async Task Restoring_settings_does_not_produce_a_plan()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2019, 1, 2, 0, 0, 0, TimeSpan.Zero);

        var saved = new AppSettings();
        fixture.ViewModel.CaptureSettings(saved);

        using var next = new WorkbenchFixture();
        next.ViewModel.ApplySettings(saved);

        // Files arrive AFTER the restore, exactly as they would on a real launch.
        await next.LoadAsync("a.jpg");

        next.ViewModel.AbsoluteDate.ShouldBeNull("a date must never come back from settings");
        next.ViewModel.Summary.FilesToWrite.ShouldBe(0, "the app must open with nothing to apply");
    }
}
