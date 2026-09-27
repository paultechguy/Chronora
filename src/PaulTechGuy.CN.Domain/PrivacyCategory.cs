// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;

namespace PaulTechGuy.CN.Domain;

/// <summary>
/// A kind of personal detail that the Private details intent can remove.
///
/// Four, fixed, and named for what a person worries about rather than for a tag format.
/// "Location" is not "EXIF GPS": the same coordinates are routinely stored in EXIF, XMP,
/// IPTC and QuickTime at once, and removing one copy while leaving the others is the
/// failure this feature exists to prevent.
///
/// What is NOT here matters as much: dates, orientation and the colour profile are always
/// kept. This is a date tool, and a privacy clean that wiped the Taken date would undo the
/// app's own work - while a missing orientation turns portraits sideways.
/// </summary>
public enum PrivacyCategory
{
    /// <summary>GPS and place names, wherever they are stored.</summary>
    Location,

    /// <summary>Body and lens serial numbers, the owner's name, the creator's name.</summary>
    CameraOwner,

    /// <summary>The software that touched the file, and the edit history it left behind.</summary>
    SoftwareEdits,

    /// <summary>
    /// Embedded thumbnail and preview images. The classic leak: crop a photo and the
    /// thumbnail often still shows the uncropped original.
    /// </summary>
    Thumbnail,
}

/// <summary>
/// Which personal details one file carries, read once and then sealed like the rest of the scan.
///
/// Tag NAMES only, never values. The whole point of the feature is that the values go; a
/// snapshot holding them would keep a copy of the coordinates in memory, in the preview, and
/// in anything that serialises a row.
/// </summary>
/// <param name="Found">Per category, the group-qualified names of the tags present.</param>
/// <param name="HasEmbeddedMedia">
/// The file carries a second image or a video inside it - a motion photo's MP4 trailer, or an
/// MPF preview. Those have their own metadata, which removing tags from the outer file does not
/// reach, so such a file is refused rather than reported clean.
/// </param>
public sealed record PrivacyFindings(
    FrozenDictionary<PrivacyCategory, IReadOnlyList<string>> Found,
    bool HasEmbeddedMedia)
{
    public static PrivacyFindings None { get; } =
        new(FrozenDictionary<PrivacyCategory, IReadOnlyList<string>>.Empty, HasEmbeddedMedia: false);

    public bool Has(PrivacyCategory category) =>
        this.Found.TryGetValue(category, out IReadOnlyList<string>? tags) && tags.Count > 0;

    public IReadOnlyList<string> TagsFor(PrivacyCategory category) =>
        this.Found.TryGetValue(category, out IReadOnlyList<string>? tags) ? tags : [];
}

/// <summary>Display names, so the UI and the journal describe the categories the same way.</summary>
public static class PrivacyCategoryNames
{
    /// <summary>Every category, in the order they are shown.</summary>
    public static IReadOnlyList<PrivacyCategory> All { get; } =
        [PrivacyCategory.Location, PrivacyCategory.CameraOwner, PrivacyCategory.SoftwareEdits, PrivacyCategory.Thumbnail];

    /// <summary>As a heading or a checkbox label: "Camera and owner".</summary>
    public static string Title(PrivacyCategory category) => category switch
    {
        PrivacyCategory.Location => "Location",
        PrivacyCategory.CameraOwner => "Camera, lens and owner",
        PrivacyCategory.SoftwareEdits => "Software and edit history",
        PrivacyCategory.Thumbnail => "Embedded thumbnail",
        _ => category.ToString(),
    };

    /// <summary>As a word in a running list: "location · serial · thumbnail".</summary>
    public static string InList(PrivacyCategory category) => category switch
    {
        PrivacyCategory.Location => "location",
        PrivacyCategory.CameraOwner => "camera",
        PrivacyCategory.SoftwareEdits => "software",
        PrivacyCategory.Thumbnail => "thumbnail",
        _ => category.ToString(),
    };
}
