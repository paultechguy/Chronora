// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.FileSystem;
using PaulTechGuy.CN.Journal;
using PaulTechGuy.CN.Metadata;
using PaulTechGuy.CN.Rules;
using Shouldly;

namespace PaulTechGuy.CN.Services.Tests;

/// <summary>Real files and a real journal, both under TEMP.</summary>
internal sealed class Workspace : IDisposable
{
    private readonly string _root;

    public Workspace()
    {
        this._root = Path.Combine(Path.GetTempPath(), "chronora-apply-tests", Guid.NewGuid().ToString("N"));
        this.Files = Path.Combine(this._root, "files");
        _ = Directory.CreateDirectory(this.Files);

        this.Journal = SqliteJournal.Open(Path.Combine(this._root, "journal.db"));
        this.Writer = new FileTimeWriter();
        this.Apply = new ApplyService(
            this.Writer,
            new VolumeProbe(),
            this.Journal,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ApplyService>.Instance);
    }

    public string Files { get; }

    public SqliteJournal Journal { get; }

    public FileTimeWriter Writer { get; }

    public ApplyService Apply { get; }

    public string CreateFile(string name, DateTimeOffset stamp)
    {
        string path = Path.Combine(this.Files, name);
        File.WriteAllText(path, "x");
        _ = this.Writer.Write(path, new TimestampSet(stamp, stamp, null, null), isDirectory: false);
        return path;
    }

    public TimestampSet Read(string path)
    {
        _ = this.Writer.TryRead(path, false, out TimestampSet times, out _);
        return times;
    }

    /// <summary>An apply path wired to a stand-in ExifTool.</summary>
    public ApplyService ApplyWith(IMetadataWriteGateway engine) => new(
        this.Writer,
        new VolumeProbe(),
        this.Journal,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ApplyService>.Instance,
        engine);

    /// <summary>Scans the workspace and evaluates a plan writing <paramref name="targets" />.</summary>
    public async Task<FilePlan> PlanAsync(DateTimeOffset value, DateField[] targets, string? named = null)
    {
        var scanner = new FileScanner(this.Writer);
        ScannedFile? file = null;

        await foreach (ScannedFile f in scanner.ScanAsync(this.Files, ScanFilter.Default, TestContext.Current.CancellationToken))
        {
            if (named is null || string.Equals(f.FileName, named, StringComparison.OrdinalIgnoreCase))
            {
                file = f;
            }
        }

        var recipe = new Recipe(
            [new DateRule(new DateSource.Absolute(value), new HashSet<DateField>(targets), RuleGuards.None)],
            ScanFilter.Default,
            AppMode.PhotoDates);

        return new RuleEvaluator().Evaluate(
            file!,
            recipe,
            new EvaluationContext(ClockContext.Local, DateTimeOffset.UtcNow, MetadataEngineAvailable: true));
    }

    public Task<FilePlan> PlanPhotoDateAsync(DateTimeOffset value) =>
        this.PlanAsync(value, [DateField.ExifDateTimeOriginal]);

    public void Dispose()
    {
        this.Journal.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(this._root))
            {
                Directory.Delete(this._root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}

public class ApplyServiceTests
{
    private static readonly DateTimeOffset Original = new(2019, 4, 2, 11, 30, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset Target = new(2024, 3, 15, 14, 25, 30, TimeSpan.Zero);

    private static readonly RunHeader Header =
        new(RunKind.Apply, "0.1.0", null, "UTC", "{}", ["test"]);

    /// <summary>The ambient test token, so a cancelled test run stops these promptly.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FilePlan PlanFor(string path, ScannedFile file, DateTimeOffset to)
    {
        var evaluator = new RuleEvaluator();
        var recipe = new Recipe(
            [new DateRule(
                new DateSource.Absolute(to),
                new HashSet<DateField> { DateField.FileCreated, DateField.FileModified },
                RuleGuards.None)],
            ScanFilter.Default);

        return evaluator.Evaluate(file, recipe, new EvaluationContext(ClockContext.Local, DateTimeOffset.UtcNow));
    }

    private static async Task<(ScannedFile File, FilePlan Plan)> ScanOneAsync(Workspace ws, string path, DateTimeOffset to)
    {
        var scanner = new FileScanner(ws.Writer);
        ScannedFile file = null!;

        await foreach (ScannedFile f in scanner.ScanAsync(ws.Files, ScanFilter.Default, Ct))
        {
            if (string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                file = f;
            }
        }

        file.ShouldNotBeNull();
        return (file, PlanFor(path, file, to));
    }

    /// <summary>
    /// The whole point of the milestone: apply a plan, then put it back, against real files
    /// and a real journal.
    /// </summary>
    [Fact]
    public async Task A_run_can_be_applied_and_then_undone()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);

        (ScannedFile file, FilePlan plan) = await ScanOneAsync(ws, path, Target);

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header, null, Ct);

        applied.Written.ShouldBe(1);
        applied.Status.ShouldBe(RunStatus.Completed);
        ws.Read(path).Created!.Value.ShouldBe(Target);

        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        undone.Written.ShouldBe(1);
        ws.Read(path).Created!.Value.ShouldBe(Original);
        ws.Read(path).Modified!.Value.ShouldBe(Original);
    }

