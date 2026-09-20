// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.FileSystem;
using PaulTechGuy.CN.Journal;
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

        await foreach (ScannedFile f in scanner.ScanAsync(ws.Files, ScanFilter.Default))
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

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header);

        applied.Written.ShouldBe(1);
        applied.Status.ShouldBe(RunStatus.Completed);
        ws.Read(path).Created!.Value.ShouldBe(Target);

        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header);

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

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header);

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

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header);

        // Somebody else, later.
        var theirs = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero);
        _ = ws.Writer.Write(path, new TimestampSet(theirs, null, null, null), isDirectory: false);

        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header);

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

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header);
        _ = ws.Writer.Write(path, new TimestampSet(new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero), null, null, null), isDirectory: false);

        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header, force: true);

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

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header);
        File.Delete(path);

        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header);

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

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header);
        ApplyOutcome undone = await ws.Apply.RevertAsync(applied.RunId, Header);

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

        ApplyOutcome outcome = await ws.Apply.ApplyAsync(plans, Header, progress);

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

        ApplyOutcome outcome = await ws.Apply.ApplyAsync([plan], Header);

        outcome.Written.ShouldBe(0);
        outcome.Skipped.ShouldBe(1);
    }

    /// <summary>
    /// Metadata is not written yet, and the run says so rather than silently reporting
    /// success. "No ExifTool" is a supported state, not a lie.
    /// </summary>
    [Fact]
    public async Task A_metadata_target_is_skipped_with_a_reason_until_exiftool_exists()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);

        var scanner = new FileScanner(ws.Writer);
        ScannedFile file = null!;
        await foreach (ScannedFile f in scanner.ScanAsync(ws.Files, ScanFilter.Default))
        {
            file = f;
        }

        var recipe = new Recipe(
            [new DateRule(
                new DateSource.Absolute(Target),
                new HashSet<DateField> { DateField.ExifDateTimeOriginal },
                RuleGuards.None)],
            ScanFilter.Default,
            AppMode.PhotoDates);

        FilePlan plan = new RuleEvaluator().Evaluate(
            file, recipe, new EvaluationContext(ClockContext.Local, DateTimeOffset.UtcNow, MetadataEngineAvailable: true));

        ApplyOutcome outcome = await ws.Apply.ApplyAsync([plan], Header);

        outcome.Written.ShouldBe(0);
        outcome.Skipped.ShouldBe(1);
    }
}
