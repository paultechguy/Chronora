// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Presentation;

/// <summary>
/// One row of the grid.
///
/// Recompute sets <see cref="Plan" /> and nothing else. Every string the row displays is
/// produced by a static formatter called from x:Bind, so a re-evaluation of 50,000 rows
/// allocates nothing and only the dozen realised rows ever format anything. The milestone 1
/// spike measured that at 2 ms against 30 ms for the eager alternative.
/// </summary>
public sealed partial class PlanRowViewModel : ObservableObject
{
    public PlanRowViewModel(ScannedFile file)
    {
        this.File = file;
        this.Name = file.FileName;
        this.IsIncluded = true;
    }

    /// <summary>
    /// The sealed snapshot this row was built from.
    ///
    /// Replaced exactly once, when the metadata pass catches up with the timestamp scan.
    /// The grid fills immediately from the fast filesystem read and the photo dates arrive
    /// behind it, because ExifTool is orders of magnitude slower than reading four
    /// timestamps and a blank window while it works would be the wrong trade.
    /// </summary>
    public ScannedFile File { get; private set; }

    /// <summary>
    /// Attaches the dates read from inside the file. The same file, with more known about
    /// it - so the snapshot rule still holds: this happens once, before the preview settles,
    /// and never again in response to an option changing.
    /// </summary>
    public void Enrich(FrozenDictionary<DateField, MetadataValue> metadata, bool quickTimeReadAsUtc)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        this.File = this.File with
        {
            Metadata = metadata,
            Traits = quickTimeReadAsUtc ? this.File.Traits | FileTraits.QuickTimeReadAsUtc : this.File.Traits,
        };

