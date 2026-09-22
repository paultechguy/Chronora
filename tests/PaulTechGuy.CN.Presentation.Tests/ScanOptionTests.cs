// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Repositories;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// How a folder gets read.
///
/// None of this was a choice until now: every entry point passed ScanFilter.Default, which
/// is recursive and unbounded, and the scanner overrode .NET's own default to sweep hidden
/// and system files in with everything else. Exposing it is only safe if there is exactly
/// one place the filter is built, because the entry point everybody forgets is the rescan
/// after a run - the one that would quietly refill the list with the tree you just said not
/// to read.
/// </summary>
public class ScanOptionTests
{
    [Fact]
    public void The_filter_is_built_from_the_settings_on_screen()
    {
        using var fixture = new WorkbenchFixture();

        ScanFilter filter = fixture.ViewModel.BuildScanFilter();

        filter.Recurse.ShouldBeTrue("dropping a folder usually means the folder");
        filter.IncludeFiles.ShouldBeTrue();
        filter.IncludeHidden.ShouldBeFalse("a drop means the files you can see");
        filter.IncludeDirectories.ShouldBeFalse();

        fixture.ViewModel.ScanRecurse = false;
        fixture.ViewModel.ScanIncludeHidden = true;
        fixture.ViewModel.ScanIncludeFolders = true;

        ScanFilter changed = fixture.ViewModel.BuildScanFilter();

        changed.Recurse.ShouldBeFalse();
        changed.IncludeHidden.ShouldBeTrue();
        changed.IncludeDirectories.ShouldBeTrue();
        changed.IncludeRootDirectory.ShouldBeTrue("the folder itself counts as one of the folders");
    }

    /// <summary>
    /// A sticky setting that is only visible inside a flyout is how somebody recursively
    /// scans a drive by accident, so the card states it - and states it as a SETTING, in
    /// the future tense, because changing it does not re-read what is already loaded.
    /// </summary>
    [Fact]
    public void The_setting_is_stated_in_words_and_in_the_right_tense()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ScanSettingLabel.ShouldBe("New drops: subfolders");

        fixture.ViewModel.ScanRecurse = false;
        fixture.ViewModel.ScanSettingLabel.ShouldBe("New drops: this folder only");

