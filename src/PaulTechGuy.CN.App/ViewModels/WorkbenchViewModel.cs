// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.FileSystem;
using PaulTechGuy.CN.Rules;

namespace PaulTechGuy.CN.App.ViewModels;

/// <summary>Where the date comes from, as the options pane offers it.</summary>
public enum SourceChoice
{
    PickADate,
    ShiftBy,
    FromAnotherDate,
    FromFileName,
}

/// <summary>How the grid is ordered.</summary>
public enum SortChoice
{
    Name,

    /// <summary>
    /// Biggest move first. This is the highest-value control in the preview: "find the one
    /// that is wrong in 5,000 rows" becomes one click, because a 1904 date sorts to the top.
    /// </summary>
    BiggestChange,

    ResultingDate,
    Status,
}

/// <summary>
/// The workbench.
///
/// The rule the whole design rests on: an option change must never re-read the disk. The
/// scan produces a sealed snapshot; every recompute after that is a pure function over it.
/// </summary>
public sealed partial class WorkbenchViewModel : ObservableObject
{
    /// <summary>
    /// Long enough to absorb typing, short enough to feel live. The recompute itself is
    /// ~2 ms for 50,000 rows, so this is about not thrashing rather than about cost.
    /// </summary>
    private static readonly TimeSpan RecomputeDebounce = TimeSpan.FromMilliseconds(120);

    private readonly FileScanner _scanner;
    private readonly RuleEvaluator _evaluator;
    private readonly ILogger<WorkbenchViewModel> _logger;

    private readonly List<PlanRowViewModel> _allRows = [];
    private CancellationTokenSource? _debounce;

    public WorkbenchViewModel(FileScanner scanner, RuleEvaluator evaluator, ILogger<WorkbenchViewModel> logger)
    {
        this._scanner = scanner;
        this._evaluator = evaluator;
        this._logger = logger;

        this.AbsoluteDate = DateTimeOffset.Now.Date;
        this.AbsoluteTime = new TimeSpan(12, 0, 0);
        this.Summary = ChangeSummary.Empty;
        this.Rows = [];

        this.ApplyDestination(DestinationChoice.Explorer);
    }

    /// <summary>Rows as the grid shows them: filtered and sorted.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlanRowViewModel> Rows { get; set; }

    [ObservableProperty]
    public partial ChangeSummary Summary { get; set; }

    [ObservableProperty]
    public partial PlanRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string ScanStatus { get; set; } = string.Empty;

    // ---- Mode -------------------------------------------------------------------------

    /// <summary>
    /// Which fields may be WRITTEN. It deliberately does not constrain what may be read:
    /// "copy the photo's Taken date onto the file dates" writes only file dates, so it
    /// belongs in the simple mode, which is where people look for it.
    /// </summary>
    [ObservableProperty]
    public partial AppMode Mode { get; set; } = AppMode.FileDates;

    partial void OnModeChanged(AppMode value) => this.QueueRecompute();

    // ---- Source -----------------------------------------------------------------------

    [ObservableProperty]
    public partial SourceChoice Source { get; set; } = SourceChoice.PickADate;

    partial void OnSourceChanged(SourceChoice value) => this.QueueRecompute();

    [ObservableProperty]
    public partial DateTimeOffset AbsoluteDate { get; set; }

    partial void OnAbsoluteDateChanged(DateTimeOffset value) => this.QueueRecompute();

    [ObservableProperty]
    public partial TimeSpan AbsoluteTime { get; set; }

    partial void OnAbsoluteTimeChanged(TimeSpan value) => this.QueueRecompute();

    [ObservableProperty]
    public partial double ShiftHours { get; set; }

    partial void OnShiftHoursChanged(double value) => this.QueueRecompute();

    /// <summary>
    /// The field a copy reads from. Includes metadata fields even in File dates mode, which
    /// is the whole point of filtering by target rather than by source.
    /// </summary>
    [ObservableProperty]
    public partial DateField CopyFromField { get; set; } = DateField.FileModified;

    partial void OnCopyFromFieldChanged(DateField value) => this.QueueRecompute();

    // ---- Targets ----------------------------------------------------------------------

    [ObservableProperty]
    public partial bool WriteCreated { get; set; } = true;

