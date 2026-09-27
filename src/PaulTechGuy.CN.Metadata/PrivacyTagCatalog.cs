// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Metadata;

/// <summary>
/// Which tags make up each category of personal detail: what to ask for, how to delete it,
/// and how to recognise it on the way back.
///
/// These lists were MEASURED, not reasoned out (2026-09-27, ExifTool 13.59). Every tag below
/// was written into a JPEG and a PNG, deleted with these arguments, and read back with
/// <c>-a -G1</c>. What that established, and why the lists look the way they do:
///
/// Unqualified names delete in EVERY group. <c>-Artist=</c> takes IFD0:Artist and PNG's own
/// Artist text chunk; <c>-Creator=</c> takes XMP-dc; <c>-By-line=</c> takes IPTC. PNG keeps
/// Artist and Software in text chunks rather than EXIF, which a group-qualified list would miss.
///
/// Wildcards delete too. <c>-*GPS*=</c> reaches the GPS IFD, XMP-exif, QuickTime's
/// GPSCoordinates and any writable maker-note GPS, which is what makes it possible to remove
/// location from a maker note WITHOUT deleting the whole maker note - <c>MakerNotes:all=</c>
/// would also take Apple's Live Photo pairing and HDR data, and is never used.
///
/// Nothing here is <c>-all=</c>. Deleting by name is what keeps the dates, Orientation and
/// the ICC profile, by construction rather than by a list of exceptions.
///
/// Recognition is by exact name, not by the read patterns. The read asks broadly so a
/// leftover is seen; the classifier is narrow so that EXIF SubjectLocation - a focus point in
/// pixels, not a place - is not reported as a location that failed to delete.
/// </summary>
public static class PrivacyTagCatalog
{
    /// <summary>
    /// The camera, the lens and how the shot was taken: Explorer's "Camera" and "Advanced
    /// photo" groups. Structural tags - ExifVersion, ColorSpace, PhotometricInterpretation -
    /// are deliberately absent; they describe the image, not the person holding the camera.
    ///
    /// Declared first because the static initialisers below read it, and they run in order.
    /// </summary>
    private static readonly string[] CameraNames =
    [
        "Make", "Model", "LensMake", "LensModel", "LensInfo", "Lens",
        "FNumber", "ApertureValue", "MaxApertureValue", "ExposureTime", "ShutterSpeedValue",
        "ISO", "SensitivityType", "RecommendedExposureIndex", "ExposureCompensation", "BrightnessValue",
        "FocalLength", "FocalLengthIn35mmFormat", "MeteringMode", "SubjectDistance", "SubjectDistanceRange",
        "SubjectArea", "SubjectLocation", "Flash", "FlashEnergy", "LightSource", "ExposureProgram",
        "ExposureMode", "WhiteBalance", "Contrast", "Saturation", "Sharpness", "DigitalZoomRatio",
        "SceneCaptureType", "SceneType", "SensingMethod", "GainControl", "FileSource", "CustomRendered",
        "CompositeImage",
    ];

    /// <summary>Deleting these takes the category out of the file.</summary>
    private static readonly Dictionary<PrivacyCategory, string[]> Deletes = new()
    {
        [PrivacyCategory.Location] =
        [
            "-*GPS*=", "-City*=", "-Country*=", "-State=", "-Province-State=", "-Sub-location=",
            "-Landmark=", "-Location=", "-LocationName=", "-LocationCreated*=", "-LocationShown*=",
        ],

        // Copyright is deliberately absent. It is the photographer's claim, not a leak, and
        // somebody sharing their own work almost always wants it to travel with the picture.
        //
        // Also everything Explorer lists under "Camera" and "Advanced photo" - maker, model,
        // lens and the shooting settings - added 2026-09-27 at Paul's request: together they
        // fingerprint a camera almost as well as its serial number does. Named one by one
        // rather than MakerNotes:all, for the Live Photo reason above.
        [PrivacyCategory.CameraOwner] =
        [
            "-*Serial*=", "-*Owner*=", "-Artist=", "-Creator=", "-By-line=", "-Author=",
            .. CameraNames.Select(name => $"-{name}="),
        ],

        // XMP-crs is deliberately absent: those are Lightroom's edit settings, and removing
        // them silently undoes somebody's editing. xmpMM goes because its ids and history
        // link this copy to every other version of the picture, and can carry file paths.
        [PrivacyCategory.SoftwareEdits] =
        [
            "-Software=", "-ProcessingSoftware=", "-HostComputer=", "-CreatorTool=",
            "-DocumentAncestors=", "-XMP-xmpMM:all=",
        ],

        // ThumbnailTIFF is not writable (measured), so it is not asked for; a file carrying
        // one is reported as still holding it rather than as clean.
        [PrivacyCategory.Thumbnail] = ["-ThumbnailImage=", "-PreviewImage="],
    };