        this.OnPropertyChanged(nameof(this.File));
    }

    public string Name { get; }

    public string FullPath => this.File.FullPath;

    /// <summary>The only thing a recompute writes.</summary>
    [ObservableProperty]
    public partial FilePlan? Plan { get; set; }

    /// <summary>
    /// A date typed in for this file alone, overriding whatever the run would give it.
    ///
    /// In four thousand photos there are always three that need a date nobody can derive -
    /// a scan with no metadata, a file whose name lies. Without this the only way to fix
    /// those three is a separate run for each, which is how a bulk tool turns into a chore.
    /// </summary>
    [ObservableProperty]
    public partial DateTimeOffset? ManualDate { get; set; }

    public bool HasManualDate => this.ManualDate is not null;

    partial void OnManualDateChanged(DateTimeOffset? value) =>
        this.OnPropertyChanged(nameof(this.HasManualDate));

    /// <summary>
    /// Whether this row takes part in the run. The checkbox is the single selection model:
    /// the Apply button reads "Apply to N of M", so there is never a question of whether it
    /// acts on the checked rows or the changed ones.
    /// </summary>
    [ObservableProperty]
    public partial bool IsIncluded { get; set; }

    /// <summary>How far the biggest change moves, for sort-by-delta.</summary>
    public long SortDeltaTicks =>
        this.Plan?.Headline?.Delta is { } d ? Math.Abs(d.Ticks) : 0;

    public DateTimeOffset? SortAfterDate => this.Plan?.Headline?.AfterDate;

    public ChangeStatus SortStatus =>
        this.Plan is null ? ChangeStatus.Unchanged
        : this.Plan.HasProblem ? ChangeStatus.Blocked
        : this.Plan.IsSuspicious ? ChangeStatus.Suspicious
        : this.Plan.WillWrite ? ChangeStatus.WillChange
        : ChangeStatus.Unchanged;

    /// <summary>
    /// Line two, marked when the row is carrying a date of its own.
    ///
    /// A per-row override with nothing on the row to show for it is the worst kind of
    /// hidden state: two rows with identical summaries would be taking their date from
    /// different places, and the only way to tell which is which would be clicking every
    /// one. The marker goes first because it changes how the rest of the line reads.
    /// </summary>
    public static string FormatSummary(FilePlan? plan, DateTimeOffset? manual) =>
        manual is null
            ? FormatSummary(plan)
            : string.Create(CultureInfo.CurrentCulture, $"by hand · {FormatSummary(plan)}");

    /// <summary>
    /// Line two, adaptive within a fixed row height.
    ///
    /// The three cases exist because a count badge is lossy in exactly the way this app is
    /// supposed to prevent: three fields all moving to one sane date and three fields moving
    /// to three different dates, one of them 1904, would otherwise render identically.
    /// </summary>
    public static string FormatSummary(FilePlan? plan)
    {
        if (plan is null)
        {
            return "reading…";
        }

        List<PlannedChange> writes = [.. plan.Changes.Where(c => c.WillWrite)];

        if (writes.Count == 0)
        {
            // Any stated reason beats "no change". A row that will do nothing is only
            // useful if it says why it will do nothing - "no change" on a file the user
            // explicitly selected reads as the app being broken, and when the cause is a
            // missing ExifTool it is also wrong.
            PlannedChange? explained = plan.Changes.FirstOrDefault(c => c.Status == ChangeStatus.Blocked)
                ?? plan.Changes.FirstOrDefault(c => c.Problem != ProblemCode.None);

            return explained is not null ? Describe(explained.Problem) : "no change";
        }

        if (writes.Count == 1)
        {
            PlannedChange only = writes[0];
            return string.Create(
                CultureInfo.CurrentCulture,
                $"{only.Target.DisplayName}  {Stamp(only.BeforeDate)} → {Stamp(only.AfterDate)}");
        }

        List<DateTimeOffset> distinct = [.. writes.Select(w => w.AfterDate).Where(d => d.HasValue).Select(d => d!.Value).Distinct()];

        if (distinct.Count == 1)
        {
            string fields = string.Join(" · ", writes.Select(w => w.Target.DisplayName));

            // With the CURRENT value, exactly as the single-field line has it.
            //
            // It used to render only "Created · Modified → 2026-09-19", which reads as a
            // statement about the file rather than a proposal about it - reported as
            // "these rows show the wrong date" when the row was in fact showing a date the
            // file did not have yet. A preview that omits what it is changing FROM is not
            // a preview.
            List<DateTimeOffset> before = [.. writes
                .Select(w => w.BeforeDate)
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .Distinct()];

            return before.Count == 1
                ? string.Create(CultureInfo.CurrentCulture, $"{fields}  {Stamp(before[0])} → {Stamp(distinct[0])}")
                : string.Create(CultureInfo.CurrentCulture, $"{fields} → {Stamp(distinct[0])}");
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{writes.Count} fields, {distinct.Count} different dates ⚠");
    }

    /// <summary>The short status chip at the end of line one.</summary>
    public static string FormatStatus(FilePlan? plan)
    {
        if (plan is null)
        {
            return string.Empty;
        }

        if (plan.HasProblem)
        {
            PlannedChange blocked = plan.Changes.First(c => c.Status == ChangeStatus.Blocked);
            return Describe(blocked.Problem);
        }

        return plan.IsSuspicious ? "⚠ looks odd" : string.Empty;
    }

    /// <summary>The full field-by-field breakdown, for the detail pane.</summary>
    public static string FormatDetail(FilePlan? plan)
    {
        if (plan is null || plan.Changes.Count == 0)
        {
            return "Nothing to change.";
        }

        return string.Join(
            Environment.NewLine,
            plan.Changes.Select(c => string.Create(
                CultureInfo.CurrentCulture,
                $"{c.Target.DisplayName,-16} {Stamp(c.BeforeDate),-20} → {Stamp(c.AfterDate),-20} {Note(c)}")));
    }

    private static string Note(PlannedChange change) => change.Status switch
    {
        ChangeStatus.Unchanged => "(already correct)",
        ChangeStatus.Skipped => change.Problem == ProblemCode.NoSourceValue ? "(no date found)" : "(skipped)",
        ChangeStatus.Blocked => Describe(change.Problem),
        ChangeStatus.Suspicious => "⚠ " + Describe(change.Problem),
        _ => string.Empty,
    };

    private static string Stamp(DateTimeOffset? value) =>
        value is { } v ? v.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : "—";

    /// <summary>
    /// Plain language, never an enum name. A blocked row is only useful if the reason tells
    /// the user what to do about it.
    /// </summary>
    public static string Describe(ProblemCode problem) => problem switch
    {
        ProblemCode.None => string.Empty,
        ProblemCode.ReadOnly => "read-only",
        ProblemCode.AccessDenied => "access denied",
        ProblemCode.FileLocked => "in use by another program",
        ProblemCode.ChangeTimeUnsupported => "this drive cannot store the Changed date",
        ProblemCode.FieldNotWritableForFormat => "not supported for this file type",
        ProblemCode.CloudPlaceholderWouldHydrate => "still in the cloud",
        ProblemCode.NoSourceValue => "no date found",
        ProblemCode.NoDateChosen => "pick a date first",
        ProblemCode.AmbiguousPatternMatch => "two possible dates in the name",
        ProblemCode.InvalidLocalTime => "that clock time does not exist (clocks went forward)",
        ProblemCode.AmbiguousLocalTime => "that clock time happens twice (clocks went back)",
        ProblemCode.ImplausibleDate => "the result looks wrong",
        ProblemCode.MetadataEngineUnavailable => "needs ExifTool",
        _ => problem.ToString(),
    };
}