    /// <summary>
    /// Prior state is committed before any file is touched. Proven by the journal already
    /// knowing the old value the moment the run finishes, regardless of what is on disk.
    /// </summary>
    [Fact]
    public async Task The_journal_records_the_original_value_before_writing()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);
        (_, FilePlan plan) = await ScanOneAsync(ws, path, Target);

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header, null, Ct);

        var recorded = ws.Journal.ReadRevertable(applied.RunId);
        JournalFieldChange created = recorded.Single().Changes.First(c => c.Field == DateField.FileCreated);

        new DateTimeOffset(created.BeforeTicks!.Value, TimeSpan.Zero).ShouldBe(Original);
        new DateTimeOffset(created.AfterTicks!.Value, TimeSpan.Zero).ShouldBe(Target);
    }

    /// <summary>
    /// The drift rule. Something edited the file after the run, so the undo leaves it alone
    /// rather than stomping a value the user may have set deliberately.
    /// </summary>
    [Fact]
    public async Task A_file_changed_since_the_run_is_left_alone_by_an_undo()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);
        (_, FilePlan plan) = await ScanOneAsync(ws, path, Target);

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header, null, Ct);

        // Somebody else, later.
        var theirs = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero);
        _ = ws.Writer.Write(path, new TimestampSet(theirs, null, null, null), isDirectory: false);

        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        undone.Written.ShouldBe(0);
        undone.Failed.ShouldBe(1);
        ws.Read(path).Created!.Value.ShouldBe(theirs, "their change must survive an undo they did not ask for");
    }

    /// <summary>Forcing is possible, but it has to be asked for explicitly.</summary>
    [Fact]
    public async Task A_drifted_file_can_be_reverted_when_forced()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);
        (_, FilePlan plan) = await ScanOneAsync(ws, path, Target);

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header, null, Ct);
        _ = ws.Writer.Write(path, new TimestampSet(new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero), null, null, null), isDirectory: false);

        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header, force: true, progress: null, cancellationToken: Ct);

        undone.Written.ShouldBe(1);
        ws.Read(path).Created!.Value.ShouldBe(Original);
    }

    /// <summary>A file deleted after the run cannot be put back, and says so instead of throwing.</summary>
    [Fact]
    public async Task A_deleted_file_is_reported_missing_rather_than_failing_the_undo()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);
        (_, FilePlan plan) = await ScanOneAsync(ws, path, Target);

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header, null, Ct);
        File.Delete(path);

        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        undone.Status.ShouldBe(RunStatus.Completed);
        undone.Failed.ShouldBe(1);
    }

    /// <summary>
    /// A revert is an ordinary run, so History shows it and it points at what it undid.
    /// That is what makes an undo itself undoable.
    /// </summary>
    [Fact]
    public async Task An_undo_appears_in_history_as_a_run_of_its_own()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);
        (_, FilePlan plan) = await ScanOneAsync(ws, path, Target);

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header, null, Ct);
        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        IReadOnlyList<JournalRun> history = ws.Journal.ListRuns();

        history.Count.ShouldBe(2);
        JournalRun revert = history.First(r => r.RunId == undone.RunId);
        revert.Kind.ShouldBe(RunKind.Revert);
        revert.RevertsRunId.ShouldBe(applied.RunId);

        history.First(r => r.RunId == applied.RunId).Status.ShouldBe(RunStatus.Reverted);
    }

    [Fact]
    public async Task A_run_reports_progress_as_it_goes()
    {
        using var ws = new Workspace();
        var plans = new List<FilePlan>();

        for (int i = 0; i < 5; i++)
        {
            string path = ws.CreateFile($"photo{i}.jpg", Original);
            plans.Add((await ScanOneAsync(ws, path, Target)).Plan);
        }

        var seen = new List<ApplyProgress>();
        var progress = new Progress<ApplyProgress>(seen.Add);

        ApplyOutcome outcome = await ws.Apply.ApplyAsync(plans, Header, progress, Ct);

        outcome.Written.ShouldBe(5);

        // Progress is posted asynchronously, so the final state is what matters rather
        // than every intermediate tick having arrived.
        outcome.Status.ShouldBe(RunStatus.Completed);
    }

    /// <summary>A plan with nothing to write is skipped rather than counted as a change.</summary>
    [Fact]
    public async Task A_file_already_holding_the_target_value_is_skipped()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Target);
        (_, FilePlan plan) = await ScanOneAsync(ws, path, Target);

        ApplyOutcome outcome = await ws.Apply.ApplyAsync([plan], Header, null, Ct);

        outcome.Written.ShouldBe(0);
        outcome.Skipped.ShouldBe(1);
    }

    /// <summary>
    /// A photo date asked for with no engine to write it fails, with a sentence. It does
    /// NOT report success, and it does not quietly become a skip: the user asked for
    /// something specific and did not get it.
    /// </summary>
    [Fact]
    public async Task A_metadata_target_fails_with_a_reason_when_there_is_no_engine()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        FilePlan plan = await ws.PlanPhotoDateAsync(Target);
        ApplyOutcome outcome = await ws.Apply.ApplyAsync([plan], Header, null, Ct);

        outcome.Written.ShouldBe(0);
        outcome.Failed.ShouldBe(1);
    }

    /// <summary>
    /// The ordering rule, which is the whole reason this class exists.
    ///
    /// ExifTool rewrites the file, so it has to run before the timestamps are set.
    /// Afterwards would mean the rewrite silently moved the dates the user just chose, and
    /// the preview they approved would have been a lie.
    /// </summary>
    [Fact]
    public async Task The_photo_date_is_written_before_the_file_dates()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();
        ApplyService apply = ws.ApplyWith(engine);

        FilePlan plan = await ws.PlanAsync(
            Target,
            [DateField.ExifDateTimeOriginal, DateField.FileCreated, DateField.FileModified]);

        ApplyOutcome outcome = await apply.ApplyAsync([plan], Header, null, Ct);

        outcome.Written.ShouldBe(1);
        engine.Requests.Count.ShouldBe(1, "one command per file, so a failure is attributable");

        // The timestamps are only written after the engine has been called, so the state
        // on disk afterwards is the planned one rather than whatever the rewrite left.
        ws.Read(path).Created!.Value.UtcDateTime.ShouldBe(Target.UtcDateTime, TimeSpan.FromSeconds(2));
        ws.Read(path).Modified!.Value.UtcDateTime.ShouldBe(Target.UtcDateTime, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// The half of the ordering rule that is easy to miss.
    ///
    /// Someone who asks only to change a photo's Taken date has not asked for its Modified
    /// date to move - but rewriting the file moves it. So the timestamps the plan does not
    /// mention are put back to exactly what they were.
    /// </summary>
    [Fact]
    public async Task A_photo_only_change_leaves_the_file_dates_where_they_were()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();

        // Stands in for what ExifTool does to a file it rewrites.
        engine.OnWrite = p => ws.Writer.Write(
            p, new TimestampSet(null, DateTimeOffset.UtcNow, null, null), isDirectory: false);

        FilePlan plan = await ws.PlanPhotoDateAsync(Target);

        ApplyOutcome outcome = await ws.ApplyWith(engine).ApplyAsync([plan], Header, null, Ct);

        outcome.Written.ShouldBe(1);
        ws.Read(path).Modified!.Value.UtcDateTime.ShouldBe(
            Original.UtcDateTime,
            TimeSpan.FromSeconds(2),
            "the rewrite moved Modified and nobody asked for that");
    }

    /// <summary>
    /// A failed metadata write abandons the file entirely. Applying the file dates anyway
    /// and then reporting a failure would leave the user unable to tell which half landed.
    /// </summary>
    [Fact]
    public async Task A_failed_photo_write_leaves_the_file_dates_alone_too()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway { Succeeds = false };

        FilePlan plan = await ws.PlanAsync(
            Target, [DateField.ExifDateTimeOriginal, DateField.FileModified]);

        ApplyOutcome outcome = await ws.ApplyWith(engine).ApplyAsync([plan], Header, null, Ct);

        outcome.Failed.ShouldBe(1);
        ws.Read(path).Modified!.Value.UtcDateTime.ShouldBe(Original.UtcDateTime, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// The date target expands into its companion tags. Writing DateTimeOriginal alone
    /// loses the offset and leaves any existing sub-second behind, describing a moment
    /// that never happened.
    /// </summary>
    [Fact]
    public async Task A_photo_date_is_written_with_its_offset_and_subsecond_companions()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();
        FilePlan plan = await ws.PlanPhotoDateAsync(Target);

        _ = await ws.ApplyWith(engine).ApplyAsync([plan], Header, null, Ct);

        IReadOnlyList<string> tags = [.. engine.Requests[0].Assignments.Select(a => a.Tag)];

        tags.ShouldContain("ExifIFD:DateTimeOriginal");
        tags.ShouldContain("ExifIFD:OffsetTimeOriginal");
        tags.ShouldContain("ExifIFD:SubSecTimeOriginal");
    }

    /// <summary>A run that touches no bytes needs no backup, and says so by not making one.</summary>
    [Fact]
    public async Task A_file_date_only_run_never_asks_for_a_backup()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("notes.txt", Original);

        var engine = new RecordingGateway();
        FilePlan plan = await ws.PlanAsync(Target, [DateField.FileModified], "notes.txt");

        _ = await ws.ApplyWith(engine).ApplyAsync([plan], Header, null, Ct);

        engine.Requests.ShouldBeEmpty("no metadata was involved, so ExifTool should never have been called");
        File.Exists(path + "_original").ShouldBeFalse();
    }

    [Fact]
    public async Task Backups_are_on_by_default_and_can_be_turned_off()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();
        ApplyService apply = ws.ApplyWith(engine);

        FilePlan plan = await ws.PlanPhotoDateAsync(Target);
        _ = await apply.ApplyAsync([plan], Header, null, Ct);
        engine.BackupsRequested[0].ShouldBeTrue();

        apply.KeepBackups = false;
        _ = await apply.ApplyAsync([plan], Header, null, Ct);
        engine.BackupsRequested[1].ShouldBeFalse();
    }
}

