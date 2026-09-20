// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PaulTechGuy.CN.Journal;

/// <summary>
/// Creates and migrates the journal database.
///
/// Migrations are forward-only, idempotent, and keyed on PRAGMA user_version. A build that
/// meets a NEWER schema than it understands opens read-only and says so rather than writing
/// a downgraded shape over it - that is the case that bites during a rollback, and losing
/// someone's undo history to it would be worse than refusing to show it.
/// </summary>
internal static class JournalSchema
{
    /// <summary>Bump this and add a migration below. Never edit an existing migration.</summary>
    internal const int CurrentVersion = 1;

    internal static void EnsureCreated(SqliteConnection connection, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);

        int version = ReadUserVersion(connection);

        if (version > CurrentVersion)
        {
            throw new JournalTooNewException(version, CurrentVersion);
        }

        if (version == CurrentVersion)
        {
            return;
        }

        logger.LogInformation("Migrating the journal from schema {From} to {To}.", version, CurrentVersion);

        using SqliteTransaction transaction = connection.BeginTransaction();

        if (version < 1)
        {
            Execute(connection, transaction, CreateV1);
        }

        // Future migrations go here, each guarded by its own version check and each additive.

        Execute(connection, transaction, $"PRAGMA user_version = {CurrentVersion};");
        transaction.Commit();
    }

    /// <summary>
    /// WAL so a reader (the History window) never blocks the writer (a running apply), and
    /// NORMAL synchronous because the journal is a safety net rather than the system of
    /// record - a power cut may cost the last batch, and the recovery pass handles that.
    /// </summary>
    internal static void ApplyPragmas(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        _ = command.ExecuteNonQuery();
    }

    internal static int ReadUserVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private const string CreateV1 = """
        CREATE TABLE runs (
            run_id           INTEGER PRIMARY KEY AUTOINCREMENT,
            started_utc      TEXT    NOT NULL,
            finished_utc     TEXT    NULL,
            status           INTEGER NOT NULL,
            kind             INTEGER NOT NULL,
            reverts_run_id   INTEGER NULL REFERENCES runs(run_id),
            app_version      TEXT    NOT NULL,
            exiftool_version TEXT    NULL,
            tz_id            TEXT    NOT NULL,
            recipe_json      TEXT    NOT NULL,
            roots_json       TEXT    NOT NULL,
            file_count       INTEGER NOT NULL DEFAULT 0,
            change_count     INTEGER NOT NULL DEFAULT 0,
            error_count      INTEGER NOT NULL DEFAULT 0,
            pinned           INTEGER NOT NULL DEFAULT 0,
            note             TEXT    NULL
        );

        CREATE TABLE run_files (
            file_row_id      INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id           INTEGER NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
            path             TEXT    NOT NULL,
            volume_serial    INTEGER NULL,
            file_id          BLOB    NULL,
            size_before      INTEGER NOT NULL,
            attrs_before     INTEGER NOT NULL,
            is_directory     INTEGER NOT NULL,
            outcome          INTEGER NOT NULL,
            error            TEXT    NULL,
            reverted_utc     TEXT    NULL,
            revert_outcome   INTEGER NULL
        );

        CREATE INDEX ix_files_run  ON run_files(run_id, file_row_id);
        CREATE INDEX ix_files_path ON run_files(path COLLATE NOCASE);

        CREATE TABLE run_field_changes (
            change_id        INTEGER PRIMARY KEY AUTOINCREMENT,
            file_row_id      INTEGER NOT NULL REFERENCES run_files(file_row_id) ON DELETE CASCADE,
            field            INTEGER NOT NULL,
            tag_name         TEXT    NULL,
            before_present   INTEGER NOT NULL,
            before_ticks     INTEGER NULL,
            before_raw       TEXT    NULL,
            after_ticks      INTEGER NULL,
            after_raw        TEXT    NULL,
            write_status     INTEGER NOT NULL
        );

        CREATE INDEX ix_changes_file ON run_field_changes(file_row_id);
        """;
}

/// <summary>
/// Thrown when the database was written by a newer build. The caller opens History
/// read-only and explains, rather than overwriting history with a shape it does not know.
/// </summary>
public sealed class JournalTooNewException(int found, int supported)
    : Exception($"The journal is at schema version {found}; this build understands {supported}. "
              + "It will be opened read-only so that a newer version's history is not damaged.")
{
    public int FoundVersion { get; } = found;

    public int SupportedVersion { get; } = supported;
}
