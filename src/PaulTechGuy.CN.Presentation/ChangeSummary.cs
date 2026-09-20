// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Presentation;

/// <summary>
/// One line of the summary band: what is happening to one field, across the whole run.
/// </summary>
/// <param name="Field">The field being written.</param>
/// <param name="Count">How many files.</param>
/// <param name="Earliest">The earliest resulting date.</param>
/// <param name="Latest">The latest resulting date.</param>
/// <param name="Suspicious">How many of them look wrong.</param>
public sealed record SummaryLine(DateField Field, int Count, DateTimeOffset? Earliest, DateTimeOffset? Latest, int Suspicious)
{
    public string FieldName => DateFieldCatalog.Get(this.Field).DisplayName;

    /// <summary>
    /// A range rather than a list. "3,937 files land between 2019 and 2024" is something a
    /// person can actually check; 3,937 individual rows is not.
    /// </summary>
    public string Range
    {
        get
        {
            if (this.Earliest is not { } from || this.Latest is not { } to)
            {
                return "—";
            }

            // With the time. This app's whole subject is dates AND times, and a run that
            // shifts everything by three hours showed an identical range at day precision
            // - a preview that could not distinguish the change from no change at all.
            string a = from.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
            string b = to.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

            return a == b ? a : $"{a} … {b}";
        }
    }

    public string Detail => string.Create(
        CultureInfo.CurrentCulture,
        $"{this.Count:N0} file{(this.Count == 1 ? string.Empty : "s")} → {this.Range}");

    public bool HasSuspicious => this.Suspicious > 0;

    public string SuspiciousDetail => string.Create(
        CultureInfo.CurrentCulture,
        $"⚠ {this.Suspicious:N0} look{(this.Suspicious == 1 ? "s" : string.Empty)} wrong");
}

/// <summary>
/// A field the user asked for that will NOT be written, and why.
///
/// This exists because omitting it is the exact failure the destination presets were added
/// to prevent: someone ticks a field, the summary lists only what will change, and the app
/// reports a clean run while quietly doing nothing about the thing they actually wanted.
/// </summary>
/// <param name="Field">The field that was requested.</param>
/// <param name="Count">How many files it affects.</param>
/// <param name="Reason">Plain language, already user-facing.</param>
public sealed record BlockedLine(DateField Field, int Count, string Reason)
{
    public string FieldName => DateFieldCatalog.Get(this.Field).DisplayName;

    public string Detail => string.Create(
        CultureInfo.CurrentCulture,
        $"{this.Count:N0} file{(this.Count == 1 ? string.Empty : "s")} — {this.Reason}");
}

