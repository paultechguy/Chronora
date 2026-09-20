// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Services;
using PaulTechGuy.CN.FileSystem;
using PaulTechGuy.CN.Abstractions;
using PaulTechGuy.CN.Journal;
using PaulTechGuy.CN.Metadata;
using PaulTechGuy.CN.Rules;

namespace PaulTechGuy.CN.App.ViewModels;

/// <summary>
/// What the user came here to do. The first and only question until it is answered.
///
/// This replaced a pair of controls that could contradict each other: a File dates / Photo
/// dates switch in the title bar, and a separate "where does this need to look right"
/// preset. Picking Google Photos set the switch, but moving the switch did not clear the
/// preset, so the app could sit there in File dates mode insisting that Google Photos reads
/// the Taken date. One control cannot disagree with itself.
/// </summary>
public enum WorkIntent
{
    /// <summary>Not yet answered. The rest of the options stay hidden.</summary>
    None,

    /// <summary>Created and Modified. No photo machinery anywhere on screen.</summary>
    FileDates,

    /// <summary>The date the photo records. What Google Photos actually reads.</summary>
    PhotoDates,

    /// <summary>
    /// Pick the fields by hand. Also where you land by editing the checkboxes, so the
    /// label never claims an intent the ticked fields no longer match.
    ///
    /// It starts with the photo date AND the file dates already ticked, because wanting
    /// both is the usual reason to come here - if you wanted only one, one of the first
    /// two answers said so. That keeps "both" a single click without giving it a top-level
    /// option that explains nothing.
    /// </summary>
    Custom,
}

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
public sealed partial class WorkbenchViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Long enough to absorb typing, short enough to feel live. The recompute itself is
    /// ~2 ms for 50,000 rows, so this is about not thrashing rather than about cost.
    /// </summary>
    private static readonly TimeSpan RecomputeDebounce = TimeSpan.FromMilliseconds(120);

    private static readonly string AppVersion =
        typeof(WorkbenchViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private readonly FileScanner _scanner;
    private readonly RuleEvaluator _evaluator;
    private readonly ApplyService _apply;
    private readonly SqliteJournal _journal;
    private readonly ExifToolService _exifTool;
    private readonly IAppPaths _paths;
    private readonly ILogger<WorkbenchViewModel> _logger;

    private readonly List<PlanRowViewModel> _allRows = [];
    private readonly List<string> _roots = [];

    /// <summary>
    /// Guards the reconcile loop: choosing an intent ticks boxes, and a ticked box would
    /// otherwise reconcile the intent straight back to Custom.
    /// </summary>
    private bool _applyingIntent;

    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _run;

    public WorkbenchViewModel(
        FileScanner scanner,
        RuleEvaluator evaluator,
        ApplyService apply,
        SqliteJournal journal,
        ExifToolService exifTool,
        IAppPaths paths,
        ILogger<WorkbenchViewModel> logger)
    {
        this._scanner = scanner;
        this._evaluator = evaluator;
        this._apply = apply;
        this._journal = journal;
        this._exifTool = exifTool;
        this._paths = paths;
        this._logger = logger;

        this.AbsoluteDate = DateTimeOffset.Now.Date;
        this.AbsoluteTime = new TimeSpan(12, 0, 0);
        this.Summary = ChangeSummary.Empty;
        this.Rows = [];
        this.RefreshHistory();
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

    // ---- Intent -----------------------------------------------------------------------

    /// <summary>
    /// What the user said they came to do. Nothing else is shown until this is answered.
    ///
    /// Note what it does NOT constrain: where a date is read FROM. "Copy the photo's taken
    /// date onto the file dates" writes only file dates, so it belongs under File dates,
    /// which is where people look for it.
    /// </summary>
    [ObservableProperty]
    public partial WorkIntent Intent { get; set; } = WorkIntent.None;

    /// <summary>
    /// The gate. Until an intent is chosen the pane shows one question and nothing else,
    /// so the first thing anyone meets is the decision that shapes everything after it.
    /// </summary>
    public bool HasChosenIntent => this.Intent != WorkIntent.None;

    /// <summary>
    /// Whether photo targets may be written, and therefore whether they are shown.
    ///
    /// Derived from the intent rather than set independently, which is what stops the two
    /// from ever disagreeing.
    /// </summary>
    public bool IsPhotoMode =>
        this.Intent == WorkIntent.PhotoDates
        || (this.Intent == WorkIntent.Custom && this.WriteTaken);

    /// <summary>
    /// Whether the file-date fields are offered. Hidden in the photo-only path, because
    /// that path is meant to contain no file machinery at all.
    /// </summary>
    public bool ShowsFileDates => this.Intent is WorkIntent.FileDates or WorkIntent.Custom;

    /// <summary>
    /// Custom is the only place the fourth NTFS timestamp and the access time are offered.
    /// They belong to someone who has said they want to pick fields by hand.
    /// </summary>
    public bool ShowsAdvancedFields => this.Intent == WorkIntent.Custom;

    /// <summary>
    /// The write scope the evaluator enforces. Purely a function of what is ticked, so a
    /// recipe can never target a field the UI is not offering.
    /// </summary>
    public AppMode Mode => this.WriteTaken ? AppMode.PhotoDates : AppMode.FileDates;

    /// <summary>What the chosen intent means, in the words that matter to the outcome.</summary>
    public string IntentNote => this.Intent switch
    {
        WorkIntent.FileDates =>
            "Windows Explorer sorts by these. Its “Date taken” column reads the photo instead, so this "
            + "will not change what that column shows.",
        WorkIntent.PhotoDates =>
            "Google Photos reads this when you upload, and ignores the Windows file dates entirely.",
        WorkIntent.Custom =>
            "Pick exactly the fields you want. It starts with the photo date and the file dates together, "
            + "which is what you need for it to look right both in Explorer and after an upload.",
        _ => string.Empty,
    };

    /// <summary>
    /// Applies an intent by ticking the fields it means. The boxes stay editable
    /// afterwards; editing them is what turns the label into Custom.
    /// </summary>
    [RelayCommand]
    public void ChooseIntent(WorkIntent intent)
    {
        this._applyingIntent = true;

        try
        {
            switch (intent)
            {
                case WorkIntent.FileDates:
                    this.WriteCreated = true;
                    this.WriteModified = true;
                    this.WriteTaken = false;
                    break;

                case WorkIntent.PhotoDates:
                    this.WriteCreated = false;
                    this.WriteModified = false;
                    this.WriteTaken = true;
                    this.WriteAccessed = false;
                    this.WriteChanged = false;
                    break;

                case WorkIntent.Custom:
                    // Seeded with both, because wanting both is the usual reason to come
                    // here. Everything stays editable from there.
                    this.WriteCreated = true;
                    this.WriteModified = true;
                    this.WriteTaken = true;
                    break;

                default:
                    break;
            }

            this.Intent = intent;
        }
        finally
        {
            this._applyingIntent = false;
        }

        this.NotifyIntentDerived();
        this.QueueRecompute();
    }

    /// <summary>
    /// Called after any target checkbox changes. If the ticked set no longer matches the
    /// chosen intent, the label becomes Custom rather than continuing to claim something
    /// that is no longer true.
    /// </summary>
    private void ReconcileIntent()
    {
        if (this._applyingIntent || this.Intent == WorkIntent.None)
        {
            return;
        }

        WorkIntent matched = (this.WriteCreated, this.WriteModified, this.WriteTaken, this.WriteAccessed, this.WriteChanged) switch
        {
            (true, true, false, false, false) => WorkIntent.FileDates,
            (false, false, true, false, false) => WorkIntent.PhotoDates,
            _ => WorkIntent.Custom,
        };

        if (matched != this.Intent)
        {
            this.Intent = matched;
        }

        this.NotifyIntentDerived();
    }

    /// <summary>
    /// Every property computed from the intent or the ticked fields.
    ///
    /// All of them, in one place, because the failure mode is silent: a computed property
    /// left out of this list simply evaluates once at startup and never again, so its
    /// control stays in whatever state the empty initial intent implied. That is exactly
    /// how Created and Modified went missing from "let me pick the fields" - they were
    /// bound to a property added after this method and never added to it.
    /// </summary>
    private void NotifyIntentDerived()
    {
        this.OnPropertyChanged(nameof(this.HasChosenIntent));
        this.OnPropertyChanged(nameof(this.IsPhotoMode));
        this.OnPropertyChanged(nameof(this.ShowsFileDates));
        this.OnPropertyChanged(nameof(this.ShowsAdvancedFields));
        this.OnPropertyChanged(nameof(this.Mode));
        this.OnPropertyChanged(nameof(this.IntentNote));
    }

    // ---- Source -----------------------------------------------------------------------

    [ObservableProperty]
    public partial SourceChoice Source { get; set; } = SourceChoice.PickADate;

    partial void OnSourceChanged(SourceChoice value)
    {
        this.OnPropertyChanged(nameof(this.NeedsAbsoluteInput));
        this.OnPropertyChanged(nameof(this.NeedsShiftInput));
        this.OnPropertyChanged(nameof(this.NeedsCopyFromInput));
        this.QueueRecompute();
    }

    // Only the input the chosen source actually uses is shown. Rendering all three at once
    // made the pane taller than the window and invited people to fill in a field that was
    // going to be ignored.
    public bool NeedsAbsoluteInput => this.Source == SourceChoice.PickADate;

    public bool NeedsShiftInput => this.Source == SourceChoice.ShiftBy;

    public bool NeedsCopyFromInput => this.Source == SourceChoice.FromAnotherDate;

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

    partial void OnWriteCreatedChanged(bool value)
    {
        this.ReconcileIntent();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial bool WriteModified { get; set; } = true;

    partial void OnWriteModifiedChanged(bool value)
    {
        this.ReconcileIntent();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial bool WriteAccessed { get; set; }

    partial void OnWriteAccessedChanged(bool value)
    {
        this.ReconcileIntent();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial bool WriteChanged { get; set; }

    partial void OnWriteChangedChanged(bool value)
    {
        this.ReconcileIntent();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial bool WriteTaken { get; set; }

    partial void OnWriteTakenChanged(bool value)
    {
        this.ReconcileIntent();
        this.QueueRecompute();
    }

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


    // ---- Scanning ---------------------------------------------------------------------

    /// <summary>
    /// Reads a folder once. Everything the preview needs is captured here and then sealed;
    /// nothing below re-reads the disk.
    /// </summary>
    public async Task AddFolderAsync(string folder, ScanFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);

        if (!this._roots.Contains(folder, StringComparer.OrdinalIgnoreCase))


        {


            this._roots.Add(folder);


        }



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

    // ---- ExifTool ---------------------------------------------------------------------

    /// <summary>
    /// What the engine can do right now, revalidated rather than remembered. A copy the
    /// user manages can be upgraded or uninstalled between sessions.
    /// </summary>
    [ObservableProperty]
    public partial EngineStatus EngineStatus { get; set; } = Metadata.EngineStatus.NotConfigured;

    partial void OnEngineStatusChanged(EngineStatus value)
    {
        this.OnPropertyChanged(nameof(this.NeedsExifTool));
        this.OnPropertyChanged(nameof(this.EngineDetail));
        this.Recompute();
    }

    /// <summary>
    /// True when the current recipe wants metadata and cannot have it. Drives the one
    /// affordance that opens the consent pane - visibly unavailable rather than hidden,
    /// because hiding it would make the app look like it cannot do what it promises.
    /// </summary>
    public bool NeedsExifTool =>
        !this.EngineStatus.Available && (this.BuildRecipe().NeedsMetadataWrite || this.BuildRecipe().NeedsMetadataRead);

    public string EngineDetail => this.EngineStatus.Detail;

    /// <summary>Checks where things stand. Reads the disk; touches the network only if asked later.</summary>
    public async Task RefreshEngineAsync(CancellationToken cancellationToken = default) =>
        this.EngineStatus = await this._exifTool.RefreshAsync(this._paths.DataDirectory, cancellationToken).ConfigureAwait(true);

    /// <summary>Everything the consent pane needs to describe the choice honestly.</summary>
    public IReadOnlyList<ExifToolCandidate> FindExistingExifTool() => this._exifTool.FindExisting();

    public Task<ExifToolManifest?> GetExifToolOfferAsync(CancellationToken cancellationToken = default) =>
        this._exifTool.GetOfferAsync(cancellationToken);

    public async Task<EngineStatus> UseExistingExifToolAsync(string path, CancellationToken cancellationToken = default)
    {
        this.EngineStatus = await this._exifTool
            .UseExistingAsync(path, this._paths.DataDirectory, cancellationToken)
            .ConfigureAwait(true);

        return this.EngineStatus;
    }

    public async Task<EngineStatus> InstallExifToolAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        this.EngineStatus = await this._exifTool
            .InstallAsync(this._paths.DataDirectory, progress, cancellationToken)
            .ConfigureAwait(true);

        return this.EngineStatus;
    }

    public async Task<EngineStatus> RepairExifToolAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        this.EngineStatus = await this._exifTool
            .RepairAsync(this._paths.DataDirectory, progress, cancellationToken)
            .ConfigureAwait(true);

        return this.EngineStatus;
    }

    public void RemoveExifTool()
    {
        this._exifTool.Remove(this._paths.DataDirectory);
        this.EngineStatus = this._exifTool.Status;
    }

    // ---- Reversible actions -----------------------------------------------------------

    /// <summary>
    /// What the last list-changing action did, with a way back.
    ///
    /// One notice rather than one per action: a drop and a start-over cannot both have
    /// just happened, and two bars offering different undos at once would be a way to
    /// press the wrong one.
    /// </summary>
    [ObservableProperty]
    public partial string? ActionNotice { get; set; }

    partial void OnActionNoticeChanged(string? value) => this.OnPropertyChanged(nameof(this.HasActionNotice));

    public bool HasActionNotice => this.ActionNotice is not null;

    /// <summary>How to put back whatever the notice is describing.</summary>
    private Action? _undoLastAction;

    /// <summary>
    /// Whether "replace the list instead" means anything.
    ///
    /// It only does when the drop landed on top of something. Dropping into an empty list
    /// and then replacing that list with what was just dropped is the same list, so the
    /// button would be an offer to do nothing. Undo still means something - back to empty -
    /// so only this one is hidden.
    /// </summary>
    [ObservableProperty]
    public partial bool CanReplaceWithDrop { get; set; }

    /// <summary>
    /// A quiet suggestion when the dropped content contradicts the chosen intent. Never
    /// acted on automatically: the options are what the user asked for, and rewriting them
    /// because of what they dragged in would be the app overruling them.
    /// </summary>
    [ObservableProperty]
    public partial string? IntentNudge { get; set; }

    partial void OnIntentNudgeChanged(string? value) => this.OnPropertyChanged(nameof(this.HasIntentNudge));

    public bool HasIntentNudge => this.IntentNudge is not null;

    /// <summary>Names the switch rather than saying "OK", so the button states its own effect.</summary>
    public string NudgeActionLabel => this._nudgeTarget switch
    {
        WorkIntent.FileDates => "Switch to file dates",
        WorkIntent.PhotoDates => "Switch to photo dates",
        _ => "Switch",
    };

    private WorkIntent _nudgeTarget = WorkIntent.None;
    private List<PlanRowViewModel> _rowsBeforeDrop = [];
    private List<PlanRowViewModel> _rowsFromDrop = [];

    /// <summary>
    /// Adds whatever was dropped: folders, individual files, or a mix of both.
    ///
    /// It ADDS rather than replaces, because someone dragging a second folder is almost
    /// always gathering rather than starting over. Both alternatives stay one click away
    /// on the notice bar, so the guess costs nothing if it is wrong.
    /// </summary>
    public async Task AddDroppedAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return;
        }

        this._rowsBeforeDrop = [.. this._allRows];
        this._rowsFromDrop = [];
        this.CanReplaceWithDrop = this._rowsBeforeDrop.Count > 0;

        this.IsScanning = true;
        this.ScanStatus = "Reading dropped items…";

        try
        {
            await foreach (ScannedFile file in this._scanner.ScanPathsAsync(paths, ScanFilter.Default, cancellationToken))
            {
                var row = new PlanRowViewModel(file);
                this._allRows.Add(row);
                this._rowsFromDrop.Add(row);
            }

            foreach (string path in paths.Where(Directory.Exists))
            {
                if (!this._roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    this._roots.Add(path);
                }
            }

            string what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar)) : $"{paths.Count} items";

            List<PlanRowViewModel> before = [.. this._rowsBeforeDrop];



            this.ActionNotice = string.Create(
                CultureInfo.CurrentCulture,
                $"Added {this._rowsFromDrop.Count:N0} file{(this._rowsFromDrop.Count == 1 ? string.Empty : "s")} from {what}.");



            this._undoLastAction = () =>


            {


                this._allRows.Clear();


                this._allRows.AddRange(before);


            };

            this.ScanStatus = this.ActionNotice;
            this.Recompute();
            this.CheckIntentAgainstContent();
        }
        catch (OperationCanceledException)
        {
            this.ScanStatus = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this._logger.LogError(ex, "Could not read the dropped items.");
            this.ScanStatus = $"Could not read what was dropped: {ex.Message}";
        }
        finally
        {
            this.IsScanning = false;
        }
    }

    /// <summary>Reverses whatever the notice is describing.</summary>
    [RelayCommand]
    public void UndoLastAction()
    {
        Action? undo = this._undoLastAction;
        this.DismissActionNotice();

        undo?.Invoke();
        this.Recompute();
    }

    /// <summary>Keeps only what the last drop brought in.</summary>
    [RelayCommand]
    public void ReplaceWithDrop()
    {
        List<PlanRowViewModel> kept = [.. this._rowsFromDrop];

        this._allRows.Clear();
        this._allRows.AddRange(kept);
        this.DismissActionNotice();
        this.Recompute();
    }

    [RelayCommand]
    public void DismissActionNotice()
    {
        this.ActionNotice = null;
        this.CanReplaceWithDrop = false;
        this._undoLastAction = null;
        this._rowsBeforeDrop = [];
        this._rowsFromDrop = [];
    }

    // ---- Clearing and starting over ---------------------------------------------------

    /// <summary>Whether there is a list at all, which is what Clear needs to mean anything.</summary>
    public bool HasAnyFiles => this._allRows.Count > 0;

    /// <summary>
    /// Whether anything would actually change. Covers the view state as well as the list,
    /// because a stale filter is precisely the thing you cannot see the cause of.
    /// </summary>
    public bool CanStartOver =>
        this.HasAnyFiles
        || this.Intent != WorkIntent.None
        || this.Sort != SortChoice.Name
        || this.ShowOnlyChanging
        || this.ShowOnlyProblems;

    /// <summary>
    /// Back to the opening question: no files, no filters, no intent.
    ///
    /// It is undoable rather than confirmed. Nothing has been written to disk either way,
    /// so a dialog would be heavier than the action deserves - but a carefully built custom
    /// field set is worth a few seconds of grace.
    /// </summary>
    [RelayCommand]
    public void StartOver()
    {
        List<PlanRowViewModel> rows = [.. this._allRows];
        List<string> roots = [.. this._roots];
        WorkIntent intent = this.Intent;
        SourceChoice source = this.Source;
        SortChoice sort = this.Sort;
        bool onlyChanging = this.ShowOnlyChanging;
        bool onlyProblems = this.ShowOnlyProblems;
        (bool created, bool modified, bool accessed, bool changed, bool taken) =
            (this.WriteCreated, this.WriteModified, this.WriteAccessed, this.WriteChanged, this.WriteTaken);

        this._allRows.Clear();
        this._roots.Clear();
        this.SelectedRow = null;
        this.ScanStatus = string.Empty;
        this.DismissNudge();

        this._applyingIntent = true;

        try
        {
            this.Intent = WorkIntent.None;
            this.Source = SourceChoice.PickADate;
            this.Sort = SortChoice.Name;
            this.ShowOnlyChanging = false;
            this.ShowOnlyProblems = false;
            this.WriteCreated = true;
            this.WriteModified = true;
            this.WriteAccessed = false;
            this.WriteChanged = false;
            this.WriteTaken = false;
        }
        finally
        {
            this._applyingIntent = false;
        }

        this.NotifyIntentDerived();
        this.Recompute();

        this.ActionNotice = "Started over.";
        this._undoLastAction = () =>
        {
            this._allRows.AddRange(rows);
            this._roots.AddRange(roots);

            this._applyingIntent = true;

            try
            {
                this.Intent = intent;
                this.Source = source;
                this.Sort = sort;
                this.ShowOnlyChanging = onlyChanging;
                this.ShowOnlyProblems = onlyProblems;
                this.WriteCreated = created;
                this.WriteModified = modified;
                this.WriteAccessed = accessed;
                this.WriteChanged = changed;
                this.WriteTaken = taken;
            }
            finally
            {
                this._applyingIntent = false;
            }

            this.NotifyIntentDerived();
        };
    }

    /// <summary>Takes the suggestion the nudge offered.</summary>
    [RelayCommand]
    public void AcceptNudge()
    {
        if (this._nudgeTarget != WorkIntent.None)
        {
            this.ChooseIntent(this._nudgeTarget);
        }

        this.DismissNudge();
    }

    [RelayCommand]
    public void DismissNudge()
    {
        this.IntentNudge = null;
        this._nudgeTarget = WorkIntent.None;
    }

    /// <summary>
    /// Offers a switch when the content plainly contradicts the intent, and only then.
    ///
    /// The app knows that a folder of documents cannot receive a Taken date; staying quiet
    /// about it and letting every row come back blocked would be withholding the answer.
    /// Acting on it unasked would be overruling a deliberate choice. A sentence and a
    /// button is the honest middle.
    /// </summary>
    private void CheckIntentAgainstContent()
    {
        this.DismissNudge();

        if (this._allRows.Count == 0)
        {
            return;
        }

        int media = this._allRows.Count(r => r.File.Kind != MediaKind.Other);
        double share = (double)media / this._allRows.Count;

        if (this.Intent == WorkIntent.PhotoDates && media == 0)
        {
            this._nudgeTarget = WorkIntent.FileDates;

            this.OnPropertyChanged(nameof(this.NudgeActionLabel));
            this.IntentNudge = "None of these are photos or videos, so there is no Taken date to set.";
        }
        else if (this.Intent == WorkIntent.FileDates && share >= 0.8)
        {
            this._nudgeTarget = WorkIntent.PhotoDates;

            this.OnPropertyChanged(nameof(this.NudgeActionLabel));
            this.IntentNudge = string.Create(
                CultureInfo.CurrentCulture,
                $"{media:N0} of these are photos or videos. Google Photos reads their Taken date, not the file dates.");
        }
    }

    /// <summary>
    /// Empties the list and nothing else. The options stay, because clearing the files is
    /// not a statement about what you wanted to do to them.
    /// </summary>
    [RelayCommand]
    public void ClearList()
    {
        if (!this.HasAnyFiles)
        {
            return;
        }

        List<PlanRowViewModel> rows = [.. this._allRows];
        List<string> roots = [.. this._roots];

        this._allRows.Clear();
        this._roots.Clear();
        this.SelectedRow = null;
        this.ScanStatus = string.Empty;
        this.DismissNudge();
        this.Recompute();

        this.ActionNotice = string.Create(
            CultureInfo.CurrentCulture,
            $"Cleared {rows.Count:N0} file{(rows.Count == 1 ? string.Empty : "s")}.");

        this._undoLastAction = () =>
        {
            this._allRows.AddRange(rows);
            this._roots.AddRange(roots);
        };
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

    // ---- Applying ---------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsApplying { get; set; }

    [ObservableProperty]
    public partial double ApplyProgressPercent { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<JournalRun> History { get; set; } = [];

    /// <summary>True when something has been applied and can still be put back.</summary>
    public bool CanUndo => this.History.Any(r =>
        r.Kind == RunKind.Apply && r.Status is RunStatus.Completed or RunStatus.PartiallyReverted);

    /// <summary>
    /// Writes the ticked rows that actually change something.
    ///
    /// The two conditions are separate on purpose: a ticked row with nothing to do is not
    /// an error, and an unticked row that would change is not written. The button says
    /// "Apply to N of M" so that distinction is visible rather than remembered.
    /// </summary>
    [RelayCommand]
    public async Task ApplyAsync()
    {
        List<FilePlan> plans = [.. this._allRows
            .Where(r => r.IsIncluded && r.Plan is { } p && p.WillWrite)
            .Select(r => r.Plan!)];

        if (plans.Count == 0)
        {
            this.ScanStatus = "Nothing to apply.";
            return;
        }

        this._run?.Cancel();
        this._run?.Dispose();
        this._run = new CancellationTokenSource();

        this.IsApplying = true;
        this.ApplyProgressPercent = 0;

        var progress = new Progress<ApplyProgress>(p =>
        {
            this.ApplyProgressPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            this.ScanStatus = string.Create(
                CultureInfo.CurrentCulture,
                $"Writing {p.Done:N0} of {p.Total:N0}… {p.Written:N0} changed, {p.Failed:N0} failed");
        });

        try
        {
            var header = new RunHeader(
                RunKind.Apply,
                AppVersion,
                ExifToolVersion: null,
                TimeZoneInfo.Local.Id,
                DescribeRecipe(),
                this._roots);

            ApplyOutcome outcome = await this._apply.ApplyAsync(plans, header, progress, this._run.Token);

            this.ScanStatus = string.Create(
                CultureInfo.CurrentCulture,
                $"Done. {outcome.Written:N0} changed, {outcome.Failed:N0} failed, {outcome.Skipped:N0} skipped.");

            // The files on disk have moved on, so the snapshot the preview was built from
            // is now stale. Re-reading is the honest thing to do rather than leaving the
            // grid showing a plan that has already happened.
            await this.RescanAsync();
        }
        catch (OperationCanceledException)
        {
            this.ScanStatus = "Cancelled.";
        }
        finally
        {
            this.IsApplying = false;
            this.ApplyProgressPercent = 0;
            this.RefreshHistory();
        }
    }

    [RelayCommand]
    public void CancelRun() => this._run?.Cancel();

    /// <summary>Puts the most recent apply back, skipping anything that has since changed.</summary>
    [RelayCommand]
    public async Task UndoLastAsync()
    {
        JournalRun? last = this.History.FirstOrDefault(r =>
            r.Kind == RunKind.Apply && r.Status is RunStatus.Completed or RunStatus.PartiallyReverted);

        if (last is null)
        {
            this.ScanStatus = "There is nothing to undo.";
            return;
        }

        await this.RevertAsync(last.RunId, force: false);
    }

    /// <summary>Puts one run back. Force overrides the drift check, and is never the default.</summary>
    public async Task RevertAsync(long runId, bool force)
    {
        this.IsApplying = true;

        var progress = new Progress<ApplyProgress>(p =>
        {
            this.ApplyProgressPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Undoing {p.Done:N0} of {p.Total:N0}…");
        });

        try
        {
            var header = new RunHeader(
                RunKind.Revert,
                AppVersion,
                ExifToolVersion: null,
                TimeZoneInfo.Local.Id,
                $"Undo of run {runId}",
                this._roots,
                runId);

            ApplyOutcome outcome = await this._apply.RevertAsync(runId, header, force, progress, CancellationToken.None);

            this.ScanStatus = outcome.Failed == 0
                ? string.Create(CultureInfo.CurrentCulture, $"Undone. {outcome.Written:N0} files put back.")
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"Undone. {outcome.Written:N0} put back, {outcome.Failed:N0} left alone because they changed since.");

            await this.RescanAsync();
        }
        finally
        {
            this.IsApplying = false;
            this.ApplyProgressPercent = 0;
            this.RefreshHistory();
        }
    }

    public void RefreshHistory()
    {
        this.History = this._journal.ListRuns(50);
        this.OnPropertyChanged(nameof(this.CanUndo));
    }

    /// <summary>
    /// Re-reads the folders after a write, because the snapshot the preview was built from
    /// describes a state that no longer exists.
    /// </summary>
    private async Task RescanAsync()
    {
        List<string> roots = [.. this._roots];
        if (roots.Count == 0)
        {
            return;
        }

        this._allRows.Clear();

        foreach (string root in roots)
        {
            await this.AddFolderAsync(root, ScanFilter.Default);
        }
    }

    private string DescribeRecipe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{{\"intent\":\"{this.Intent}\",\"source\":\"{this.Source}\",\"mode\":\"{this.Mode}\"}}");

    /// <summary>
    /// Cancels anything in flight. The debounce and the run each own a token source, and a
    /// run that is still writing when the window closes has to be told to stop rather than
    /// left to finish against a disposed journal.
    /// </summary>
    public void Dispose()
    {
        this._debounce?.Cancel();
        this._debounce?.Dispose();
        this._debounce = null;

        this._run?.Cancel();
        this._run?.Dispose();
        this._run = null;
    }

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

        // The real state, not an assumption. A machine-wide ExifTool can be upgraded or
        // uninstalled between sessions, so the preview reflects whatever the last
        // validation found rather than what was true when the app started.
        var context = new EvaluationContext(
            ClockContext.Local,
            DateTimeOffset.Now,
            this._exifTool.Status.Available);

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
        this.OnPropertyChanged(nameof(this.HasAnyFiles));
        this.OnPropertyChanged(nameof(this.CanStartOver));
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

        // Taken stands alone perfectly well, and for Google Photos it is the ONLY correct
        // choice: the upload reads the photo's Taken date and ignores file dates entirely.
        // Nothing here requires Created or Modified to be ticked alongside it.
        if (this.WriteTaken && this.IsPhotoMode)
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