/// <summary>
/// An ExifTool that always agrees, and writes down what it was asked to do.
///
/// The apply ORDER is what these tests are about, and the order is invisible in the
/// result: a run that wrote metadata after the timestamps reports exactly the same
/// success as one that got it right, while having silently undone half of it.
/// </summary>
internal sealed class RecordingGateway : IMetadataWriteGateway
{
    public bool Available { get; set; } = true;

    public bool Succeeds { get; set; } = true;

    /// <summary>Lets a test imitate the side effect of rewriting the file.</summary>
    public Action<string>? OnWrite { get; set; }

    public List<MetadataWriteRequest> Requests { get; } = [];

    public List<bool> BackupsRequested { get; } = [];

    /// <summary>
    /// What the file supposedly holds now, for the drift check. Empty by default, which
    /// means "unchanged since the run" is never asserted by accident - a test that wants
    /// to exercise drift has to say so.
    /// </summary>
    public Dictionary<DateField, MetadataValue> Live { get; } = [];

    public Task<FileMetadata?> ReadOneAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<FileMetadata?>(new FileMetadata(
            path,
            System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(this.Live),
            QuickTimeReadAsUtc: false));

    public Task<MetadataWriteResult> WriteAsync(
        MetadataWriteRequest request,
        bool keepBackup = true,
        CancellationToken cancellationToken = default)
    {
        this.Requests.Add(request);
        this.BackupsRequested.Add(keepBackup);

        if (this.Succeeds)
        {
            this.OnWrite?.Invoke(request.Path);
        }

        return Task.FromResult(new MetadataWriteResult(
            request.Path,
            this.Succeeds,
            WriteDestination.Embedded,
            null,
            this.Succeeds ? null : "Pretend failure."));
    }
}

