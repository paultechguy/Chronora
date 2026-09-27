// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Repositories;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// The Private details intent in the workbench: it stays itself, it never comes back on its
/// own, and nothing reachable from History offers to undo it.
/// </summary>
public class PrivacyIntentTests
{
    /// <summary>
    /// The app opens with nothing to apply. For a date intent that is guaranteed by not
    /// restoring the date; for this one, whose run needs no date at all, only by not
    /// restoring the intent.
    /// </summary>
    [Fact]
    public void The_intent_never_comes_back_but_the_choices_do()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PrivateDetails);
        fixture.ViewModel.RemoveThumbnail = false;
        fixture.ViewModel.WarnBeforeMetadataRemoval = false;

        var saved = new AppSettings();
        fixture.ViewModel.CaptureSettings(saved);

        using var next = new WorkbenchFixture();
        next.ViewModel.ApplySettings(saved);

        next.ViewModel.Intent.ShouldBe(WorkIntent.None);
        next.ViewModel.RemoveThumbnail.ShouldBeFalse();
        next.ViewModel.RemoveLocation.ShouldBeTrue();
        next.ViewModel.WarnBeforeMetadataRemoval.ShouldBeFalse();
    }

    /// <summary>A settings file from before this feature must not read the warning back as off.</summary>
    [Fact]
    public void An_older_settings_file_leaves_the_warning_on()
    {
        string root = Path.Combine(Path.GetTempPath(), "chronora-settings-tests", Guid.NewGuid().ToString("N"));
        var paths = new TempPaths(root);
        paths.EnsureCreated();

        try
        {
            File.WriteAllText(paths.SettingsFilePath, """{ "schemaVersion": 1, "intent": "FileDates" }""");

            AppSettings old = new SettingsStore(paths).Current;

            old.Intent.ShouldBe("FileDates", "the file was read, not replaced by defaults");
            old.WarnBeforeMetadataRemoval.ShouldBeTrue();
            old.RemoveLocation.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Editing_a_hidden_date_box_does_not_turn_it_into_Custom()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PrivateDetails);

        fixture.ViewModel.WriteTaken = !fixture.ViewModel.WriteTaken;

        fixture.ViewModel.Intent.ShouldBe(WorkIntent.PrivateDetails);
        fixture.ViewModel.IsDateIntent.ShouldBeFalse();
    }

    [Fact]
    public void A_template_steps_out_to_the_date_intent_it_describes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PrivateDetails);

        fixture.ViewModel.UseTemplate(fixture.ViewModel.Templates[0]);

        fixture.ViewModel.IsPrivacyIntent.ShouldBeFalse();
        fixture.ViewModel.IsDateIntent.ShouldBeTrue();
    }

    [Fact]
    public void Ticking_a_category_announces_everything_it_changes()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.PrivateDetails);

        using var watcher = new NotificationWatcher(fixture.ViewModel);
        fixture.ViewModel.RemoveLocation = false;

        watcher.SilentChanges().ShouldBeEmpty();
    }

    [Fact]
    public void History_never_offers_to_undo_a_strip()
    {
        var run = new JournalRun(
            RunId: 7, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, RunStatus.Completed, RunKind.PrivacyStrip,
            RevertsRunId: null, "0.1.0", null, "UTC", """{"summary":"Removed location"}""", ["C:\\p"],
            FileCount: 3, ChangeCount: 3, ErrorCount: 0, Pinned: false, Note: null);

        var row = new HistoryRowViewModel(run);

        row.CanRevert.ShouldBeFalse("even with a count that would otherwise qualify");
        row.StatusText.ShouldBe("Cannot be undone");
        row.Scale.ShouldNotContain("changes");
    }
}
