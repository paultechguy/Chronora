// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Domain;

/// <summary>
/// Every date Chronora can read or write, from both genres, in one vocabulary.
///
/// Both genres share one enum deliberately: it is what lets a rule copy a date across the
/// boundary, and "copy the photo's Taken date onto the file dates" is the most common real
/// job in this domain. Two separate enums would have made the product's differentiator a
/// special case.
/// </summary>
public enum DateField
{
    // Filesystem. All four live in FILE_BASIC_INFO and are written in one call.
    FileCreated,
    FileModified,
    FileAccessed,

    /// <summary>
    /// The NTFS MFT record-change time. Invisible in Explorer and unreachable from the BCL.
    /// </summary>
    FileChanged,

    // EXIF
    ExifDateTimeOriginal,
    ExifCreateDate,
    ExifModifyDate,

    // XMP
    XmpDateCreated,

    // QuickTime (video)
    QuickTimeCreateDate,
    QuickTimeModifyDate,

    // IPTC
    IptcDateCreated,
}

/// <summary>Which half of the app a field belongs to.</summary>
public enum FieldGenre
{
    /// <summary>Written by SetFileInformationByHandle. Never needs ExifTool.</summary>
    FileSystem,

    /// <summary>Written by ExifTool. Rewrites the file, so it must be written first.</summary>
    Metadata,
}

/// <summary>
/// What one <see cref="DateField" /> is made of.
///
/// A date target is rarely one tag. Writing DateTimeOriginal without also writing
/// OffsetTimeOriginal loses the time zone silently, and without clearing SubSecTimeOriginal
/// leaves a stale fraction describing a moment that never existed. So a field names its
/// companions and the writer plans them together.
/// </summary>
/// <param name="Field">The field this describes.</param>
/// <param name="Genre">Filesystem or metadata.</param>
/// <param name="Tag">
/// The group-qualified ExifTool tag, or null for a filesystem field. Group-qualified because
/// reads use -G1 and the same tag name exists in more than one group.
/// </param>
/// <param name="OffsetTag">
/// The EXIF 2.31 offset tag that carries this field's UTC offset, if it has one. EXIF date
/// tags have no room for a zone, and ExifTool silently discards one you try to write into
/// them, so the offset has to be written here explicitly or it is lost.
/// </param>
/// <param name="SubSecondTag">
/// The companion sub-second tag, if any. Writing a new date does NOT clear it, so a rule that
/// does not supply sub-seconds has to delete it.
/// </param>
/// <param name="CarriesOwnOffset">
/// True when the value's own format includes the offset, as XMP does. Such a field needs no
/// separate offset tag.
/// </param>
public sealed record DateFieldSpec(
    DateField Field,
    FieldGenre Genre,
    string? Tag,
    string? OffsetTag,
    string? SubSecondTag,
    bool CarriesOwnOffset)
{
    /// <summary>A short label for the UI. Deliberately plain language, not the tag name.</summary>
    public string DisplayName => this.Field switch
    {
        DateField.FileCreated => "Created",
        DateField.FileModified => "Modified",
        DateField.FileAccessed => "Accessed",
        DateField.FileChanged => "Changed (NTFS)",
        DateField.ExifDateTimeOriginal => "Taken",
        DateField.ExifCreateDate => "Digitized",
        DateField.ExifModifyDate => "EXIF modified",
        DateField.XmpDateCreated => "XMP created",
        DateField.QuickTimeCreateDate => "Video created",
        DateField.QuickTimeModifyDate => "Video modified",
        DateField.IptcDateCreated => "IPTC created",
        _ => this.Field.ToString(),
    };
}

/// <summary>
/// The single source of truth for what each field is. Everything else reads from here.
/// </summary>
public static class DateFieldCatalog
{
    private static readonly DateFieldSpec[] SpecsByField = BuildSpecs();

    /// <summary>Every field, in a stable display order.</summary>
    public static IReadOnlyList<DateFieldSpec> All { get; } = SpecsByField;

    public static DateFieldSpec Get(DateField field) => SpecsByField[(int)field];

    public static FieldGenre GenreOf(DateField field) => Get(field).Genre;

    /// <summary>
    /// Whether this field means anything for this kind of file.
    ///
    /// A video keeps its date in a QuickTime atom and a photo keeps one in EXIF, and
    /// neither has the other's. Without this, a rule targeting both would try to write an
    /// EXIF tag into an MP4 and a QuickTime tag into a JPEG - so one template could not
    /// cover a folder holding both, which is exactly what a phone produces.
    ///
    /// Filesystem dates apply to everything, folders included. That is the whole reason
    /// the simple path works on any file at all.
    /// </summary>
    public static bool AppliesTo(DateField field, MediaKind kind)
    {
        // Accessed applies to nothing, deliberately, and this is the chokepoint that makes
        // that stick. RuleEvaluator filters every target through here, so a recipe that
        // still names Accessed - an old template, a settings file from a previous build -
        // produces no planned change, no preview line and no write. Filtering only at the
        // write end would have been worse than leaving it alone: the preview would have
        // promised an Accessed change that silently never happened.
        //
        // It is not that Accessed is meaningless, it is that it cannot be made to hold.
        // With last-access updates on - the Windows default is "System Managed", which
        // means on - merely reading a file moves it, so the thumbnailer, the indexer and
        // the antivirus scanner undo a run seconds after it finishes. Measured at 1.6s.
        //
        // Undo is unaffected: it restores from the journal's recorded values and never
        // consults this method.
        if (field == DateField.FileAccessed)
        {
            return false;
        }

        if (GenreOf(field) == FieldGenre.FileSystem)
        {
            return true;
        }

        return field switch
        {
            DateField.QuickTimeCreateDate or DateField.QuickTimeModifyDate =>
                kind == MediaKind.Video,

            // EXIF lives in images. PNG can carry it, and DNG and raw are images whose
            // EXIF is read even when the write goes to a sidecar.
            DateField.ExifDateTimeOriginal or DateField.ExifCreateDate or DateField.ExifModifyDate =>
                kind is MediaKind.Jpeg or MediaKind.Heic or MediaKind.Tiff or MediaKind.Png
                    or MediaKind.Dng or MediaKind.RawProprietary,

            // XMP is a container of its own and rides along in almost anything, video
            // included.
            DateField.XmpDateCreated => kind != MediaKind.Other,

            DateField.IptcDateCreated => kind is MediaKind.Jpeg or MediaKind.Tiff,

            _ => false,
        };
    }

