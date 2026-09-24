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

        // It sliced the card label one character too far and said "his folder only".
        fixture.ViewModel.ActionNotice.ShouldBe("Read the folders again: this folder only.");
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

    /// <summary>
    /// A drop of any size says where it has got to.
    ///
    /// The folder path did this from the day it was written and the drop path never did,
    /// which is the whole of what made a big drop feel like a hang: the spinner and the
    /// Cancel button were live the entire time, with nothing moving beside them to show it.
    /// Exactly one batch's worth of files, because the point is that the first report
    /// happens at all, not how many follow.
    /// </summary>
    [Fact]
    public async Task A_drop_reports_progress_while_it_reads()
    {
        using var fixture = new WorkbenchFixture();

        string tree = Path.Combine(fixture.Files, "big");
        _ = Directory.CreateDirectory(tree);

        for (int i = 0; i < 500; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(tree, $"{i:D4}.txt"), "x", TestContext.Current.CancellationToken);
        }

        List<string> said = [];

        fixture.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.ProgressStatus))
            {
                said.Add(fixture.ViewModel.ProgressStatus);
            }
        };

        await fixture.ViewModel.AddDroppedAsync([tree], TestContext.Current.CancellationToken);

        said.ShouldContain(
            s => s.StartsWith("Read 500 files", StringComparison.Ordinal),
            "the drop ran to completion in silence");

        // And the footer keeps the short sentence, not the notice, which can be longer
        // than one trimmed line.
        fixture.ViewModel.ProgressStatus.ShouldBe("Added 500 files from big.");
    }

    /// <summary>
    /// The one number in this feature. It is a remark on the notice, not a cap: nothing is
    /// truncated or refused, so the only thing to pin is that it speaks up when it should
    /// and stays quiet otherwise.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(24_999, false)]
    [InlineData(25_000, true)]
    public void A_large_list_says_so_and_an_ordinary_one_does_not(int added, bool expected)
    {
        const string Summary = "Added some files from Photos.";

        string notice = WorkbenchViewModel.DescribeDrop(Summary, added);

        notice.ShouldStartWith(Summary, Case.Sensitive, "the count comes first either way");
        notice.Contains("large list", StringComparison.Ordinal).ShouldBe(expected);
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

    // ---- The name filters behind "More filters…" -------------------------------------

    private static ScanPatterns SkipSidecars => ScanPatterns.FromText(null, "*.aae", null, "@eaDir");

    /// <summary>
    /// SameScan used to leave the patterns out, because they were always "*". Left out
    /// now, changing a filter would never offer to re-read - and the other half: a pane
    /// cleared to nothing is the same setting as "*", so it must not offer either.
    /// </summary>
    [Fact]
    public async Task A_name_filter_alone_offers_to_read_the_folders_again()
    {
        using var fixture = new WorkbenchFixture();
        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.ApplyScanPatterns(ScanPatterns.FromText(string.Empty, string.Empty, "*", string.Empty), savePreference: false);
        fixture.ViewModel.CanRescanWithOptions.ShouldBeFalse("an empty include pane and \"*\" are the same setting");

        fixture.ViewModel.ApplyScanPatterns(SkipSidecars, savePreference: false);
        fixture.ViewModel.CanRescanWithOptions.ShouldBeTrue();
    }

    /// <summary>
    /// Session only means the settings file never hears of it - including on a clean
    /// close, when CaptureSettings sweeps everything else up.
    /// </summary>
    [Fact]
    public void Session_only_filters_are_used_but_never_saved()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ApplyScanPatterns(SkipSidecars, savePreference: false);

        fixture.ViewModel.BuildScanFilter().ExcludeFolders.ShouldBe(["@eaDir"]);

        var saved = new AppSettings();
        fixture.ViewModel.CaptureSettings(saved);

        using (var reopened = new WorkbenchFixture())
        {
            reopened.ViewModel.ApplySettings(saved);
            reopened.ViewModel.BuildScanFilter().IsNarrowed.ShouldBeFalse("session-only filters end with the session");
        }

        fixture.ViewModel.ApplyScanPatterns(SkipSidecars, savePreference: true);
        fixture.ViewModel.CaptureSettings(saved);

        using var again = new WorkbenchFixture();
        again.ViewModel.ApplySettings(saved);

        ScanFilter restored = again.ViewModel.BuildScanFilter();
        restored.ExcludeFiles.ShouldBe(["*.aae"]);
        restored.ExcludeFolders.ShouldBe(["@eaDir"]);
        again.ViewModel.ScanPatternsAreSessionOnly.ShouldBeFalse();
    }

    /// <summary>
    /// The entry point everybody forgets, again: the rescan after a run must read with the
    /// name filters too, or it refills the list with the @eaDir folders just excluded.
    /// </summary>
    [Fact]
    public async Task The_rescan_uses_the_name_filters()
    {
        using var fixture = new WorkbenchFixture();

        _ = Directory.CreateDirectory(Path.Combine(fixture.Files, "@eaDir"));
        _ = fixture.CreateFile(Path.Combine("@eaDir", "thumb.jpg"));
        _ = fixture.CreateFile("edit.aae");

        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("top.jpg");
        fixture.ViewModel.Rows.Count.ShouldBe(3);

        fixture.ViewModel.ApplyScanPatterns(SkipSidecars, savePreference: false);
        await fixture.ViewModel.RescanWithOptionsAsync();

        fixture.ViewModel.Rows.Select(r => r.Name).ShouldBe(["top.jpg"]);
        fixture.ViewModel.ActionNotice.ShouldBe("Read the folders again: subfolders · name filters (this session).");
        fixture.ViewModel.CanRescanWithOptions.ShouldBeFalse("the list matches its settings again");
    }

    /// <summary>
    /// Read a folder, change the filters, read another. The list is now half one and half
    /// the other, and neither filter describes it - so the card must offer to re-read it,
    /// even after the filters are put back.
    /// </summary>
    [Fact]
    public async Task A_list_read_under_two_filters_offers_to_read_again()
    {
        using var fixture = new WorkbenchFixture();
        await fixture.LoadAsync("a.jpg");

        string more = Path.Combine(fixture.Files, "more");
        _ = Directory.CreateDirectory(more);
        await File.WriteAllTextAsync(Path.Combine(more, "b.jpg"), "x", TestContext.Current.CancellationToken);

        fixture.ViewModel.ApplyScanPatterns(SkipSidecars, savePreference: false);
        await fixture.ViewModel.AddFolderAsync(more, fixture.ViewModel.BuildScanFilter(), TestContext.Current.CancellationToken);

        fixture.ViewModel.ApplyScanPatterns(ScanPatterns.None, savePreference: false);

        fixture.ViewModel.CanRescanWithOptions.ShouldBeTrue("part of the list was read under the other filter");
    }

    [Fact]
    public void The_card_says_when_name_filters_are_on_and_whether_they_last()
    {
        using var fixture = new WorkbenchFixture();

        fixture.ViewModel.ApplyScanPatterns(SkipSidecars, savePreference: false);
        fixture.ViewModel.ScanSettingLabel.ShouldBe("New drops: subfolders · name filters (this session)");
        fixture.ViewModel.ScanSettingTooltip.ShouldContain("Skip folders named @eaDir");

        fixture.ViewModel.ApplyScanPatterns(SkipSidecars, savePreference: true);
        fixture.ViewModel.ScanSettingLabel.ShouldBe("New drops: subfolders · name filters");

        // Include folders means nothing when only the dropped folder itself is read.
        fixture.ViewModel.ApplyScanPatterns(ScanPatterns.FromText(null, null, "2019", null), savePreference: true);
        fixture.ViewModel.ScanRecurse = false;
        fixture.ViewModel.ScanSettingLabel.ShouldBe("New drops: this folder only");
    }
}
