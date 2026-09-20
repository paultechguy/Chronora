// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Rules.Tests;

/// <summary>
/// Which date fields a given kind of file actually has.
///
/// This is what lets one template cover photos and videos together, which is the whole
/// reason it exists: a folder off a phone holds both, and which tag carries the date is
/// the app's problem rather than the user's. Without it a template naming both would try
/// to write an EXIF tag into an MP4 and a QuickTime tag into a JPEG.
///
/// It drops fields SILENTLY rather than reporting them blocked, so it is worth being
/// precise about: a rule that quietly does nothing is the failure mode this whole app
/// exists to prevent, and the line between "not applicable" and "should have worked" is
/// exactly here.
/// </summary>
public class FieldApplicabilityTests
{
    /// <summary>File dates are on everything, which is why the simple path works anywhere.</summary>
    [Theory]
    [InlineData(MediaKind.Jpeg)]
    [InlineData(MediaKind.Video)]
    [InlineData(MediaKind.Other)]
    [InlineData(MediaKind.RawProprietary)]
    public void Every_kind_of_file_has_file_dates(MediaKind kind)
    {
        DateFieldCatalog.AppliesTo(DateField.FileCreated, kind).ShouldBeTrue();
        DateFieldCatalog.AppliesTo(DateField.FileModified, kind).ShouldBeTrue();
        DateFieldCatalog.AppliesTo(DateField.FileAccessed, kind).ShouldBeTrue();
        DateFieldCatalog.AppliesTo(DateField.FileChanged, kind).ShouldBeTrue();
    }

    [Theory]
    [InlineData(MediaKind.Jpeg)]
    [InlineData(MediaKind.Heic)]
    [InlineData(MediaKind.Tiff)]
    [InlineData(MediaKind.Png)]
    [InlineData(MediaKind.Dng)]
    [InlineData(MediaKind.RawProprietary)]
    public void An_image_has_an_exif_taken_date(MediaKind kind) =>
        DateFieldCatalog.AppliesTo(DateField.ExifDateTimeOriginal, kind).ShouldBeTrue();

    [Theory]
    [InlineData(MediaKind.Video)]
    [InlineData(MediaKind.Other)]
    public void A_video_has_no_exif_taken_date(MediaKind kind) =>
        DateFieldCatalog.AppliesTo(DateField.ExifDateTimeOriginal, kind).ShouldBeFalse();

    [Fact]
    public void Only_a_video_has_quicktime_dates()
    {
        DateFieldCatalog.AppliesTo(DateField.QuickTimeCreateDate, MediaKind.Video).ShouldBeTrue();
        DateFieldCatalog.AppliesTo(DateField.QuickTimeModifyDate, MediaKind.Video).ShouldBeTrue();

        foreach (MediaKind kind in Enum.GetValues<MediaKind>().Where(k => k != MediaKind.Video))
        {
            DateFieldCatalog.AppliesTo(DateField.QuickTimeCreateDate, kind)
                .ShouldBeFalse($"{kind} has no QuickTime atoms");
        }
    }

    /// <summary>
    /// A file with nothing to read has no metadata fields at all. Anything else would send
    /// ExifTool at a .txt.
    /// </summary>
    [Fact]
    public void A_plain_file_has_no_metadata_fields()
    {
        foreach (DateFieldSpec spec in DateFieldCatalog.All.Where(s => s.Genre == FieldGenre.Metadata))
        {
            DateFieldCatalog.AppliesTo(spec.Field, MediaKind.Other).ShouldBeFalse();
        }
    }
}
