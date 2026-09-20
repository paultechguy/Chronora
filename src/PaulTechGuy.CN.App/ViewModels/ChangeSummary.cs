// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.App.ViewModels;

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

            string a = from.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
            string b = to.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

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
        : string.Create(CultureInfo.CurrentCulture, $"Apply to {this.FilesToWrite:N0} of {this.FilesTotal:N0} files");

    public static ChangeSummary Build(IReadOnlyList<PlanRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return Empty;
        }

        var byField = new Dictionary<DateField, (int Count, DateTimeOffset? Min, DateTimeOffset? Max, int Odd)>();

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
                if (!change.WillWrite || change.Target is not ChangeTarget.Field field)
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

        return new ChangeSummary(lines, rows.Count, changing, blocked, suspicious, included)
        {
            FilesToWrite = toWrite,
        };
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
