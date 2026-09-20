// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Presentation;

/// <summary>
/// One past run, as History shows it.
///
/// Everything here comes from the journal rather than from the files, which is the point:
/// a run stays explicable after every file it touched has been moved, renamed or deleted.
/// That is also why the description is rendered from what the run recorded about itself
/// rather than recomputed from anything still on disk.
/// </summary>
public sealed class HistoryRowViewModel(JournalRun run)
{
    public JournalRun Run { get; } = run;

    public long RunId => this.Run.RunId;

    /// <summary>
    /// When, in local time and in a form a person reads rather than parses.
    ///
    /// The journal stores UTC, which is right for storage and wrong for a list someone is
    /// scanning to find "the one I did before lunch".
    /// </summary>
    public string When => this.Run.StartedUtc.ToLocalTime()
        .ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);

    /// <summary>What it did, in one line.</summary>
    public string What => this.Run.Kind == RunKind.Revert
        ? string.Create(CultureInfo.CurrentCulture, $"Undo of run {this.Run.RevertsRunId}")
        : Describe(this.Run);

    /// <summary>The scale of it, which is what someone is really scanning for.</summary>
    public string Scale => this.Run.ErrorCount > 0
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"{this.Run.FileCount:N0} files · {this.Run.ChangeCount:N0} changes · {this.Run.ErrorCount:N0} failed")
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{this.Run.FileCount:N0} files · {this.Run.ChangeCount:N0} changes");

    /// <summary>Where, shortened. A full path per row would crowd out everything else.</summary>
    public string Where => this.Run.Roots.Count switch
    {
        0 => string.Empty,
        1 => this.Run.Roots[0],
        _ => string.Create(CultureInfo.CurrentCulture, $"{this.Run.Roots[0]} and {this.Run.Roots.Count - 1:N0} more"),
    };

    public string StatusText => this.Run.Status switch
    {
        RunStatus.Completed => "Applied",
        RunStatus.Cancelled => "Cancelled part way",
        RunStatus.Failed => "Failed",
        RunStatus.Interrupted => "Interrupted",
        RunStatus.Reverted => "Undone",
        RunStatus.PartiallyReverted => "Partly undone",
        _ => this.Run.Status.ToString(),
    };

    /// <summary>
    /// Whether undoing this is worth offering.
    ///
    /// A revert is itself an ordinary run and is itself revertible, which is deliberate -
    /// undoing an undo is a thing people want. What cannot be undone is a run that never
    /// wrote anything, and offering a button that would do nothing is worse than not
    /// offering one.
    /// </summary>
    public bool CanRevert =>
        this.Run.Status is RunStatus.Completed or RunStatus.PartiallyReverted or RunStatus.Interrupted
        && this.Run.ChangeCount > 0;

    /// <summary>Already put back, so the button would have nothing to do.</summary>
    public bool IsFullyReverted => this.Run.Status == RunStatus.Reverted;

    /// <summary>
    /// A one-line rendering of what the run set out to do.
    ///
    /// Read loosely on purpose. A stored run is history, and binding it to today's rule
    /// model would mean an old run stopped listing the day that model changed - which is
    /// exactly when somebody would want to undo it. So this looks for a written summary
    /// and otherwise says something true but vague, rather than failing.
    /// </summary>
    private static string Describe(JournalRun run)
    {
        if (string.IsNullOrWhiteSpace(run.RecipeJson))
        {
            return "Date change";
        }

        // Runs written before the summary existed, and reverts, store a plain sentence.
        if (!run.RecipeJson.TrimStart().StartsWith('{'))
        {
            return run.RecipeJson;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(run.RecipeJson);

            if (document.RootElement.TryGetProperty("summary", out System.Text.Json.JsonElement summary)
                && summary.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable is not a reason to hide the run. Whatever it did, it is still
            // the thing the user may be looking for, and it is still revertible.
        }

        return "Date change";
    }
}
