// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Repositories;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// The controls that belong to the list: sorting, the type filter, and the two selection
/// verbs.
///
/// These moved out of a toolbar and onto the list itself, and two of them changed shape on
/// the way. The tests are about the shapes, because that is where the traps are: a sort
/// direction that cannot be read off the caret, and a pair of selection verbs whose scopes
/// differ in a way that a single control would hide.
/// </summary>
public class ListHeaderTests
{
    private static async Task<WorkbenchFixture> MixedFolderAsync()
    {
        var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.png", "b.png", "c.jpg");

        return fixture;
    }

    /// <summary>
    /// Each sort opens at the end of itself that people mean when they ask for it. A
    /// "biggest change" that opened on the smallest change would be a strange thing to call
    /// biggest.
    /// </summary>
    [Fact]
    public async Task Each_sort_opens_at_the_end_people_mean()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.ChooseSort(SortChoice.Name);
        fixture.ViewModel.SortDescending.ShouldBeFalse("names read A to Z");

        fixture.ViewModel.ChooseSort(SortChoice.BiggestChange);
        fixture.ViewModel.SortDescending.ShouldBeTrue("biggest first is what biggest means");

        fixture.ViewModel.ChooseSort(SortChoice.Status);
        fixture.ViewModel.SortDescending.ShouldBeTrue("worst first");

        fixture.ViewModel.ChooseSort(SortChoice.ResultingDate);
        fixture.ViewModel.SortDescending.ShouldBeFalse("earliest first");
    }

    /// <summary>
    /// A column header click means "this column, or the other way round if it already is".
    /// A menu pick means only "this one" - picking the sort already in force must not hand
    /// somebody the reverse of what they were looking at.
    /// </summary>
    [Fact]
    public async Task A_header_click_toggles_where_a_menu_pick_only_selects()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.ChooseSort(SortChoice.Status);
        fixture.ViewModel.SortDescending.ShouldBeTrue();

        fixture.ViewModel.ChooseSort(SortChoice.Status);
        fixture.ViewModel.SortDescending.ShouldBeTrue("a menu pick selects and nothing more");

        fixture.ViewModel.ToggleSort(SortChoice.Status);
        fixture.ViewModel.SortDescending.ShouldBeFalse("a second header click reverses it");
        fixture.ViewModel.StatusHeaderCaret.ShouldBe("▲");

        fixture.ViewModel.ReverseSort();
        fixture.ViewModel.SortDescending.ShouldBeTrue("and the menu has a verb of its own for it");
    }

    /// <summary>
    /// The caret appears on a header only when the sort is that header's own.
    ///
    /// Two of the four sorts have no column. Parking their caret on the nearest header, or
    /// renaming that header to match, would leave a header describing something other than
    /// the column underneath it - so those two are stated in words instead.
    /// </summary>
    [Fact]
    public async Task A_sort_with_no_column_shows_no_caret_and_says_so_in_words()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.ChooseSort(SortChoice.Name);
        fixture.ViewModel.FileHeaderCaret.ShouldBe("▲");
        fixture.ViewModel.StatusHeaderCaret.ShouldBeEmpty();
        fixture.ViewModel.IsSortOffColumn.ShouldBeFalse();

        fixture.ViewModel.ChooseSort(SortChoice.Status);
        fixture.ViewModel.FileHeaderCaret.ShouldBeEmpty();
        fixture.ViewModel.StatusHeaderCaret.ShouldBe("▼");
        fixture.ViewModel.IsSortOffColumn.ShouldBeFalse();

        fixture.ViewModel.ChooseSort(SortChoice.ResultingDate);
        fixture.ViewModel.FileHeaderCaret.ShouldBeEmpty("there is no resulting-date column to caret");
        fixture.ViewModel.StatusHeaderCaret.ShouldBeEmpty();
        fixture.ViewModel.IsSortOffColumn.ShouldBeTrue();
        fixture.ViewModel.SortChipLabel.ShouldContain("resulting date");
    }

    /// <summary>The direction has to come back, or reopening silently reverses the list.</summary>
    [Fact]
    public async Task The_sort_direction_survives_a_restart()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.ChooseSort(SortChoice.ResultingDate);
        fixture.ViewModel.ReverseSort();

        var saved = new AppSettings();
        fixture.ViewModel.CaptureSettings(saved);

        saved.Sort.ShouldBe("ResultingDate");
        saved.SortDescending.ShouldBeTrue();

        using var reopened = new WorkbenchFixture();
        reopened.ViewModel.ApplySettings(saved);

        reopened.ViewModel.Sort.ShouldBe(SortChoice.ResultingDate);
        reopened.ViewModel.SortDescending.ShouldBeTrue();
    }

    /// <summary>
    /// The two selection verbs cover different sets, and each label says which.
    ///
    /// Select all covers what is SHOWN; Select none covers everything loaded, including
    /// what a filter is hiding. That asymmetry is deliberate - both err the same way, so
    /// neither can leave a file ticked that nobody laid eyes on - and it is exactly why
    /// these two can never become one tri-state checkbox in a list header, where unticking
    /// would appear to mean the rows on screen and would in fact clear the lot.
    /// </summary>
    [Fact]
    public async Task The_selection_labels_carry_their_own_scope()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.SelectAllLabel.ShouldBe("All shown (3)");
        fixture.ViewModel.SelectNoneLabel.ShouldBe("None (3)", "nothing is hidden, so there is nothing to warn about");

        fixture.ViewModel.TypeFilter = "*.png";

        fixture.ViewModel.SelectAllLabel.ShouldBe("All shown (2)");
        fixture.ViewModel.SelectNoneLabel.ShouldBe("None, including hidden (3)");
    }

    /// <summary>Select none really does reach past the filter, which is why the label says so.</summary>
    [Fact]
    public async Task Select_none_reaches_the_files_a_filter_is_hiding()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.SelectAllShown();
        fixture.ViewModel.TypeFilter = "*.png";
        fixture.ViewModel.Rows.Count.ShouldBe(2);

        fixture.ViewModel.SelectNone();
        fixture.ViewModel.TypeFilter = string.Empty;

        fixture.ViewModel.Rows.Count.ShouldBe(3);
        fixture.ViewModel.Rows.All(r => !r.IsIncluded).ShouldBeTrue(
            "the hidden jpg was unticked too, which is the whole point of the wider verb");
    }

    /// <summary>The type filter states what it is costing the run, beside the box that set it.</summary>
    [Fact]
    public async Task The_type_filter_chip_says_how_much_it_excludes()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.HasTypeFilter.ShouldBeFalse();

        fixture.ViewModel.TypeFilter = "*.png";

        fixture.ViewModel.HasTypeFilter.ShouldBeTrue();
        fixture.ViewModel.TypeFilterChipLabel.ShouldContain("*.png");
        fixture.ViewModel.TypeFilterChipLabel.ShouldContain("1 excluded");

        fixture.ViewModel.ClearTypeFilter();

        fixture.ViewModel.HasTypeFilter.ShouldBeFalse();
        fixture.ViewModel.Rows.Count.ShouldBe(3);
    }
}
