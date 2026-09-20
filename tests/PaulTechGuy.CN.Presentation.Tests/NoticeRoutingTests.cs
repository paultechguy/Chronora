// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// Which messages get the banner and which get the bottom bar.
///
/// The banner is highlighted and carries an Undo button, so it has to mean one thing:
/// something happened that you can take back. Using it for ordinary option changes was
/// reported as overkill, and it is - a highlight on everything teaches people to stop
/// reading the one place the app says something urgent.
///
/// Tested rather than eyeballed because the first attempt at this silently did not apply:
/// a text substitution failed to match, and the check that was supposed to confirm it
/// counted the wrong thing. These assert the routing directly.
/// </summary>
public class NoticeRoutingTests
{
    [Fact]
    public void Using_a_template_does_not_raise_the_banner()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.UseTemplate(fixture.ViewModel.Templates[0]);

        fixture.ViewModel.ActionNotice.ShouldBeNull("choosing a template is an option change");
        fixture.ViewModel.ScanStatus.ShouldContain(fixture.ViewModel.Templates[0].Name);
    }

    [Fact]
    public void Leaving_a_template_does_not_raise_the_banner_but_still_says_so()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        fixture.ViewModel.UseTemplate(fixture.ViewModel.Templates[0]);

        fixture.ViewModel.WriteAccessed = true;

        fixture.ViewModel.ActionNotice.ShouldBeNull();

        // Still SAID: dropping the template can change what Apply does in ways the
        // controls cannot show. Just said quietly.
        fixture.ViewModel.ScanStatus.ShouldContain("Stopped using");
    }

    [Fact]
    public void Saving_a_template_does_not_raise_the_banner()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        fixture.ViewModel.SaveCurrentAsTemplate("My fix").ShouldBeNull();

        fixture.ViewModel.ActionNotice.ShouldBeNull();
        fixture.ViewModel.ScanStatus.ShouldContain("My fix");
    }

    /// <summary>A real list action still gets the banner, because it has a real Undo.</summary>
    [Fact]
    public async Task Adding_files_still_raises_the_banner()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        string dropped = fixture.CreateFile("dropped.jpg");
        await fixture.ViewModel.AddDroppedAsync([dropped], TestContext.Current.CancellationToken);

        fixture.ViewModel.ActionNotice.ShouldNotBeNull("a drop can be undone, so it earns the banner");
    }

    /// <summary>
    /// And it goes away once somebody moves on to configuring the run. It never expiring
    /// is the other half of why the banner looked like it was reacting to option changes:
    /// it was simply still there from the last drop.
    /// </summary>
    [Fact]
    public async Task The_banner_clears_when_an_option_is_changed()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);

        string dropped = fixture.CreateFile("dropped.jpg");
        await fixture.ViewModel.AddDroppedAsync([dropped], TestContext.Current.CancellationToken);
        fixture.ViewModel.ActionNotice.ShouldNotBeNull();

        fixture.ViewModel.WriteAccessed = true;

        fixture.ViewModel.ActionNotice.ShouldBeNull("carrying on is accepting the list");
    }

    [Fact]
    public async Task Starting_over_still_raises_the_banner()
    {
        using var fixture = new WorkbenchFixture();
        fixture.ViewModel.ChooseIntent(WorkIntent.FileDates);
        await fixture.LoadAsync("a.jpg");

        fixture.ViewModel.StartOver();

        fixture.ViewModel.ActionNotice.ShouldNotBeNull("start over can be undone");
    }
}