        fixture.ViewModel.ScanIncludeHidden = true;
        fixture.ViewModel.ScanSettingLabel.ShouldContain("hidden files");
    }

    /// <summary>
    /// Changing the setting does not re-read the disk, so the card has to know when the
    /// list no longer matches it and offer the way to make it match.
    /// </summary>
    [Fact]
    public async Task Changing_the_setting_offers_to_read_the_folders_again()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.CanRescanWithOptions.ShouldBeFalse("nothing is loaded");

        await fixture.LoadAsync("a.txt", "b.txt");

        fixture.ViewModel.CanRescanWithOptions.ShouldBeFalse("the list matches the settings that read it");

        fixture.ViewModel.ScanRecurse = false;

        fixture.ViewModel.CanRescanWithOptions.ShouldBeTrue("the settings have moved on from the list");
    }

    /// <summary>
    /// The entry point that is easiest to miss and worst to get wrong. RescanAsync runs
    /// after every apply; left on ScanFilter.Default it would repopulate the list with the
    /// whole tree, a moment after a run the user had scoped deliberately.
    /// </summary>
    [Fact]
    public async Task The_rescan_after_a_run_uses_the_same_settings()
    {
        using var fixture = new WorkbenchFixture();

        string nested = Path.Combine(fixture.Files, "deeper");
        _ = Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "buried.txt"), "x", TestContext.Current.CancellationToken);

        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("top.txt");

        fixture.ViewModel.Rows.Count.ShouldBe(2, "recursive by default, so the buried file comes too");

        fixture.ViewModel.ScanRecurse = false;
        await fixture.ViewModel.RescanWithOptionsAsync();

        fixture.ViewModel.Rows.Count.ShouldBe(1, "the buried file is out of scope now");
        fixture.ViewModel.Rows[0].Name.ShouldBe("top.txt");
    }

    /// <summary>Hidden files are left alone unless asked for, which is a change of behaviour.</summary>
    [Fact]
    public async Task Hidden_files_stay_out_unless_asked_for()
    {
        using var fixture = new WorkbenchFixture();

        string hidden = fixture.CreateFile("secret.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        await fixture.LoadAsync("plain.txt");

        fixture.ViewModel.Rows.Count.ShouldBe(1);
        fixture.ViewModel.Rows[0].Name.ShouldBe("plain.txt");

        fixture.ViewModel.ScanIncludeHidden = true;
        await fixture.ViewModel.RescanWithOptionsAsync();

        fixture.ViewModel.Rows.Count.ShouldBe(2, "asked for, so collected");
    }

    /// <summary>
    /// A file dropped on its own survives the rescan that follows a run.
    ///
    /// The rescan re-reads the ROOTS, and a file named on its own belongs to no root - so
    /// clearing the rows and rebuilding them from the folders quietly threw every loose
    /// file away. It needs a folder in the list as well to show up at all: with only loose
    /// files there were no roots, the rescan returned early, and nothing was lost.
    /// </summary>
    [Fact]
    public async Task A_file_dropped_on_its_own_survives_a_rescan()
    {
        using var fixture = new WorkbenchFixture();

        // A folder, so the rescan has something to rebuild from...
        await fixture.LoadAsync("in-folder.txt");

        // ...and a file from somewhere else entirely, named on its own.
        string elsewhere = Path.Combine(Path.GetTempPath(), "chronora-loose", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(elsewhere);

        string loose = Path.Combine(elsewhere, "on-its-own.txt");
        await File.WriteAllTextAsync(loose, "x", TestContext.Current.CancellationToken);

        try
        {
            await fixture.ViewModel.AddDroppedAsync([loose], TestContext.Current.CancellationToken);

            fixture.ViewModel.Rows.Count.ShouldBe(2);

            await fixture.ViewModel.RescanWithOptionsAsync();

            fixture.ViewModel.Rows.Count.ShouldBe(2, "the loose file was dropped on the floor by the rescan");
            fixture.ViewModel.Rows.Any(r => r.Name == "on-its-own.txt").ShouldBeTrue();
            fixture.ViewModel.Rows.Any(r => r.Name == "in-folder.txt").ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    /// <summary>A loose file that has since gone leaves the list rather than coming back broken.</summary>
    [Fact]
    public async Task A_loose_file_that_has_gone_does_not_come_back()
    {
        using var fixture = new WorkbenchFixture();

        await fixture.LoadAsync("in-folder.txt");

        string elsewhere = Path.Combine(Path.GetTempPath(), "chronora-loose", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(elsewhere);

        string loose = Path.Combine(elsewhere, "doomed.txt");
        await File.WriteAllTextAsync(loose, "x", TestContext.Current.CancellationToken);

        try
        {
            await fixture.ViewModel.AddDroppedAsync([loose], TestContext.Current.CancellationToken);
            fixture.ViewModel.Rows.Count.ShouldBe(2);

            File.Delete(loose);

            await fixture.ViewModel.RescanWithOptionsAsync();

            fixture.ViewModel.Rows.Count.ShouldBe(1);
            fixture.ViewModel.Rows.Any(r => r.Name == "doomed.txt").ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void The_settings_survive_a_restart()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ScanRecurse = false;
        fixture.ViewModel.ScanIncludeHidden = true;

        var saved = new AppSettings();
        fixture.ViewModel.CaptureSettings(saved);

        saved.ScanRecurse.ShouldBeFalse();
        saved.ScanIncludeHidden.ShouldBeTrue();

        using var reopened = new WorkbenchFixture();
        reopened.ViewModel.ApplySettings(saved);

        reopened.ViewModel.ScanRecurse.ShouldBeFalse();
        reopened.ViewModel.ScanIncludeHidden.ShouldBeTrue();
        reopened.ViewModel.BuildScanFilter().Recurse.ShouldBeFalse();
    }
}