    /// <summary>
    /// Whether Windows Explorer will DISPLAY a Taken date for this kind of file.
    ///
    /// Not whether Chronora can write one. It can, and does: the EXIF tag goes in, and
    /// ExifTool, photo libraries and third-party metadata viewers all read it back. This is
    /// only about Explorer's Details tab, which has its own idea of which formats carry a
    /// photo date - and a user checking their work in Explorer is checking the one reader
    /// that will not show it.
    ///
    /// Measured, not assumed. The identical DateTimeOriginal was written to one file of
    /// each format with plain ExifTool, and Explorer's own "Date taken" column read back:
    ///
    ///     JPEG   tag present, Explorer shows it
    ///     TIFF   tag present, Explorer shows it
    ///     PNG    tag present, Explorer BLANK
    ///     GIF    tag present, Explorer BLANK   (never reaches here - MediaKind.Other)
    ///     BMP    not EXIF-writable at all      (blocked earlier, by -listwf)
    ///
    /// HEIC and DNG are deliberately NOT listed: there was no genuine file of either to
    /// test, and warning wrongly is worse than not warning. Measure one before adding it.
    /// If a kind IS added here, update the confirmation dialog's wording, which names PNG.
    /// </summary>
    public static bool ExplorerShowsTakenDate(MediaKind kind) => kind != MediaKind.Png;

    /// <summary>
    /// The fields a given mode may WRITE. Note this constrains targets only: a File dates
    /// rule may still READ a metadata field, which is what makes "copy the photo's Taken
    /// date onto the file dates" reachable from the simple mode.
    /// </summary>
    public static IEnumerable<DateFieldSpec> WritableIn(AppMode mode) => mode switch
    {
        // FileAccessed is excluded everywhere, not just here: it cannot be made to hold on
        // a volume with last-access updates on, which is the Windows default.
        AppMode.FileDates => All.Where(s =>
            s.Genre == FieldGenre.FileSystem && s.Field != DateField.FileAccessed),
        _ => All,
    };

    private static DateFieldSpec[] BuildSpecs()
    {
        var specs = new[]
        {
            Fs(DateField.FileCreated),
            Fs(DateField.FileModified),
            Fs(DateField.FileAccessed),
            Fs(DateField.FileChanged),

            new DateFieldSpec(
                DateField.ExifDateTimeOriginal,
                FieldGenre.Metadata,
                "ExifIFD:DateTimeOriginal",
                "ExifIFD:OffsetTimeOriginal",
                "ExifIFD:SubSecTimeOriginal",
                CarriesOwnOffset: false),

            new DateFieldSpec(
                DateField.ExifCreateDate,
                FieldGenre.Metadata,
                "ExifIFD:CreateDate",
                "ExifIFD:OffsetTimeDigitized",
                "ExifIFD:SubSecTimeDigitized",
                CarriesOwnOffset: false),

            new DateFieldSpec(
                DateField.ExifModifyDate,
                FieldGenre.Metadata,
                "IFD0:ModifyDate",
                "ExifIFD:OffsetTime",
                "ExifIFD:SubSecTime",
                CarriesOwnOffset: false),

            // XMP stores an ISO-8601 string, so the offset travels inside the value.
            new DateFieldSpec(
                DateField.XmpDateCreated,
                FieldGenre.Metadata,
                "XMP-photoshop:DateCreated",
                OffsetTag: null,
                SubSecondTag: null,
                CarriesOwnOffset: true),

            new DateFieldSpec(
                DateField.QuickTimeCreateDate,
                FieldGenre.Metadata,
                "QuickTime:CreateDate",
                OffsetTag: null,
                SubSecondTag: null,
                CarriesOwnOffset: false),

            new DateFieldSpec(
                DateField.QuickTimeModifyDate,
                FieldGenre.Metadata,
                "QuickTime:ModifyDate",
                OffsetTag: null,
                SubSecondTag: null,
                CarriesOwnOffset: false),

            new DateFieldSpec(
                DateField.IptcDateCreated,
                FieldGenre.Metadata,
                "IPTC:DateCreated",
                OffsetTag: null,
                SubSecondTag: null,
                CarriesOwnOffset: false),
        };

        // Indexed by enum value, so Get is an array lookup and a new field that is added to
        // the enum but not to this table fails loudly at startup rather than silently later.
        var byField = new DateFieldSpec[Enum.GetValues<DateField>().Length];
        foreach (DateFieldSpec spec in specs)
        {
            byField[(int)spec.Field] = spec;
        }

        for (int i = 0; i < byField.Length; i++)
        {
            if (byField[i] is null)
            {
                throw new InvalidOperationException(
                    $"DateFieldCatalog has no entry for {(DateField)i}. Every field needs a spec.");
            }
        }

        return byField;
    }

    private static DateFieldSpec Fs(DateField field) =>
        new(field, FieldGenre.FileSystem, Tag: null, OffsetTag: null, SubSecondTag: null, CarriesOwnOffset: false);
}
