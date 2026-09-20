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

/// <summary>
/// Reading a metadata date with no ExifTool.
///
/// Reported from the app: dropping two files, picking the template that copies the
/// camera's date onto the file dates, and getting no dates at all - every row just said
/// "no change".
///
/// The targets are filesystem fields, so the blocked-target check saw nothing wrong. The
/// SOURCE was the metadata, it read as empty with no engine, and the rule came out as an
/// ordinary skip. The app was quietly doing nothing and not saying so, in the one feature
/// that is the reason it exists.
/// </summary>
public class MetadataSourceTests
{
    private static readonly DateTimeOffset Stamp = new(2024, 3, 15, 14, 25, 30, TimeSpan.Zero);

    private static ScannedFile Photo() => new(
        @"C:\photos\IMG_1234.jpg",
        1024,
        MediaKind.Jpeg,
        FileAttributes.Normal,
        new TimestampSet(Stamp, Stamp, null, null),
        System.Collections.Frozen.FrozenDictionary<DateField, MetadataValue>.Empty,
        FileTraits.None);

    /// <summary>The recipe behind "Explorer shows the wrong date".</summary>
    private static Recipe CopyTakenOntoFileDates() => new(
        [new DateRule(
            new DateSource.CopyFrom(
                Aggregate.Earliest,
                [DateField.ExifDateTimeOriginal, DateField.QuickTimeCreateDate]),
            new HashSet<DateField> { DateField.FileCreated, DateField.FileModified },
            RuleGuards.None)],
        ScanFilter.Default);

    [Fact]
    public void Reading_a_photo_date_without_exiftool_says_so_rather_than_nothing()
    {
        FilePlan plan = new RuleEvaluator().Evaluate(
            Photo(),
            CopyTakenOntoFileDates(),
            new EvaluationContext(ClockContext.Local, DateTimeOffset.UtcNow, MetadataEngineAvailable: false));

        plan.Changes.ShouldNotBeEmpty();
        plan.Changes.ShouldAllBe(c => c.Status == ChangeStatus.Blocked);
        plan.Changes.ShouldAllBe(c => c.Problem == ProblemCode.MetadataEngineUnavailable);
    }

    /// <summary>
    /// With the engine present and the photo genuinely having no taken date, the honest
    /// answer is different: nothing to copy from, which is not the same as needing a tool.
    /// </summary>
    [Fact]
    public void A_photo_with_no_taken_date_reports_no_source_rather_than_a_missing_tool()
    {
        FilePlan plan = new RuleEvaluator().Evaluate(
            Photo(),
            CopyTakenOntoFileDates(),
            new EvaluationContext(ClockContext.Local, DateTimeOffset.UtcNow, MetadataEngineAvailable: true));

        plan.Changes.ShouldAllBe(c => c.Problem == ProblemCode.NoSourceValue);
        plan.Changes.ShouldAllBe(c => c.Status == ChangeStatus.Skipped);
    }

    /// <summary>
    /// A rule reading only file dates is untouched by any of this. Blocking it would
    /// refuse work the app can plainly do with no engine at all.
    /// </summary>
    [Fact]
    public void A_rule_that_reads_only_file_dates_still_works_without_exiftool()
    {
        var recipe = new Recipe(
            [new DateRule(
                new DateSource.CopyFrom(Aggregate.Earliest, [DateField.FileModified]),
                new HashSet<DateField> { DateField.FileCreated },
                RuleGuards.None)],
            ScanFilter.Default);

        FilePlan plan = new RuleEvaluator().Evaluate(
            Photo(),
            recipe,
            new EvaluationContext(ClockContext.Local, DateTimeOffset.UtcNow, MetadataEngineAvailable: false));

        plan.Changes.ShouldAllBe(c => c.Problem != ProblemCode.MetadataEngineUnavailable);
    }
}
