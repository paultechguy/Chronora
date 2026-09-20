// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Domain;

/// <summary>What the preview says will happen to one field.</summary>
public enum ChangeStatus
{
    /// <summary>Already correct. Shown muted, not written.</summary>
    Unchanged,

    WillChange,

    /// <summary>
    /// Will change, but the result looks wrong: before 1990, in the future, shifted by more
    /// than a year, or two candidate sources disagreeing by more than a day.
    ///
    /// This exists because a run where 63 videos get a 1904 date from a zeroed QuickTime atom
    /// otherwise looks exactly like a clean run. Counted separately and filterable.
    /// </summary>
    Suspicious,

    /// <summary>Cannot be written, with a reason the user can act on.</summary>
    Blocked,

    /// <summary>A guard or a missing source means this rule does not apply here.</summary>
    Skipped,
}

/// <summary>Why a change is blocked, skipped or suspicious. Plain-language mapping lives in the UI.</summary>
public enum ProblemCode
{
    None,
    ReadOnly,
    AccessDenied,
    FileLocked,

    /// <summary>The volume does not honour ChangeTime. exFAT, FAT32, some shares.</summary>
    ChangeTimeUnsupported,

    /// <summary>The field cannot be written to this file format.</summary>
    FieldNotWritableForFormat,

    /// <summary>Reading or writing metadata would pull the file down from the cloud.</summary>
    CloudPlaceholderWouldHydrate,

    /// <summary>The rule found no date and no fallback produced one.</summary>
    NoSourceValue,

    /// <summary>The filename matched more than one pattern plausibly. Never guess.</summary>
    AmbiguousPatternMatch,

    /// <summary>A local time that does not exist, in a spring-forward gap.</summary>
    InvalidLocalTime,

    /// <summary>A local time that happens twice, in a fall-back overlap.</summary>
    AmbiguousLocalTime,

    /// <summary>The result is outside the plausible range for a photo.</summary>
    ImplausibleDate,

    /// <summary>ExifTool is needed for this field and is not available.</summary>
    MetadataEngineUnavailable,
}

/// <summary>
/// What is written to one tag or timestamp.
///
/// Deletion is a first-class case, not an absence. Writing a new date does not clear a stale
/// SubSecTimeOriginal or OffsetTimeOriginal, so a rule that supplies no sub-seconds has to
/// delete them explicitly; and reverting "tag was absent, then set" has to delete rather than
/// blank. One representation serves both directions.
/// </summary>
public abstract record FieldWrite
{
    /// <summary>Write a date. The writer expands this into value, offset and sub-second tags.</summary>
    public sealed record SetDate(DateTimeOffset Value, DatePrecision Precision) : FieldWrite;

    /// <summary>Write an exact string. Used by revert, to restore a value byte-for-byte.</summary>
    public sealed record SetRaw(string Raw) : FieldWrite;

    /// <summary>Remove the tag. Emits an empty assignment to ExifTool.</summary>
    public sealed record Delete : FieldWrite;
}

/// <summary>What a change acts on: a date, or an attribute flag.</summary>
public abstract record ChangeTarget
{
    public sealed record Field(DateField Which) : ChangeTarget
    {
        public override string DisplayName => DateFieldCatalog.Get(this.Which).DisplayName;
    }

    /// <summary>
    /// Attributes are structurally unlike dates: tri-state rather than a value, and set or
    /// cleared rather than moved. A date-shaped record could not express them, which is why
    /// PlannedChange is widened rather than duplicated.
    /// </summary>
    public sealed record Flag(System.IO.FileAttributes Attribute, bool Set) : ChangeTarget
    {
        public override string DisplayName => this.Attribute.ToString();
    }

    public abstract string DisplayName { get; }
}

/// <summary>
/// One row of the preview: exactly what will happen to one target on one file, and why.
/// </summary>
/// <param name="Target">The date field or attribute affected.</param>
/// <param name="Before">Current state. Null when the tag is absent.</param>
/// <param name="After">Intended state. Null when nothing will be written.</param>
/// <param name="Status">Whether it will change, and how confident we are that it should.</param>
/// <param name="Problem">Why, when the status is not a plain WillChange.</param>
/// <param name="RuleIndex">
/// Which rule in the recipe produced this, so the preview can explain itself when several
/// rules target the same field and the last one wins.
/// </param>
public sealed record PlannedChange(
    ChangeTarget Target,
    FieldWrite? Before,
    FieldWrite? After,
    ChangeStatus Status,
    ProblemCode Problem = ProblemCode.None,
    int RuleIndex = -1)
{
    public bool WillWrite => this.Status is ChangeStatus.WillChange or ChangeStatus.Suspicious;

    /// <summary>The before value as a date, when it is one. Convenience for the preview and sorting.</summary>
    public DateTimeOffset? BeforeDate => (this.Before as FieldWrite.SetDate)?.Value;

    public DateTimeOffset? AfterDate => (this.After as FieldWrite.SetDate)?.Value;

    /// <summary>
    /// How far this moves the date. The preview sorts by this so that "find the one that is
    /// wrong in 5,000 rows" is one click rather than a scroll.
    /// </summary>
    public TimeSpan? Delta =>
        this.BeforeDate is { } b && this.AfterDate is { } a ? a - b : null;
}

/// <summary>Every planned change for one file, plus the file it belongs to.</summary>
/// <param name="File">The scanned file, unchanged.</param>
/// <param name="Changes">One entry per target the recipe touched.</param>
public sealed record FilePlan(ScannedFile File, IReadOnlyList<PlannedChange> Changes)
{
    public bool WillWrite => this.Changes.Any(c => c.WillWrite);

    public bool HasProblem => this.Changes.Any(c => c.Status == ChangeStatus.Blocked);

    public bool IsSuspicious => this.Changes.Any(c => c.Status == ChangeStatus.Suspicious);

    /// <summary>
    /// The row's headline: the largest move, which is the one most worth seeing at a glance.
    /// </summary>
    public PlannedChange? Headline =>
        this.Changes
            .Where(c => c.WillWrite)
            .OrderByDescending(c => c.Delta.HasValue ? Math.Abs(c.Delta.Value.Ticks) : 0)
            .FirstOrDefault();
}
