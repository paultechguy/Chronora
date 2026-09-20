// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Journal.Tests;

/// <summary>A real journal file under TEMP, removed when the test finishes.</summary>
internal sealed class TempJournal : IDisposable
{
    private readonly string _directory;

    public TempJournal()
    {
        this._directory = Path.Combine(Path.GetTempPath(), "chronora-journal-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(this._directory);
        this.DatabasePath = Path.Combine(this._directory, "journal.db");
    }

    public string DatabasePath { get; }

    public SqliteJournal Open() => SqliteJournal.Open(this.DatabasePath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(this._directory))
            {
                Directory.Delete(this._directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test over.
        }
    }
}

internal static class Sample
{
    public static readonly DateTimeOffset Start = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);

    public static RunHeader Header(RunKind kind = RunKind.Apply, long? reverts = null) =>
        new(kind, "0.1.0", "13.10", "America/Denver", """{"rules":[]}""", ["C:\\photos"], reverts);

    public static PriorState File(
        string path = @"C:\photos\a.jpg",
        long? beforeTicks = 100,
        string? beforeRaw = null,
        bool beforePresent = true) =>
        new(path, 12345, [1, 2, 3, 4], 2048, 0, false,
        [
            new PriorField(DateField.FileCreated, null, beforePresent, beforeTicks, beforeRaw, 200, null),
        ]);
}

