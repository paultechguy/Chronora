// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;

namespace PaulTechGuy.CN.Domain;

/// <summary>Which half of the app is on screen. A filter over one engine, never a second code path.</summary>
public enum AppMode
{
    /// <summary>The default, and a complete product on its own. Writes only filesystem dates.</summary>
    FileDates,

    PhotoDates,
}

/// <summary>What kind of file this is, which decides how metadata may be written to it.</summary>
public enum MediaKind
{
    Other,
    Jpeg,
    Heic,
    Png,
    Tiff,

    /// <summary>Proprietary raw. Defaults to an XMP sidecar rather than an embedded write.</summary>
    RawProprietary,

    /// <summary>An open spec designed to be written in place.</summary>
    Dng,

    Video,
}

/// <summary>
/// Facts about a file that change what may be done to it. Established once during the scan
/// and then sealed, because the preview must never re-read the disk.
/// </summary>
[Flags]
public enum FileTraits
{
    None = 0,
    ReadOnly = 1 << 0,
    Directory = 1 << 1,

    /// <summary>A reparse point. Stamped as the link, never followed, unless asked.</summary>
    ReparsePoint = 1 << 2,

    /// <summary>More than one hard link. Forces an in-place metadata write or the link breaks.</summary>
    HardLinked = 1 << 3,

    /// <summary>
    /// A cloud placeholder. Filesystem dates still work via FILE_FLAG_OPEN_NO_RECALL, but a
    /// metadata read or write would hydrate it and pull the bytes down.
    /// </summary>
    CloudDehydrated = 1 << 4,

    /// <summary>The volume does not honour ChangeTime. Proven by read-back, never assumed.</summary>
    NoChangeTimeSupport = 1 << 5,

    /// <summary>Path over the legacy limit, so every native call needs the extended prefix.</summary>
    LongPath = 1 << 6,

    /// <summary>
    /// This file's QuickTime dates were read as UTC rather than as local time.
    ///
    /// Recorded because it is an inference, not a fact. The atom is specified as UTC and
    /// a great many cameras write local time into it anyway, so the decision is made per
    /// file against the EXIF date - and the user is entitled to see which way it went.
    /// </summary>
    QuickTimeReadAsUtc = 1 << 7,
}

/// <summary>
/// One metadata value as it exists on disk.
///
/// <see cref="Raw" /> is kept byte-exact and is what a revert restores. Parsing a camera's
/// value into a DateTimeOffset and re-emitting it is a second edit, not a restoration, and
/// real files carry values that do not round-trip: "0000:00:00 00:00:00", partial dates, and
/// outright malformed strings.
///
/// <see cref="Present" /> is not inferrable from Raw being null: a tag can exist and be
/// empty, and reverting "absent" means deleting the tag while reverting "empty" means writing
/// an empty value.
/// </summary>
/// <param name="Present">Whether the tag exists at all.</param>
/// <param name="Raw">The exact string on disk, or null when the tag is absent.</param>
/// <param name="Parsed">The interpreted value, or null when it could not be parsed.</param>
public readonly record struct MetadataValue(bool Present, string? Raw, DateTimeOffset? Parsed)
{
    public static MetadataValue Absent => new(Present: false, Raw: null, Parsed: null);

    /// <summary>True when there is a usable date here, as opposed to an absent or junk value.</summary>
    public bool HasUsableDate => this.Parsed.HasValue;
}

/// <summary>The four filesystem timestamps, which are read and written together.</summary>
/// <param name="Created">Creation time.</param>
/// <param name="Modified">Last write time, which tracks the data stream.</param>
/// <param name="Accessed">Last access time. Often meaningless: Windows disables updates by default.</param>
/// <param name="Changed">The NTFS MFT record-change time. Null when the volume does not expose it.</param>
public readonly record struct TimestampSet(
    DateTimeOffset? Created,
    DateTimeOffset? Modified,
    DateTimeOffset? Accessed,
    DateTimeOffset? Changed)
{
    public DateTimeOffset? Get(DateField field) => field switch
    {
        DateField.FileCreated => this.Created,
        DateField.FileModified => this.Modified,
        DateField.FileAccessed => this.Accessed,
        DateField.FileChanged => this.Changed,
        _ => null,
    };
}

/// <summary>
/// A file as the scan found it: the sealed snapshot the evaluator runs against.
///
/// Nothing here is re-read when an option changes. That is the rule that makes a live preview
/// possible at 50,000 files.
/// </summary>
/// <param name="FullPath">The display path, with no extended-length prefix.</param>
/// <param name="Length">Size in bytes. Used to price a metadata rewrite before Apply.</param>
/// <param name="Kind">How metadata may be written to it.</param>
/// <param name="Attributes">Raw filesystem attributes, for the attribute-editing path.</param>
/// <param name="Times">The four filesystem timestamps.</param>
/// <param name="Metadata">Date tags read from the file. Empty until ExifTool has run.</param>
/// <param name="Traits">Facts that constrain what may be done.</param>
public sealed record ScannedFile(
    string FullPath,
    long Length,
    MediaKind Kind,
    System.IO.FileAttributes Attributes,
    TimestampSet Times,
    FrozenDictionary<DateField, MetadataValue> Metadata,
    FileTraits Traits)
{
    public string FileName => System.IO.Path.GetFileName(this.FullPath);

    public bool IsDirectory => this.Traits.HasFlag(FileTraits.Directory);

    /// <summary>
    /// The current value of any field, from whichever genre it belongs to. This is what lets
    /// a rule read across the boundary without caring which side it is on.
    /// </summary>
    public DateTimeOffset? Current(DateField field) =>
        DateFieldCatalog.GenreOf(field) == FieldGenre.FileSystem
            ? this.Times.Get(field)
            : this.Metadata.TryGetValue(field, out MetadataValue value) ? value.Parsed : null;

    public MetadataValue MetadataFor(DateField field) =>
        this.Metadata.TryGetValue(field, out MetadataValue value) ? value : MetadataValue.Absent;
}