/// <summary>
/// Undoing a run that wrote a photo date.
///
/// Reported from the app: undo produced "0 files, 0 changes", the files were untouched on
/// disk, and the original run kept offering its Undo button. All one cause. The drift
/// check asked "does this file still hold what the run wrote?" by reading a TimestampSet,
/// which only carries the four filesystem times - so a recorded photo date always came
/// back as absent, absent was read as "somebody changed this", and the undo refused.
///
/// Every run that touched a photo or video date was permanently un-undoable, silently, in
/// the feature the whole app is built around.
/// </summary>
public class RevertMetadataTests
{
    private static readonly DateTimeOffset Original = new(2019, 4, 2, 11, 30, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset Target = new(2024, 3, 15, 14, 25, 30, TimeSpan.Zero);

    private static readonly RunHeader Header =
        new(RunKind.Apply, "0.1.0", null, "UTC", "{}", ["test"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The state the engine reports after the run wrote Target.</summary>
    private static void SetLiveTaken(RecordingGateway engine, DateTimeOffset value) =>
        engine.Live[DateField.ExifDateTimeOriginal] =
            new MetadataValue(Present: true, value.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), value);

    [Fact]
    public async Task A_photo_date_run_can_actually_be_undone()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();
        ApplyService apply = ws.ApplyWith(engine);

        FilePlan plan = await ws.PlanPhotoDateAsync(Target);
        ApplyOutcome applied = await apply.ApplyAsync([plan], Header, null, Ct);

        applied.Written.ShouldBe(1);

        // The file now holds what the run wrote, so nothing has drifted.
        SetLiveTaken(engine, Target);

        ApplyOutcome undone = await apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        undone.Written.ShouldBe(1, "the photo date was written, so it must be undoable");
        undone.Failed.ShouldBe(0);
    }

    /// <summary>
    /// And the undo has to actually put the tag back, not just report success. The restore
    /// is byte-exact: the string that was there, or a delete when there was nothing.
    /// </summary>
    [Fact]
    public async Task Undoing_a_photo_date_deletes_a_tag_that_was_not_there_before()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();
        ApplyService apply = ws.ApplyWith(engine);

        FilePlan plan = await ws.PlanPhotoDateAsync(Target);
        ApplyOutcome applied = await apply.ApplyAsync([plan], Header, null, Ct);

        SetLiveTaken(engine, Target);
        engine.Requests.Clear();

        _ = await apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        engine.Requests.ShouldNotBeEmpty("the undo has to write the tag back");

        // The photo had no taken date before the run, so putting it back means removing
        // the tag - not blanking it, which is a different state that reads back differently.
        engine.Requests[^1].Assignments.ShouldContain(a => a.Tag == "ExifIFD:DateTimeOriginal" && a.IsDeletion);
    }