    partial void OnWriteCreatedChanged(bool value) => this.QueueRecompute();

    [ObservableProperty]
    public partial bool WriteModified { get; set; } = true;

    partial void OnWriteModifiedChanged(bool value) => this.QueueRecompute();

    [ObservableProperty]
    public partial bool WriteAccessed { get; set; }

    partial void OnWriteAccessedChanged(bool value) => this.QueueRecompute();

    [ObservableProperty]
    public partial bool WriteChanged { get; set; }

    partial void OnWriteChangedChanged(bool value) => this.QueueRecompute();

    [ObservableProperty]
    public partial bool WriteTaken { get; set; }

    partial void OnWriteTakenChanged(bool value) => this.QueueRecompute();

    // ---- Sorting ----------------------------------------------------------------------

    [ObservableProperty]
    public partial SortChoice Sort { get; set; } = SortChoice.Name;

    partial void OnSortChanged(SortChoice value) => this.Reproject();

    [ObservableProperty]
    public partial bool ShowOnlyChanging { get; set; }

    partial void OnShowOnlyChangingChanged(bool value) => this.Reproject();

    [ObservableProperty]
    public partial bool ShowOnlyProblems { get; set; }

    partial void OnShowOnlyProblemsChanged(bool value) => this.Reproject();

    // ---- Destination presets ----------------------------------------------------------