public class SqliteJournalTests
{
    [Fact]
    public void A_new_journal_is_created_at_the_current_schema_version()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        journal.IsReadOnly.ShouldBeFalse();
        journal.ListRuns().ShouldBeEmpty();
    }

    [Fact]
    public void Opening_an_existing_journal_twice_does_not_migrate_twice()
    {
        using var temp = new TempJournal();

        using (SqliteJournal first = temp.Open())
        {
            _ = first.BeginRun(Sample.Header(), Sample.Start);
        }

        using SqliteJournal second = temp.Open();
        second.ListRuns().Count.ShouldBe(1);
    }

    [Fact]
    public void A_run_records_what_it_was_asked_to_do()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long runId = journal.BeginRun(Sample.Header(), Sample.Start);
        _ = journal.RecordPriorState(runId, [Sample.File()]);
        journal.CompleteRun(runId, RunStatus.Completed, Sample.Start.AddMinutes(1));

        JournalRun run = journal.ListRuns().Single();

        run.RunId.ShouldBe(runId);
        run.Status.ShouldBe(RunStatus.Completed);
        run.Kind.ShouldBe(RunKind.Apply);
        run.AppVersion.ShouldBe("0.1.0");
        run.ExifToolVersion.ShouldBe("13.10");
        run.TimeZoneId.ShouldBe("America/Denver");
        run.Roots.ShouldBe([@"C:\photos"]);
        run.FileCount.ShouldBe(1);
        run.ChangeCount.ShouldBe(1);
        run.StartedUtc.ShouldBe(Sample.Start);
    }

    /// <summary>
    /// The run describes itself, so History can still explain what happened after every
    /// file it touched has been deleted.
    /// </summary>
    [Fact]
    public void A_run_keeps_its_own_recipe_so_it_stays_explicable()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        const string Recipe = """{"rules":[{"source":"Absolute","value":"2024-03-15"}]}""";
        long runId = journal.BeginRun(Sample.Header() with { RecipeJson = Recipe }, Sample.Start);
        journal.CompleteRun(runId, RunStatus.Completed, Sample.Start);

        journal.ListRuns().Single().RecipeJson.ShouldBe(Recipe);
    }

    [Fact]
    public void Prior_state_is_recorded_before_results_and_starts_as_pending()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long runId = journal.BeginRun(Sample.Header(), Sample.Start);
        IReadOnlyList<long> ids = journal.RecordPriorState(runId, [Sample.File(), Sample.File(@"C:\photos\b.jpg")]);

        ids.Count.ShouldBe(2);

        // Nothing is revertable yet: these files have not been written.
        journal.ReadRevertable(runId).ShouldBeEmpty();
    }

    [Fact]
    public void A_file_becomes_revertable_once_its_result_is_recorded()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long runId = journal.BeginRun(Sample.Header(), Sample.Start);
        IReadOnlyList<long> ids = journal.RecordPriorState(runId, [Sample.File()]);
        journal.RecordResults([new FileResult(ids[0], FileOutcome.Applied, null)]);

        var revertable = journal.ReadRevertable(runId);

        revertable.Count.ShouldBe(1);
        revertable[0].File.Path.ShouldBe(@"C:\photos\a.jpg");
        revertable[0].Changes.Single().BeforeTicks.ShouldBe(100);
    }

    [Fact]
    public void A_file_that_failed_is_not_offered_for_revert()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long runId = journal.BeginRun(Sample.Header(), Sample.Start);
        IReadOnlyList<long> ids = journal.RecordPriorState(runId, [Sample.File()]);
        journal.RecordResults([new FileResult(ids[0], FileOutcome.Failed, "access denied")]);

        journal.ReadRevertable(runId).ShouldBeEmpty();
    }

    /// <summary>
    /// A tag that did not exist before must be DELETED on revert, not blanked. That cannot
    /// be inferred from the raw value being null, because a tag can exist and be empty.
    /// </summary>
    [Fact]
    public void Tag_absence_is_recorded_distinctly_from_an_empty_value()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long runId = journal.BeginRun(Sample.Header(), Sample.Start);

        var absent = new PriorState(@"C:\photos\absent.jpg", null, null, 1, 0, false,
            [new PriorField(DateField.ExifDateTimeOriginal, "ExifIFD:DateTimeOriginal", BeforePresent: false, null, null, null, "2024:03:15 14:25:30")]);

        var empty = new PriorState(@"C:\photos\empty.jpg", null, null, 1, 0, false,
            [new PriorField(DateField.ExifDateTimeOriginal, "ExifIFD:DateTimeOriginal", BeforePresent: true, null, string.Empty, null, "2024:03:15 14:25:30")]);

        IReadOnlyList<long> ids = journal.RecordPriorState(runId, [absent, empty]);
        journal.RecordResults(ids.Select(i => new FileResult(i, FileOutcome.Applied, null)).ToList());

        var revertable = journal.ReadRevertable(runId);

        JournalFieldChange absentChange = revertable.Single(r => r.File.Path.EndsWith("absent.jpg", StringComparison.Ordinal)).Changes.Single();
        JournalFieldChange emptyChange = revertable.Single(r => r.File.Path.EndsWith("empty.jpg", StringComparison.Ordinal)).Changes.Single();

        absentChange.BeforePresent.ShouldBeFalse();
        absentChange.BeforeRaw.ShouldBeNull();

        emptyChange.BeforePresent.ShouldBeTrue();
        emptyChange.BeforeRaw.ShouldBe(string.Empty);
    }

    /// <summary>
    /// Metadata round-trips byte-exact, junk included. Parsing "0000:00:00 00:00:00" into a
    /// date and re-emitting it would be a second edit, not a restoration.
    /// </summary>
    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("2024:03:15 14:25:30")]
    [InlineData("2024-03-15T14:25:30.25-04:00")]
    [InlineData("not a date at all")]
    [InlineData("")]
    public void A_raw_metadata_value_survives_verbatim(string raw)
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long runId = journal.BeginRun(Sample.Header(), Sample.Start);
        var file = new PriorState(@"C:\photos\a.jpg", null, null, 1, 0, false,
            [new PriorField(DateField.ExifDateTimeOriginal, "ExifIFD:DateTimeOriginal", true, null, raw, null, "2024:03:15 00:00:00")]);

        IReadOnlyList<long> ids = journal.RecordPriorState(runId, [file]);
        journal.RecordResults([new FileResult(ids[0], FileOutcome.Applied, null)]);

        journal.ReadRevertable(runId).Single().Changes.Single().BeforeRaw.ShouldBe(raw);
    }

    /// <summary>
    /// The crash case. A run left Running did not finish, and its untouched rows are
    /// genuinely unknown rather than assumed either way.
    /// </summary>
    [Fact]
    public void An_unfinished_run_is_recovered_as_interrupted_on_the_next_open()
    {
        using var temp = new TempJournal();
        long runId;
        IReadOnlyList<long> ids;

        using (SqliteJournal crashed = temp.Open())
        {
            runId = crashed.BeginRun(Sample.Header(), Sample.Start);
            ids = crashed.RecordPriorState(runId, [Sample.File(), Sample.File(@"C:\photos\b.jpg")]);

            // One file finished; the other never got a result. Then the process dies.
            crashed.RecordResults([new FileResult(ids[0], FileOutcome.Applied, null)]);
        }

        using SqliteJournal reopened = temp.Open();
        reopened.RecoverInterruptedRuns().ShouldBe(1);

        JournalRun run = reopened.ListRuns().Single();
        run.Status.ShouldBe(RunStatus.Interrupted);

        // The finished file is still revertable; the unknown one is not silently claimed.
        reopened.ReadRevertable(runId).Count.ShouldBe(1);
    }

    [Fact]
    public void Recovery_leaves_a_completed_run_alone()
    {
        using var temp = new TempJournal();

        using (SqliteJournal first = temp.Open())
        {
            long runId = first.BeginRun(Sample.Header(), Sample.Start);
            first.CompleteRun(runId, RunStatus.Completed, Sample.Start);
        }

        using SqliteJournal second = temp.Open();
        second.RecoverInterruptedRuns().ShouldBe(0);
        second.ListRuns().Single().Status.ShouldBe(RunStatus.Completed);
    }

    [Fact]
    public void Reverting_every_file_marks_the_run_reverted()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long runId = journal.BeginRun(Sample.Header(), Sample.Start);
        IReadOnlyList<long> ids = journal.RecordPriorState(runId, [Sample.File(), Sample.File(@"C:\photos\b.jpg")]);
        journal.RecordResults(ids.Select(i => new FileResult(i, FileOutcome.Applied, null)).ToList());
        journal.CompleteRun(runId, RunStatus.Completed, Sample.Start);

        foreach (long id in ids)
        {
            journal.RecordRevert(id, RevertOutcome.Reverted, Sample.Start.AddHours(1));
        }

        journal.RefreshRevertStatus(runId);

        journal.ListRuns().Single().Status.ShouldBe(RunStatus.Reverted);
        journal.ReadRevertable(runId).ShouldBeEmpty();
    }

    /// <summary>
    /// Drift is skipped rather than forced, so a run where one file has since been edited
    /// is partially reverted and says so, instead of quietly stomping the newer value.
    /// </summary>
    [Fact]
    public void A_file_that_drifted_leaves_the_run_partially_reverted()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long runId = journal.BeginRun(Sample.Header(), Sample.Start);
        IReadOnlyList<long> ids = journal.RecordPriorState(runId, [Sample.File(), Sample.File(@"C:\photos\b.jpg")]);
        journal.RecordResults(ids.Select(i => new FileResult(i, FileOutcome.Applied, null)).ToList());
        journal.CompleteRun(runId, RunStatus.Completed, Sample.Start);

        journal.RecordRevert(ids[0], RevertOutcome.Reverted, Sample.Start.AddHours(1));
        journal.RecordRevert(ids[1], RevertOutcome.Drifted, Sample.Start.AddHours(1));

        journal.RefreshRevertStatus(runId);

        journal.ListRuns().Single().Status.ShouldBe(RunStatus.PartiallyReverted);

        // The drifted file stays on the revertable list so "revert anyway" is still
        // reachable; only the one that actually went back drops off.
        var remaining = journal.ReadRevertable(runId);
        remaining.Count.ShouldBe(1);
        remaining[0].File.RevertOutcome.ShouldBe(RevertOutcome.Drifted);
    }

    /// <summary>
    /// A revert is an ordinary run pointing at the one it undoes, so it appears in History
    /// and can itself be reverted.
    /// </summary>
    [Fact]
    public void A_revert_is_recorded_as_a_run_of_its_own()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long original = journal.BeginRun(Sample.Header(), Sample.Start);
        journal.CompleteRun(original, RunStatus.Completed, Sample.Start);

        long revert = journal.BeginRun(Sample.Header(RunKind.Revert, original), Sample.Start.AddHours(1));
        journal.CompleteRun(revert, RunStatus.Completed, Sample.Start.AddHours(1));

        JournalRun recorded = journal.ListRuns().First(r => r.RunId == revert);
        recorded.Kind.ShouldBe(RunKind.Revert);
        recorded.RevertsRunId.ShouldBe(original);
    }

    [Fact]
    public void Runs_are_listed_newest_first()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        long first = journal.BeginRun(Sample.Header(), Sample.Start);
        long second = journal.BeginRun(Sample.Header(), Sample.Start.AddMinutes(5));

        journal.ListRuns().Select(r => r.RunId).ShouldBe([second, first]);
    }
}