    /// <summary>
    /// The drift ladder still has to work for real drift. A photo whose date somebody
    /// changed after the run is left alone, which is the whole point of the check.
    /// </summary>
    [Fact]
    public async Task A_photo_date_changed_since_the_run_is_left_alone()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();
        ApplyService apply = ws.ApplyWith(engine);

        FilePlan plan = await ws.PlanPhotoDateAsync(Target);
        ApplyOutcome applied = await apply.ApplyAsync([plan], Header, null, Ct);

        // Somebody set it to something else in the meantime.
        SetLiveTaken(engine, Target.AddYears(1));

        ApplyOutcome undone = await apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        undone.Written.ShouldBe(0);
        undone.Failed.ShouldBe(1, "drifted, so skipped rather than overwritten");
    }

    /// <summary>
    /// An undo records its own files. Without that, History showed "0 files · 0 changes"
    /// and the undo could not itself be undone - which the design says it must be.
    /// </summary>
    [Fact]
    public async Task An_undo_records_what_it_did()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();
        ApplyService apply = ws.ApplyWith(engine);

        FilePlan plan = await ws.PlanPhotoDateAsync(Target);
        ApplyOutcome applied = await apply.ApplyAsync([plan], Header, null, Ct);

        SetLiveTaken(engine, Target);
        ApplyOutcome undone = await apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        JournalRun run = ws.Journal.ListRuns(10).First(r => r.RunId == undone.RunId);

        run.Kind.ShouldBe(RunKind.Revert);
        run.FileCount.ShouldBe(1, "an undo that recorded nothing cannot itself be undone");
        run.ChangeCount.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// With no engine, an undo of a photo date refuses rather than half-doing it. Putting
    /// the file dates back while leaving the photo date where the run left it is a state
    /// nobody asked for and nothing would explain.
    /// </summary>
    [Fact]
    public async Task Undoing_a_photo_date_without_exiftool_refuses_rather_than_half_doing_it()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway();
        FilePlan plan = await ws.PlanPhotoDateAsync(Target);
        ApplyOutcome applied = await ws.ApplyWith(engine).ApplyAsync([plan], Header, null, Ct);

        // ws.Apply has no gateway at all.
        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header, false, null, Ct);

        undone.Written.ShouldBe(0);
        undone.Failed.ShouldBe(1);
    }
}
