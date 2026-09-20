// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// Ticking and unticking in bulk, and the count on the Apply button keeping up with it.
///
/// The button states its own scope - "Apply to N of M" - so that number has to follow what
/// is actually ticked. It did not: the checkbox bound two-way to the row and nothing told
/// the summary, so unticking half a list left the button still offering to write all of it.
/// </summary>
public class SelectionCommandTests
{
    private static async Task<WorkbenchFixture> LoadedAsync()
    {
        var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.png", "b.png", "c.jpg", "d.jpg");

        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2019, 1, 2, 0, 0, 0, TimeSpan.Zero);
        fixture.ViewModel.Recompute();

        return fixture;
    }

    [Fact]
    public async Task Unticking_one_row_updates_the_apply_count()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(4);

        fixture.ViewModel.Rows[0].IsIncluded = false;

        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(3, "the Apply button has to follow the checkboxes");
    }

    [Fact]
    public async Task Select_none_unticks_everything()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        fixture.ViewModel.SelectNone();

        fixture.ViewModel.Rows.ShouldAllBe(r => !r.IsIncluded);
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(0);
        fixture.ViewModel.Summary.ApplyLabel.ShouldBe("Nothing to apply");
    }

    [Fact]
    public async Task Select_all_ticks_everything_back()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        fixture.ViewModel.SelectNone();
        fixture.ViewModel.SelectAllShown();

        fixture.ViewModel.Rows.ShouldAllBe(r => r.IsIncluded);
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(4);
    }

    /// <summary>
    /// Select all covers what is SHOWN. Ticking files a filter is deliberately excluding
    /// would contradict the filter, which narrows the run rather than only the view.
    /// </summary>
    [Fact]
    public async Task Select_all_leaves_filtered_out_files_alone()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        fixture.ViewModel.SelectNone();
        fixture.ViewModel.TypeFilter = "*.png";
        fixture.ViewModel.SelectAllShown();

        fixture.ViewModel.Rows.Count.ShouldBe(2);
        fixture.ViewModel.Rows.ShouldAllBe(r => r.IsIncluded);

        // Clearing the filter reveals the JPEGs, still untouched by Select all.
        fixture.ViewModel.TypeFilter = string.Empty;

        fixture.ViewModel.Rows.Count(r => r.IsIncluded).ShouldBe(2, "only the shown files were ticked");
    }

    /// <summary>
    /// Select none is deliberately wider: it covers everything loaded, filter or no filter.
    /// Both commands err the same way, so neither leaves a file ticked that nobody saw.
    /// </summary>
    [Fact]
    public async Task Select_none_reaches_files_the_filter_is_hiding()
    {
        using WorkbenchFixture fixture = await LoadedAsync();

        fixture.ViewModel.TypeFilter = "*.png";
        fixture.ViewModel.SelectNone();
        fixture.ViewModel.TypeFilter = string.Empty;

        fixture.ViewModel.Rows.ShouldAllBe(r => !r.IsIncluded, "nothing should be left ticked out of sight");
    }
}