    /// <summary>
    /// Pre-ticks the fields a given destination actually reads, and says why.
    ///
    /// Without this the app walks into its own worst failure: someone ticks Created and
    /// Modified because those are the familiar words, runs 4,000 files, achieves nothing for
    /// their actual goal, and the preview reports a perfect success. Google Photos reads the
    /// photo's Taken date on upload and ignores file dates entirely.
    /// </summary>
    [RelayCommand]
    public void ApplyDestination(DestinationChoice destination)
    {
        switch (destination)
        {
            case DestinationChoice.GooglePhotos:
                this.Mode = AppMode.PhotoDates;
                this.WriteTaken = true;
                this.WriteCreated = false;
                this.WriteModified = false;
                this.DestinationNote = "Google Photos reads the photo's Taken date when you upload. File dates are ignored.";
                break;

            case DestinationChoice.Everywhere:
                this.Mode = AppMode.PhotoDates;
                this.WriteTaken = true;
                this.WriteCreated = true;
                this.WriteModified = true;
                this.DestinationNote = "Sets both the photo's Taken date and the Windows file dates.";
                break;

            default:
                this.Mode = AppMode.FileDates;
                this.WriteTaken = false;
                this.WriteCreated = true;
                this.WriteModified = true;
                this.DestinationNote = "Windows Explorer sorts by the file dates. Its “Date taken” column reads the photo instead.";
                break;
        }

        this.Destination = destination;
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial DestinationChoice Destination { get; set; } = DestinationChoice.Explorer;

    [ObservableProperty]
    public partial string DestinationNote { get; set; } = string.Empty;

    // ---- Scanning ---------------------------------------------------------------------

    /// <summary>
    /// Reads a folder once. Everything the preview needs is captured here and then sealed;
    /// nothing below re-reads the disk.
    /// </summary>
    public async Task AddFolderAsync(string folder, ScanFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        this.IsScanning = true;
        this.ScanStatus = $"Reading {folder}…";

        try
        {
            int added = 0;

            await foreach (ScannedFile file in this._scanner.ScanAsync(folder, filter, cancellationToken))
            {
                this._allRows.Add(new PlanRowViewModel(file));
                added++;

                // The grid fills as the scan runs rather than after it, so a big folder
                // shows progress instead of an empty window.
                if (added % 500 == 0)
                {
                    this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Read {added:N0} files…");
                    this.Recompute();
                }
            }

            this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Read {added:N0} files from {folder}.");
            this.Recompute();
        }
        catch (OperationCanceledException)
        {
            this.ScanStatus = "Scan cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this._logger.LogError(ex, "Could not scan {Folder}.", folder);
            this.ScanStatus = $"Could not read {folder}: {ex.Message}";
        }
        finally
        {
            this.IsScanning = false;
        }
    }

    [RelayCommand]
    public void Clear()
    {
        this._allRows.Clear();
        this.SelectedRow = null;
        this.ScanStatus = string.Empty;
        this.Recompute();
    }

    [RelayCommand]
    public void SelectAllShown()
    {
        foreach (PlanRowViewModel row in this.Rows)
        {
            row.IsIncluded = true;
        }

        this.RefreshSummary();
    }

    [RelayCommand]
    public void SelectNone()
    {
        foreach (PlanRowViewModel row in this._allRows)
        {
            row.IsIncluded = false;
        }

        this.RefreshSummary();
    }

    /// <summary>Called by the view when a checkbox changes, so the Apply count keeps up.</summary>
    public void RefreshSummary() => this.Summary = ChangeSummary.Build(this._allRows);

    // ---- Recompute --------------------------------------------------------------------

    /// <summary>
    /// Coalesces a burst of option changes into one evaluation. Superseded work is
    /// cancelled rather than queued, so holding a spinner does not build a backlog.
    /// </summary>
    private void QueueRecompute()
    {
        this._debounce?.Cancel();
        this._debounce?.Dispose();

        var cts = new CancellationTokenSource();
        this._debounce = cts;

        _ = Task.Delay(RecomputeDebounce, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    this.Recompute();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// The whole preview, recomputed. Pure: it reads the sealed snapshot and writes only
    /// each row's Plan, which the template formats lazily.
    /// </summary>
    public void Recompute()
    {
        Recipe recipe = this.BuildRecipe();
        var context = new EvaluationContext(ClockContext.Local, DateTimeOffset.Now, MetadataEngineAvailable: false);

        foreach (PlanRowViewModel row in this._allRows)
        {
            row.Plan = this._evaluator.Evaluate(row.File, recipe, context);
        }

        this.Reproject();
    }

    /// <summary>Applies the current filter and sort. Cheap: no evaluation happens here.</summary>
    private void Reproject()
    {
        IEnumerable<PlanRowViewModel> query = this._allRows;

        if (this.ShowOnlyChanging)
        {
            query = query.Where(r => r.Plan?.WillWrite == true);
        }

        if (this.ShowOnlyProblems)
        {
            query = query.Where(r => r.Plan?.HasProblem == true || r.Plan?.IsSuspicious == true);
        }

        query = this.Sort switch
        {
            SortChoice.BiggestChange => query.OrderByDescending(r => r.SortDeltaTicks).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            SortChoice.ResultingDate => query.OrderBy(r => r.SortAfterDate ?? DateTimeOffset.MaxValue).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            SortChoice.Status => query.OrderByDescending(r => (int)r.SortStatus).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        this.Rows = [.. query];
        this.RefreshSummary();
    }

    private Recipe BuildRecipe()
    {
        var targets = new HashSet<DateField>();

        if (this.WriteCreated)
        {
            _ = targets.Add(DateField.FileCreated);
        }

        if (this.WriteModified)
        {
            _ = targets.Add(DateField.FileModified);
        }

        if (this.WriteAccessed)
        {
            _ = targets.Add(DateField.FileAccessed);
        }

        if (this.WriteChanged)
        {
            _ = targets.Add(DateField.FileChanged);
        }

        if (this.WriteTaken && this.Mode == AppMode.PhotoDates)
        {
            _ = targets.Add(DateField.ExifDateTimeOriginal);
        }

        DateSource source = this.Source switch
        {
            SourceChoice.ShiftBy => new DateSource.Shift(TimeSpan.FromHours(this.ShiftHours), ShiftBasis.WallClock),
            SourceChoice.FromAnotherDate => new DateSource.CopyFrom(Aggregate.FirstPresent, [this.CopyFromField]),
            SourceChoice.FromFileName => new DateSource.FromFileName(string.Empty),
            _ => new DateSource.Absolute(new DateTimeOffset(this.AbsoluteDate.Date.Add(this.AbsoluteTime), DateTimeOffset.Now.Offset)),
        };

        return new Recipe(
            [new DateRule(source, targets, RuleGuards.None)],
            ScanFilter.Default,
            this.Mode);
    }
}

/// <summary>Where the corrected dates need to look right.</summary>
public enum DestinationChoice
{
    Explorer,
    GooglePhotos,
    Everywhere,
}
