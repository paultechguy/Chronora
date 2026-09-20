// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Domain;

/// <summary>How a run ended.</summary>
public enum RunStatus
{
    /// <summary>In progress. A run still in this state at startup did not finish.</summary>
    Running,

    Completed,
    Cancelled,
    Failed,

    /// <summary>Every applied file has been put back.</summary>
    Reverted,

    PartiallyReverted,

    /// <summary>
    /// The process died mid-run. Some files may or may not carry their new values, and the
    /// History view offers to re-read them and find out.
    /// </summary>
    Interrupted,
}

/// <summary>Whether a run applied a recipe or undid one.</summary>
public enum RunKind
{
    Apply,

    /// <summary>
    /// A revert is an ordinary run whose plan came from the journal. That is deliberate: it
    /// means reverts are journaled, appear in History, and can themselves be reverted.
    /// </summary>
    Revert,
}

/// <summary>What happened to one file during a run.</summary>
public enum FileOutcome
{
    /// <summary>Recorded, not yet attempted. Written before any file is touched.</summary>
    Pending,

    Applied,
    Skipped,
    Failed,

    /// <summary>
    /// The run died between recording and finishing this file, so whether the write landed
    /// is genuinely unknown until the file is re-read.
    /// </summary>
    Indeterminate,
}

/// <summary>Why a revert did or did not put a file back.</summary>
public enum RevertOutcome
{
    Reverted,

    /// <summary>The file is gone, or its name now belongs to a different file.</summary>
    Missing,

    /// <summary>
    /// Something changed the file after the run. Skipped by default: silently stomping a
    /// value the user deliberately set last Tuesday is the one unforgivable bug in an undo
    /// feature.
    /// </summary>
    Drifted,

    Locked,
    Failed,
}

/// <summary>Whether one field's write landed.</summary>
public enum FieldWriteStatus
{
    Planned,
    Ok,

    /// <summary>Written and then read back to confirm.</summary>
    Verified,

    /// <summary>The target could not accept it, for example ChangeTime on exFAT.</summary>
    Unsupported,

    Failed,
}

/// <summary>One run, as History lists it.</summary>
/// <param name="RunId">Assigned by the journal.</param>
/// <param name="StartedUtc">When it began.</param>
/// <param name="FinishedUtc">When it ended, or null while it is running.</param>
/// <param name="Status">How it ended.</param>
/// <param name="Kind">Apply or revert.</param>
/// <param name="RevertsRunId">For a revert, the run it undoes.</param>
/// <param name="AppVersion">Which build made it.</param>
/// <param name="ExifToolVersion">Which ExifTool, when one was involved.</param>
/// <param name="TimeZoneId">
/// The zone in force. Reverting a run made in Tokyo on a laptop now set to New York must not
/// reinterpret anything, and History should be able to say which zone it was.
/// </param>
/// <param name="RecipeJson">
/// The run's own description of what it did. History renders from this, so a run stays
/// explicable even after every file it touched has been deleted.
/// </param>
/// <param name="Roots">The folders it covered.</param>
/// <param name="FileCount">Files recorded.</param>
/// <param name="ChangeCount">Individual field writes recorded.</param>
/// <param name="ErrorCount">Files that failed.</param>
/// <param name="Pinned">Pinned runs are never pruned.</param>
/// <param name="Note">Anything the user typed about it.</param>
public sealed record JournalRun(
    long RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    RunStatus Status,
    RunKind Kind,
    long? RevertsRunId,
    string AppVersion,
    string? ExifToolVersion,
    string TimeZoneId,
    string RecipeJson,
    IReadOnlyList<string> Roots,
    int FileCount,
    int ChangeCount,
    int ErrorCount,
    bool Pinned,
    string? Note);

/// <summary>One file within a run, and how it was identified.</summary>
/// <param name="FileRowId">Assigned by the journal.</param>
/// <param name="RunId">The run it belongs to.</param>
/// <param name="Path">Display path, never the extended-length form.</param>
/// <param name="VolumeSerial">Which volume, so a moved file can be looked up by id.</param>
/// <param name="FileId">
/// The NTFS file id. Identity is this rather than a content hash, because a metadata write
/// changes the content by design and a hash would flag every successful write as drift.
/// </param>
/// <param name="SizeBefore">Size before the run.</param>
/// <param name="AttributesBefore">Attributes before the run, so read-only can be put back.</param>
/// <param name="IsDirectory">Directories need backup semantics to reopen.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Error">Why not, when it failed.</param>
/// <param name="RevertedUtc">When it was put back, if it was.</param>
/// <param name="RevertOutcome">How that went.</param>
public sealed record JournalFile(
    long FileRowId,
    long RunId,
    string Path,
    long? VolumeSerial,
    byte[]? FileId,
    long SizeBefore,
    int AttributesBefore,
    bool IsDirectory,
    FileOutcome Outcome,
    string? Error,
    DateTimeOffset? RevertedUtc,
    RevertOutcome? RevertOutcome);

/// <summary>
/// One field's before and after, which is what a revert reads back.
/// </summary>
/// <param name="ChangeId">Assigned by the journal.</param>
/// <param name="FileRowId">The file it belongs to.</param>
/// <param name="Field">Which date.</param>
/// <param name="TagName">The exact tag written, for a metadata field.</param>
/// <param name="BeforePresent">
/// Whether the tag existed at all. Not inferrable from BeforeRaw being null, because a tag
/// can exist and be empty: reverting "absent" must DELETE, reverting "empty" must write an
/// empty value.
/// </param>
/// <param name="BeforeTicks">For a filesystem field, the exact prior value.</param>
/// <param name="BeforeRaw">
/// For a metadata field, the byte-exact prior string. Kept verbatim because real files carry
/// values that do not round-trip - "0000:00:00 00:00:00", partial dates, outright junk - and
/// parsing one into a DateTimeOffset and re-emitting it is a second edit, not a restoration.
/// </param>
/// <param name="AfterTicks">What was written, for a filesystem field.</param>
/// <param name="AfterRaw">What was written, for a metadata field.</param>
/// <param name="Status">Whether it landed.</param>
public sealed record JournalFieldChange(
    long ChangeId,
    long FileRowId,
    DateField Field,
    string? TagName,
    bool BeforePresent,
    long? BeforeTicks,
    string? BeforeRaw,
    long? AfterTicks,
    string? AfterRaw,
    FieldWriteStatus Status);

/// <summary>How long history is kept. Whichever limit bites first wins.</summary>
/// <param name="MaxRuns">Beyond this many runs, the oldest go.</param>
/// <param name="MaxAge">Beyond this age, runs go.</param>
/// <param name="AlwaysKeep">
/// The most recent N are never pruned regardless, so a burst of activity cannot push the
/// run someone actually wants to undo off the end.
/// </param>
public readonly record struct RetentionPolicy(int MaxRuns, TimeSpan MaxAge, int AlwaysKeep)
{
    public static RetentionPolicy Default => new(200, TimeSpan.FromDays(180), 20);
}
