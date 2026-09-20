// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Domain;

/// <summary>
/// A saved job: where the date comes from, which fields it goes to, and a name that says
/// what it is for.
///
/// Deliberately a named <see cref="DateRule" /> rather than a snapshot of the options
/// pane. The pane's shape will change; what a template MEANS will not. It also means a
/// template can be reasoned about and tested without a UI in the room.
/// </summary>
/// <param name="Id">Stable across renames, so a reference survives one.</param>
/// <param name="Name">What the user picks from a list.</param>
/// <param name="Description">
/// Why you would choose this one. Shown beside the name, because a name can only ever say
/// which problem a template is for - the description is where what it actually does lives,
/// including the parts the options pane has no control for.
/// </param>
/// <param name="Source">Where the date comes from.</param>
/// <param name="Targets">Which fields it is written to.</param>
/// <param name="Guards">Conditions under which a file is left alone.</param>
/// <param name="IsBuiltIn">
/// Compiled in and read-only. They can be duplicated and the copy edited, which is how
/// someone gets a variant without being able to break the thing they started from.
/// </param>
public sealed record DateTemplate(
    string Id,
    string Name,
    string Description,
    DateSource Source,
    IReadOnlySet<DateField> Targets,
    RuleGuards Guards,
    bool IsBuiltIn = false)
{
    /// <summary>The rule this template stands for.</summary>
    public DateRule ToRule() => new(this.Source, this.Targets, this.Guards);

    /// <summary>True when using this needs ExifTool, for either reading or writing.</summary>
    public bool NeedsMetadata =>
        this.Targets.Any(f => DateFieldCatalog.GenreOf(f) == FieldGenre.Metadata)
        || (this.Source is DateSource.CopyFrom copy
            && copy.Fields.Any(f => DateFieldCatalog.GenreOf(f) == FieldGenre.Metadata));
}

/// <summary>
/// The templates that ship with the app.
///
/// These are the product's answer to "I do not know what any of these options mean". Each
/// one is a real job somebody actually has, named the way they would describe it rather
/// than the way the machinery works - which is why none of them is called "CopyFrom".
/// </summary>
public static class BuiltInTemplates
{
    private static readonly IReadOnlySet<DateField> FileDates =
        new HashSet<DateField> { DateField.FileCreated, DateField.FileModified };

    private static readonly IReadOnlySet<DateField> Taken =
        new HashSet<DateField> { DateField.ExifDateTimeOriginal };

    /// <summary>
    /// A video keeps its own date in a QuickTime atom, nowhere near where a photo keeps
    /// one. That is why a run aimed at photos silently skips every video in the folder.
    /// </summary>
    private static readonly IReadOnlySet<DateField> Video =
        new HashSet<DateField>
        {
            DateField.QuickTimeCreateDate,
            DateField.FileCreated,
            DateField.FileModified,
        };

    private static readonly IReadOnlySet<DateField> Everything =
        new HashSet<DateField>
        {
            DateField.FileCreated,
            DateField.FileModified,
            DateField.ExifDateTimeOriginal,
        };

    /// <summary>
    /// In the order they are offered. The order is the recommendation: the first two are
    /// the two directions of the single most common job in this whole domain.
    /// </summary>
    public static IReadOnlyList<DateTemplate> All { get; } =
    [
        new DateTemplate(
            "builtin.photos-sort-wrong-in-explorer",
            "Photos sort wrong in Explorer",
            "Copies each photo's Taken date onto its file dates, so Explorer and anything "
            + "else that sorts by file date finally agrees with when the picture was taken.",
            new DateSource.CopyFrom(Aggregate.FirstPresent, [DateField.ExifDateTimeOriginal]),
            FileDates,
            RuleGuards.None,
            IsBuiltIn: true),

        // The id is deliberately unchanged from when this was called "Photos land on
        // today in Google Photos". Ids are what saved references point at; renaming one
        // would orphan them, and the name is the part that was wrong.
        new DateTemplate(
            "builtin.photos-land-on-today-in-google-photos",
            "Photo “taken” date is missing",
            "The reverse. Scans, WhatsApp downloads and Facebook exports usually have a "
            + "sensible file date and no Taken date at all, which is why photo libraries "
            + "pile them onto today. This fills the Taken date in from the file date, and "
            + "leaves photos that already have one alone.",
            new DateSource.CopyFrom(Aggregate.Earliest, [DateField.FileModified, DateField.FileCreated]),
            Taken,

            // Only where there is nothing already. A photo that knows when it was taken
            // knows better than its file date does, and overwriting that would be the
            // most destructive thing in the starting set. The name says "missing" rather
            // than "wrong" so that it describes what this actually does.
            new RuleGuards(OnlyIfTargetEmpty: true),
            IsBuiltIn: true),

        new DateTemplate(
            "builtin.video-date-is-wrong",
            "Video date is wrong",
            "Sets a video's own recorded date, and its file dates, from the earliest date "
            + "it already has. Videos carry their date in a different place from photos, "
            + "so a run aimed at photos leaves them behind.",
            new DateSource.CopyFrom(
                Aggregate.Earliest,
                [DateField.QuickTimeCreateDate, DateField.FileCreated, DateField.FileModified]),
            Video,
            RuleGuards.None,
            IsBuiltIn: true),

        new DateTemplate(
            "builtin.make-every-date-consistent",
            "Make every date consistent",
            "Sets every date on the file to the earliest one it already has. Use it when a "
            + "file has picked up a spread of dates and you want them to agree.",
            new DateSource.CopyFrom(
                Aggregate.Earliest,
                [DateField.ExifDateTimeOriginal, DateField.FileCreated, DateField.FileModified]),
            Everything,
            RuleGuards.None,
            IsBuiltIn: true),

        new DateTemplate(
            "builtin.dates-from-filenames",
            "Dates from file names",
            "Reads the date out of names like IMG_20240315_142530 or Screenshot 2024-03-15. "
            + "Useful for anything that lost its metadata on the way here.",
            new DateSource.FromFileName(string.Empty),
            Everything,
            RuleGuards.None,
            IsBuiltIn: true),

        // Deliberately last, and deliberately not in the same family as the others. It
        // flattens however many distinct photos into one identical timestamp, which is the
        // most destructive thing here and almost never what someone means on their first
        // run. Kept because it is occasionally exactly right - scanned documents, say.
        new DateTemplate(
            "builtin.stamp-one-date-on-everything",
            "Stamp one date on every file",
            "Sets the same date on every selected file. This flattens the differences "
            + "between them, so check the preview before applying it to a photo library.",
            new DateSource.Absolute(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero)),
            FileDates,
            RuleGuards.None,
            IsBuiltIn: true),
    ];

    public static DateTemplate? ById(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}
