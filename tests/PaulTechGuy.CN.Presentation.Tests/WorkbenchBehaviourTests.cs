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

        // Changed is an Advanced field. Reaching it IS picking fields by hand, which is
        // what separates it from Accessed below.
        fixture.ViewModel.WriteChanged = true;

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.Custom);
    }

    /// <summary>
    /// Accessed does NOT. Explorer shows it beside Created and Modified, so the pane offers
    /// it there too - and ticking the third control on the simple path is not the same as
    /// leaving the simple path.
    ///
    /// Reported from the app: a run moved Created and Modified, Explorer went on showing an
    /// untouched Accessed date, and nothing said why.
    /// </summary>
    [Fact]
    public void Ticking_accessed_stays_on_the_file_dates_answer()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.WriteAccessed = true;

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.FileDates);
        fixture.ViewModel.ShowsFileDates.ShouldBeTrue();
    }

    /// <summary>Offered, not chosen: it starts off.</summary>
    [Fact]
    public void The_file_dates_answer_does_not_tick_accessed_for_you()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.WriteAccessed.ShouldBeFalse();
        fixture.ViewModel.WriteChanged.ShouldBeFalse();
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

/// <summary>
/// The controls that report a choice AND have to show one.
///
/// Both of these were reported from the running app, and both were the same fault: the
/// radio groups raised a Checked event and read nothing back, so they were write-only.
/// Whenever the view model decided something for itself - a template being applied, Start
/// over clearing the run - the control kept displaying the previous answer.
///
/// The real fix is structural: the groups are bound two-way now instead of being driven by
/// handlers. These tests cover the view-model half of that contract. They cannot prove the
/// XAML is bound - only running it can - which is exactly why this class of bug keeps
/// reaching a person first.
/// </summary>
public class WorkbenchSelectionTests
{
    /// <summary>
    /// Reported: choosing "Photos sort wrong in Explorer" left the source showing "A date
    /// I pick" while the run actually copied from another date.
    /// </summary>
    [Fact]
    public void A_template_moves_the_source_selection_to_match_itself()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        DateTemplate explorer = fixture.ViewModel.Templates
            .First(t => t.Id == "builtin.photos-sort-wrong-in-explorer");

        fixture.ViewModel.UseTemplate(explorer);

        fixture.ViewModel.Source.ShouldBe(SourceChoice.FromAnotherDate);
        fixture.ViewModel.SourceIndex.ShouldBe((int)SourceChoice.FromAnotherDate);
    }

    [Fact]
    public void A_template_moves_the_intent_selection_to_match_its_targets()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);

        DateTemplate explorer = fixture.ViewModel.Templates
            .First(t => t.Id == "builtin.photos-sort-wrong-in-explorer");

        fixture.ViewModel.UseTemplate(explorer);

        // It writes file dates, so the answer above has to stop claiming photo dates.
        fixture.ViewModel.Intent.ShouldBe(WorkIntent.FileDates);
        fixture.ViewModel.IntentIndex.ShouldBe(0);
    }

    /// <summary>
    /// Reported: after Start over the options were hidden but "Photo and video dates" was
    /// still selected above them - the control contradicting the app.
    /// </summary>
    [Fact]
    public async Task Starting_over_leaves_no_answer_selected()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PhotoDates);
        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.StartOver();

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.None);
        fixture.ViewModel.IntentIndex.ShouldBe(-1, "nothing should be selected after starting over");
        fixture.ViewModel.HasChosenIntent.ShouldBeFalse();
    }

    [Fact]
    public void Starting_over_also_clears_the_active_template()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.UseTemplate(fixture.ViewModel.Templates[0]);

        fixture.ViewModel.StartOver();

        fixture.ViewModel.ActiveTemplate.ShouldBeNull();
        fixture.ViewModel.HasActiveTemplate.ShouldBeFalse();
    }

    /// <summary>
    /// A two-way binding echoes the value back when it pushes one in. If that echo were
    /// treated as a fresh choice, reconciling to Custom would bounce through ChooseIntent
    /// and stamp the default checkboxes over the edit that caused it.
    /// </summary>
    [Fact]
    public void Setting_the_selection_to_what_it_already_is_changes_nothing()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.WriteChanged = true;

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.Custom);

        // The echo the control sends back after the binding updates it.
        fixture.ViewModel.IntentIndex = fixture.ViewModel.IntentIndex;

        fixture.ViewModel.WriteChanged.ShouldBeTrue("the edit must survive the echo");
        fixture.ViewModel.Intent.ShouldBe(WorkIntent.Custom);
    }

    [Fact]
    public void The_selection_round_trips_through_the_index()
    {
        using var fixture = new WorkbenchFixture();

        foreach ((int index, WorkIntent expected) in new[]
        {
            (0, WorkIntent.FileDates),
            (1, WorkIntent.PhotoDates),
            (2, WorkIntent.Custom),
        })
        {
            fixture.ViewModel.IntentIndex = index;

            fixture.ViewModel.Intent.ShouldBe(expected);
            fixture.ViewModel.IntentIndex.ShouldBe(index);
        }
    }
}
