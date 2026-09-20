// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// The specific faults that reached the user, pinned so they cannot come back.
///
/// The notification tests cover the mechanism; these cover the behaviour, because a
/// property can be announced correctly and still hold the wrong value.
/// </summary>
public class WorkbenchBehaviourTests
{
    /// <summary>
    /// Reported as "the Taken checkbox does nothing". It was rendered in File dates mode,
    /// where it cannot be written, and the recipe silently dropped it - so it ticked, the
    /// preview said nothing would change, and nothing explained why.
    /// </summary>
    [Fact]
    public void Photo_targets_are_not_offered_when_only_file_dates_are_being_written()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.IsPhotoMode.ShouldBeFalse("the Taken checkbox must not be on screen here");
        fixture.ViewModel.ShowsFileDates.ShouldBeTrue();
    }

    /// <summary>
    /// Reported as "only Taken is listed". ShowsFileDates was computed once at startup
    /// with no intent chosen, so Created and Modified never appeared.
    /// </summary>
    [Fact]
    public void Picking_the_fields_by_hand_offers_every_field()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ChooseIntent(WorkIntent.Custom);

        fixture.ViewModel.ShowsFileDates.ShouldBeTrue();
        fixture.ViewModel.IsPhotoMode.ShouldBeTrue();
        fixture.ViewModel.ShowsAdvancedFields.ShouldBeTrue();
    }

    /// <summary>
    /// Custom starts with both, because wanting both is the usual reason to go there.
    /// That is what keeps "both" one click without a top-level option that teaches nothing.
    /// </summary>
    [Fact]
    public void Picking_by_hand_starts_with_the_photo_date_and_the_file_dates()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ChooseIntent(WorkIntent.Custom);

        fixture.ViewModel.WriteCreated.ShouldBeTrue();
        fixture.ViewModel.WriteModified.ShouldBeTrue();
        fixture.ViewModel.WriteTaken.ShouldBeTrue();
    }

    /// <summary>Taken stands alone: for Google Photos it is the only correct choice.</summary>
    [Fact]
    public void The_photo_date_can_be_written_without_any_file_date()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);

        fixture.ViewModel.WriteTaken.ShouldBeTrue();
        fixture.ViewModel.WriteCreated.ShouldBeFalse();
        fixture.ViewModel.WriteModified.ShouldBeFalse();
        fixture.ViewModel.Mode.ShouldBe(AppMode.PhotoDates);
    }

    /// <summary>Editing the boxes moves the label, so it never describes a stale selection.</summary>
    [Fact]
    public void Editing_the_fields_by_hand_turns_the_answer_into_custom()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.WriteAccessed = true;

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.Custom);
    }

    /// <summary>And editing back to a named shape restores that name rather than sticking on Custom.</summary>
    [Fact]
    public void Editing_back_to_a_named_shape_restores_that_name()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.Custom);

        fixture.ViewModel.WriteTaken = false;

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.FileDates);
    }

    /// <summary>
    /// The gate: nothing else is on screen until the question is answered.
    /// </summary>
    [Fact]
    public void Nothing_is_offered_before_the_question_is_answered()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.HasChosenIntent.ShouldBeFalse();
        fixture.ViewModel.ShowsFileDates.ShouldBeFalse();
        fixture.ViewModel.IsPhotoMode.ShouldBeFalse();
        fixture.ViewModel.ShowsAdvancedFields.ShouldBeFalse();
    }

    /// <summary>Reported: Clear was active on an empty list, where it does nothing.</summary>
    [Fact]
    public async Task Clearing_is_only_offered_when_there_is_something_to_clear()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.HasAnyFiles.ShouldBeFalse();

        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.HasAnyFiles.ShouldBeTrue();
    }

    /// <summary>
    /// Start over covers the view state too, because a stale filter is the thing you
    /// cannot see the cause of.
    /// </summary>
    [Fact]
    public void Starting_over_is_offered_once_a_filter_is_set_even_with_no_files()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.CanStartOver.ShouldBeFalse();

        fixture.ViewModel.ShowOnlyProblems = true;

        fixture.ViewModel.CanStartOver.ShouldBeTrue();
    }

    [Fact]
    public async Task Starting_over_returns_to_the_opening_question()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.Custom);
        fixture.ViewModel.Sort = SortChoice.BiggestChange;
        fixture.ViewModel.ShowOnlyChanging = true;
        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.StartOver();

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.None);
        fixture.ViewModel.Sort.ShouldBe(SortChoice.Name);
        fixture.ViewModel.ShowOnlyChanging.ShouldBeFalse();
        fixture.ViewModel.HasAnyFiles.ShouldBeFalse();
    }

    [Fact]
    public async Task Starting_over_can_be_undone()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);
        await fixture.LoadAsync("a.jpg", "b.jpg");

        fixture.ViewModel.StartOver();
        fixture.ViewModel.UndoLastAction();

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.PhotoDates);
        fixture.ViewModel.HasAnyFiles.ShouldBeTrue();
    }

    /// <summary>Reported: "replace the list" offered when the list was empty, which is a no-op.</summary>
    [Fact]
    public async Task Replacing_is_only_offered_when_the_drop_landed_on_something()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        string first = fixture.CreateFile("first.jpg");
        await fixture.ViewModel.AddDroppedAsync([first], TestContext.Current.CancellationToken);

        fixture.ViewModel.CanReplaceWithDrop.ShouldBeFalse("the list was empty, so replacing changes nothing");

        string second = fixture.CreateFile("second.jpg");
        await fixture.ViewModel.AddDroppedAsync([second], TestContext.Current.CancellationToken);

        fixture.ViewModel.CanReplaceWithDrop.ShouldBeTrue();
    }

    [Fact]
    public async Task A_drop_adds_rather_than_replaces_and_can_be_undone()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg");

        string dropped = fixture.CreateFile("dropped.jpg");
        await fixture.ViewModel.AddDroppedAsync([dropped], TestContext.Current.CancellationToken);

        fixture.ViewModel.Rows.Count.ShouldBe(2, "the drop adds to the list rather than replacing it");

        fixture.ViewModel.UndoLastAction();

        fixture.ViewModel.Rows.Count.ShouldBe(1);
    }

    /// <summary>
    /// Reported as the answer "no" to whether the confirmation said enough: a field the
    /// user asked for that cannot be written vanished from the summary entirely.
    /// </summary>
    [Fact]
    public async Task A_field_that_cannot_be_written_is_reported_rather_than_omitted()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);
        await fixture.LoadAsync("a.jpg");

        // No ExifTool in a test environment, so the photo date cannot be written.
        fixture.ViewModel.EngineStatus.Available.ShouldBeFalse();

        fixture.ViewModel.Summary.HasBlocked.ShouldBeTrue();
        fixture.ViewModel.Summary.BlockedLines
            .ShouldContain(line => line.Field == DateField.ExifDateTimeOriginal);
    }

    /// <summary>The bar that offers the consent pane appears only when something needs it.</summary>
    [Fact]
    public void The_exiftool_prompt_appears_only_when_the_recipe_needs_it()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.NeedsExifTool.ShouldBeFalse();

        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);
        fixture.ViewModel.NeedsExifTool.ShouldBeTrue();
    }

    /// <summary>
    /// Reading a photo date to write onto file dates needs ExifTool too, even though
    /// nothing metadata-shaped is being written. That is the product's whole differentiator
    /// and it is easy to miss when thinking only about targets.
    /// </summary>
    [Fact]
    public void Reading_a_photo_date_needs_exiftool_even_when_only_file_dates_are_written()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.Source = SourceChoice.FromAnotherDate;

        fixture.ViewModel.CopyFromField = DateField.FileModified;
        fixture.ViewModel.NeedsExifTool.ShouldBeFalse();

        fixture.ViewModel.CopyFromField = DateField.ExifDateTimeOriginal;
        fixture.ViewModel.NeedsExifTool.ShouldBeTrue();
    }

    /// <summary>The Apply button states its own scope, so it can never be read two ways.</summary>
    [Fact]
    public async Task The_apply_label_names_both_counts()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg", "b.jpg");

        fixture.ViewModel.Summary.ApplyLabel.ShouldContain("of 2");
    }
}
