// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.IO.Enumeration;
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
using PaulTechGuy.CN.Repositories;
using PaulTechGuy.CN.Rules;

namespace PaulTechGuy.CN.Presentation;

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

    /// <summary>The date the file records for itself. What a photo library actually reads.</summary>
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
    private readonly MetadataGateway _metadata;
    private readonly TemplateStore _templates;

    /// <summary>
    /// The same instance the evaluator holds, which is the whole reason it is injected
    /// here: a pattern registered on this object is one the evaluator can already use.
    /// </summary>
    private readonly FilenameDateParser _filenames;
    private readonly IAppPaths _paths;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<WorkbenchViewModel> _logger;

    private readonly List<PlanRowViewModel> _allRows = [];
    private readonly List<string> _roots = [];

    /// <summary>
    /// Files dropped one at a time, rather than found inside a folder.
    ///
    /// Tracked separately because a rescan re-reads the folders, and these have no folder
    /// to be re-read from. Without them the list silently lost every individually dropped
    /// file the moment a run finished - the rescan after an apply cleared the rows and put
    /// back only what the roots gave it. Quiet until the deck put a file count at the top
    /// of the window, where it became a number that visibly dropped after every run.
    /// </summary>
    private readonly List<string> _looseFiles = [];

    /// <summary>
    /// Guards the reconcile loop: choosing an intent ticks boxes, and a ticked box would
    /// otherwise reconcile the intent straight back to Custom.
    /// </summary>
    private bool _applyingIntent;

    /// <summary>
    /// Same idea for templates: using one ticks boxes, and a ticked box would otherwise be
    /// read as the user editing their way out of the template they just chose.
    /// </summary>
    private bool _applyingTemplate;

    /// <summary>
    /// Same idea again, for the three commands that tick or untick every row in a loop.
    ///
    /// Each row's change triggers a full RefreshSummary, which walks all of `_allRows` AND
    /// rebuilds the recipe — so a loop over N rows did N of those, on a list built to hold
    /// 50,000. It survived only because nobody had clicked Select all on a big enough list
    /// to notice. The commands refresh once at the end instead.
    /// </summary>
    private bool _selectingInBulk;

    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _run;

    /// <summary>
    /// The cancellation source for whatever scan is reading the disk right now.
    ///
    /// One for the WHOLE scan, not one per folder. RescanAsync loops over every root, so a
    /// source per call would let Cancel stop the folder being read and then watch the loop
    /// calmly start the next one. Whoever opens it closes it; nested calls join in.
    /// </summary>
    private CancellationTokenSource? _scan;

    public WorkbenchViewModel(
        FileScanner scanner,
        RuleEvaluator evaluator,
        ApplyService apply,
        SqliteJournal journal,
        ExifToolService exifTool,
        MetadataGateway metadata,
        TemplateStore templates,
        FilenameDateParser filenames,
        IAppPaths paths,
        IUiDispatcher dispatcher,
        ILogger<WorkbenchViewModel> logger)
    {
        this._filenames = filenames;
        this._scanner = scanner;
        this._evaluator = evaluator;
        this._apply = apply;
        this._journal = journal;
        this._exifTool = exifTool;
        this._metadata = metadata;
        this._templates = templates;
        this._paths = paths;
        this._dispatcher = dispatcher;
        this._logger = logger;

        this.AbsoluteTime = new TimeSpan(12, 0, 0);
        this.Summary = ChangeSummary.Empty;
        this.Templates = templates.All;
        this.RefreshHistory();

        // Before any settings are restored, so a host that never calls ApplySettings still
        // gets a sensible baseline rather than a null one.
        this.MarkResting();
    }

    /// <summary>
    /// Rows as the grid shows them: filtered and sorted.
    ///
    /// One instance, never replaced. See <see cref="RowCollection"/> for why that matters.
    /// </summary>
    public RowCollection Rows { get; } = [];

    [ObservableProperty]
    public partial ChangeSummary Summary { get; set; }

    [ObservableProperty]
    public partial PlanRowViewModel? SelectedRow { get; set; }

    /// <summary>
    /// Whether a row is selected, so the detail pane knows whether to hold a slot for the
    /// thumbnail. With nothing selected there is no file to show a picture of, and an empty
    /// grey square would be furniture rather than information.
    /// </summary>
    public bool HasSelection => this.SelectedRow is not null;

    partial void OnSelectedRowChanged(PlanRowViewModel? value) =>
        this.OnPropertyChanged(nameof(this.HasSelection));

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    /// <summary>
    /// Anything long enough to want a spinner and a way out.
    ///
    /// IsScanning was set on both scan paths and bound to NOTHING: the footer's ring and
    /// Cancel button both watched IsApplying only, so dropping a large tree gave a growing
    /// "Read 45,000 files…" with no way to stop it - while FileScanner had taken a
    /// cancellation token all along and checked it per entry. Exposing the recursion
    /// setting turns deep drops into something people do deliberately, which is what makes
    /// this worth fixing now rather than later.
    /// </summary>
    public bool IsBusy => this.IsApplying || this.IsScanning;

    partial void OnIsScanningChanged(bool value)
    {
        this.OnPropertyChanged(nameof(this.IsBusy));
        this.NotifyDeck();
    }

    /// <summary>
    /// What the long operation in flight is doing, on the one line in the footer.
    ///
    /// It was called ScanStatus, and the name is most of why fifty-two assignment sites
    /// accumulated on it: "the scan's status" reads like "the app's status line", so
    /// routing a toast, a mode statement or a run report here looked correct at every
    /// individual call site. It is not a status line. It is a progress meter, it is
    /// overwritten constantly and by design, and **nothing written here survives**.
    ///
    /// Anything that has to be read rather than glanced at belongs somewhere that keeps
    /// it, such as <see cref="ActionNotice"/> for an event with a way back.
    /// </summary>
    [ObservableProperty]
    public partial string ProgressStatus { get; set; } = string.Empty;

    // The deck's headline quotes this while a run is in flight, so it has to move with it.
    partial void OnProgressStatusChanged(string value)
    {
        if (this.IsApplying)
        {
            this.OnPropertyChanged(nameof(this.ResultHeadline));
        }
    }

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
    /// The intent as a list position, so the radio group can be bound both ways.
    ///
    /// The radio buttons used to report their choice through a Checked handler and read
    /// nothing back, which made them write-only: anything the view model decided for
    /// itself - a template being applied, Start over clearing the run - left them showing
    /// the previous answer while the state underneath had moved. The view model was right
    /// and the control was lying about it.
    ///
    /// -1 means nothing is chosen, which is what lets Start over actually look like
    /// starting over instead of leaving a stale answer selected above a hidden pane.
    /// </summary>
    public int IntentIndex
    {
        get => this.Intent switch
        {
            WorkIntent.FileDates => 0,
            WorkIntent.PhotoDates => 1,
            WorkIntent.Custom => 2,
            _ => -1,
        };

        set
        {
            WorkIntent chosen = value switch
            {
                0 => WorkIntent.FileDates,
                1 => WorkIntent.PhotoDates,
                2 => WorkIntent.Custom,
                _ => WorkIntent.None,
            };

            // The control echoes the value back when the binding pushes one in, so a
            // no-op set has to stay a no-op. Otherwise reconciling to Custom would bounce
            // back through ChooseIntent and stamp the default checkboxes over the edit
            // that caused it.
            if (chosen != WorkIntent.None && chosen != this.Intent)
            {
                this.ChooseIntent(chosen);
            }
        }
    }

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
            "The date a photo or video records for itself. Photo libraries read this when you "
            + "upload, and ignore the Windows file dates entirely.",
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

                    // Changed belongs to Advanced, so picking this answer always lands on
                    // the same two boxes rather than inheriting whatever the previous
                    // answer left behind.
                    this.WriteChanged = false;
                    break;

                case WorkIntent.PhotoDates:
                    this.WriteCreated = false;
                    this.WriteModified = false;
                    this.WriteTaken = true;
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

        // Accessed counts as an ordinary file date here, because Explorer shows it beside
        // Changed still forces Custom: it is an Advanced field nobody reaches by accident,
        // and reaching it IS picking fields by hand.
        bool anyFileDate = this.WriteCreated || this.WriteModified;

        WorkIntent matched = (anyFileDate, this.WriteTaken, this.WriteChanged) switch
        {
            (true, false, false) => WorkIntent.FileDates,
            (false, true, false) => WorkIntent.PhotoDates,
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

        // The radio group's own binding. Without this the control keeps whatever was last
        // clicked even after Start over cleared the intent underneath it.
        this.OnPropertyChanged(nameof(this.IntentIndex));
        this.OnPropertyChanged(nameof(this.IsPhotoMode));
        this.OnPropertyChanged(nameof(this.ShowsFileDates));
        this.OnPropertyChanged(nameof(this.ShowsAdvancedFields));
        this.OnPropertyChanged(nameof(this.Mode));
        this.OnPropertyChanged(nameof(this.IntentNote));

        // Depends on the intent AND on the engine, so it has to be raised from both
        // places. Splitting the notifications by which input changed is what let this go
        // missing twice: whoever adds the next computed property will think about only one
        // of its inputs. Everything derived is raised together, from here, always.
        this.OnPropertyChanged(nameof(this.NeedsExifTool));
        this.OnPropertyChanged(nameof(this.EngineDetail));

        // And the region that ranks NeedsExifTool against the other two, for the same
        // reason and by the same rule as the comment above.
        this.NotifyNoticeRegion();

        // Depends on the intent as well as the list, so choosing an intent has to raise
        // it. Without this the Start over button stayed disabled after picking an intent -
        // precisely the moment someone who picked the wrong one wants it. Found by the
        // notification test rather than by a person, which is the point of that test.
        this.OnPropertyChanged(nameof(this.HasAnyFiles));
        this.OnPropertyChanged(nameof(this.IsListEmpty));
        this.OnPropertyChanged(nameof(this.CanStartOver));

        // The deck restates the intent, the source and the ticked fields, so every one of
        // those edits reaches it through here. Summary alone is not enough: it is a record,
        // so an edit that leaves the counts identical raises nothing.
        this.NotifyDeck();
    }

    // ---- Templates --------------------------------------------------------------------

    /// <summary>Built-ins followed by the user's own.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DateTemplate> Templates { get; set; } = [];

    /// <summary>
    /// The template currently driving the run, or null when the options pane is.
    ///
    /// This is kept as the authoritative rule rather than being flattened into the
    /// checkboxes, because some templates say more than the pane can. "Make every date
    /// consistent" reads the EARLIEST of three fields; the pane offers one field and no
    /// aggregate. Flattening it would quietly turn it into something else that still
    /// carried its name, which is the worst of both.
    ///
    /// The pane still shows the template's targets, and the preview still shows every
    /// resulting change per file, so nothing about the run is hidden - the name and its
    /// description carry the part the controls cannot express.
    /// </summary>
    [ObservableProperty]
    public partial DateTemplate? ActiveTemplate { get; set; }

    public bool HasActiveTemplate => this.ActiveTemplate is not null;

    public string ActiveTemplateNote => this.ActiveTemplate?.Description ?? string.Empty;

    /// <summary>A built-in cannot be overwritten or deleted; it can be duplicated.</summary>
    public bool CanDeleteActiveTemplate => this.ActiveTemplate is { IsBuiltIn: false };

    /// <summary>
    /// Puts a template in charge. The checkboxes move to match its targets so the pane is
    /// never describing a different run from the one that will happen.
    /// </summary>
    [RelayCommand]
    public void UseTemplate(DateTemplate? template)
    {
        if (template is null)
        {
            return;
        }

        this._applyingTemplate = true;

        try
        {
            this.WriteCreated = template.Targets.Contains(DateField.FileCreated);
            this.WriteModified = template.Targets.Contains(DateField.FileModified);
            this.WriteChanged = template.Targets.Contains(DateField.FileChanged);
            this.WriteTaken = template.Targets.Contains(DateField.ExifDateTimeOriginal);

            switch (template.Source)
            {
                case DateSource.Absolute absolute:
                    this.Source = SourceChoice.PickADate;
                    this.AbsoluteDate = absolute.Value.Date;
                    this.AbsoluteTime = absolute.Value.TimeOfDay;
                    break;

                case DateSource.Shift shift:
                    this.Source = SourceChoice.ShiftBy;
                    this.ShiftHours = shift.Delta.TotalHours;
                    break;

                case DateSource.CopyFrom copy:
                    this.Source = SourceChoice.FromAnotherDate;

                    // The pane shows the first field. When the template names more, the
                    // template stays in charge and its description says what it really does.
                    this.CopyFromField = copy.Fields.Count > 0 ? copy.Fields[0] : DateField.FileModified;
                    break;

                case DateSource.FromFileName:
                    this.Source = SourceChoice.FromFileName;
                    break;

                default:
                    break;
            }

            this.ActiveTemplate = template;
        }
        finally
        {
            this._applyingTemplate = false;
        }

        // The ticked set has moved, so the intent label has to catch up with it.
        this.ReconcileIntent();

        // The bottom bar, not the banner. Choosing a template is an option change, and a
        // highlighted bar with an Undo button on every option change is noise that teaches
        // people to stop reading the one place the app says something urgent.
        this.Confirm(string.Create(CultureInfo.CurrentCulture, $"Using “{template.Name}”."));
        this.NotifyTemplateState();
        this.QueueRecompute();
    }

    /// <summary>
    /// Saves the current options under a name.
    /// </summary>
    /// <returns>Null on success, or a sentence explaining why not.</returns>
    public string? SaveCurrentAsTemplate(string name, string description = "")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the template a name first.";
        }

        if (BuiltInTemplates.All.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"“{name}” is the name of a template that ships with Chronora. Pick another.";
        }

        if (this._templates.IsReadOnly)
        {
            return this._templates.ReadOnlyReason;
        }

        Recipe recipe = this.BuildRecipe();
        DateRule rule = recipe.Rules[0];

        var template = new DateTemplate(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            description.Trim(),
            rule.Source,
            rule.Targets,
            rule.Guards);

        if (!this._templates.Save(template))
        {
            return "Chronora could not write the templates file.";
        }

        this.ReloadTemplates();
        this.ActiveTemplate = this.Templates.FirstOrDefault(t => t.Id == template.Id) ?? template;
        this.Confirm(string.Create(CultureInfo.CurrentCulture, $"Saved “{template.Name}”."));
        this.NotifyTemplateState();

        return null;
    }

    [RelayCommand]
    public void DeleteActiveTemplate()
    {
        if (this.ActiveTemplate is not { IsBuiltIn: false } template)
        {
            return;
        }

        _ = this._templates.Delete(template.Id);
        this.ReloadTemplates();

        this.ActiveTemplate = null;
        this.Confirm(string.Create(CultureInfo.CurrentCulture, $"Deleted “{template.Name}”."));
        this.NotifyTemplateState();
        this.QueueRecompute();
    }

    /// <summary>Copies a template so it can be edited, which is how a built-in is adapted.</summary>
    public string? DuplicateActiveTemplate(string newName)
    {
        if (this.ActiveTemplate is not { } template)
        {
            return "Choose a template first.";
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            return "Give the copy a name.";
        }

        DateTemplate copy = TemplateStore.Duplicate(template, newName.Trim());

        if (!this._templates.Save(copy))
        {
            return "Chronora could not write the templates file.";
        }

        this.ReloadTemplates();
        this.ActiveTemplate = this.Templates.FirstOrDefault(t => t.Id == copy.Id) ?? copy;
        this.Confirm(string.Create(CultureInfo.CurrentCulture, $"Copied to “{copy.Name}”."));
        this.NotifyTemplateState();

        return null;
    }

    public bool ExportActiveTemplate(string path) =>
        this.ActiveTemplate is { } template && this._templates.Export(template, path);

    public string? ImportTemplate(string path)
    {
        DateTemplate? imported = this._templates.Import(path);

        if (imported is null)
        {
            return "That file is not a Chronora template.";
        }

        if (!this._templates.Save(imported))
        {
            return "Chronora could not write the templates file.";
        }

        this.ReloadTemplates();
        this.UseTemplate(this.Templates.FirstOrDefault(t => t.Id == imported.Id));

        return null;
    }

    private void ReloadTemplates()
    {
        this._templates.Reload();
        this.Templates = this._templates.All;
    }

    /// <summary>
    /// Steps out of a template when the user edits an option it was responsible for.
    ///
    /// Announced rather than silent. The template can mean more than the pane shows, so
    /// dropping it can change the run in a way the controls do not visibly account for -
    /// and an unexplained change to what Apply will do is the one thing this app must
    /// never do.
    /// </summary>
    private void LeaveTemplateOnEdit()
    {
        if (this._applyingTemplate)
        {
            return;
        }

        // A toast offering to undo a list action is stale the moment somebody moves on to
        // configuring the run: carrying on IS accepting the list. That it never went away
        // on its own is the other half of why the banner looked like it was reacting to
        // every option change.
        //
        // DismissActionNotice, not `ActionNotice = null`. Nulling the text left
        // _undoLastAction holding the drop's way back - invisible while the only thing
        // watching was the toast's visibility, and a lie the moment the Undo BUTTON
        // started asking whether there was anything to undo. The full dismissal is what
        // this line always meant.
        this.DismissActionNotice();

        if (this.ActiveTemplate is not { } template)
        {
            return;
        }

        this.ActiveTemplate = null;

        // Said, but quietly. It still has to be said - dropping the template can change what
        // Apply does in ways the controls cannot show - but it follows an ordinary option
        // change, and a banner on every one of those is the overkill reported.
        this.Confirm(string.Create(
            CultureInfo.CurrentCulture,
            $"Stopped using “{template.Name}” because you changed the options."));

        this.NotifyTemplateState();
    }

    private void NotifyTemplateState()
    {
        this.OnPropertyChanged(nameof(this.HasActiveTemplate));
        this.OnPropertyChanged(nameof(this.ActiveTemplateNote));
        this.OnPropertyChanged(nameof(this.CanDeleteActiveTemplate));

        // Picking up or dropping a template changes WHICH sentence the deck's rule segment
        // shows, not just its words.
        this.NotifyDeck();
    }

    // ---- Source -----------------------------------------------------------------------

    [ObservableProperty]
    public partial SourceChoice Source { get; set; } = SourceChoice.PickADate;

    /// <summary>
    /// The source as a list position, bound both ways for the same reason as
    /// <see cref="IntentIndex" />: a template sets the source, and the control has to
    /// follow. Choosing "photos sort wrong in Explorer" and being left looking at
    /// "a date I pick" is the exact failure this fixes.
    /// </summary>
    public int SourceIndex
    {
        get => (int)this.Source;

        set
        {
            if (value >= 0 && (SourceChoice)value != this.Source)
            {
                this.Source = (SourceChoice)value;
            }
        }
    }

    partial void OnSourceChanged(SourceChoice value)
    {
        this.LeaveTemplateOnEdit();
        this.OnPropertyChanged(nameof(this.SourceIndex));
        this.OnPropertyChanged(nameof(this.NeedsAbsoluteInput));
        this.OnPropertyChanged(nameof(this.NeedsShiftInput));
        this.OnPropertyChanged(nameof(this.NeedsCopyFromInput));
        this.OnPropertyChanged(nameof(this.NeedsFilenameInput));
        this.NotifyIntentDerived();
        this.QueueRecompute();
    }

    // Only the input the chosen source actually uses is shown. Rendering all three at once
    // made the pane taller than the window and invited people to fill in a field that was
    // going to be ignored.
    public bool NeedsAbsoluteInput => this.Source == SourceChoice.PickADate;

    public bool NeedsShiftInput => this.Source == SourceChoice.ShiftBy;

    public bool NeedsCopyFromInput => this.Source == SourceChoice.FromAnotherDate;

    public bool NeedsFilenameInput => this.Source == SourceChoice.FromFileName;

    [ObservableProperty]
    public partial DateTimeOffset? AbsoluteDate { get; set; }

    partial void OnAbsoluteDateChanged(DateTimeOffset? value)
    {
        this.LeaveTemplateOnEdit();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial TimeSpan AbsoluteTime { get; set; }

    /// <summary>
    /// Fills in the current local date and time.
    ///
    /// Local, always, and there is deliberately no UTC option beside it. Every date in this
    /// app is entered as the wall-clock reading a person would recognise; which frame it
    /// gets STORED in is per-format and the app already decides it - an EXIF date is local
    /// with its offset in a companion tag, a QuickTime atom is UTC or local depending on
    /// what that camera does, and a filesystem time is an absolute instant. Offering a
    /// Local/UTC switch here would let somebody assert a frame that contradicts all three.
    /// </summary>
    [RelayCommand]
    public void UseNow()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        this.AbsoluteDate = now.Date;
        this.AbsoluteTime = new TimeSpan(now.Hour, now.Minute, now.Second);
    }

    partial void OnAbsoluteTimeChanged(TimeSpan value)
    {
        this.LeaveTemplateOnEdit();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial double ShiftHours { get; set; }

    partial void OnShiftHoursChanged(double value)
    {
        this.LeaveTemplateOnEdit();
        this.QueueRecompute();
    }

    /// <summary>
    /// The field a copy reads from. Includes metadata fields even in File dates mode, which
    /// is the whole point of filtering by target rather than by source.
    /// </summary>
    [ObservableProperty]
    public partial DateField CopyFromField { get; set; } = DateField.FileModified;

    /// <summary>
    /// The dates a rule can read FROM.
    ///
    /// Deliberately every genre, because reading across the boundary is the product's whole
    /// differentiator: "copy the photo's taken date onto the file dates" reads metadata and
    /// writes filesystem fields, and the mode only ever constrains what may be WRITTEN.
    ///
    /// Choosing a photo date here is what makes the ExifTool prompt appear even in the
    /// simple file-dates path, which is intended.
    /// </summary>
    public IReadOnlyList<DateFieldSpec> CopyFromOptions { get; } =
    [
        DateFieldCatalog.Get(DateField.ExifDateTimeOriginal),
        DateFieldCatalog.Get(DateField.QuickTimeCreateDate),
        DateFieldCatalog.Get(DateField.FileCreated),
        DateFieldCatalog.Get(DateField.FileModified),
        DateFieldCatalog.Get(DateField.FileAccessed),
    ];

    /// <summary>
    /// Which of those is chosen, as a list position so the control can be bound both ways.
    ///
    /// There was no control at all until now: picking "another date on the file" showed
    /// nothing to choose from and quietly used Modified, so the option asked a question
    /// and then never let anyone answer it.
    /// </summary>
    public int CopyFromIndex
    {
        get
        {
            int found = -1;

            for (int i = 0; i < this.CopyFromOptions.Count; i++)
            {
                if (this.CopyFromOptions[i].Field == this.CopyFromField)
                {
                    found = i;
                    break;
                }
            }

            return found;
        }

        set
        {
            if (value >= 0 && value < this.CopyFromOptions.Count)
            {
                this.CopyFromField = this.CopyFromOptions[value].Field;
            }
        }
    }

    partial void OnCopyFromFieldChanged(DateField value)
    {
        this.OnPropertyChanged(nameof(this.CopyFromIndex));
        this.LeaveTemplateOnEdit();
        this.NotifyIntentDerived();
        this.QueueRecompute();
    }

    // ---- Targets ----------------------------------------------------------------------

    [ObservableProperty]
    public partial bool WriteCreated { get; set; } = true;

    partial void OnWriteCreatedChanged(bool value)
    {
        this.LeaveTemplateOnEdit();
        this.ReconcileIntent();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial bool WriteModified { get; set; } = true;

    partial void OnWriteModifiedChanged(bool value)
    {
        this.LeaveTemplateOnEdit();
        this.ReconcileIntent();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial bool WriteChanged { get; set; }

    partial void OnWriteChangedChanged(bool value)
    {
        this.LeaveTemplateOnEdit();
        this.ReconcileIntent();
        this.QueueRecompute();
    }

    [ObservableProperty]
    public partial bool WriteTaken { get; set; }

    partial void OnWriteTakenChanged(bool value)
    {
        this.LeaveTemplateOnEdit();
        this.ReconcileIntent();
        this.QueueRecompute();
    }

    // ---- Sorting ----------------------------------------------------------------------

    [ObservableProperty]
    public partial SortChoice Sort { get; set; } = SortChoice.Name;

    partial void OnSortChanged(SortChoice value)
    {
        if (this._applyingSort)
        {
            return;
        }

        this.Reproject();
    }

    /// <summary>
    /// Which way round the chosen sort runs.
    ///
    /// Stored as the direction of the COMPARISON, not as "the natural order or the other
    /// one", so the caret on a header can be read literally. That means each choice has its
    /// own sensible starting direction: names read A to Z, but "biggest change" that opened
    /// on the smallest change would be a strange thing to call biggest.
    /// </summary>
    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    partial void OnSortDescendingChanged(bool value)
    {
        if (this._applyingSort)
        {
            return;
        }

        this.Reproject();
    }

    private bool _applyingSort;

    private static bool OpensDescending(SortChoice choice) =>
        choice is SortChoice.BiggestChange or SortChoice.Status;

    /// <summary>
    /// Picking a sort. Always a selection, never a toggle.
    ///
    /// This is what a MENU item does, and a menu item that quietly reversed the list
    /// because you picked the option already in force would be a nasty little surprise -
    /// you asked for "sort by name" and got the opposite of what you were looking at.
    /// Reversing has its own verb.
    /// </summary>
    public void ChooseSort(SortChoice choice)
    {
        // Both at once, then reproject once. Setting them one after the other would sort
        // the list twice and throw the scroll position away twice with it.
        this._applyingSort = true;

        try
        {
            this.Sort = choice;
            this.SortDescending = OpensDescending(choice);
        }
        finally
        {
            this._applyingSort = false;
        }

        this.Reproject();
    }

    /// <summary>
    /// What a COLUMN HEADER does: pick this column, or reverse it if it is already the one.
    ///
    /// The thirty-year-old behaviour of every file list anybody has used, and the reason it
    /// is separate from <see cref="ChooseSort" /> is that a header click and a menu pick
    /// genuinely mean different things.
    /// </summary>
    public void ToggleSort(SortChoice choice)
    {
        if (this.Sort == choice)
        {
            this.SortDescending = !this.SortDescending;
            return;
        }

        this.ChooseSort(choice);
    }

    /// <summary>
    /// Reversing whatever is in force, for the sorts whose header is a menu rather than a
    /// column label and so has no second click to give.
    /// </summary>
    [RelayCommand]
    public void ReverseSort() => this.SortDescending = !this.SortDescending;

    /// <summary>The caret, as the direction it actually sorts in.</summary>
    public string SortCaret => this.SortDescending ? "▼" : "▲";

    // The caret shows on a column header ONLY when the sort is that column's. Two of the
    // four sorts have no column, and parking the caret on the nearest header - or worse,
    // renaming that header to match - would have a header describing something other than
    // what is underneath it.
    public string FileHeaderCaret => this.Sort == SortChoice.Name ? this.SortCaret : string.Empty;

    public string StatusHeaderCaret => this.Sort == SortChoice.Status ? this.SortCaret : string.Empty;

    /// <summary>Whether the sort is one with no column of its own, and so needs stating in words.</summary>
    public bool IsSortOffColumn => this.Sort is SortChoice.BiggestChange or SortChoice.ResultingDate;

    public string SortChipLabel => this.Sort switch
    {
        SortChoice.BiggestChange => $"Sorted by biggest change {this.SortCaret}",
        SortChoice.ResultingDate => $"Sorted by resulting date {this.SortCaret}",
        _ => string.Empty,
    };

    /// <summary>Back to the default, for the chip's dismiss button.</summary>
    [RelayCommand]
    public void SortByName() => this.ChooseSort(SortChoice.Name);

    private void NotifySortState()
    {
        this.OnPropertyChanged(nameof(this.SortCaret));
        this.OnPropertyChanged(nameof(this.FileHeaderCaret));
        this.OnPropertyChanged(nameof(this.StatusHeaderCaret));
        this.OnPropertyChanged(nameof(this.IsSortOffColumn));
        this.OnPropertyChanged(nameof(this.SortChipLabel));

        this.OnPropertyChanged(nameof(this.SelectAllLabel));
        this.OnPropertyChanged(nameof(this.SelectNoneLabel));
        this.OnPropertyChanged(nameof(this.TypeFilterChipLabel));
    }

    /// <summary>
    /// The two selection verbs, each carrying its own SCOPE as a number.
    ///
    /// These cover different sets on purpose - all-shown versus none-of-everything - and
    /// that asymmetry is deliberate: both err the same way, so neither can leave a file
    /// ticked that nobody laid eyes on. It also means they can never be merged into one
    /// tri-state checkbox in the list header, because a checkbox above a list means the
    /// rows below it, and unticking one with a filter on would silently clear hundreds of
    /// files that are not on screen. Putting the counts in the labels is what makes the
    /// difference visible at the moment of clicking rather than afterwards.
    /// </summary>
    public string SelectAllLabel => string.Create(
        CultureInfo.CurrentCulture,
        $"All shown ({this.Rows.Count:N0})");

    public string SelectNoneLabel => this.Rows.Count == this._allRows.Count
        ? string.Create(CultureInfo.CurrentCulture, $"None ({this._allRows.Count:N0})")
        : string.Create(
            CultureInfo.CurrentCulture,
            $"None, including hidden ({this._allRows.Count:N0})");

    /// <summary>
    /// What the type filter is doing, in words, beside the box that set it.
    ///
    /// The Apply button already states it, and that is the last line of defence rather than
    /// the first. This filter scopes the RUN, not the view, so the place it was typed
    /// should say so too.
    /// </summary>
    public string TypeFilterChipLabel => string.Create(
        CultureInfo.CurrentCulture,
        $"Run limited to {this.TypeFilter} — {this.Summary.FilesHiddenByTypeFilter:N0} excluded");

    [RelayCommand]
    public void ClearTypeFilter() => this.TypeFilter = string.Empty;

    /// <summary>
    /// Which files the run covers, as semicolon-separated wildcards. Empty means all.
    ///
    /// The same language Explorer uses - "*.png", "mountain*.jpg", "IMG_????.CR2" - because
    /// people already know one wildcard syntax for filenames and inventing a second would
    /// be a gratuitous thing to make them learn. Matching is delegated to the framework's
    /// own implementation of it rather than reimplemented here.
    /// </summary>
    [ObservableProperty]
    public partial string TypeFilter { get; set; } = string.Empty;

    private List<string> _typePatterns = [];

    partial void OnTypeFilterChanged(string value)
    {
        this._typePatterns = [.. (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)

            // A bare extension is what people type. Accepting "png" and ".png" as well as
            // "*.png" costs nothing and removes a way to get no results and no explanation.
            //
            // Only a word with no dot in it, or a lone ".ext", is an extension. The rule
            // was "anything without a wildcard", which turned an exact name like
            // "Thumbs.db" into "*.Thumbs.db" - a pattern that cannot match the very file
            // it was typed to find, with nothing on screen saying why.
            .Select(p => IsBareExtension(p) ? "*" + (p.StartsWith('.') ? p : "." + p) : p)];

        this.OnPropertyChanged(nameof(this.HasTypeFilter));
        this.OnPropertyChanged(nameof(this.CanStartOver));
        this.Reproject();
    }

    private static bool IsBareExtension(string pattern) =>
        !pattern.Contains('*', StringComparison.Ordinal)
        && !pattern.Contains('?', StringComparison.Ordinal)
        && pattern.LastIndexOf('.') <= 0;

    public bool HasTypeFilter => this._typePatterns.Count > 0;

    [ObservableProperty]
    public partial bool ShowOnlyChanging { get; set; }

    partial void OnShowOnlyChangingChanged(bool value) => this.Reproject();

    [ObservableProperty]
    public partial bool ShowOnlyProblems { get; set; }

    partial void OnShowOnlyProblemsChanged(bool value) => this.Reproject();


    // ---- Scanning ---------------------------------------------------------------------

    /// <summary>
    /// The second pass: the dates that live inside the files.
    ///
    /// Deliberately behind the timestamp scan rather than part of it. Four NTFS timestamps
    /// come back in microseconds and ExifTool takes milliseconds per file, so running them
    /// together would mean staring at an empty grid; running them in sequence means the
    /// list appears at once and the photo dates fill in underneath.
    ///
    /// Costs nothing when there is no engine, which is the normal state for someone who
    /// only ever changes file dates.
    /// </summary>
    private async Task ReadMetadataAsync(CancellationToken cancellationToken)
    {
        if (!this._metadata.Available)
        {
            return;
        }

        List<PlanRowViewModel> candidates = [.. this._allRows.Where(r => MetadataGateway.CanRead(r.File))];

        if (candidates.Count == 0)
        {
            return;
        }

        this.IsReadingMetadata = true;
        this.ProgressStatus = string.Create(CultureInfo.CurrentCulture, $"Reading photo dates from {candidates.Count:N0} files…");

        try
        {
            var progress = new Progress<int>(done => this.ProgressStatus = string.Create(
                CultureInfo.CurrentCulture, $"Read photo dates from {done:N0} of {candidates.Count:N0} files…"));

            IReadOnlyDictionary<string, FileMetadata> read = await this._metadata
                .ReadAsync([.. candidates.Select(r => r.File)], progress, cancellationToken)
                .ConfigureAwait(true);

            foreach (PlanRowViewModel row in candidates)
            {
                if (read.TryGetValue(row.File.FullPath, out FileMetadata? file))
                {
                    row.Enrich(file.Values, file.QuickTimeReadAsUtc);
                }
            }

            this.ProgressStatus = string.Create(
                CultureInfo.CurrentCulture, $"Read photo dates from {read.Count:N0} of {candidates.Count:N0} files.");

            // The snapshot changed, so every plan built against the old one is stale.
            this.Recompute();
        }
        catch (OperationCanceledException)
        {
            this.ProgressStatus = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The file dates are already on screen and still correct, so this costs the
            // photo dates rather than the whole scan.
            this._logger.LogWarning(ex, "Could not read photo dates.");
            this.ReportProblem("The file dates were read, but the photo dates could not be.");
        }
        finally
        {
            this.IsReadingMetadata = false;
        }
    }

    /// <summary>
    /// True while the second pass runs. Separate from IsScanning because the list is
    /// already usable: the file dates are there and only the photo dates are still filling.
    /// </summary>
    [ObservableProperty]
    public partial bool IsReadingMetadata { get; set; }

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



        bool ownsScan = this.BeginScan(cancellationToken, out CancellationToken token);

        // Remembered so the source card can tell when the settings have moved on from the
        // list they produced, and offer to read it again rather than describing it wrongly.
        this._listScanFilter = filter;

        this.IsScanning = true;
        this.ProgressStatus = $"Reading {folder}…";

        try
        {
            int added = 0;

            await foreach (ScannedFile file in this._scanner.ScanAsync(folder, filter, token))
            {
                this._allRows.Add(this.TrackRow(new PlanRowViewModel(file)));
                added++;

                // The grid fills as the scan runs rather than after it, so a big folder
                // shows progress instead of an empty window.
                if (added % ScanProgressBatch == 0)
                {
                    this.ProgressStatus = string.Create(CultureInfo.CurrentCulture, $"Read {added:N0} files…");
                    this.Recompute();
                }
            }

            this.ProgressStatus = string.Create(CultureInfo.CurrentCulture, $"Read {added:N0} files from {folder}.");
            this.Recompute();

            await this.ReadMetadataAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            this.ProgressStatus = "Scan cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this._logger.LogError(ex, "Could not scan {Folder}.", folder);
            this.ReportProblem($"Could not read {folder}: {ex.Message}");
        }
        finally
        {
            this.IsScanning = false;
            this.EndScan(ownsScan);
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
        this.NotifyIntentDerived();
        this.Recompute();
    }

    /// <summary>
    /// True when the current recipe wants metadata and cannot have it. Drives the one
    /// affordance that opens the consent pane - visibly unavailable rather than hidden,
    /// because hiding it would make the app look like it cannot do what it promises.
    /// </summary>
    public bool NeedsExifTool
    {
        get
        {
            if (this.EngineStatus.Available)
            {
                return false;
            }

            Recipe recipe = this.BuildRecipe();

            // Reading counts as much as writing. "Copy the photo's taken date onto the
            // file dates" writes nothing but file dates and still cannot run without it.
            return recipe.NeedsMetadataWrite || recipe.NeedsMetadataRead;
        }
    }

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

    partial void OnActionNoticeChanged(string? value)
    {
        this.OnPropertyChanged(nameof(this.HasActionNotice));

        // Raised from here rather than from each assignment to _undoLastAction, because
        // every one of those is immediately followed by setting this. A button whose
        // visibility is only refreshed at some of the sites that change it is how the
        // toast would end up offering to undo the previous action.
        this.OnPropertyChanged(nameof(this.NoticeHasUndo));
    }

    public bool HasActionNotice => this.ActionNotice is not null;

    /// <summary>How to put back whatever the notice is describing.</summary>
    private Action? _undoLastAction;

    /// <summary>
    /// Whether the toast shows its Undo button.
    ///
    /// The toast was built for one thing — something happened and you can take it back —
    /// and using it for ordinary option changes was reported as overkill, correctly: an
    /// Undo button on everything teaches people to stop reading the one place the app says
    /// something reversible just happened.
    ///
    /// The confirmations that used to go to the footer have nowhere else to be now that the
    /// footer is a progress meter, so the toast takes them QUIETLY: same card, same few
    /// seconds, no Undo. The complaint was about the button, not the card.
    /// </summary>
    public bool NoticeHasUndo => this._undoLastAction is not null;

    /// <summary>
    /// Confirms something that just happened and cannot be taken back.
    ///
    /// The home for the two dozen sentences that used to be written to the footer, where
    /// the next scan erased them. Deliberately not a way to raise the Undo toast: anything
    /// reversible sets <see cref="_undoLastAction"/> as well, and this clears it so a stale
    /// Undo from an earlier action cannot end up attached to this sentence.
    /// </summary>
    public void Confirm(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        this._undoLastAction = null;
        this.CanReplaceWithDrop = false;
        this.ActionNotice = message;
    }

    /// <summary>
    /// Says what happened and how to take it back, in that order.
    ///
    /// The order is the reason this exists. Every site did it the other way round — notice
    /// first, undo second — which was harmless while nothing watched, and stopped being
    /// harmless the moment the Undo button's visibility started depending on the field:
    /// setting the notice raises it, so it would have been evaluated against the PREVIOUS
    /// action's undo every time.
    /// </summary>
    private void AnnounceUndoable(string message, Action undo)
    {
        this._undoLastAction = undo;
        this.ActionNotice = message;
    }

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

    partial void OnIntentNudgeChanged(string? value)
    {
        this.OnPropertyChanged(nameof(this.HasIntentNudge));
        this.NotifyNoticeRegion();
    }

    public bool HasIntentNudge => this.IntentNudge is not null;

    /// <summary>Names the switch rather than saying "OK", so the button states its own effect.</summary>
    public string NudgeActionLabel => this._nudgeTarget switch
    {
        WorkIntent.FileDates => "Switch to file dates",
        WorkIntent.PhotoDates => "Switch to photo dates",
        _ => "Switch",
    };

    // ---- The notice region ---------------------------------------------------------------

    /// <summary>
    /// What a finished run did, kept until it is read.
    ///
    /// This is the message the whole item was about. It used to go to the footer, and the
    /// line after the one that wrote it is <c>await this.RescanAsync()</c> — which re-enters
    /// AddFolderAsync and overwrites the footer twice more before control ever returns to
    /// the UI. Measured 2026-09-23: the trail is
    ///
    ///   Writing 2 of 2… → Done. 2 changed, 0 failed, 0 skipped. → Reading C:\… → Read 2 files from C:\…
    ///
    /// So it was not that a run report COULD be lost before it was read. It was destroyed
    /// every single time, by the app itself, in the same await chain — a run that half
    /// failed reported "Read 2 files from C:\Photos." Nobody has ever seen one of these.
    /// </summary>
    [ObservableProperty]
    public partial string? RunNotice { get; set; }

    partial void OnRunNoticeChanged(string? value) => this.NotifyNoticeRegion();

    /// <summary>True when the run being reported had failures, which colours the notice.</summary>
    [ObservableProperty]
    public partial bool RunNoticeIsBad { get; set; }

    /// <summary>Clears the run report. The only thing that does, besides the next run.</summary>
    [RelayCommand]
    public void DismissRunNotice()
    {
        this.RunNotice = null;
        this.RunNoticeIsBad = false;
    }

    /// <summary>
    /// One region, one notice, highest priority first — and the run report outranks both
    /// standing conditions.
    ///
    /// That order is the whole reason this is ranked rather than stacked. The two
    /// conditions describe things that are still true and will still be true in a minute;
    /// a run report describes something that has just happened and will never be said
    /// again. Ranked the other way, the message that a run half failed would queue behind
    /// a warning the user has already read and decided to live with.
    /// </summary>
    /// <summary>
    /// Something went wrong just now, and it is not a run's own business.
    ///
    /// "Could not open x.jpg" used to go to the footer, which is a progress meter that the
    /// next scan overwrites - so the app's error messages were the most losable thing in
    /// it. They rank above a run report because both are events and this one is newer:
    /// a report is a summary that may already have been read, and a failure that has just
    /// happened has not been.
    /// </summary>
    [ObservableProperty]
    public partial string? ProblemNotice { get; set; }

    partial void OnProblemNoticeChanged(string? value) => this.NotifyNoticeRegion();

    /// <summary>Says that something failed, somewhere it will still be there to read.</summary>
    public void ReportProblem(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        this.ProblemNotice = message;
    }

    [RelayCommand]
    public void DismissProblemNotice() => this.ProblemNotice = null;

    public bool ShowsProblemNotice => this.ProblemNotice is not null;

    public bool ShowsRunNotice => this.ProblemNotice is null && this.RunNotice is not null;

    public bool ShowsExifToolNotice =>
        this.ProblemNotice is null && this.RunNotice is null && this.NeedsExifTool;

    public bool ShowsIntentNudge =>
        this.ProblemNotice is null && this.RunNotice is null && !this.NeedsExifTool && this.HasIntentNudge;

    /// <summary>
    /// How many notices are open but not on screen.
    ///
    /// Shown as a count rather than left silent because one region that hides the rest is
    /// otherwise indistinguishable from one region with nothing else to say - and the
    /// hidden one can be the ExifTool warning that explains why the run just failed.
    /// </summary>
    public int QueuedNoticeCount
    {
        get
        {
            int open = (this.ProblemNotice is not null ? 1 : 0)
                + (this.RunNotice is not null ? 1 : 0)
                + (this.NeedsExifTool ? 1 : 0)
                + (this.HasIntentNudge ? 1 : 0);

            return Math.Max(0, open - 1);
        }
    }

    public bool HasQueuedNotices => this.QueuedNoticeCount > 0;

    public string QueuedNoticeLabel => this.QueuedNoticeCount == 1
        ? "1 more notice"
        : string.Create(CultureInfo.CurrentCulture, $"{this.QueuedNoticeCount:N0} more notices");

    /// <summary>
    /// Every derived member of the region, announced together.
    ///
    /// They are all computed from the same three sources, so anything that moves one moves
    /// most of the others. Raising them one at a time is how a region like this ends up
    /// showing a notice it has already been told to hide.
    /// </summary>
    private void NotifyNoticeRegion()
    {
        this.OnPropertyChanged(nameof(this.ShowsProblemNotice));
        this.OnPropertyChanged(nameof(this.ShowsRunNotice));
        this.OnPropertyChanged(nameof(this.ShowsExifToolNotice));
        this.OnPropertyChanged(nameof(this.ShowsIntentNudge));
        this.OnPropertyChanged(nameof(this.QueuedNoticeCount));
        this.OnPropertyChanged(nameof(this.HasQueuedNotices));
        this.OnPropertyChanged(nameof(this.QueuedNoticeLabel));
    }

    private WorkIntent _nudgeTarget = WorkIntent.None;
    private List<PlanRowViewModel> _rowsBeforeDrop = [];
    private List<PlanRowViewModel> _rowsFromDrop = [];

    /// <summary>
    /// How often a running scan says where it has got to.
    ///
    /// Shared by both scan paths deliberately. They disagreed — the folder path reported
    /// and the drop path did not — and the only reason that survived unnoticed is that the
    /// number lived inside one of them as a literal, where nothing pointed at its absence
    /// in the other.
    /// </summary>
    private const int ScanProgressBatch = 500;

    /// <summary>
    /// When a list is large enough to say so out loud.
    ///
    /// Past any ordinary folder and half the 50,000 the preview is built to hold, so it
    /// speaks up for the tree somebody did not mean to drop and stays quiet for the rest.
    ///
    /// It is a REMARK, not a limit. Nothing is capped, truncated or refused at this number.
    /// A cap that silently dropped files would be worse than no cap, and a modal mid-drop
    /// would interrupt the one gesture in the app that is already reversible: a drop writes
    /// nothing, Apply confirms with its own counts and defaults to Cancel, and Undo is a
    /// button on the very notice this sentence is appended to.
    /// </summary>
    private const int LargeList = 25_000;

    /// <summary>
    /// The notice a drop leaves behind, with a word about the size when there is one worth
    /// saying. Split out from the drop itself so the wording can be tested without putting
    /// twenty-five thousand files on a disk to see it.
    /// </summary>
    internal static string DescribeDrop(string summary, int added) =>
        added < LargeList
            ? summary
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{summary} That is a large list, and everything will be slower until it is trimmed.");

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

        bool ownsScan = this.BeginScan(cancellationToken, out CancellationToken token);

        this.IsScanning = true;
        this.ProgressStatus = "Reading dropped items…";

        try
        {
            ScanFilter filter = this.BuildScanFilter();
            this._listScanFilter = filter;

            int added = 0;

            await foreach (ScannedFile file in this._scanner.ScanPathsAsync(paths, filter, token))
            {
                PlanRowViewModel row = this.TrackRow(new PlanRowViewModel(file));
                this._allRows.Add(row);
                this._rowsFromDrop.Add(row);
                added++;

                // The drop is the gesture that needed this most and was the one without it.
                // AddFolderAsync has counted up every 500 files since it was written; this
                // loop ran to completion in silence, so dropping a deep tree left "Reading
                // dropped items…" and an empty grid on screen for the whole scan. The
                // spinner and the Cancel beside it were both live the entire time and both
                // looked like decoration, because nothing visible was moving. That is what
                // made a big drop feel like a hang — not the size of it, and not any
                // missing guard rail.
                if (added % ScanProgressBatch == 0)
                {
                    this.ProgressStatus = string.Create(CultureInfo.CurrentCulture, $"Read {added:N0} files…");
                    this.Recompute();
                }
            }

            foreach (string path in paths.Where(Directory.Exists))
            {
                if (!this._roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    this._roots.Add(path);
                }
            }

            // And the other half of what was dropped. A file named on its own belongs to no
            // folder, so a rescan has nothing to find it by unless it is remembered here.
            foreach (string path in paths.Where(File.Exists))
            {
                if (!this._looseFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    this._looseFiles.Add(path);
                }
            }

            string what = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar)) : $"{paths.Count} items";

            List<PlanRowViewModel> before = [.. this._rowsBeforeDrop];



            string summary = string.Create(
                CultureInfo.CurrentCulture,
                $"Added {this._rowsFromDrop.Count:N0} file{(this._rowsFromDrop.Count == 1 ? string.Empty : "s")} from {what}.");

            this.AnnounceUndoable(
                DescribeDrop(summary, this._rowsFromDrop.Count),
                () =>
                {
                    this.ClearRows();
                    this.RestoreRows(before);
                });

            // The short form, not the notice. The footer trims to a single line and the
            // notice can now carry a second sentence, which would be the half that got cut.
            // The toast is where the longer one has room to be read.
            this.ProgressStatus = summary;
            this.Recompute();
            this.CheckIntentAgainstContent();

            this._logger.LogInformation(
                "Added {Added} file(s); the list now holds {Total}.",
                this._rowsFromDrop.Count,
                this._allRows.Count);

            await this.ReadMetadataAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            this.ProgressStatus = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this._logger.LogError(ex, "Could not read the dropped items.");
            this.ReportProblem($"Could not read what was dropped: {ex.Message}");
        }
        finally
        {
            this.IsScanning = false;
            this.EndScan(ownsScan);
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

        this.ClearRows();
        this.RestoreRows(kept);
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

    // ---- How a folder is read ----------------------------------------------------------
    //
    // Sticky rather than asked on every drop, and stated on the source card in words rather
    // than left inside the flyout. Until now none of this was a choice at all: every entry
    // point - drop, Add folder, Send To, the command line, and the rescan after a run -
    // passed ScanFilter.Default, which is recursive and unbounded, and the scanner then
    // overrode .NET's own default to sweep in hidden and system files as well.
    //
    // ONE builder, used by all of them. Two mechanisms is how the fifth entry point gets
    // missed, and the fifth entry point is the rescan after an apply - the one that would
    // quietly refill the list with the whole tree you just told it not to read.

    [ObservableProperty]
    public partial bool ScanRecurse { get; set; } = true;

    [ObservableProperty]
    public partial bool ScanIncludeHidden { get; set; }

    [ObservableProperty]
    public partial bool ScanIncludeFolders { get; set; }

    partial void OnScanRecurseChanged(bool value) => this.NotifyDeck();

    partial void OnScanIncludeHiddenChanged(bool value) => this.NotifyDeck();

    partial void OnScanIncludeFoldersChanged(bool value) => this.NotifyDeck();

    /// <summary>The filter every scan uses, built from the settings that are on screen.</summary>
    public ScanFilter BuildScanFilter() => new(
        ["*"],
        Recurse: this.ScanRecurse,
        IncludeFiles: true,
        IncludeDirectories: this.ScanIncludeFolders,
        IncludeRootDirectory: this.ScanIncludeFolders,
        IncludeHidden: this.ScanIncludeHidden);

    /// <summary>What produced the list currently on screen, so the card can tell when it is stale.</summary>
    private ScanFilter? _listScanFilter;

    /// <summary>
    /// The setting, worded as a setting rather than as a description of the list.
    ///
    /// Those are different tenses and putting them on adjacent lines is a trap: turn
    /// "include subfolders" off with four thousand files already loaded and nothing
    /// rescans, so a card reading "4,000 files / 3 folders · deep" would be describing a
    /// list that no longer matches its own settings, directly above the list that does.
    /// </summary>
    public string ScanSettingLabel => "New drops: " + this.ScanSettingBody;

    /// <summary>
    /// The settings alone, for both the card and the rescan confirmation.
    ///
    /// The confirmation used to slice the label at [12..], one past the end of an
    /// eleven-character prefix, and so read "Read the folders again: ubfolders." Building
    /// both from one piece means neither has to know how long the other's prefix is.
    /// </summary>
    private string ScanSettingBody
    {
        get
        {
            var parts = new List<string> { this.ScanRecurse ? "subfolders" : "this folder only" };

            if (this.ScanIncludeHidden)
            {
                parts.Add("hidden files");
            }

            if (this.ScanIncludeFolders)
            {
                parts.Add("folders too");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Whether the settings now differ from the ones that read the current list.
    ///
    /// This is what makes the difference between a setting and a lie: changing it does not
    /// re-read the disk, so when it no longer matches what is on screen the card has to
    /// offer the way to make it match.
    /// </summary>
    public bool CanRescanWithOptions =>
        this._roots.Count > 0
        && this._listScanFilter is { } used
        && !SameScan(used, this.BuildScanFilter());

    /// <summary>
    /// Compares the parts that are SETTINGS, which is not the same as comparing the records.
    ///
    /// ScanFilter is a record, so == looks like the obvious answer and is not: Patterns is
    /// an IReadOnlyList and records compare members with the default equality comparer,
    /// which for a collection is reference equality. Two filters built a second apart are
    /// never equal, so the card offered to re-read the folders the instant they were read.
    /// Patterns is excluded on purpose anyway - it is always ["*"] here, because narrowing
    /// by type is the type filter's job and lives on the list header.
    /// </summary>
    private static bool SameScan(ScanFilter a, ScanFilter b) =>
        a.Recurse == b.Recurse
        && a.IncludeFiles == b.IncludeFiles
        && a.IncludeDirectories == b.IncludeDirectories
        && a.IncludeRootDirectory == b.IncludeRootDirectory
        && a.IncludeHidden == b.IncludeHidden;

    /// <summary>Re-reads every folder in the list with the settings as they are now.</summary>
    [RelayCommand]
    public async Task RescanWithOptionsAsync()
    {
        await this.RescanAsync().ConfigureAwait(true);

        this.Confirm(string.Create(
            CultureInfo.CurrentCulture,
            $"Read the folders again: {this.ScanSettingBody}."));
    }

    // ---- Clearing and starting over ----------------------------------------------------

    /// <summary>Whether there is a list at all, which is what Clear needs to mean anything.</summary>
    public bool HasAnyFiles => this._allRows.Count > 0;

    /// <summary>
    /// Nothing in the list yet, which is when the pane should say what to do about it.
    ///
    /// The empty state used to be a line of ordinary text in the summary band at the top -
    /// far from the large blank area that is the thing you would actually drop onto, and
    /// styled like a status rather than an invitation.
    /// </summary>
    public bool IsListEmpty => this._allRows.Count == 0;

    /// <summary>
    /// Files are loaded but the filter is hiding all of them.
    ///
    /// A separate state from an empty list, and it has to be, because they need opposite
    /// messages. An empty list wants "drop files here"; this one wants "your filter matches
    /// nothing" - and showing the first would be telling somebody to add files they have
    /// already added.
    /// </summary>
    public bool IsFilteredToNothing => this._allRows.Count > 0 && this.Rows.Count == 0;

    /// <summary>
    /// What is hiding everything, named - and it has to name the RIGHT one.
    ///
    /// This used to be hardcoded to the type filter, on the assumption that the type filter
    /// was the only thing that could empty the list. It is not: the two view toggles can
    /// too, and with an empty filter box the sentence came out as "Nothing matches . 128
    /// files are hidden by it", followed by an instruction to clear a box that is already
    /// empty. The worst path is the common one - Apply rescans on success, so finishing a
    /// run with "only files that will change" on empties the list, and the app announced
    /// that it had lost 128 files immediately after writing them correctly.
    /// </summary>
    public string FilteredToNothingNote
    {
        get
        {
            string hidden = string.Create(
                CultureInfo.CurrentCulture,
                $"{this._allRows.Count:N0} file{(this._allRows.Count == 1 ? string.Empty : "s")} hidden.");

            List<string> causes = [];

            if (this.HasTypeFilter)
            {
                causes.Add(string.Create(
                    CultureInfo.CurrentCulture,
                    $"Only {this.TypeFilter} is shown, and nothing matches."));
            }

            if (this.ShowOnlyChanging)
            {
                causes.Add("Only files that will change are shown, and none do right now.");
            }

            if (this.ShowOnlyProblems)
            {
                causes.Add("Only files that need a look are shown, and none do.");
            }

            // With several on at once, naming them all reads like an accusation and none of
            // them is individually the culprit anyway - the combination is.
            string why = causes.Count switch
            {
                0 => "Nothing is shown.",
                1 => causes[0],
                _ => "Between them, the filters in force leave nothing to show.",
            };

            return $"{why} {hidden}";
        }
    }

    /// <summary>
    /// Whether anything would actually change. Covers the view state as well as the list,
    /// because a stale filter is precisely the thing you cannot see the cause of.
    /// </summary>
    /// <summary>
    /// Everything Start over would put back, as one comparable value.
    /// </summary>
    private sealed record SessionState(
        WorkIntent Intent,
        SourceChoice Source,
        SortChoice Sort,
        bool SortDescending,
        bool ShowOnlyChanging,
        bool ShowOnlyProblems,
        string TypeFilter,
        bool WriteCreated,
        bool WriteModified,
        bool WriteChanged,
        bool WriteTaken,
        string? Template);

    private SessionState Snapshot() => new(
        this.Intent,
        this.Source,
        this.Sort,
        this.SortDescending,
        this.ShowOnlyChanging,
        this.ShowOnlyProblems,
        this.TypeFilter,
        this.WriteCreated,
        this.WriteModified,
        this.WriteChanged,
        this.WriteTaken,
        this.ActiveTemplate?.Name);

    /// <summary>
    /// Where this session came in. Anything different from this is something to start over
    /// FROM; anything equal to it is the state the app opened in.
    /// </summary>
    private SessionState _restingState;

    /// <summary>
    /// Declares the current state to be the one Start over has nothing to do about.
    ///
    /// Called when the app opens, again once the saved settings have been restored, and
    /// again after a Start over - each of which is a moment where the user has, by
    /// definition, not yet changed anything.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_restingState))]
    private void MarkResting() => this._restingState = this.Snapshot();

    /// <summary>
    /// Whether anything would actually change.
    ///
    /// Compared against where the session STARTED rather than against hard defaults, and
    /// that distinction is the whole point. Settings persist the intent, the sort and the
    /// filters, so a fresh launch restores them and a rule built from defaults is the
    /// exception rather than the norm - which left Start over lit up on an empty window
    /// where nothing had been done yet, offering to undo a choice made days ago.
    ///
    /// The intent still counts, which it has to: without it the button stayed disabled
    /// right after somebody picked the wrong one, and that is the moment they want it.
    /// It now counts as a CHANGE to the intent rather than as the intent being set at all.
    /// </summary>
    public bool CanStartOver => this.HasAnyFiles || this.Snapshot() != this._restingState;

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
        List<string> loose = [.. this._looseFiles];
        WorkIntent intent = this.Intent;
        SourceChoice source = this.Source;
        SortChoice sort = this.Sort;
        bool onlyChanging = this.ShowOnlyChanging;
        bool onlyProblems = this.ShowOnlyProblems;
        (bool created, bool modified, bool changed, bool taken) =
            (this.WriteCreated, this.WriteModified, this.WriteChanged, this.WriteTaken);
        DateTemplate? template = this.ActiveTemplate;

        this.ClearRows();
        this._roots.Clear();
        this._looseFiles.Clear();
        this.SelectedRow = null;
        this.ProgressStatus = string.Empty;

        // The run report and any problem went with the list they described. A report of a
        // run over files that are no longer listed is not a report, it is a leftover.
        this.DismissRunNotice();
        this.DismissProblemNotice();
        this.DismissNudge();

        this._applyingIntent = true;
        this._applyingTemplate = true;

        try
        {
            this.ActiveTemplate = null;
            this.Intent = WorkIntent.None;
            this.Source = SourceChoice.PickADate;
            this.AbsoluteDate = null;
            this.Sort = SortChoice.Name;
            this.ShowOnlyChanging = false;
            this.ShowOnlyProblems = false;
            this.WriteCreated = true;
            this.WriteModified = true;
            this.WriteChanged = false;
            this.WriteTaken = false;
        }
        finally
        {
            this._applyingIntent = false;
            this._applyingTemplate = false;
        }

        // Having just started over, there is nothing left to start over from - so this
        // becomes the new resting state and the button goes out. Undoing it puts the old
        // values back, which differ from this one, and the button returns on its own.
        this.MarkResting();

        this.NotifyIntentDerived();
        this.NotifyTemplateState();
        this.Recompute();

        // The undo first, the sentence second. Setting the notice is what raises the Undo
        // button's visibility, so the other order evaluates it against the previous action.
        this._undoLastAction = () =>
        {
            this.RestoreRows(rows);
            this._roots.AddRange(roots);
            this._looseFiles.AddRange(loose);

            this._applyingIntent = true;
            this._applyingTemplate = true;

            try
            {
                this.ActiveTemplate = template;
                this.Intent = intent;
                this.Source = source;
                this.Sort = sort;
                this.ShowOnlyChanging = onlyChanging;
                this.ShowOnlyProblems = onlyProblems;
                this.WriteCreated = created;
                this.WriteModified = modified;
                this.WriteChanged = changed;
                this.WriteTaken = taken;
            }
            finally
            {
                this._applyingIntent = false;
                this._applyingTemplate = false;
            }

            this.NotifyIntentDerived();
            this.NotifyTemplateState();
        };

        this.ActionNotice = "Started over.";
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
                $"{media:N0} of these are photos or videos. Photo libraries read their taken date, not the file dates.");
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
        List<string> loose = [.. this._looseFiles];

        this.ClearRows();
        this._roots.Clear();
        this._looseFiles.Clear();
        this.SelectedRow = null;
        this.ProgressStatus = string.Empty;

        // The run report and any problem went with the list they described. A report of a
        // run over files that are no longer listed is not a report, it is a leftover.
        this.DismissRunNotice();
        this.DismissProblemNotice();
        this.DismissNudge();
        this.Recompute();

        this.AnnounceUndoable(
            string.Create(
                CultureInfo.CurrentCulture,
                $"Cleared {rows.Count:N0} file{(rows.Count == 1 ? string.Empty : "s")}."),
            () =>
            {
                this.RestoreRows(rows);
                this._roots.AddRange(roots);
                this._looseFiles.AddRange(loose);
            });
    }

    // ---- One row at a time -------------------------------------------------------------
    //
    // Every one of these acts on the row that was right-clicked and on nothing else,
    // whatever happens to be ticked. One rule, no exceptions: a menu item that sometimes
    // means "this file" and sometimes means "these three hundred files" is how somebody
    // removes three hundred files by accident.

    /// <summary>
    /// Takes one file out of the list. Nothing on disk changes.
    ///
    /// There was no way to do this: Clear empties everything, so one stray file meant
    /// building the list again.
    /// </summary>
    public void RemoveRow(PlanRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!this._allRows.Remove(row))
        {
            return;
        }

        if (ReferenceEquals(this.SelectedRow, row))
        {
            this.SelectedRow = null;
        }

        this.Confirm(string.Create(CultureInfo.CurrentCulture, $"Removed {row.Name} from the list."));
        this.Reproject();
        this.OnPropertyChanged(nameof(this.CanStartOver));
    }

    /// <summary>Unticks everything else, so the run covers this file alone.</summary>
    public void SelectOnly(PlanRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The third loop of this shape, and the one the item that prompted this missed.
        // It walks every loaded row, so on a big list it was the same N x O(N).
        this.SelectInBulk(() =>
        {
            foreach (PlanRowViewModel other in this._allRows)
            {
                other.IsIncluded = ReferenceEquals(other, row);
            }
        });

        this.Confirm(string.Create(CultureInfo.CurrentCulture, $"The run now covers {row.Name} only."));
    }

    /// <summary>
    /// Fills the run's date picker from this file, so "make everything match this one" is
    /// two clicks rather than reading a date off the screen and typing it back in.
    /// </summary>
    /// <returns>False when the file has no date to offer.</returns>
    public bool UseRowDateForRun(PlanRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (BestDate(row) is not { } value)
        {
            this.Confirm(string.Create(CultureInfo.CurrentCulture, $"{row.Name} has no date to copy."));
            return false;
        }

        DateTimeOffset local = value.ToLocalTime();

        this.Source = SourceChoice.PickADate;
        this.AbsoluteDate = local.Date;
        this.AbsoluteTime = local.TimeOfDay;

        this.Confirm(string.Create(
            CultureInfo.CurrentCulture,
            $"The run will use {local:yyyy-MM-dd HH:mm}, taken from {row.Name}."));

        return true;
    }

    /// <summary>
    /// The date this file actually has, preferring the one it recorded for itself.
    ///
    /// One order, shared by everything that needs "this file's date" - the row menu seeds
    /// its date dialog from it and "use this file's date for the run" copies it - so the
    /// two can never disagree about which of a file's dates is the real one.
    /// </summary>
    public static DateTimeOffset? BestDate(PlanRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.File.Current(DateField.ExifDateTimeOriginal)
            ?? row.File.Current(DateField.QuickTimeCreateDate)
            ?? row.File.Times.Modified
            ?? row.File.Times.Created;
    }

    // ---- A pattern the user built ---------------------------------------------------

    /// <summary>
    /// One id, reused. There is only ever one hand-built pattern in play, and giving each
    /// attempt its own id would leave a session quietly accumulating dead patterns that
    /// an old recipe could still name.
    /// </summary>
    public const string CustomPatternId = "user.built";

    /// <summary>The tokens of the pattern built here, or null when there is not one.</summary>
    [ObservableProperty]
    public partial string? CustomPatternTokens { get; set; }

    public bool HasCustomPattern => this.CustomPatternTokens is not null;

    partial void OnCustomPatternTokensChanged(string? value) =>
        this.OnPropertyChanged(nameof(this.HasCustomPattern));

    /// <summary>
    /// Takes the pattern built from one filename and points the run at it.
    ///
    /// Named explicitly by the recipe rather than thrown in with the built-ins, so it is
    /// used on every file but competes with nothing. A pattern built from one camera's
    /// filenames, let loose on the general matching path, starts finding dates in serial
    /// numbers.
    /// </summary>
    public void UseFilenamePattern(string tokens, DatePrecision precision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokens);

        this._filenames.Register(new FilenamePattern(
            CustomPatternId,
            "Your pattern",
            PatternMode.Tokens,
            tokens,
            PatternScope.FileNameWithoutExtension,
            precision));

        this.CustomPatternTokens = tokens;
        this.Source = SourceChoice.FromFileName;

        this.Confirm("Using the pattern you built from the file name.");

        this.Recompute();
    }

    /// <summary>Goes back to trying the built-in patterns.</summary>
    public void ForgetFilenamePattern()
    {
        this.CustomPatternTokens = null;
        this.Confirm("Back to the file name patterns Chronora knows.");

        this.Recompute();
    }

    /// <summary>
    /// What a candidate pattern would do to the files already loaded, without committing
    /// to it.
    ///
    /// Through the real parser rather than a bare regex match, so the plausibility and
    /// ambiguity gates apply here exactly as they will in the run. A preview that counted
    /// regex hits would happily promise 1,284 matches and then deliver far fewer.
    /// </summary>
    public FilenamePatternPreview PreviewFilenamePattern(string tokens, DatePrecision precision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokens);

        var trial = new FilenameDateParser();

        trial.Register(new FilenamePattern(
            "preview",
            "preview",
            PatternMode.Tokens,
            tokens,
            PatternScope.FileNameWithoutExtension,
            precision));

        int matched = 0;
        var samples = new List<string>();

        foreach (PlanRowViewModel row in this._allRows)
        {
            FilenameParseResult result;

            try
            {
                result = trial.ParseWith(row.File.FullPath, "preview");
            }
            catch (FormatException)
            {
                // A half-finished pattern. Nothing matches, which is the honest answer.
                return new FilenamePatternPreview(0, this._allRows.Count, []);
            }

            if (!result.Success)
            {
                continue;
            }

            matched++;

            if (samples.Count < 3)
            {
                samples.Add(string.Create(
                    CultureInfo.CurrentCulture,
                    $"{row.Name} → {result.Match.Value:yyyy-MM-dd HH:mm}"));
            }
        }

        return new FilenamePatternPreview(matched, this._allRows.Count, samples);
    }

    // ---- Remembering the last run's shape -----------------------------------------------

    /// <summary>
    /// Restores the options from a previous session.
    ///
    /// Order matters. Choosing the intent sets the write targets as a side effect - that is
    /// its job - so the saved targets have to land after it, or picking an intent last time
    /// would quietly undo the boxes that were ticked after it.
    ///
    /// Nothing here loads files or sets a date, so restoring cannot produce a plan. The app
    /// still opens with nothing to apply.
    /// </summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (Enum.TryParse(settings.Intent, out WorkIntent intent) && intent != WorkIntent.None)
        {
            this.ChooseIntent(intent);
        }

        this.WriteCreated = settings.WriteCreated;
        this.WriteModified = settings.WriteModified;
        this.WriteChanged = settings.WriteChanged;
        this.WriteTaken = settings.WriteTaken;

        if (Enum.TryParse(settings.Source, out SourceChoice source))
        {
            this.Source = source;
        }

        if (Enum.TryParse(settings.CopyFromField, out DateField copyFrom))
        {
            this.CopyFromField = copyFrom;
        }

        if (Enum.TryParse(settings.Sort, out SortChoice sort))
        {
            this.Sort = sort;
            this.SortDescending = settings.SortDescending;
        }

        this.ShiftHours = settings.ShiftHours;
        this.ShowOnlyChanging = settings.ShowOnlyChanging;
        this.ShowOnlyProblems = settings.ShowOnlyProblems;

        // Restored before the command line and Send To paths run, which is what makes a
        // remembered "this folder only" actually apply to a launch that starts with files.
        this.ScanRecurse = settings.ScanRecurse;
        this.ScanIncludeHidden = settings.ScanIncludeHidden;
        this.ScanIncludeFolders = settings.ScanIncludeFolders;

        // What was restored is where this session starts, not something the user has done.
        // Without this the window opened with Start over already live, offering to undo a
        // choice made on another day.
        this.MarkResting();
        this.OnPropertyChanged(nameof(this.CanStartOver));
    }

    /// <summary>
    /// What a finished run says, and why it says more than a count.
    ///
    /// "Done. 0 changed, 5 failed, 0 skipped." was the whole message. The reason was recorded
    /// against every one of those files in the journal and shown nowhere, so the only way to
    /// learn it was to open the database - which is not a thing anybody does while the app is
    /// still sitting there reporting the failure. One wiring bug cost two sessions that way.
    ///
    /// The first distinct reason goes on the line. Anything beyond that is a count and a
    /// pointer to History, because a status bar that tries to hold three sentences holds none.
    /// </summary>
    public static string DescribeOutcome(ApplyOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        string counts = string.Create(
            CultureInfo.CurrentCulture,
            $"Done. {outcome.Written:N0} changed, {outcome.Failed:N0} failed, {outcome.Skipped:N0} skipped.");

        if (outcome.FailureReasons.Count == 0)
        {
            return counts;
        }

        if (outcome.FailureReasons.Count == 1)
        {
            return $"{counts} {outcome.FailureReasons[0]}";
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{counts} {outcome.FailureReasons[0]} ({outcome.FailureReasons.Count - 1:N0} other reason(s) - see History.)");
    }

    /// <summary>Copies the current options into the settings about to be written.</summary>
    public void CaptureSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Intent = this.Intent.ToString();
        settings.Source = this.Source.ToString();
        settings.WriteCreated = this.WriteCreated;
        settings.WriteModified = this.WriteModified;
        settings.WriteChanged = this.WriteChanged;
        settings.WriteTaken = this.WriteTaken;
        settings.CopyFromField = this.CopyFromField.ToString();
        settings.ShiftHours = this.ShiftHours;
        settings.Sort = this.Sort.ToString();
        settings.SortDescending = this.SortDescending;
        settings.ShowOnlyChanging = this.ShowOnlyChanging;
        settings.ShowOnlyProblems = this.ShowOnlyProblems;
        settings.ScanRecurse = this.ScanRecurse;
        settings.ScanIncludeHidden = this.ScanIncludeHidden;
        settings.ScanIncludeFolders = this.ScanIncludeFolders;
    }

    /// <summary>
    /// A row writing to fields outside the file system has to be planned in photo mode,
    /// whatever mode the rest of the run is in - otherwise the metadata target is filtered
    /// straight back out and the row silently does nothing.
    /// </summary>
    private static AppMode ModeFor(IReadOnlySet<DateField> targets, AppMode runMode) =>
        targets.Any(t => DateFieldCatalog.GenreOf(t) != FieldGenre.FileSystem)
            ? AppMode.PhotoDates
            : runMode;

    /// <summary>
    /// Which fields a single row would write, and which it is allowed to offer.
    ///
    /// The run's own targets are the starting point, minus anything this file cannot carry
    /// - there is no Taken date on a text file, and offering one produces a tick that the
    /// recipe silently drops. If that leaves nothing at all, the file dates everybody
    /// recognises are a better answer than an empty dialog.
    /// </summary>
    public IReadOnlySet<DateField> DefaultTargetsFor(PlanRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.ManualTargets is { } chosen)
        {
            return chosen;
        }

        HashSet<DateField> applicable =
        [
            .. this.BuildRecipe().Rules[0].Targets.Where(t => DateFieldCatalog.AppliesTo(t, row.File.Kind)),
        ];

        return applicable.Count > 0 ? applicable : [DateField.FileCreated, DateField.FileModified];
    }

    /// <summary>Whether a field can be written to this file at all.</summary>
    public static bool CanTarget(PlanRowViewModel row, DateField field)
    {
        ArgumentNullException.ThrowIfNull(row);

        return DateFieldCatalog.AppliesTo(field, row.File.Kind);
    }

    /// <summary>
    /// Sets a date and the fields it goes into, for this file alone. Null for both clears
    /// the override and hands the row back to the run.
    /// </summary>
    public void SetManualDate(PlanRowViewModel row, DateTimeOffset? value, IReadOnlySet<DateField>? targets = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.ManualDate = value;
        row.ManualTargets = targets;

        this.Confirm(value is { } set
            ? string.Create(CultureInfo.CurrentCulture, $"{row.Name} is set to {set:yyyy-MM-dd HH:mm} by hand.")
            : string.Create(CultureInfo.CurrentCulture, $"{row.Name} follows the run again."));

        this.Recompute();
    }

    /// <summary>
    /// How many rows are carrying a date of their own.
    ///
    /// Counted across the whole list rather than the filtered view: an override on a row a
    /// type filter is hiding is still an override, and "remove every one" that quietly left
    /// some behind would be the worst kind of half-done.
    /// </summary>
    public int RowsSetByHand => this._allRows.Count(r => r.HasManualDate);

    /// <summary>
    /// Hands every by-hand row back to the run at once.
    ///
    /// One at a time is fine for the three files that usually need it and hopeless past
    /// that. An override is visible only as a marker on its own row, so eight of them in
    /// five hundred files can only be found by scrolling - and nothing anywhere said how
    /// many there were. Reported from use: having set one, there was no obvious way back.
    ///
    /// Reversible through the same notice as the other bulk actions, and it sets the fields
    /// directly rather than calling SetManualDate in a loop, which would recompute once per
    /// row and leave the status line describing whichever row happened to be last.
    /// </summary>
    public void ClearAllManualDates()
    {
        List<(PlanRowViewModel Row, DateTimeOffset? Date, IReadOnlySet<DateField>? Targets)> had =
        [
            .. this._allRows
                .Where(r => r.HasManualDate)
                .Select(r => (Row: r, Date: r.ManualDate, Targets: r.ManualTargets)),
        ];

        if (had.Count == 0)
        {
            return;
        }

        foreach ((PlanRowViewModel row, _, _) in had)
        {
            row.ManualDate = null;
            row.ManualTargets = null;
        }

        this.Recompute();

        this.AnnounceUndoable(
            string.Create(
                CultureInfo.CurrentCulture,
                $"Removed {had.Count:N0} by-hand date{(had.Count == 1 ? string.Empty : "s")}."),
            () =>
            {
                foreach ((PlanRowViewModel row, DateTimeOffset? date, IReadOnlySet<DateField>? targets) in had)
                {
                    row.ManualDate = date;
                    row.ManualTargets = targets;
                }
            });
    }

    /// <summary>Ticks or unticks one row, and keeps the Apply count with it.</summary>
    public static void ToggleRow(PlanRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.IsIncluded = !row.IsIncluded;
    }

    /// <summary>
    /// Watches a row so ticking its checkbox updates the Apply count.
    ///
    /// Subscribed here rather than left to the view to remember. It was left to the view,
    /// and the view did not: the checkbox bound two-way to IsIncluded and nothing told the
    /// summary, so unticking half a list left the Apply button still offering to write all
    /// of it. The number on the destructive button has to follow what is ticked.
    /// </summary>
    private PlanRowViewModel TrackRow(PlanRowViewModel row)
    {
        // Detached first so a row can be tracked twice without being counted twice. On a
        // fresh row this is a no-op; on one coming back from an undo it is the difference
        // between one handler and two.
        row.PropertyChanged -= this.OnRowChanged;
        row.PropertyChanged += this.OnRowChanged;

        return row;
    }

    /// <summary>
    /// The one handler every row is watched with.
    ///
    /// A named method rather than the lambda this used to be, for the dull reason that a
    /// lambda cannot be unsubscribed: `_allRows.Clear()` happened in five places and not
    /// one of them detached anything, so every row ever loaded stayed wired to this view
    /// model for the life of the window. A leak on its own, and a correctness bug waiting
    /// for the day a discarded row's IsIncluded could still move — at which point a list
    /// the user has replaced would go on editing the summary of the list that replaced it.
    /// </summary>
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The bulk commands set IsIncluded on every row and refresh once themselves.
        if (this._selectingInBulk)
        {
            return;
        }

        if (e.PropertyName == nameof(PlanRowViewModel.IsIncluded))
        {
            this.RefreshSummary();
        }
    }

    /// <summary>
    /// Empties the list, detaching every row on the way out.
    ///
    /// Paired with <see cref="RestoreRows"/> so that nothing touches `_allRows.Clear()`
    /// directly any more. That was the whole failure mode: the subscription was made in one
    /// place and the list was emptied in five, and the five had no reason to know about it.
    /// </summary>
    private void ClearRows()
    {
        foreach (PlanRowViewModel row in this._allRows)
        {
            row.PropertyChanged -= this.OnRowChanged;
        }

        this._allRows.Clear();
    }

    /// <summary>
    /// Puts rows back that were taken out, re-watching each one.
    ///
    /// Every undo in here works by keeping the old list and adding it back, and rows added
    /// back after a <see cref="ClearRows"/> have been detached — so an undone drop would
    /// otherwise restore a list whose checkboxes no longer moved the Apply count.
    /// </summary>
    private void RestoreRows(IEnumerable<PlanRowViewModel> rows)
    {
        foreach (PlanRowViewModel row in rows)
        {
            this._allRows.Add(this.TrackRow(row));
        }
    }

    /// <summary>
    /// Runs a command that ticks or unticks many rows, and refreshes once at the end.
    ///
    /// The refresh is in a finally because skipping it would be worse than the storm it is
    /// suppressing: the summary would be left describing the selection as it was before.
    /// </summary>
    private void SelectInBulk(Action select)
    {
        this._selectingInBulk = true;

        try
        {
            select();
        }
        finally
        {
            this._selectingInBulk = false;
            this.RefreshSummary();
        }
    }

    /// <summary>
    /// Ticks everything currently shown - not everything loaded, because a type filter
    /// narrows what the run covers and selecting files it is excluding would contradict it.
    /// </summary>
    [RelayCommand]
    public void SelectAllShown() => this.SelectInBulk(() =>
    {
        foreach (PlanRowViewModel row in this.Rows)
        {
            row.IsIncluded = true;
        }
    });

    /// <summary>
    /// Unticks everything loaded, including anything a filter is hiding.
    ///
    /// Deliberately wider than SelectAllShown. Both err the same way: selecting covers only
    /// what you can see, and deselecting covers everything - so neither can leave a file
    /// ticked that you never laid eyes on.
    /// </summary>
    [RelayCommand]
    public void SelectNone() => this.SelectInBulk(() =>
    {
        foreach (PlanRowViewModel row in this._allRows)
        {
            row.IsIncluded = false;
        }
    });

    // ---- The command deck --------------------------------------------------------------
    //
    // Three statements across the top of the window: what is loaded, what the run will do,
    // and what comes out. Every one of them is derived - the deck sets nothing that the
    // options pane also sets, because two controls for one question is what the title bar
    // mode switch was removed for.
    //
    // Everything here is raised together by NotifyDeck(). Do not split the notifications up
    // by input; that is how the older derived properties went stale twice.

    /// <summary>
    /// What the SOURCE segment states: how much is loaded, and how many folders it came
    /// from. Files dropped individually have no folder, so the second half is conditional
    /// rather than reading "0 folders" over a list of twelve files.
    /// </summary>
    public string SourceHeadline
    {
        get
        {
            if (this._allRows.Count == 0)
            {
                return "No files yet";
            }

            string files = string.Create(
                CultureInfo.CurrentCulture,
                $"{this._allRows.Count:N0} file{(this._allRows.Count == 1 ? string.Empty : "s")}");

            if (this._roots.Count == 0)
            {
                return files;
            }

            return string.Create(
                CultureInfo.CurrentCulture,
                $"{files} · {this._roots.Count:N0} folder{(this._roots.Count == 1 ? string.Empty : "s")}");
        }
    }

    /// <summary>
    /// Whether the RULE segment must state the TEMPLATE rather than the pane's controls.
    ///
    /// This is not cosmetic. A template can carry a rule the pane cannot show: the built-in
    /// "Make every date consistent" reads the EARLIEST of three fields, while the pane
    /// displays only the first of them. Paraphrasing the controls would put a confident,
    /// wrong sentence in the most prominent place in the window, contradicting the
    /// template's own description - which is the only place that part of the run is stated.
    /// So when a template is in charge, the deck says so and quotes it instead of guessing.
    /// </summary>
    public bool IsRuleFromTemplate => this.HasActiveTemplate;

    /// <summary>The other half of <see cref="IsRuleFromTemplate" />, so the view needs no converter.</summary>
    public bool IsRuleFromPane => !this.HasActiveTemplate;

    public string RuleTemplateName => this.ActiveTemplate?.Name ?? string.Empty;

    /// <summary>The intent, in the pane's own words so the two can never read differently.</summary>
    public string RuleIntentLine => this.Intent switch
    {
        WorkIntent.FileDates => "File dates",
        WorkIntent.PhotoDates => "Photo and video dates",
        WorkIntent.Custom => "Let me pick the fields",
        _ => "Nothing chosen yet",
    };

    /// <summary>Where the date comes from. The arrow carries the "from".</summary>
    public string RuleSourceLine => !this.HasChosenIntent
        ? string.Empty
        : this.Source switch
        {
            SourceChoice.PickADate => "← a date I pick",
            SourceChoice.ShiftBy => "← the existing date, shifted",
            SourceChoice.FromAnotherDate => "← a date the file already has",
            SourceChoice.FromFileName => "← the file name",
            _ => string.Empty,
        };

    /// <summary>
    /// The fields the run is AIMED at, which is not the same as the fields it will manage -
    /// that is the summary band's job, and the band is where a blocked field is reported.
    /// Read from the same conditions that decide whether each checkbox is on screen, so the
    /// card cannot name a field the pane is not offering.
    ///
    /// "Taken" rather than "Taken (photo)": the parenthetical disambiguates a checkbox in a
    /// list of four, and is noise in a sentence that has already said Photo and video dates.
    /// </summary>
    public string RuleTargetLine
    {
        get
        {
            if (!this.HasChosenIntent)
            {
                return string.Empty;
            }

            List<string> fields = [];

            if (this.ShowsFileDates && this.WriteCreated)
            {
                fields.Add("Created");
            }

            if (this.ShowsFileDates && this.WriteModified)
            {
                fields.Add("Modified");
            }

            if (this.IsPhotoMode && this.WriteTaken)
            {
                fields.Add("Taken");
            }

            if (this.ShowsAdvancedFields && this.WriteChanged)
            {
                fields.Add("Changed");
            }

            return fields.Count == 0 ? "→ no fields ticked" : "→ " + string.Join(" · ", fields);
        }
    }

    /// <summary>
    /// The RESULT headline, and it counts what APPLY counts.
    ///
    /// FilesChanging - the old summary's headline number - ignores ticking, so unticking
    /// eighty rows left the top of the window saying 96 while the button said 12. The
    /// headline of a dashboard has to be the number the button acts on; "can change" is
    /// demoted to the line below it, where it is a filter rather than a promise.
    ///
    /// Mid-run it defers to the footer's own sentence. The summary is not rebuilt while
    /// writing, so anything else here would be a stale number sitting beside a live one.
    /// </summary>
    public string ResultHeadline
    {
        get
        {
            if (this.IsApplying)
            {
                return this.ProgressStatus;
            }

            if (this.Summary.FilesTotal == 0)
            {
                return "Nothing loaded yet";
            }

            if (this.Summary.FilesToWrite == 0)
            {
                return "Nothing to apply";
            }

            return string.Create(
                CultureInfo.CurrentCulture,
                $"{this.Summary.FilesToWrite:N0} of {this.Summary.FilesTotal:N0} will be written");
        }
    }

    public string ChangingFilterLabel => string.Create(
        CultureInfo.CurrentCulture,
        $"{this.Summary.FilesChanging:N0} can change");

    /// <summary>
    /// Labelled from FilesWithProblems, never from FilesBlocked or FilesSuspicious. Those
    /// are independent tallies that double-count a file which is both, and neither is the
    /// size of the set this toggle reveals.
    /// </summary>
    public string ProblemsFilterLabel => string.Create(
        CultureInfo.CurrentCulture,
        $"{this.Summary.FilesWithProblems:N0} need{(this.Summary.FilesWithProblems == 1 ? "s" : string.Empty)} a look");

    /// <summary>
    /// A count of zero is not worth clicking, so it greys - but only while its own filter
    /// is OFF. Apply rescans on success, so a run that succeeds takes "can change" to zero
    /// while that filter is still on; disabling it there would lock someone inside a view
    /// of nothing with the way out greyed.
    /// </summary>
    public bool CanFilterChanging =>
        !this.IsApplying && (this.Summary.FilesChanging > 0 || this.ShowOnlyChanging);

    public bool CanFilterProblems =>
        !this.IsApplying && (this.Summary.FilesWithProblems > 0 || this.ShowOnlyProblems);

    /// <summary>Everything in the deck goes quiet mid-run. Disabled, not hidden.</summary>
    public bool DeckEnabled => !this.IsApplying;

    /// <summary>Whether any view is narrowing the list, and therefore whether there is a way back.</summary>
    public bool IsAnyFilterOn => this.HasTypeFilter || this.ShowOnlyChanging || this.ShowOnlyProblems;

    /// <summary>
    /// The single way out of a view that is hiding everything, whichever filter is doing it.
    ///
    /// Offered as one button rather than three because somebody looking at an empty list
    /// does not know which of the three emptied it - that is the entire problem.
    /// </summary>
    [RelayCommand]
    public void ShowAllFiles()
    {
        this.TypeFilter = string.Empty;
        this.ShowOnlyChanging = false;
        this.ShowOnlyProblems = false;
    }

    private void NotifyDeck()
    {
        this.OnPropertyChanged(nameof(this.SourceHeadline));

        // The scan settings belong to the source segment, and CanRescanWithOptions moves
        // with the LIST as well as with the settings - clearing the list takes it false
        // without anything about the settings having changed. Raised here, with everything
        // else the segment shows, rather than from its own cluster that half the callers
        // would forget: the notification tests caught exactly that.
        this.OnPropertyChanged(nameof(this.ScanSettingLabel));
        this.OnPropertyChanged(nameof(this.CanRescanWithOptions));

        this.OnPropertyChanged(nameof(this.IsRuleFromTemplate));
        this.OnPropertyChanged(nameof(this.IsRuleFromPane));
        this.OnPropertyChanged(nameof(this.RuleTemplateName));
        this.OnPropertyChanged(nameof(this.RuleIntentLine));
        this.OnPropertyChanged(nameof(this.RuleSourceLine));
        this.OnPropertyChanged(nameof(this.RuleTargetLine));

        this.OnPropertyChanged(nameof(this.ResultHeadline));
        this.OnPropertyChanged(nameof(this.ChangingFilterLabel));
        this.OnPropertyChanged(nameof(this.ProblemsFilterLabel));
        this.OnPropertyChanged(nameof(this.CanFilterChanging));
        this.OnPropertyChanged(nameof(this.CanFilterProblems));

        this.OnPropertyChanged(nameof(this.DeckEnabled));
        this.OnPropertyChanged(nameof(this.IsAnyFilterOn));
        this.OnPropertyChanged(nameof(this.IsBusy));
    }

    partial void OnSummaryChanged(ChangeSummary value) => this.NotifyDeck();

    /// <summary>Called by the view when a checkbox changes, so the Apply count keeps up.</summary>
    public void RefreshSummary() =>
        this.Summary = ChangeSummary.Build(
            [.. this._allRows.Where(this.MatchesTypeFilter)],
            this.BuildRecipe().AllTargets,
            this.HasTypeFilter ? this.TypeFilter : null,
            this._allRows.Count);

    // ---- Applying ---------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsApplying { get; set; }

    partial void OnIsApplyingChanged(bool value)
    {
        this.OnPropertyChanged(nameof(this.IsBusy));
        this.NotifyDeck();
    }

    [ObservableProperty]
    public partial double ApplyProgressPercent { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<JournalRun> History { get; set; } = [];

    /// <summary>
    /// The run this session applied, if it has applied one. Null until then, and null again
    /// once it has been put back.
    /// </summary>
    private long? _lastRunThisSession;

    /// <summary>
    /// True when THIS SESSION has applied a run that can still be put back.
    ///
    /// Deliberately not "the journal contains a revertible run". It used to be, and the
    /// button that says "Undo last run" was therefore live the moment the app opened,
    /// offering to revert whatever was applied days ago - while sitting beside Apply, where
    /// the comment says the moment someone wants it is the moment straight after the run
    /// they regret. Undoing Tuesday's work from a button captioned "last run" is exactly
    /// the surprise the rest of this app is built to avoid.
    ///
    /// Older runs are not lost, and are not meant to be reached from here: History lists
    /// every one of them with what it did and when, and its Undo sits on the row it undoes
    /// where it cannot be misread.
    /// </summary>
    public bool CanUndo => this._lastRunThisSession is { } runId
        && this.History.Any(r =>
            r.RunId == runId
            && r.Kind == RunKind.Apply
            && r.Status is RunStatus.Completed or RunStatus.PartiallyReverted);

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
        // Scoped by the type filter as well as the checkboxes, because that filter narrows
        // what the RUN covers rather than only what the list shows. The Apply button and
        // the confirmation both say so.
        List<FilePlan> plans = [.. this._allRows
            .Where(this.MatchesTypeFilter)
            .Where(r => r.IsIncluded && r.Plan is { } p && p.WillWrite)
            .Select(r => r.Plan!)];

        if (plans.Count == 0)
        {
            this.Confirm("Nothing to apply.");
            return;
        }

        this._run?.Cancel();
        this._run?.Dispose();
        this._run = new CancellationTokenSource();

        // Last run's report, cleared as this one starts. A notice that persists until it is
        // dismissed has to be retired by the thing that makes it untrue, or the counts from
        // the previous run sit over the top of this one while it writes.
        this.DismissRunNotice();

        this.IsApplying = true;
        this.ApplyProgressPercent = 0;

        var progress = new Progress<ApplyProgress>(p =>
        {
            this.ApplyProgressPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            this.ProgressStatus = string.Create(
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

            // What "Undo last run" means from here on. Recorded even when the run partly
            // failed, because the part that succeeded is exactly what somebody would want
            // back.
            this._lastRunThisSession = outcome.RunId;

            // To the notice region, not the footer. The very next line is the reason: the
            // rescan re-enters AddFolderAsync, which writes to the footer twice more before
            // control returns to the UI, so a run report written there was destroyed by
            // this method every time it ran. It was never a message that COULD be missed.
            this.RunNotice = DescribeOutcome(outcome);
            this.RunNoticeIsBad = outcome.Failed > 0;

            // The files on disk have moved on, so the snapshot the preview was built from
            // is now stale. Re-reading is the honest thing to do rather than leaving the
            // grid showing a plan that has already happened.
            await this.RescanAsync();
        }
        catch (OperationCanceledException)
        {
            this.ProgressStatus = "Cancelled.";
        }
        finally
        {
            this.IsApplying = false;
            this.ApplyProgressPercent = 0;
            this.RefreshHistory();
        }
    }

    [RelayCommand]
    public void CancelRun()
    {
        this._run?.Cancel();

        // Reading the disk is the other thing worth escaping, and for a recursive drop it
        // is the longer of the two. Both are behind one button because from the outside
        // they are one thing: Chronora is busy and you want it to stop.
        this._scan?.Cancel();
    }

    /// <summary>
    /// Opens a cancellable scan, or joins the one already in flight.
    /// </summary>
    /// <returns>True when this call owns the source and must close it.</returns>
    private bool BeginScan(CancellationToken outer, out CancellationToken token)
    {
        if (this._scan is not null)
        {
            token = this._scan.Token;
            return false;
        }

        this._scan = CancellationTokenSource.CreateLinkedTokenSource(outer);
        token = this._scan.Token;

        return true;
    }

    private void EndScan(bool owned)
    {
        if (!owned)
        {
            return;
        }

        this._scan?.Dispose();
        this._scan = null;
    }

    /// <summary>Puts the most recent apply back, skipping anything that has since changed.</summary>
    [RelayCommand]
    public async Task UndoLastAsync()
    {
        // The run this session applied, not merely the newest revertible one in the
        // journal - see CanUndo. Without the run id this reached back into previous days.
        JournalRun? last = this._lastRunThisSession is { } runId
            ? this.History.FirstOrDefault(r =>
                r.RunId == runId
                && r.Kind == RunKind.Apply
                && r.Status is RunStatus.Completed or RunStatus.PartiallyReverted)
            : null;

        if (last is null)
        {
            this.Confirm("There is nothing to undo. Older runs are in History.");
            return;
        }

        await this.RevertAsync(last.RunId, force: false);
    }

    /// <summary>Puts one run back. Force overrides the drift check, and is never the default.</summary>
    public async Task RevertAsync(long runId, bool force)
    {
        this.DismissRunNotice();
        this.IsApplying = true;

        var progress = new Progress<ApplyProgress>(p =>
        {
            this.ApplyProgressPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            this.ProgressStatus = string.Create(CultureInfo.CurrentCulture, $"Undoing {p.Done:N0} of {p.Total:N0}…");
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

            // An undo is a run and its report is a run report, with the same rescan
            // underneath it destroying the same sentence.
            this.RunNotice = outcome.Failed == 0
                ? string.Create(CultureInfo.CurrentCulture, $"Undone. {outcome.Written:N0} files put back.")
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"Undone. {outcome.Written:N0} put back, {outcome.Failed:N0} left alone because they changed since.");

            this.RunNoticeIsBad = outcome.Failed > 0;

            await this.RescanAsync();
        }
        finally
        {
            this.IsApplying = false;
            this.ApplyProgressPercent = 0;
            this.RefreshHistory();
        }
    }

    /// <summary>History as the window shows it, newest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryRowViewModel> HistoryRows { get; set; } = [];

    public bool HasHistory => this.HistoryRows.Count > 0;

    /// <summary>
    /// For the empty state in the History window.
    ///
    /// The History button is deliberately never greyed out, because a greyed one teaches
    /// people the app has no undo. That only works if the window it opens says so out loud
    /// rather than showing a blank list, which reads as a failure to load.
    /// </summary>
    public bool IsHistoryEmpty => this.HistoryRows.Count == 0;

    /// <summary>
    /// How far back undo still reaches, stated rather than left to be discovered.
    ///
    /// Retention prunes old runs, and the one thing worse than a limit is a limit nobody
    /// mentioned until the run they wanted had already gone.
    /// </summary>
    public string RetentionNote => this.HistoryRows.Count == 0
        ? "Nothing has been applied yet."
        : string.Create(
            CultureInfo.CurrentCulture,
            $"Oldest run still here: {this.HistoryRows[^1].When}.");

    /// <summary>
    /// The word someone has to type to clear history.
    ///
    /// Typed rather than clicked, and compared exactly. This is the only action in the app
    /// that destroys something no amount of later work can rebuild: the files survive, but
    /// the record of what they used to look like does not, so every run stops being
    /// undoable at once. A button people can hit by reflex is the wrong shape for that.
    /// </summary>
    public const string ClearHistoryConfirmation = "DELETE";

    /// <summary>
    /// Deletes every run. The caller is responsible for having got the typed confirmation
    /// first; the word is checked here too so the rule lives with the action rather than
    /// only in the dialog that happens to call it.
    /// </summary>
    /// <returns>How many runs went, or null when the confirmation did not match.</returns>
    public int? ClearHistory(string typed)
    {
        if (!string.Equals(typed, ClearHistoryConfirmation, StringComparison.Ordinal))
        {
            return null;
        }

        int removed = this._journal.ClearAll();
        this.RefreshHistory();

        this.Confirm(removed == 0
            ? "History was already empty."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"History cleared. {removed:N0} run{(removed == 1 ? string.Empty : "s")} deleted; nothing on disk changed."));

        return removed;
    }

    public void RefreshHistory()
    {
        this.History = this._journal.ListRuns(50);
        this.HistoryRows = [.. this.History.Select(r => new HistoryRowViewModel(r))];

        this.OnPropertyChanged(nameof(this.CanUndo));
        this.OnPropertyChanged(nameof(this.HasHistory));
        this.OnPropertyChanged(nameof(this.IsHistoryEmpty));
        this.OnPropertyChanged(nameof(this.RetentionNote));
    }

    /// <summary>
    /// Re-reads the folders after a write, because the snapshot the preview was built from
    /// describes a state that no longer exists.
    /// </summary>
    private async Task RescanAsync()
    {
        List<string> roots = [.. this._roots];

        // Files dropped one at a time as well as the folders. Left out, the clear below
        // threw them away and nothing put them back: an apply ended with every loose file
        // gone from the list, which is a poor thing to discover after a write.
        //
        // Checked against the disk, because a file that has since been moved or deleted
        // should leave rather than come back as a row pointing at nothing.
        List<string> loose = [.. this._looseFiles.Where(File.Exists)];

        if (roots.Count == 0 && loose.Count == 0)
        {
            return;
        }

        this.ClearRows();

        // One scan source around the whole thing, claimed here so that cancelling partway
        // stops the rescan rather than just the folder currently being read. AddFolderAsync
        // joins it instead of opening its own.
        bool ownsScan = this.BeginScan(CancellationToken.None, out CancellationToken token);

        try
        {
            // The fifth entry point, and the one that matters most here: this runs after
            // every apply. Left on ScanFilter.Default, turning "include subfolders" off and
            // then running a job would silently repopulate the list with the whole tree.
            ScanFilter filter = this.BuildScanFilter();

            foreach (string root in roots)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                await this.AddFolderAsync(root, filter, token);
            }

            if (loose.Count > 0 && !token.IsCancellationRequested)
            {
                // ScanPathsAsync takes a file as readily as a folder, which is what makes
                // this one call rather than a second code path.
                await foreach (ScannedFile file in this._scanner.ScanPathsAsync(loose, filter, token))
                {
                    this._allRows.Add(this.TrackRow(new PlanRowViewModel(file)));
                }

                this.Recompute();
                await this.ReadMetadataAsync(token).ConfigureAwait(true);
            }
        }
        finally
        {
            this.EndScan(ownsScan);
        }
    }

    /// <summary>
    /// What the run recorded about itself, for History to render from.
    ///
    /// Carries a written summary as well as the machine-readable parts, because History
    /// has to stay explicable after every file the run touched has been moved or deleted -
    /// and "{"intent":"FileDates","source":"FromAnotherDate"}" is not an explanation.
    ///
    /// Built with a serialiser rather than string concatenation: a template name is
    /// user-supplied text and can contain a quote, which would otherwise produce a broken
    /// record that History could never read back.
    /// </summary>
    private string DescribeRecipe()
    {
        var record = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["summary"] = this.SummariseRun(),
            ["intent"] = this.Intent.ToString(),
            ["source"] = this.Source.ToString(),
            ["mode"] = this.Mode.ToString(),
        };

        if (this.ActiveTemplate is { } template)
        {
            record["template"] = template.Name;
            record["templateId"] = template.Id;
        }

        return System.Text.Json.JsonSerializer.Serialize(record, RunRecordJsonContext.Default.DictionaryStringString);
    }

    /// <summary>One line naming what was written and where it came from.</summary>
    private string SummariseRun()
    {
        if (this.ActiveTemplate is { } template)
        {
            return template.Name;
        }

        List<string> targets = [];

        if (this.WriteCreated)
        {
            targets.Add("Created");
        }

        if (this.WriteModified)
        {
            targets.Add("Modified");
        }

        if (this.WriteChanged)
        {
            targets.Add("Changed");
        }

        if (this.WriteTaken)
        {
            targets.Add("Taken");
        }

        string source = this.Source switch
        {
            SourceChoice.ShiftBy => string.Create(CultureInfo.CurrentCulture, $"shifted by {this.ShiftHours:0.##} hours"),
            SourceChoice.FromAnotherDate => $"from {DateFieldCatalog.Get(this.CopyFromField).DisplayName}",
            SourceChoice.FromFileName => "from the file name",
            _ => this.AbsoluteDate is { } picked
                ? string.Create(CultureInfo.CurrentCulture, $"set to {picked.Date.Add(this.AbsoluteTime):yyyy-MM-dd HH:mm}")
                : "set to a chosen date",
        };

        return targets.Count == 0
            ? source
            : string.Create(CultureInfo.CurrentCulture, $"{string.Join(", ", targets)} {source}");
    }

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

        this._scan?.Cancel();
        this._scan?.Dispose();
        this._scan = null;
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

        // TaskScheduler.Default plus an explicit dispatcher, rather than
        // FromCurrentSynchronizationContext, which throws outright when there is no
        // context and so tied this class to running inside a WinUI message pump.
        _ = Task.Delay(RecomputeDebounce, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    this._dispatcher.Post(this.Recompute);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

        // One rule always exists, even when no date has been picked - BuildRecipe uses an
        // Unset source rather than an empty recipe precisely so the targets survive.
        IReadOnlySet<DateField> targets = recipe.Rules[0].Targets;

        DateSource runSource = recipe.Rules[0].Source;

        foreach (PlanRowViewModel row in this._allRows)
        {
            // A row set by hand answers the same two questions the pane asks - which date,
            // and which fields - for itself. Either can be overridden without the other,
            // so a file can take the run's date into different fields, or its own date
            // into the run's fields.
            Recipe forRow = row.HasManualDate
                ? new Recipe(
                    [new DateRule(
                        row.ManualDate is { } manual ? new DateSource.Absolute(manual) : runSource,
                        row.ManualTargets ?? targets,
                        RuleGuards.None)],
                    ScanFilter.Default,
                    ModeFor(row.ManualTargets ?? targets, this.Mode))
                : recipe;

            row.Plan = this._evaluator.Evaluate(row.File, forRow, context);
        }

        this.Reproject();
    }

    /// <summary>
    /// Whether a row survives the type filter.
    ///
    /// This filter differs from the other two in a way that matters. "Only changing" and
    /// "only problems" narrow what you LOOK at; this narrows what the run COVERS. A folder
    /// off a camera holds JPEG, PNG and raw together, and wanting to touch only one of
    /// those is an ordinary request that unticking several hundred rows by hand is a silly
    /// way to answer.
    ///
    /// Because it scopes the run it is stated on the Apply button and in the confirmation.
    /// A filter that quietly changes what a destructive button does is how you get a bug
    /// report titled "it changed files I did not select".
    /// </summary>
    private bool MatchesTypeFilter(PlanRowViewModel row)
    {
        if (this._typePatterns.Count == 0)
        {
            return true;
        }

        foreach (string pattern in this._typePatterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, row.Name, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Moves the shown list to what the projection says, and says nothing at all when the
    /// projection has not moved.
    ///
    /// That silent case is the ordinary one and it is the whole reason this exists.
    /// Picking a date, nudging a shift, taking one row's date for the run - none of those
    /// change which rows are shown or what order they are in, yet every one of them used
    /// to hand the grid a brand new list, which makes it discard every container and
    /// rebuild: scroll back to the top, thumbnails re-requested, for nothing.
    ///
    /// When rows genuinely have come, gone or moved, this is still one reset, exactly as
    /// before - no worse, and a real diff can come later if the jump on removing a single
    /// row turns out to be worth more code than it costs.
    /// </summary>
    private void SyncRows(List<PlanRowViewModel> projected)
    {
        if (this.Rows.Count == projected.Count)
        {
            bool same = true;

            for (int i = 0; i < projected.Count && same; i++)
            {
                // Reference equality on purpose. These are the same row objects a
                // recompute mutated in place; only their identity and order matter here.
                same = ReferenceEquals(this.Rows[i], projected[i]);
            }

            if (same)
            {
                return;
            }
        }

        this.Rows.ResetTo(projected);
    }

    /// <summary>Applies the current filter and sort. Cheap: no evaluation happens here.</summary>
    private void Reproject()
    {
        IEnumerable<PlanRowViewModel> query = this._allRows.Where(this.MatchesTypeFilter);

        if (this.ShowOnlyChanging)
        {
            query = query.Where(r => r.Plan?.WillWrite == true);
        }

        if (this.ShowOnlyProblems)
        {
            query = query.Where(r => r.Plan?.HasProblem == true || r.Plan?.IsSuspicious == true);
        }

        // The name is always the tie-break, whatever the primary key is, so that two files
        // the sort cannot separate still come out in a stable and obvious order.
        IOrderedEnumerable<PlanRowViewModel> ordered = this.Sort switch
        {
            SortChoice.BiggestChange => this.SortDescending
                ? query.OrderByDescending(r => r.SortDeltaTicks)
                : query.OrderBy(r => r.SortDeltaTicks),

            SortChoice.ResultingDate => this.SortDescending
                ? query.OrderByDescending(r => r.SortAfterDate ?? DateTimeOffset.MinValue)
                : query.OrderBy(r => r.SortAfterDate ?? DateTimeOffset.MaxValue),

            SortChoice.Status => this.SortDescending
                ? query.OrderByDescending(r => (int)r.SortStatus)
                : query.OrderBy(r => r.SortStatus),

            _ => this.SortDescending
                ? query.OrderByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                : query.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        query = ordered.ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);

        this.SyncRows([.. query]);
        this.RefreshSummary();
        this.OnPropertyChanged(nameof(this.IsFilteredToNothing));
        this.OnPropertyChanged(nameof(this.FilteredToNothingNote));
        this.OnPropertyChanged(nameof(this.HasAnyFiles));
        this.OnPropertyChanged(nameof(this.IsListEmpty));
        this.OnPropertyChanged(nameof(this.CanStartOver));

        // The filters decide whether the deck's toggles can be turned OFF again, which is
        // not something Summary knows about.
        this.NotifyDeck();

        // Carets, the off-column sort chip, and the two selection scopes - all of which
        // move with the projection rather than with the summary.
        this.NotifySortState();
    }

    private Recipe BuildRecipe()
    {
        // A template that is in charge stays in charge, because it can say more than the
        // pane can show - an aggregate over several source fields, for instance. Its
        // targets are still reflected in the checkboxes, so the pane is never describing
        // a different run; it just cannot express all of this one.
        if (this.ActiveTemplate is { } template)
        {
            return new Recipe([template.ToRule()], ScanFilter.Default, this.Mode);
        }

        var targets = new HashSet<DateField>();

        if (this.WriteCreated)
        {
            _ = targets.Add(DateField.FileCreated);
        }

        if (this.WriteModified)
        {
            _ = targets.Add(DateField.FileModified);
        }

        if (this.WriteChanged)
        {
            _ = targets.Add(DateField.FileChanged);
        }

        // Taken stands alone perfectly well, and for anything that reads a file's own date
        // it is the ONLY correct choice: an upload reads the taken date and ignores the
        // file dates entirely.
        // Nothing here requires Created or Modified to be ticked alongside it.
        if (this.WriteTaken && this.IsPhotoMode)
        {
            _ = targets.Add(DateField.ExifDateTimeOriginal);
        }

        // Until a date is actually chosen there is no run to describe, and saying nothing
        // beats inventing one.
        //
        // This used to default to today at noon, so adding files immediately produced
        // "2 of 2 will change" and a preview proposing to stamp everything with today,
        // before anyone had made a single decision. Reported as rows showing the wrong
        // date - and they were: they showed a date nobody had asked for. An app that edits
        // irreplaceable files must not arrive with a destructive plan already loaded.
        DateSource source = this.Source switch
        {
            SourceChoice.ShiftBy => new DateSource.Shift(TimeSpan.FromHours(this.ShiftHours), ShiftBasis.WallClock),
            SourceChoice.FromAnotherDate => new DateSource.CopyFrom(Aggregate.FirstPresent, [this.CopyFromField]),
            SourceChoice.FromFileName => new DateSource.FromFileName(
                this.CustomPatternTokens is null ? string.Empty : CustomPatternId),
            // Unset until a date is picked. The targets stay in the recipe either way, so
            // the app goes on saying that photo dates need ExifTool and that a field
            // cannot be written - warnings that are just as true before the date is chosen.
            _ => this.AbsoluteDate is { } picked
                ? new DateSource.Absolute(new DateTimeOffset(picked.Date.Add(this.AbsoluteTime), DateTimeOffset.Now.Offset))
                : new DateSource.Unset(),
        };

        return new Recipe(
            [new DateRule(source, targets, RuleGuards.None)],
            ScanFilter.Default,
            this.Mode);
    }
}