    /// <summary>
    /// What to ask ExifTool for, with <c>-a</c> so a tag present in several groups comes back
    /// once per group. Broader than the classifier on purpose.
    /// </summary>
    internal static IReadOnlyList<string> ReadArguments { get; } =
    [
        "-a",
        "-*GPS*", "-City*", "-Country*", "-State", "-Province-State", "-Sub-location", "-Landmark",
        "-Location", "-LocationName", "-LocationCreated*", "-LocationShown*",
        "-*Serial*", "-*Owner*", "-Artist", "-Creator", "-By-line", "-Author",
        .. CameraNames.Select(name => "-" + name),
        "-Software", "-ProcessingSoftware", "-HostComputer", "-CreatorTool", "-DocumentAncestors", "-XMP-xmpMM:all",
        "-ThumbnailImage", "-PreviewImage", "-ThumbnailTIFF",
        "-*EmbeddedVideo*", "-MotionPhoto", "-MicroVideo",
    ];

    private static readonly HashSet<string> PlaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "State", "Province-State", "Sub-location", "Landmark", "Location", "LocationName",
    };

    private static readonly HashSet<string> PeopleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Artist", "Creator", "By-line", "Author",
    };

    private static readonly HashSet<string> SoftwareNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Software", "ProcessingSoftware", "HostComputer", "CreatorTool", "DocumentAncestors",
    };

    private static readonly HashSet<string> ThumbnailNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ThumbnailImage", "PreviewImage", "ThumbnailTIFF",
    };

    /// <summary>The delete arguments for the chosen categories, in category order.</summary>
    public static IReadOnlyList<string> DeleteArgumentsFor(IEnumerable<PrivacyCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        return [.. categories.Distinct().Order().SelectMany(c => Deletes[c])];
    }

    /// <summary>
    /// Which category a group-qualified key from <c>-G1 -j</c> belongs to, or null when it is
    /// none of them. Composite tags are derived from the real ones and never count: deleting
    /// the GPS IFD makes Composite:GPSPosition vanish on its own.
    /// </summary>
    public static PrivacyCategory? CategoryOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        int colon = key.LastIndexOf(':');
        string group = colon < 0 ? string.Empty : key[..colon];
        string name = colon < 0 ? key : key[(colon + 1)..];

        if (group.Equals("Composite", StringComparison.OrdinalIgnoreCase) || name.Equals("SourceFile", StringComparison.Ordinal))
        {
            return null;
        }

        if (name.Contains("GPS", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("City", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Country", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LocationCreated", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LocationShown", StringComparison.OrdinalIgnoreCase)
            || PlaceNames.Contains(name))
        {
            return PrivacyCategory.Location;
        }

        if (name.Contains("Serial", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Owner", StringComparison.OrdinalIgnoreCase)
            || PeopleNames.Contains(name)
            || CameraNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return PrivacyCategory.CameraOwner;
        }

        if (SoftwareNames.Contains(name) || group.Equals("XMP-xmpMM", StringComparison.OrdinalIgnoreCase))
        {
            return PrivacyCategory.SoftwareEdits;
        }

        return ThumbnailNames.Contains(name) ? PrivacyCategory.Thumbnail : null;
    }

    /// <summary>
    /// Whether a key and its value say the file carries a video of its own - a motion photo.
    ///
    /// Embedded video only, and that is a decision rather than an oversight. An MPF image is
    /// also "a second picture inside", but modern iPhone JPEGs carry their HDR gain map that
    /// way, so refusing MPF would refuse most of somebody's camera roll. Whether an MPF
    /// preview carries location of its own has not been measured.
    /// </summary>
    public static bool IsEmbeddedVideo(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        int colon = key.LastIndexOf(':');
        string name = colon < 0 ? key : key[(colon + 1)..];

        if (name.StartsWith("EmbeddedVideo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (name.Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase) || name.Equals("MicroVideo", StringComparison.OrdinalIgnoreCase))
            && value is not null && value != "0";
    }
}
