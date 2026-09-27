// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Metadata;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// Recognising personal details, and the arguments that remove them. The dangerous mistakes
/// here all succeed: a strip that also takes the maker notes, or the Taken date, reports
/// exactly the same as one that does not.
/// </summary>
public class PrivacyTests
{
    /// <summary>
    /// Real ExifTool output (13.59, from the measurement behind PrivacyTagCatalog), plus the
    /// three things the classifier must NOT count: a Composite tag, SubjectLocation - a focus
    /// point in pixels - and Copyright.
    /// </summary>
    private const string Json = """
        [{
          "SourceFile": "C:/p/a.jpg",
          "GPS:GPSLatitude": "48 deg 51' 30.24\"",
          "XMP-exif:GPSLatitude": "48 deg 51' 30.24\" N",
          "Composite:GPSPosition": "48 deg 51' 30.24\" N, 2 deg 17' 40.20\" E",
          "IPTC:City": "Paris",
          "IPTC:Province-State": "IDF",
          "XMP-iptcExt:LocationCreatedCity": "Paris",
          "Panasonic:Landmark": "Eiffel Tower",
          "ExifIFD:SubjectLocation": "1024 768",
          "ExifIFD:SerialNumber": "SN123",
          "IFD0:Artist": "Jane Doe",
          "PNG:Author": "Jane Doe",
          "IFD0:Copyright": "(c) Jane",
          "IFD0:Software": "Photoshop 25",
          "XMP-xmpMM:DocumentID": "xmp.did:123",
          "IFD1:ThumbnailImage": "(Binary data 678 bytes, use -b option to extract)"
        },
        {
          "SourceFile": "C:/p/b.jpg",
          "XMP-GCamera:MotionPhoto": 1
        },
        {
          "SourceFile": "C:/p/c.jpg",
          "XMP-GCamera:MicroVideo": 0
        }]
        """;

    [Fact]
    public void Each_tag_lands_in_its_category_and_the_lookalikes_do_not()
    {
        PrivacyFindings a = new MetadataReader().ParsePrivacy(Json)[0].Findings;

        a.TagsFor(PrivacyCategory.Location).ShouldBe(
            ["GPS:GPSLatitude", "XMP-exif:GPSLatitude", "IPTC:City", "IPTC:Province-State", "XMP-iptcExt:LocationCreatedCity", "Panasonic:Landmark"],
            ignoreOrder: true);
        a.TagsFor(PrivacyCategory.CameraOwner).ShouldBe(["ExifIFD:SerialNumber", "IFD0:Artist", "PNG:Author"], ignoreOrder: true);
        a.TagsFor(PrivacyCategory.SoftwareEdits).ShouldBe(["IFD0:Software", "XMP-xmpMM:DocumentID"], ignoreOrder: true);
        a.TagsFor(PrivacyCategory.Thumbnail).ShouldBe(["IFD1:ThumbnailImage"]);
        a.HasEmbeddedMedia.ShouldBeFalse();
    }

    [Fact]
    public void A_motion_photo_is_recognised_and_a_flag_that_says_no_is_not()
    {
        IReadOnlyList<FilePrivacy> files = new MetadataReader().ParsePrivacy(Json);

        files[1].Findings.HasEmbeddedMedia.ShouldBeTrue();
        files[2].Findings.HasEmbeddedMedia.ShouldBeFalse();
        files[1].Findings.Found.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_strip_is_in_place_and_asks_only_for_the_chosen_categories()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);

        MetadataWriteResult result = await new MetadataWriter().StripAsync(
            session, "C:/p/a.jpg", [PrivacyCategory.Thumbnail], TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.BackupPath.ShouldBeNull("no copy of the details is kept anywhere");
        session.LastCommand.ShouldBe(["-overwrite_original_in_place", "-P", "-ThumbnailImage=", "-PreviewImage=", "C:/p/a.jpg"]);
    }

    /// <summary>
    /// The three deletes that would do real harm while looking like thoroughness. -all= takes
    /// the dates this app exists to fix; MakerNotes:all= breaks Live Photo pairing and HDR;
    /// XMP-crs:all= silently undoes somebody's Lightroom edit.
    /// </summary>
    [Fact]
    public void Every_category_together_never_deletes_wholesale()
    {
        IReadOnlyList<string> args = PrivacyTagCatalog.DeleteArgumentsFor(PrivacyCategoryNames.All);

        args.ShouldNotContain("-all=");
        args.ShouldNotContain(a => a.Contains("MakerNotes", StringComparison.OrdinalIgnoreCase));
        args.ShouldNotContain(a => a.Contains("crs", StringComparison.OrdinalIgnoreCase));
        args.ShouldNotContain(a => a.Contains("Date", StringComparison.OrdinalIgnoreCase));
        args.ShouldNotContain(a => a.Contains("Copyright", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_warning_on_a_successful_write_is_still_a_success()
    {
        var session = new FakeExifToolSession().Answers(
            "    1 image files updated", "Warning: [minor] Creating non-standard IPTC in PNG - a.png", status: 0);

        MetadataWriteResult result = await new MetadataWriter().StripAsync(
            session, "C:/p/a.png", [PrivacyCategory.Location], TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
    }
}
