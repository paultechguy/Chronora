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
    private readonly IAppPaths _paths;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<WorkbenchViewModel> _logger;

    private readonly List<PlanRowViewModel> _allRows = [];
    private readonly List<string> _roots = [];

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

    private CancellationTokenSource? _debounce;
    private CancellationTokenSource? _run;

    public WorkbenchViewModel(
        FileScanner scanner,
        RuleEvaluator evaluator,
        ApplyService apply,
        SqliteJournal journal,
        ExifToolService exifTool,
        MetadataGateway metadata,
        TemplateStore templates,
        IAppPaths paths,
        IUiDispatcher dispatcher,
        ILogger<WorkbenchViewModel> logger)
    {
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
        this.Rows = [];
        this.Templates = templates.All;
        this.RefreshHistory();
    }

    /// <summary>Rows as the grid shows them: filtered and sorted.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlanRowViewModel> Rows { get; set; }

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

                    // Offered on this path but not chosen for them, and Changed belongs to
                    // Advanced - so picking this answer always lands on the same two boxes
                    // rather than inheriting whatever the previous answer left behind.
                    this.WriteAccessed = false;
                    this.WriteChanged = false;
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

        // Accessed counts as an ordinary file date here, because Explorer shows it beside
        // Created and Modified and the pane now offers it there too. Ticking it should not
        // relabel the answer as "let me pick the fields" - the user has not left the
        // simple path, they have used the third control on it.
        //
        // Changed still forces Custom: it is an Advanced field nobody reaches by accident,
        // and reaching it IS picking fields by hand.
        bool anyFileDate = this.WriteCreated || this.WriteModified || this.WriteAccessed;

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

        // Depends on the intent as well as the list, so choosing an intent has to raise
        // it. Without this the Start over button stayed disabled after picking an intent -
        // precisely the moment someone who picked the wrong one wants it. Found by the
        // notification test rather than by a person, which is the point of that test.
        this.OnPropertyChanged(nameof(this.HasAnyFiles));
        this.OnPropertyChanged(nameof(this.IsListEmpty));
        this.OnPropertyChanged(nameof(this.CanStartOver));
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
            this.WriteAccessed = template.Targets.Contains(DateField.FileAccessed);
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
        this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Using “{template.Name}”.");
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
        this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Saved “{template.Name}”.");
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
        this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Deleted “{template.Name}”.");
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
        this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Copied to “{copy.Name}”.");
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

        // A banner offering to undo a list action is stale the moment somebody moves on to
        // configuring the run: carrying on IS accepting the list. That it never went away
        // on its own is the other half of why the banner looked like it was reacting to
        // every option change.
        this.ActionNotice = null;

        if (this.ActiveTemplate is not { } template)
        {
            return;
        }

        this.ActiveTemplate = null;

        // Said, but quietly. It still has to be said - dropping the template can change what
        // Apply does in ways the controls cannot show - but it follows an ordinary option
        // change, and a banner on every one of those is the overkill reported.
        this.ScanStatus = string.Create(
            CultureInfo.CurrentCulture,
            $"Stopped using “{template.Name}” because you changed the options.");

        this.NotifyTemplateState();
    }

    private void NotifyTemplateState()
    {
        this.OnPropertyChanged(nameof(this.HasActiveTemplate));
        this.OnPropertyChanged(nameof(this.ActiveTemplateNote));
        this.OnPropertyChanged(nameof(this.CanDeleteActiveTemplate));
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
        this.NotifyIntentDerived();
        this.QueueRecompute();
    }

    // Only the input the chosen source actually uses is shown. Rendering all three at once
    // made the pane taller than the window and invited people to fill in a field that was
    // going to be ignored.
    public bool NeedsAbsoluteInput => this.Source == SourceChoice.PickADate;

    public bool NeedsShiftInput => this.Source == SourceChoice.ShiftBy;

    public bool NeedsCopyFromInput => this.Source == SourceChoice.FromAnotherDate;

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
    public partial bool WriteAccessed { get; set; }

    partial void OnWriteAccessedChanged(bool value)
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

    partial void OnSortChanged(SortChoice value) => this.Reproject();

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
            .Select(p => p.Contains('*', StringComparison.Ordinal) || p.Contains('?', StringComparison.Ordinal)
                ? p
                : "*" + (p.StartsWith('.') ? p : "." + p))];

        this.OnPropertyChanged(nameof(this.HasTypeFilter));
        this.OnPropertyChanged(nameof(this.CanStartOver));
        this.Reproject();
    }

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
        this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Reading photo dates from {candidates.Count:N0} files…");

        try
        {
            var progress = new Progress<int>(done => this.ScanStatus = string.Create(
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

            this.ScanStatus = string.Create(
                CultureInfo.CurrentCulture, $"Read photo dates from {read.Count:N0} of {candidates.Count:N0} files.");

            // The snapshot changed, so every plan built against the old one is stale.
            this.Recompute();
        }
        catch (OperationCanceledException)
        {
            this.ScanStatus = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // The file dates are already on screen and still correct, so this costs the
            // photo dates rather than the whole scan.
            this._logger.LogWarning(ex, "Could not read photo dates.");
            this.ScanStatus = "The file dates were read, but the photo dates could not be.";
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



        this.IsScanning = true;
        this.ScanStatus = $"Reading {folder}…";

        try
        {
            int added = 0;

            await foreach (ScannedFile file in this._scanner.ScanAsync(folder, filter, cancellationToken))
            {
                this._allRows.Add(this.TrackRow(new PlanRowViewModel(file)));
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

            await this.ReadMetadataAsync(cancellationToken).ConfigureAwait(true);
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
                PlanRowViewModel row = this.TrackRow(new PlanRowViewModel(file));
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

            this._logger.LogInformation(
                "Added {Added} file(s); the list now holds {Total}.",
                this._rowsFromDrop.Count,
                this._allRows.Count);

            await this.ReadMetadataAsync(cancellationToken).ConfigureAwait(true);
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

    /// <summary>The filter, for the message that says what is hiding everything.</summary>
    public string FilteredToNothingNote => string.Create(
        CultureInfo.CurrentCulture,
        $"Nothing matches {this.TypeFilter}. {this._allRows.Count:N0} file{(this._allRows.Count == 1 ? string.Empty : "s")} are hidden by it.");

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
        DateTemplate? template = this.ActiveTemplate;

        this._allRows.Clear();
        this._roots.Clear();
        this.SelectedRow = null;
        this.ScanStatus = string.Empty;
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
            this.WriteAccessed = false;
            this.WriteChanged = false;
            this.WriteTaken = false;
        }
        finally
        {
            this._applyingIntent = false;
            this._applyingTemplate = false;
        }

        this.NotifyIntentDerived();
        this.NotifyTemplateState();
        this.Recompute();

        this.ActionNotice = "Started over.";
        this._undoLastAction = () =>
        {
            this._allRows.AddRange(rows);
            this._roots.AddRange(roots);

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
                this.WriteAccessed = accessed;
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

        this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"Removed {row.Name} from the list.");
        this.Reproject();
        this.OnPropertyChanged(nameof(this.CanStartOver));
    }

    /// <summary>Unticks everything else, so the run covers this file alone.</summary>
    public void SelectOnly(PlanRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        foreach (PlanRowViewModel other in this._allRows)
        {
            other.IsIncluded = ReferenceEquals(other, row);
        }

        this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"The run now covers {row.Name} only.");
        this.RefreshSummary();
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
            this.ScanStatus = string.Create(CultureInfo.CurrentCulture, $"{row.Name} has no date to copy.");
            return false;
        }

        DateTimeOffset local = value.ToLocalTime();

        this.Source = SourceChoice.PickADate;
        this.AbsoluteDate = local.Date;
        this.AbsoluteTime = local.TimeOfDay;

        this.ScanStatus = string.Create(
            CultureInfo.CurrentCulture,
            $"The run will use {local:yyyy-MM-dd HH:mm}, taken from {row.Name}.");

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

    /// <summary>
    /// Sets a date for this file alone. Null clears it and hands the row back to the run.
    /// </summary>
    public void SetManualDate(PlanRowViewModel row, DateTimeOffset? value)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.ManualDate = value;

        this.ScanStatus = value is { } set
            ? string.Create(CultureInfo.CurrentCulture, $"{row.Name} is set to {set:yyyy-MM-dd HH:mm} by hand.")
            : string.Create(CultureInfo.CurrentCulture, $"{row.Name} follows the run again.");

        this.Recompute();
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
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlanRowViewModel.IsIncluded))
            {
                this.RefreshSummary();
            }
        };

        return row;
    }

    /// <summary>
    /// Ticks everything currently shown - not everything loaded, because a type filter
    /// narrows what the run covers and selecting files it is excluding would contradict it.
    /// </summary>
    [RelayCommand]
    public void SelectAllShown()
    {
        foreach (PlanRowViewModel row in this.Rows)
        {
            row.IsIncluded = true;
        }

        this.RefreshSummary();
    }

    /// <summary>
    /// Unticks everything loaded, including anything a filter is hiding.
    ///
    /// Deliberately wider than SelectAllShown. Both err the same way: selecting covers only
    /// what you can see, and deselecting covers everything - so neither can leave a file
    /// ticked that you never laid eyes on.
    /// </summary>
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
    public void RefreshSummary() =>
        this.Summary = ChangeSummary.Build(
            [.. this._allRows.Where(this.MatchesTypeFilter)],
            this.BuildRecipe().AllTargets,
            this.HasTypeFilter ? this.TypeFilter : null,
            this._allRows.Count);

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
        // Scoped by the type filter as well as the checkboxes, because that filter narrows
        // what the RUN covers rather than only what the list shows. The Apply button and
        // the confirmation both say so.
        List<FilePlan> plans = [.. this._allRows
            .Where(this.MatchesTypeFilter)
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

    /// <summary>History as the window shows it, newest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<HistoryRowViewModel> HistoryRows { get; set; } = [];

    public bool HasHistory => this.HistoryRows.Count > 0;

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

        this.ScanStatus = removed == 0
            ? "History was already empty."
            : string.Create(
                CultureInfo.CurrentCulture,
                $"History cleared. {removed:N0} run{(removed == 1 ? string.Empty : "s")} deleted; nothing on disk changed.");

        return removed;
    }

    public void RefreshHistory()
    {
        this.History = this._journal.ListRuns(50);
        this.HistoryRows = [.. this.History.Select(r => new HistoryRowViewModel(r))];

        this.OnPropertyChanged(nameof(this.CanUndo));
        this.OnPropertyChanged(nameof(this.HasHistory));
        this.OnPropertyChanged(nameof(this.RetentionNote));
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

        if (this.WriteAccessed)
        {
            targets.Add("Accessed");
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

        foreach (PlanRowViewModel row in this._allRows)
        {
            // A hand-typed date replaces the source for that row and nothing else: its
            // targets, guards and mode stay exactly as the run says. The override is about
            // WHERE the date comes from, not about exempting the file from the rules.
            Recipe forRow = row.ManualDate is { } manual
                ? new Recipe(
                    [new DateRule(new DateSource.Absolute(manual), targets, RuleGuards.None)],
                    ScanFilter.Default,
                    this.Mode)
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

        query = this.Sort switch
        {
            SortChoice.BiggestChange => query.OrderByDescending(r => r.SortDeltaTicks).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            SortChoice.ResultingDate => query.OrderBy(r => r.SortAfterDate ?? DateTimeOffset.MaxValue).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            SortChoice.Status => query.OrderByDescending(r => (int)r.SortStatus).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        this.Rows = [.. query];
        this.RefreshSummary();
        this.OnPropertyChanged(nameof(this.IsFilteredToNothing));
        this.OnPropertyChanged(nameof(this.FilteredToNothingNote));
        this.OnPropertyChanged(nameof(this.HasAnyFiles));
        this.OnPropertyChanged(nameof(this.IsListEmpty));
        this.OnPropertyChanged(nameof(this.CanStartOver));
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

        if (this.WriteAccessed)
        {
            _ = targets.Add(DateField.FileAccessed);
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
            SourceChoice.FromFileName => new DateSource.FromFileName(string.Empty),
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

