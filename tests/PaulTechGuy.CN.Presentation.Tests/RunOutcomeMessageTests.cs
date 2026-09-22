// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Services;
using Shouldly;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// The sentence a finished run leaves on screen.
///
/// It used to be counts alone - "Done. 0 changed, 5 failed, 0 skipped." - while the journal
/// held the exact reason against every one of those files. The information existed and was
/// shown nowhere, so the only way to reach it was to open the database, which is not something
/// anybody does while the app is still sitting there reporting the failure.
/// </summary>
public class RunOutcomeMessageTests
{
    private static ApplyOutcome Outcome(int written, int failed, params string[] reasons) =>
        new(1, written, failed, 0, RunStatus.Completed) { FailureReasons = reasons };

    [Fact]
    public void A_clean_run_says_only_the_counts()
    {
        string message = WorkbenchViewModel.DescribeOutcome(Outcome(5, 0));

        message.ShouldBe("Done. 5 changed, 0 failed, 0 skipped.");
    }

    [Fact]
    public void A_failed_run_puts_the_reason_on_the_line()
    {
        string message = WorkbenchViewModel.DescribeOutcome(
            Outcome(0, 5, "ExifTool is not available, so the photo date was not written."));

        message.ShouldBe(
            "Done. 0 changed, 5 failed, 0 skipped. "
            + "ExifTool is not available, so the photo date was not written.");
    }

    /// <summary>
    /// Several reasons get the first one and a pointer. A status bar that tries to hold three
    /// sentences holds none of them.
    /// </summary>
    [Fact]
    public void Several_reasons_get_the_first_and_a_pointer_to_history()
    {
        string message = WorkbenchViewModel.DescribeOutcome(
            Outcome(1, 4, "The file is read-only.", "The file is in use.", "Access was denied."));

        message.ShouldStartWith("Done. 1 changed, 4 failed, 0 skipped. The file is read-only.");
        message.ShouldContain("2 other reason(s)");
        message.ShouldContain("History");
    }
}
