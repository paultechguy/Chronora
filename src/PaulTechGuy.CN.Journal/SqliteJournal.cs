// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Journal;

/// <summary>What a run is about to do, recorded before anything is touched.</summary>
/// <param name="Kind">Apply or revert.</param>
/// <param name="AppVersion">Which build.</param>
/// <param name="ExifToolVersion">Which ExifTool, when one is involved.</param>
/// <param name="TimeZoneId">The zone in force.</param>
/// <param name="RecipeJson">The run's self-description.</param>
/// <param name="Roots">Folders covered.</param>
/// <param name="RevertsRunId">For a revert, the run being undone.</param>
public sealed record RunHeader(
    RunKind Kind,
    string AppVersion,
    string? ExifToolVersion,
    string TimeZoneId,
    string RecipeJson,
    IReadOnlyList<string> Roots,
    long? RevertsRunId = null);

/// <summary>One file's prior state, written before the file is touched.</summary>
public sealed record PriorState(
    string Path,
    long? VolumeSerial,
    byte[]? FileId,
    long SizeBefore,
    int AttributesBefore,
    bool IsDirectory,
    IReadOnlyList<PriorField> Fields);

/// <summary>One field's prior and intended value.</summary>
public sealed record PriorField(
    DateField Field,
    string? TagName,
    bool BeforePresent,
    long? BeforeTicks,
    string? BeforeRaw,
    long? AfterTicks,
    string? AfterRaw);

/// <summary>How one file actually turned out.</summary>
public sealed record FileResult(long FileRowId, FileOutcome Outcome, string? Error);

/// <summary>
/// The undo journal.
///
/// The ordering rule that makes it worth having: a batch's prior state is committed BEFORE
/// any file in that batch is touched. A journal written afterwards is no journal at all,
/// because the crash it needs to survive is the one that happens mid-write.
/// </summary>
public sealed class SqliteJournal : IDisposable
{
    /// <summary>
    /// Files per transaction. One transaction per run would build a huge WAL and lose
    /// everything on a crash; one per file would be tens of thousands of commits. This is
    /// the knee.
    /// </summary>
    public const int BatchSize = 500;

    private readonly SqliteConnection _connection;
    private readonly ILogger<SqliteJournal> _logger;

    private SqliteJournal(SqliteConnection connection, bool readOnly, ILogger<SqliteJournal> logger)
    {
        this._connection = connection;
        this._logger = logger;
        this.IsReadOnly = readOnly;
    }

    /// <summary>True when the file was written by a newer build and must not be modified.</summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// Opens or creates the journal. A database from a newer build opens read-only rather
    /// than being migrated backwards.
    /// </summary>
    public static SqliteJournal Open(string path, ILogger<SqliteJournal>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ILogger<SqliteJournal> log = logger ?? NullLogger<SqliteJournal>.Instance;

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        JournalSchema.ApplyPragmas(connection);

        try
        {
            JournalSchema.EnsureCreated(connection, log);
            return new SqliteJournal(connection, readOnly: false, log);
        }
        catch (JournalTooNewException ex)
        {
            log.LogWarning(ex, "Opening the journal read-only.");
            return new SqliteJournal(connection, readOnly: true, log);
        }
    }

