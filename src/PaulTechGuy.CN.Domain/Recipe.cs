// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Domain;

/// <summary>How to reduce several candidate fields to one date.</summary>
public enum Aggregate
{
    /// <summary>First field in the list that has a usable value wins.</summary>
    FirstPresent,

    /// <summary>The oldest of the candidates. Powers "make every date consistent".</summary>
    Earliest,

    Latest,
}

/// <summary>
/// What a shift means, which is not obvious and cannot be left implicit.
///
/// "+1 hour" on a photo's Taken date means the displayed time reads one hour later. "+1 hour"
/// on a filesystem timestamp means the UTC instant moves by exactly one hour. Across a DST
/// boundary those are different answers, and a single TimeSpan cannot express which was meant.
/// </summary>
public enum ShiftBasis
{
    /// <summary>Shift the wall-clock reading. The default for metadata dates.</summary>
    WallClock,

    /// <summary>Shift the absolute instant. The default for filesystem dates.</summary>
    Instant,
}

/// <summary>How precise a derived date is, which decides how much of the target it may set.</summary>
public enum DatePrecision
{
    /// <summary>Date only. A WhatsApp filename yields this, and must not zero the time of day.</summary>
    Day,
    Minute,
    Second,
    Millisecond,
}

/// <summary>Where a rule gets its date.</summary>
public abstract record DateSource
{
    /// <summary>A literal date the user typed.</summary>
    public sealed record Absolute(DateTimeOffset Value) : DateSource;

    /// <summary>Move the existing value. See <see cref="ShiftBasis" /> for why the basis is explicit.</summary>
    public sealed record Shift(TimeSpan Delta, ShiftBasis Basis) : DateSource;

    /// <summary>
    /// Reinterpret the wall-clock reading as having been taken in a different zone. The
    /// common form of "the camera clock was wrong" is a trip to another country, and
    /// "shift by -05:00:00" is exactly the arithmetic this app exists to spare people.
    /// </summary>
    public sealed record ZoneChange(TimeZoneInfo From, TimeZoneInfo To) : DateSource;

    /// <summary>
    /// Take the date from other fields. The list may cross the filesystem/metadata boundary
    /// in either direction, which is the product's differentiator.
    /// </summary>
    public sealed record CopyFrom(Aggregate How, IReadOnlyList<DateField> Fields) : DateSource;

    /// <summary>Parse it out of the file or folder name.</summary>
    public sealed record FromFileName(string PatternId) : DateSource;

    /// <summary>
    /// Nothing chosen yet.
    ///
    /// An explicit state rather than an absent rule, because the TARGETS are still known
    /// and still matter: the app has to go on saying that photo dates will need ExifTool,
    /// and that a field cannot be written, before anyone picks the date. A recipe with no
    /// rules would take all of that away and look like there was nothing to warn about.
    /// </summary>
    public sealed record Unset : DateSource;

    // 0.2.0: FromTakeoutJson, once there is a real export to build against.
}

/// <summary>Conditions that stop a rule touching a file it should leave alone.</summary>
/// <param name="OnlyIfTargetEmpty">Only write where the target has no usable value already.</param>
/// <param name="OnlyIfNewer">Only write when the new value is later than the current one.</param>
/// <param name="OnlyIfOlder">Only write when the new value is earlier than the current one.</param>
public readonly record struct RuleGuards(
    bool OnlyIfTargetEmpty = false,
    bool OnlyIfNewer = false,
    bool OnlyIfOlder = false)
{
    public static RuleGuards None => default;
}

/// <summary>One rule: get a date from somewhere, write it to some fields, subject to guards.</summary>
/// <param name="Source">Where the date comes from.</param>
/// <param name="Targets">Which fields it is written to.</param>
/// <param name="Guards">Conditions under which it is skipped.</param>
/// <param name="Fallback">
/// Tried when <paramref name="Source" /> yields nothing. A declarative chain, so
/// "Takeout, else the filename, else nothing" is one rule rather than special-case code.
/// </param>
public sealed record DateRule(
    DateSource Source,
    IReadOnlySet<DateField> Targets,
    RuleGuards Guards,
    DateSource? Fallback = null);

/// <summary>Which files the scan collects.</summary>
/// <param name="Patterns">
/// Wildcards, entered semicolon-separated ("*.jpg;*.png"). Carried over from FileTouch, the
/// author's earlier WinForms attempt, which had this and revision 1 of the plan did not.
/// </param>
/// <param name="Recurse">Whether to descend into subfolders.</param>
/// <param name="IncludeFiles">Whether files are collected.</param>
/// <param name="IncludeDirectories">Whether folders are collected.</param>
/// <param name="IncludeRootDirectory">
/// Whether the root folder itself is collected, independent of its contents. FileTouch
/// separated this correctly and it is easy to miss.
/// </param>
public sealed record ScanFilter(
    IReadOnlyList<string> Patterns,
    bool Recurse = true,
    bool IncludeFiles = true,
    bool IncludeDirectories = false,
    bool IncludeRootDirectory = false)
{
    public static ScanFilter Default => new(["*"]);
}

/// <summary>
/// Everything one run will do.
///
/// A run applies a LIST of rules, not one. FileTouch let each of Created/Modified/Accessed
/// take its own value in a single pass, and a single-rule model cannot express that. Rules
/// apply in order and a later rule targeting the same field wins, so the preview can always
/// say which rule produced a given change.
/// </summary>
/// <param name="Rules">Applied in order.</param>
/// <param name="Filter">Which files the run covers.</param>
/// <param name="Mode">Constrains which fields rules may target.</param>
public sealed record Recipe(
    IReadOnlyList<DateRule> Rules,
    ScanFilter Filter,
    AppMode Mode = AppMode.FileDates)
{
    /// <summary>
    /// Every field this recipe writes. Used to price the run and to warn when a rule targets
    /// fields the current mode hides.
    /// </summary>
    public IReadOnlySet<DateField> AllTargets =>
        this.Rules.SelectMany(r => r.Targets).ToHashSet();

    /// <summary>True when any rule writes metadata, which is what requires ExifTool.</summary>
    public bool NeedsMetadataWrite =>
        this.AllTargets.Any(f => DateFieldCatalog.GenreOf(f) == FieldGenre.Metadata);

    /// <summary>
    /// True when any rule READS metadata, which also requires ExifTool even in File dates
    /// mode. This is the case that makes "copy the photo's Taken date onto the file dates"
    /// trigger the consent pane from the simple surface.
    /// </summary>
    public bool NeedsMetadataRead =>
        this.Rules.Any(r => SourceReadsMetadata(r.Source) || (r.Fallback is not null && SourceReadsMetadata(r.Fallback)));

    private static bool SourceReadsMetadata(DateSource source) => source switch
    {
        DateSource.CopyFrom copy => copy.Fields.Any(f => DateFieldCatalog.GenreOf(f) == FieldGenre.Metadata),
        _ => false,
    };
}