/// <summary>
/// What the whole run will do, grouped so it can be verified at a glance.
///
/// This exists because a per-row diff does not scale: at 4,000 files, scanning every row is
/// not a preview, it is a spot check. Six lines that group the run into change classes is
/// something a person can actually confirm before committing.
/// </summary>
/// <param name="Lines">One per field being written.</param>
/// <param name="FilesTotal">Every file in the list.</param>
/// <param name="FilesChanging">Files with at least one write.</param>
/// <param name="FilesBlocked">Files with at least one blocked field.</param>
/// <param name="FilesSuspicious">Files with at least one questionable result.</param>
/// <param name="FilesIncluded">Files the user has ticked.</param>
public sealed record ChangeSummary(
    IReadOnlyList<SummaryLine> Lines,
    int FilesTotal,
    int FilesChanging,
    int FilesBlocked,
    int FilesSuspicious,
    int FilesIncluded)
{
    public static ChangeSummary Empty { get; } = new([], 0, 0, 0, 0, 0);

    /// <summary>Files that are both ticked and will actually change: what Apply acts on.</summary>
    public int FilesToWrite { get; init; }

    /// <summary>
    /// Fields the user asked for that will not happen. Shown as prominently as the ones
    /// that will, because "you asked for this and it is not going to work" is more urgent
    /// information than "these other things will".
    /// </summary>
    public IReadOnlyList<BlockedLine> BlockedLines { get; init; } = [];

    public bool HasBlocked => this.BlockedLines.Count > 0;

    /// <summary>
    /// File dates this run is NOT writing, so the confirmation can say so before anyone
    /// commits rather than leaving them to find out in Explorer.
    ///
    /// Explorer's Properties dialog shows Created, Modified and Accessed together. A run
    /// that moves two of the three leaves one visibly sitting on its original date with
    /// nothing on screen to explain it - reported from the app, and only obvious to
    /// somebody who already knew the field was there to ask for.
    /// </summary>
    public IReadOnlyList<DateField> UntouchedFileDates { get; init; } = [];

    public bool HasUntouched => this.UntouchedFileDates.Count > 0;

    /// <summary>The type filter in force, or null. Stated because it narrows the RUN.</summary>
    public string? TypeFilter { get; init; }

    /// <summary>How many added files the type filter is keeping out of the run.</summary>
    public int FilesHiddenByTypeFilter { get; init; }

    public bool HasTypeFilter => !string.IsNullOrWhiteSpace(this.TypeFilter);

    public bool HasAnything => this.FilesTotal > 0;

    public string StatusLine
    {
        get
        {
            if (this.FilesTotal == 0)
            {
                return "No files yet. Add a folder to begin.";
            }

            var parts = new List<string>
            {
                string.Create(CultureInfo.CurrentCulture, $"{this.FilesChanging:N0} of {this.FilesTotal:N0} will change"),
            };

            if (this.FilesBlocked > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{this.FilesBlocked:N0} blocked"));
            }

            if (this.FilesSuspicious > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{this.FilesSuspicious:N0} look odd"));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The Apply button states its own scope, so "did it act on what I ticked or on what
    /// changed?" is never a question the user has to hold in their head.
    /// </summary>
    public string ApplyLabel => this.FilesToWrite == 0
        ? "Nothing to apply"
        : this.HasTypeFilter
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"Apply to {this.FilesToWrite:N0} of {this.FilesTotal:N0} matching files")
            : string.Create(CultureInfo.CurrentCulture, $"Apply to {this.FilesToWrite:N0} of {this.FilesTotal:N0} files");

    /// <param name="targets">
    /// What the run is actually writing, so the summary can name the file dates it is NOT.
    /// Passed in rather than inferred from the rows, because a field nothing changes and a
    /// field nobody asked for look identical once the plans are built.
    /// </param>
    /// <param name="typeFilter">
    /// The type filter in force, when there is one. Recorded so the Apply button and the
    /// confirmation can say that the run is narrower than the list somebody added.
    /// </param>
    /// <param name="totalBeforeFilter">Files added, before the type filter narrowed them.</param>
    public static ChangeSummary Build(
        IReadOnlyList<PlanRowViewModel> rows,
        IReadOnlySet<DateField>? targets = null,
        string? typeFilter = null,
        int totalBeforeFilter = 0)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return Empty;
        }

        var byField = new Dictionary<DateField, (int Count, DateTimeOffset? Min, DateTimeOffset? Max, int Odd)>();
        var blockedByField = new Dictionary<DateField, (int Count, ProblemCode Reason)>();

        int changing = 0;
        int blocked = 0;
        int suspicious = 0;
        int included = 0;
        int toWrite = 0;

        foreach (PlanRowViewModel row in rows)
        {
            if (row.IsIncluded)
            {
                included++;
            }

            FilePlan? plan = row.Plan;
            if (plan is null)
            {
                continue;
            }

            bool rowWrites = plan.WillWrite;

            if (rowWrites)
            {
                changing++;
            }

            if (rowWrites && row.IsIncluded)
            {
                toWrite++;
            }

            if (plan.HasProblem)
            {
                blocked++;
            }

            if (plan.IsSuspicious)
            {
                suspicious++;
            }

            foreach (PlannedChange change in plan.Changes)
            {
                if (change.Target is not ChangeTarget.Field field)
                {
                    continue;
                }

                if (change.Status == ChangeStatus.Blocked)
                {
                    (int Count, ProblemCode Reason) tally =
                        blockedByField.TryGetValue(field.Which, out var seen) ? seen : (0, change.Problem);

                    blockedByField[field.Which] = (tally.Count + 1, tally.Reason);
                    continue;
                }

                if (!change.WillWrite)
                {
                    continue;
                }

                (int Count, DateTimeOffset? Min, DateTimeOffset? Max, int Odd) current =
                    byField.TryGetValue(field.Which, out var existing) ? existing : (0, null, null, 0);

                DateTimeOffset? after = change.AfterDate;

                byField[field.Which] = (
                    current.Count + 1,
                    Min(current.Min, after),
                    Max(current.Max, after),
                    current.Odd + (change.Status == ChangeStatus.Suspicious ? 1 : 0));
            }
        }

        List<SummaryLine> lines = [.. byField
            .OrderBy(kv => (int)kv.Key)
            .Select(kv => new SummaryLine(kv.Key, kv.Value.Count, kv.Value.Min, kv.Value.Max, kv.Value.Odd))];

        List<BlockedLine> blockedLines = [.. blockedByField
            .OrderBy(kv => (int)kv.Key)
            .Select(kv => new BlockedLine(kv.Key, kv.Value.Count, PlanRowViewModel.Describe(kv.Value.Reason)))];

        // Only the three Explorer puts side by side. Listing the fourth, or every unticked
        // field in the catalogue, would bury the real warnings under things nobody wanted.
        DateField[] untouched = targets is null
            ? []
            : [.. new[] { DateField.FileCreated, DateField.FileModified, DateField.FileAccessed }
                .Where(f => !targets.Contains(f))];

        return new ChangeSummary(lines, rows.Count, changing, blocked, suspicious, included)
        {
            FilesToWrite = toWrite,
            BlockedLines = blockedLines,
            UntouchedFileDates = untouched,
            TypeFilter = typeFilter,
            FilesHiddenByTypeFilter = Math.Max(0, totalBeforeFilter - rows.Count),
        };
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