    /// <summary>Starts a run. It is Running until closed, which is how a crash is detected.</summary>
    public long BeginRun(RunHeader header, DateTimeOffset startedUtc)
    {
        ArgumentNullException.ThrowIfNull(header);
        this.ThrowIfReadOnly();

        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs (started_utc, status, kind, reverts_run_id, app_version,
                              exiftool_version, tz_id, recipe_json, roots_json)
            VALUES ($started, $status, $kind, $reverts, $app, $exif, $tz, $recipe, $roots);
            SELECT last_insert_rowid();
            """;

        _ = command.Parameters.AddWithValue("$started", Iso(startedUtc));
        _ = command.Parameters.AddWithValue("$status", (int)RunStatus.Running);
        _ = command.Parameters.AddWithValue("$kind", (int)header.Kind);
        _ = command.Parameters.AddWithValue("$reverts", (object?)header.RevertsRunId ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$app", header.AppVersion);
        _ = command.Parameters.AddWithValue("$exif", (object?)header.ExifToolVersion ?? DBNull.Value);
        _ = command.Parameters.AddWithValue("$tz", header.TimeZoneId);
        _ = command.Parameters.AddWithValue("$recipe", header.RecipeJson);
        _ = command.Parameters.AddWithValue("$roots", string.Join('\n', header.Roots));

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Records what a batch of files looked like BEFORE it is written, in one transaction.
    /// Returns the row ids, in the order given, so results can be matched back.
    /// </summary>
    public IReadOnlyList<long> RecordPriorState(long runId, IReadOnlyList<PriorState> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        this.ThrowIfReadOnly();

        var ids = new List<long>(files.Count);

        using SqliteTransaction transaction = this._connection.BeginTransaction();

        using SqliteCommand insertFile = this._connection.CreateCommand();
        insertFile.Transaction = transaction;
        insertFile.CommandText = """
            INSERT INTO run_files (run_id, path, volume_serial, file_id, size_before,
                                   attrs_before, is_directory, outcome)
            VALUES ($run, $path, $volume, $fileId, $size, $attrs, $dir, $outcome);
            SELECT last_insert_rowid();
            """;

        using SqliteCommand insertChange = this._connection.CreateCommand();
        insertChange.Transaction = transaction;
        insertChange.CommandText = """
            INSERT INTO run_field_changes (file_row_id, field, tag_name, before_present,
                                           before_ticks, before_raw, after_ticks, after_raw, write_status)
            VALUES ($file, $field, $tag, $present, $bTicks, $bRaw, $aTicks, $aRaw, $status);
            """;

        foreach (PriorState file in files)
        {
            insertFile.Parameters.Clear();
            _ = insertFile.Parameters.AddWithValue("$run", runId);
            _ = insertFile.Parameters.AddWithValue("$path", file.Path);
            _ = insertFile.Parameters.AddWithValue("$volume", (object?)file.VolumeSerial ?? DBNull.Value);
            _ = insertFile.Parameters.AddWithValue("$fileId", (object?)file.FileId ?? DBNull.Value);
            _ = insertFile.Parameters.AddWithValue("$size", file.SizeBefore);
            _ = insertFile.Parameters.AddWithValue("$attrs", file.AttributesBefore);
            _ = insertFile.Parameters.AddWithValue("$dir", file.IsDirectory ? 1 : 0);
            _ = insertFile.Parameters.AddWithValue("$outcome", (int)FileOutcome.Pending);

            long fileRowId = Convert.ToInt64(insertFile.ExecuteScalar(), CultureInfo.InvariantCulture);
            ids.Add(fileRowId);

            foreach (PriorField field in file.Fields)
            {
                insertChange.Parameters.Clear();
                _ = insertChange.Parameters.AddWithValue("$file", fileRowId);
                _ = insertChange.Parameters.AddWithValue("$field", (int)field.Field);
                _ = insertChange.Parameters.AddWithValue("$tag", (object?)field.TagName ?? DBNull.Value);
                _ = insertChange.Parameters.AddWithValue("$present", field.BeforePresent ? 1 : 0);
                _ = insertChange.Parameters.AddWithValue("$bTicks", (object?)field.BeforeTicks ?? DBNull.Value);
                _ = insertChange.Parameters.AddWithValue("$bRaw", (object?)field.BeforeRaw ?? DBNull.Value);
                _ = insertChange.Parameters.AddWithValue("$aTicks", (object?)field.AfterTicks ?? DBNull.Value);
                _ = insertChange.Parameters.AddWithValue("$aRaw", (object?)field.AfterRaw ?? DBNull.Value);
                _ = insertChange.Parameters.AddWithValue("$status", (int)FieldWriteStatus.Planned);

                _ = insertChange.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return ids;
    }

    /// <summary>Records how a batch actually turned out, after the writes.</summary>
    public void RecordResults(IReadOnlyList<FileResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        this.ThrowIfReadOnly();

        using SqliteTransaction transaction = this._connection.BeginTransaction();

        using SqliteCommand update = this._connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE run_files SET outcome = $outcome, error = $error WHERE file_row_id = $id;";

        using SqliteCommand updateFields = this._connection.CreateCommand();
        updateFields.Transaction = transaction;
        updateFields.CommandText = """
            UPDATE run_field_changes SET write_status = $status WHERE file_row_id = $id;
            """;

        foreach (FileResult result in results)
        {
            update.Parameters.Clear();
            _ = update.Parameters.AddWithValue("$outcome", (int)result.Outcome);
            _ = update.Parameters.AddWithValue("$error", (object?)result.Error ?? DBNull.Value);
            _ = update.Parameters.AddWithValue("$id", result.FileRowId);
            _ = update.ExecuteNonQuery();

            updateFields.Parameters.Clear();
            _ = updateFields.Parameters.AddWithValue(
                "$status",
                (int)(result.Outcome == FileOutcome.Applied ? FieldWriteStatus.Ok : FieldWriteStatus.Failed));
            _ = updateFields.Parameters.AddWithValue("$id", result.FileRowId);
            _ = updateFields.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Closes a run and rolls up its counts.</summary>
    public void CompleteRun(long runId, RunStatus status, DateTimeOffset finishedUtc)
    {
        this.ThrowIfReadOnly();

        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET
                status       = $status,
                finished_utc = $finished,
                file_count   = (SELECT COUNT(*) FROM run_files WHERE run_id = $id),
                change_count = (SELECT COUNT(*) FROM run_field_changes c
                                JOIN run_files f ON f.file_row_id = c.file_row_id
                                WHERE f.run_id = $id),
                error_count  = (SELECT COUNT(*) FROM run_files WHERE run_id = $id AND outcome = $failed)
            WHERE run_id = $id;
            """;

        _ = command.Parameters.AddWithValue("$status", (int)status);
        _ = command.Parameters.AddWithValue("$finished", Iso(finishedUtc));
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.Parameters.AddWithValue("$failed", (int)FileOutcome.Failed);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Called at startup. A run still marked Running did not finish, so it becomes
    /// Interrupted and its untouched rows become Indeterminate - genuinely unknown, rather
    /// than quietly assumed either way. History can then offer to re-read and resolve them.
    /// </summary>
    public int RecoverInterruptedRuns()
    {
        this.ThrowIfReadOnly();

        using SqliteTransaction transaction = this._connection.BeginTransaction();

        using SqliteCommand files = this._connection.CreateCommand();
        files.Transaction = transaction;
        files.CommandText = """
            UPDATE run_files SET outcome = $indeterminate
            WHERE outcome = $pending
              AND run_id IN (SELECT run_id FROM runs WHERE status = $running);
            """;
        _ = files.Parameters.AddWithValue("$indeterminate", (int)FileOutcome.Indeterminate);
        _ = files.Parameters.AddWithValue("$pending", (int)FileOutcome.Pending);
        _ = files.Parameters.AddWithValue("$running", (int)RunStatus.Running);
        _ = files.ExecuteNonQuery();

        using SqliteCommand runs = this._connection.CreateCommand();
        runs.Transaction = transaction;
        runs.CommandText = "UPDATE runs SET status = $interrupted WHERE status = $running;";
        _ = runs.Parameters.AddWithValue("$interrupted", (int)RunStatus.Interrupted);
        _ = runs.Parameters.AddWithValue("$running", (int)RunStatus.Running);
        int affected = runs.ExecuteNonQuery();

        transaction.Commit();

        if (affected > 0)
        {
            this._logger.LogWarning("{Count} run(s) did not finish and were marked interrupted.", affected);
        }

        return affected;
    }

    /// <summary>Runs, newest first.</summary>
    public IReadOnlyList<JournalRun> ListRuns(int limit = 100)
    {
        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, started_utc, finished_utc, status, kind, reverts_run_id, app_version,
                   exiftool_version, tz_id, recipe_json, roots_json, file_count, change_count,
                   error_count, pinned, note
            FROM runs
            ORDER BY run_id DESC
            LIMIT $limit;
            """;
        _ = command.Parameters.AddWithValue("$limit", limit);

        var runs = new List<JournalRun>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            runs.Add(new JournalRun(
                reader.GetInt64(0),
                ParseIso(reader.GetString(1)),
                reader.IsDBNull(2) ? null : ParseIso(reader.GetString(2)),
                (RunStatus)reader.GetInt32(3),
                (RunKind)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10).Split('\n', StringSplitOptions.RemoveEmptyEntries),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14) != 0,
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return runs;
    }

    /// <summary>
    /// The files of a run that were actually applied and have not yet been put back. This
    /// is the raw material a revert plan is built from.
    /// </summary>
    public IReadOnlyList<(JournalFile File, IReadOnlyList<JournalFieldChange> Changes)> ReadRevertable(long runId)
    {
        var byFile = new List<(JournalFile, IReadOnlyList<JournalFieldChange>)>();

        using SqliteCommand fileCommand = this._connection.CreateCommand();
        // Files that were applied and have not SUCCESSFULLY gone back. A drifted or locked
        // file stays in this list on purpose: the user skipped it by default, but "revert
        // anyway" has to remain reachable, and dropping it here would take that away.
        fileCommand.CommandText = """
            SELECT file_row_id, run_id, path, volume_serial, file_id, size_before, attrs_before,
                   is_directory, outcome, error, reverted_utc, revert_outcome
            FROM run_files
            WHERE run_id = $run
              AND outcome = $applied
              AND (revert_outcome IS NULL OR revert_outcome <> $revertedOutcome)
            ORDER BY file_row_id;
            """;
        _ = fileCommand.Parameters.AddWithValue("$run", runId);
        _ = fileCommand.Parameters.AddWithValue("$applied", (int)FileOutcome.Applied);
        _ = fileCommand.Parameters.AddWithValue("$revertedOutcome", (int)RevertOutcome.Reverted);

        var files = new List<JournalFile>();
        using (SqliteDataReader reader = fileCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                files.Add(new JournalFile(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : (byte[])reader[4],
                    reader.GetInt64(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7) != 0,
                    (FileOutcome)reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : ParseIso(reader.GetString(10)),
                    reader.IsDBNull(11) ? null : (RevertOutcome)reader.GetInt32(11)));
            }
        }

        foreach (JournalFile file in files)
        {
            byFile.Add((file, this.ReadChanges(file.FileRowId)));
        }

        return byFile;
    }

    /// <summary>Marks one file as put back, or explains why it was not.</summary>
    public void RecordRevert(long fileRowId, RevertOutcome outcome, DateTimeOffset whenUtc)
    {
        this.ThrowIfReadOnly();

        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = """
            UPDATE run_files SET reverted_utc = $when, revert_outcome = $outcome
            WHERE file_row_id = $id;
            """;
        _ = command.Parameters.AddWithValue("$when", Iso(whenUtc));
        _ = command.Parameters.AddWithValue("$outcome", (int)outcome);
        _ = command.Parameters.AddWithValue("$id", fileRowId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Rolls a source run up to Reverted or PartiallyReverted, depending on whether every
    /// applied file actually went back.
    /// </summary>
    public void RefreshRevertStatus(long runId)
    {
        this.ThrowIfReadOnly();

        using SqliteCommand command = this._connection.CreateCommand();

        // "Fully reverted" means every applied file actually went BACK, which is not the
        // same as every applied file having an outcome recorded. A drifted or missing file
        // has an outcome and was deliberately left alone; counting it as handled would
        // report a complete undo while a file still carries the new value.
        command.CommandText = """
            UPDATE runs SET status = CASE
                WHEN (SELECT COUNT(*) FROM run_files
                      WHERE run_id = $id
                        AND outcome = $applied
                        AND (revert_outcome IS NULL OR revert_outcome <> $revertedOutcome)) = 0
                THEN $reverted ELSE $partial END
            WHERE run_id = $id
              AND (SELECT COUNT(*) FROM run_files
                   WHERE run_id = $id AND reverted_utc IS NOT NULL) > 0;
            """;
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.Parameters.AddWithValue("$applied", (int)FileOutcome.Applied);
        _ = command.Parameters.AddWithValue("$revertedOutcome", (int)RevertOutcome.Reverted);
        _ = command.Parameters.AddWithValue("$reverted", (int)RunStatus.Reverted);
        _ = command.Parameters.AddWithValue("$partial", (int)RunStatus.PartiallyReverted);
        _ = command.ExecuteNonQuery();
    }

    public void SetPinned(long runId, bool pinned)
    {
        this.ThrowIfReadOnly();

        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = "UPDATE runs SET pinned = $pinned WHERE run_id = $id;";
        _ = command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops old runs. Never touches a pinned run, never touches the most recent few, and
    /// never touches a run another un-reverted run points at.
    /// </summary>
    public int Prune(RetentionPolicy policy, DateTimeOffset nowUtc)
    {
        this.ThrowIfReadOnly();

        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = """
            DELETE FROM runs
            WHERE pinned = 0
              AND run_id NOT IN (SELECT run_id FROM runs ORDER BY run_id DESC LIMIT $keep)
              AND run_id NOT IN (SELECT reverts_run_id FROM runs WHERE reverts_run_id IS NOT NULL)
              AND (
                    started_utc < $cutoff
                 OR run_id NOT IN (SELECT run_id FROM runs ORDER BY run_id DESC LIMIT $maxRuns)
              );
            """;
        _ = command.Parameters.AddWithValue("$keep", policy.AlwaysKeep);
        _ = command.Parameters.AddWithValue("$maxRuns", policy.MaxRuns);
        _ = command.Parameters.AddWithValue("$cutoff", Iso(nowUtc - policy.MaxAge));

        int removed = command.ExecuteNonQuery();

        if (removed > 0)
        {
            this._logger.LogInformation("Pruned {Count} run(s) from the journal.", removed);
        }

        return removed;
    }

    /// <summary>The oldest run still available to undo, so retention is never a silent surprise.</summary>
    public DateTimeOffset? OldestRevertableRun()
    {
        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = "SELECT MIN(started_utc) FROM runs WHERE status = $completed;";
        _ = command.Parameters.AddWithValue("$completed", (int)RunStatus.Completed);

        object? value = command.ExecuteScalar();
        return value is string s ? ParseIso(s) : null;
    }

    private List<JournalFieldChange> ReadChanges(long fileRowId)
    {
        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = """
            SELECT change_id, file_row_id, field, tag_name, before_present, before_ticks,
                   before_raw, after_ticks, after_raw, write_status
            FROM run_field_changes
            WHERE file_row_id = $id
            ORDER BY change_id;
            """;
        _ = command.Parameters.AddWithValue("$id", fileRowId);

        var changes = new List<JournalFieldChange>();
        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            changes.Add(new JournalFieldChange(
                reader.GetInt64(0),
                reader.GetInt64(1),
                (DateField)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) != 0,
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                (FieldWriteStatus)reader.GetInt32(9)));
        }

        return changes;
    }

    private void ThrowIfReadOnly()
    {
        if (this.IsReadOnly)
        {
            throw new InvalidOperationException(
                "The journal was written by a newer version of Chronora and is open read-only.");
        }
    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public void Dispose()
    {
        this._connection.Dispose();
        SqliteConnection.ClearPool(this._connection);
    }
}
