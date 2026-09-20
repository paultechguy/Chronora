// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// Limiting a run to certain file types.
///
/// This filter is not like "only changing" and "only problems". Those narrow what you look
/// at; this narrows what the run COVERS, because the request behind it is "I dropped a
/// folder of JPEG, PNG and raw and only want to touch the PNGs". Unticking several hundred
/// rows by hand is a silly way to answer that.
///
/// Because it scopes a destructive action, the tests care as much about the run as about
/// the list.
/// </summary>
public class TypeFilterTests
{
    private static async Task<WorkbenchFixture> MixedFolderAsync()
    {
        var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        await fixture.LoadAsync("a.png", "b.png", "c.jpg", "d.CR2");

        fixture.ViewModel.AbsoluteDate = new DateTimeOffset(2019, 1, 2, 0, 0, 0, TimeSpan.Zero);
        fixture.ViewModel.Recompute();

        return fixture;
    }

    [Fact]
    public async Task With_no_filter_every_file_is_in_the_run()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.HasTypeFilter.ShouldBeFalse();
        fixture.ViewModel.Rows.Count.ShouldBe(4);
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(4);
    }

    [Fact]
    public async Task A_filter_narrows_both_the_list_and_the_run()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.TypeFilter = "*.png";

        fixture.ViewModel.Rows.Count.ShouldBe(2);
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(2, "the filter scopes the run, not just the view");
    }

    /// <summary>
    /// The count has to be visible on the button, because the filter is what makes it
    /// smaller. An unexplained change to what a destructive button does is the surprise
    /// this whole design exists to avoid.
    /// </summary>
    [Fact]
    public async Task The_apply_button_says_the_run_is_filtered()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.TypeFilter = "*.png";

        fixture.ViewModel.Summary.ApplyLabel.ShouldContain("matching");
        fixture.ViewModel.Summary.HasTypeFilter.ShouldBeTrue();
        fixture.ViewModel.Summary.FilesHiddenByTypeFilter.ShouldBe(2);
    }

    [Fact]
    public async Task Several_types_can_be_listed()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.TypeFilter = "*.png;*.jpg";

        fixture.ViewModel.Rows.Count.ShouldBe(3);
    }

    /// <summary>
    /// A bare extension is what somebody types. Accepting it costs nothing and removes a
    /// way to get an empty list with no explanation.
    /// </summary>
    [Theory]
    [InlineData("png")]
    [InlineData(".png")]
    [InlineData("*.png")]
    [InlineData("  png  ")]
    public async Task A_bare_extension_works_as_well_as_a_wildcard(string typed)
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.TypeFilter = typed;

        fixture.ViewModel.Rows.Count.ShouldBe(2, $"'{typed}' should select the two PNGs");
    }

    [Fact]
    public async Task The_filter_ignores_case()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.TypeFilter = "*.cr2";

        fixture.ViewModel.Rows.Count.ShouldBe(1, "d.CR2 should match despite the case");
    }

    [Fact]
    public async Task Clearing_the_filter_puts_everything_back()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.TypeFilter = "*.png";
        fixture.ViewModel.TypeFilter = string.Empty;

        fixture.ViewModel.HasTypeFilter.ShouldBeFalse();
        fixture.ViewModel.Rows.Count.ShouldBe(4);
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(4);
    }

    /// <summary>A filter matching nothing is honest about it rather than silently applying to all.</summary>
    [Fact]
    public async Task A_filter_that_matches_nothing_runs_on_nothing()
    {
        using WorkbenchFixture fixture = await MixedFolderAsync();

        fixture.ViewModel.TypeFilter = "*.heic";

        fixture.ViewModel.Rows.ShouldBeEmpty();
        fixture.ViewModel.Summary.FilesToWrite.ShouldBe(0);
        fixture.ViewModel.Summary.ApplyLabel.ShouldBe("Nothing to apply");
    }
}

/// <summary>The "use the time now" button, which is deliberately local-only.</summary>
public class UseNowTests
{
    [Fact]
    public void It_fills_in_the_current_local_date_and_time()
    {
        using var fixture = new WorkbenchFixture();
        DateTimeOffset before = DateTimeOffset.Now;

        fixture.ViewModel.UseNow();

        fixture.ViewModel.AbsoluteDate.ShouldNotBeNull();
        fixture.ViewModel.AbsoluteDate!.Value.Date.ShouldBe(before.Date);

        // Local wall-clock, not UTC. Which frame each format STORES it in is decided per
        // field by the writer; offering the choice here would let somebody assert one that
        // contradicts all three.
        fixture.ViewModel.AbsoluteTime.Hours.ShouldBe(before.Hour);
    }

    [Fact]
    public void It_is_enough_on_its_own_to_produce_a_plan()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.UseNow();
        fixture.ViewModel.Recompute();

        fixture.ViewModel.AbsoluteDate.ShouldNotBeNull("picking 'now' counts as picking a date");
    }
}