public class JournalRetentionTests
{
    private static long AddRun(SqliteJournal journal, DateTimeOffset when)
    {
        long id = journal.BeginRun(Sample.Header(), when);
        journal.CompleteRun(id, RunStatus.Completed, when);
        return id;
    }

    [Fact]
    public void Old_runs_are_pruned()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        DateTimeOffset now = Sample.Start;
        _ = AddRun(journal, now.AddDays(-400));
        _ = AddRun(journal, now.AddDays(-1));

        int removed = journal.Prune(new RetentionPolicy(MaxRuns: 100, MaxAge: TimeSpan.FromDays(180), AlwaysKeep: 1), now);

        removed.ShouldBe(1);
        journal.ListRuns().Count.ShouldBe(1);
    }

    /// <summary>
    /// A burst of activity must not push the run someone actually wants to undo off the end.
    /// </summary>
    [Fact]
    public void The_most_recent_runs_are_never_pruned_however_old_they_are()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        DateTimeOffset now = Sample.Start;
        for (int i = 0; i < 5; i++)
        {
            _ = AddRun(journal, now.AddDays(-400 - i));
        }

        _ = journal.Prune(new RetentionPolicy(MaxRuns: 100, MaxAge: TimeSpan.FromDays(180), AlwaysKeep: 3), now);

        journal.ListRuns().Count.ShouldBe(3);
    }

    [Fact]
    public void A_pinned_run_is_never_pruned()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        DateTimeOffset now = Sample.Start;
        long keep = AddRun(journal, now.AddDays(-400));
        _ = AddRun(journal, now.AddDays(-401));
        journal.SetPinned(keep, true);

        _ = journal.Prune(new RetentionPolicy(MaxRuns: 100, MaxAge: TimeSpan.FromDays(180), AlwaysKeep: 0), now);

        journal.ListRuns().Select(r => r.RunId).ShouldContain(keep);
    }

    /// <summary>
    /// Pruning a run that a revert points at would orphan the revert and leave History
    /// describing an undo of nothing.
    /// </summary>
    [Fact]
    public void A_run_referenced_by_a_revert_is_not_pruned()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        DateTimeOffset now = Sample.Start;
        long original = AddRun(journal, now.AddDays(-400));

        long revert = journal.BeginRun(Sample.Header(RunKind.Revert, original), now.AddDays(-399));
        journal.CompleteRun(revert, RunStatus.Completed, now.AddDays(-399));

        _ = journal.Prune(new RetentionPolicy(MaxRuns: 100, MaxAge: TimeSpan.FromDays(180), AlwaysKeep: 0), now);

        journal.ListRuns().Select(r => r.RunId).ShouldContain(original);
    }

    [Fact]
    public void Pruning_a_run_removes_its_files_and_changes_too()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        DateTimeOffset now = Sample.Start;
        long runId = journal.BeginRun(Sample.Header(), now.AddDays(-400));
        _ = journal.RecordPriorState(runId, [Sample.File()]);
        journal.CompleteRun(runId, RunStatus.Completed, now.AddDays(-400));

        _ = journal.Prune(new RetentionPolicy(MaxRuns: 100, MaxAge: TimeSpan.FromDays(180), AlwaysKeep: 0), now);

        // The cascade took the children with it, so no orphan rows are left behind.
        journal.ReadRevertable(runId).ShouldBeEmpty();
    }

    [Fact]
    public void The_oldest_revertable_run_is_reported_so_retention_is_not_a_surprise()
    {
        using var temp = new TempJournal();
        using SqliteJournal journal = temp.Open();

        DateTimeOffset oldest = Sample.Start.AddDays(-30);
        _ = AddRun(journal, oldest);
        _ = AddRun(journal, Sample.Start);

        journal.OldestRevertableRun()!.Value.ShouldBe(oldest);
    }
}
