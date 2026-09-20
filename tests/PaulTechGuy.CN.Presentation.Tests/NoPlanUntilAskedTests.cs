// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// The app must not arrive with a destructive plan already loaded.
///
/// Reported as "the rows show the wrong date": two files whose dates were 2000-01-01 were
/// listed as becoming 2026-09-19 12:00. The files had been read correctly - that was
/// today's date at noon, which is what "a date I pick" was pre-filled with. So adding
/// files produced "2 of 2 will change" and a preview proposing to stamp everything with
/// today, before anybody had made a decision.
///
/// For an app that edits irreplaceable files, a default that is one click from flattening
/// a folder is the wrong default however convenient it looks.
/// </summary>
public class NoPlanUntilAskedTests
{
    [Fact]
    public async Task Adding_files_proposes_nothing_until_a_date_is_chosen()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.jpg", "b.jpg");

        fixture.ViewModel.AbsoluteDate.ShouldBeNull("nothing has been picked yet");
        fixture.ViewModel.Summary.FilesChanging.ShouldBe(0);
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(0);
    }

    [Fact]
    public async Task Choosing_a_date_is_what_produces_the_plan()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg", "b.jpg");

        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2019, 1, 2, 0, 0, 0, TimeSpan.Zero);
        fixture.ViewModel.Recompute();

        fixture.ViewModel.Summary.FilesChanging.ShouldBe(2);
    }

    /// <summary>Start over puts it back to unchosen, or the next run inherits the last one.</summary>
    [Fact]
    public async Task Starting_over_unpicks_the_date()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2019, 1, 2, 0, 0, 0, TimeSpan.Zero);
        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.StartOver();

        fixture.ViewModel.AbsoluteDate.ShouldBeNull();
    }

    /// <summary>
    /// The other sources are unaffected: they derive their value from the file, so there is
    /// no unchosen state to wait for and blocking them would be gratuitous.
    /// </summary>
    [Fact]
    public async Task A_source_that_reads_the_file_still_works_with_no_date_picked()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.Source = SourceChoice.ShiftBy;
        fixture.ViewModel.ShiftHours = 3;
        fixture.ViewModel.Recompute();

        fixture.ViewModel.AbsoluteDate.ShouldBeNull();
        fixture.ViewModel.Summary.FilesChanging.ShouldBe(1, "a shift needs no date to be picked");
    }
}

/// <summary>
/// What a row says it is doing.
///
/// The multi-field line showed only the value it was moving TO - "Created · Modified →
/// 2026-09-19 12:00" - which reads as a statement about the file rather than a proposal
/// about it. Reported exactly that way. The single-field line had always shown both.
/// </summary>
public class RowSummaryTests
{
    private static readonly DateTimeOffset Before = new(2000, 1, 1, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset After = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private static PlannedChange Change(DateField field) => new(
        new ChangeTarget.Field(field),
        new FieldWrite.SetDate(Before, DatePrecision.Second),
        new FieldWrite.SetDate(After, DatePrecision.Second),
        ChangeStatus.WillChange,
        ProblemCode.None,
        0);

    private static ScannedFile File() => new(
        @"C:\photos\x.png",
        1,
        MediaKind.Png,
        FileAttributes.Normal,
        new TimestampSet(Before, Before, null, null),
        System.Collections.Frozen.FrozenDictionary<DateField, MetadataValue>.Empty,
        FileTraits.None);

    [Fact]
    public void Several_fields_moving_together_still_show_what_they_move_from()
    {
        var plan = new FilePlan(File(), [Change(DateField.FileCreated), Change(DateField.FileModified)]);

        string line = PlanRowViewModel.FormatSummary(plan);

        line.ShouldContain("2000-01-01", Case.Sensitive, "the row has to say what it is changing FROM");
        line.ShouldContain("2026-09-19");
        line.ShouldContain("→");
    }

    [Fact]
    public void One_field_shows_both_as_it_always_did()
    {
        var plan = new FilePlan(File(), [Change(DateField.FileModified)]);

        string line = PlanRowViewModel.FormatSummary(plan);

        line.ShouldContain("2000-01-01");
        line.ShouldContain("2026-09-19");
    }
}
