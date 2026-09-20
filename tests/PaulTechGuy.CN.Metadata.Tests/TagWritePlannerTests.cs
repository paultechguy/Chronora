// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// The timezone and sub-second corrections, as executable claims.
///
/// Every one of these guards a failure that is SILENT: ExifTool reports success either way,
/// and the damage only shows up later when something reads the file back. Worth stating as
/// tests rather than as comments.
/// </summary>
public class TagWritePlannerTests
{
    private static readonly DateTimeOffset Stamp =
        new(2024, 3, 15, 14, 25, 30, 847, TimeSpan.FromHours(1));

    private static string ValueOf(IReadOnlyList<TagAssignment> plan, string tagSuffix) =>
        plan.Single(a => a.Tag.EndsWith(tagSuffix, StringComparison.Ordinal)).Value;

    /// <summary>
    /// Correction 1: an EXIF date tag has no room for a zone, and ExifTool drops one
    /// written into it without complaint. The offset must be its own tag or it is lost.
    /// </summary>
    [Fact]
    public void An_exif_date_is_written_naive_with_the_offset_as_a_separate_tag()
    {
        IReadOnlyList<TagAssignment> plan = TagWritePlanner.Plan(
            DateField.ExifDateTimeOriginal, Stamp, DatePrecision.Second);

        ValueOf(plan, "DateTimeOriginal").ShouldBe("2024:03:15 14:25:30");

        ValueOf(plan, "DateTimeOriginal")
            .Contains('+', StringComparison.Ordinal)
            .ShouldBeFalse("a zone written into an EXIF date tag is silently discarded");

        ValueOf(plan, "OffsetTimeOriginal").ShouldBe("+01:00");
    }

    /// <summary>
    /// Correction 2, first half. The source knew only seconds, so an existing sub-second
    /// value has to go: 14:25:30 with a leftover .847 describes a moment that never was.
    /// </summary>
    [Fact]
    public void A_second_precision_source_clears_any_existing_sub_second_tag()
    {
        IReadOnlyList<TagAssignment> plan = TagWritePlanner.Plan(
            DateField.ExifDateTimeOriginal, Stamp, DatePrecision.Second);

        TagAssignment subSecond = plan.Single(a => a.Tag.EndsWith("SubSecTimeOriginal", StringComparison.Ordinal));

        subSecond.IsDeletion.ShouldBeTrue("a stale fraction would contradict the new time");
        subSecond.ToArgument().ShouldBe("-ExifIFD:SubSecTimeOriginal=");
    }

    [Fact]
    public void A_millisecond_precision_source_writes_the_sub_second_tag()
    {
        IReadOnlyList<TagAssignment> plan = TagWritePlanner.Plan(
            DateField.ExifDateTimeOriginal, Stamp, DatePrecision.Millisecond);

        ValueOf(plan, "SubSecTimeOriginal").ShouldBe("847");
    }

    /// <summary>
    /// Correction 2, second half. Declining to write an offset must CLEAR the existing one,
    /// not skip it: leaving it makes the file assert a zone the new value was never in.
    /// </summary>
    [Fact]
    public void Declining_to_write_an_offset_clears_the_existing_one()
    {
        IReadOnlyList<TagAssignment> plan = TagWritePlanner.Plan(
            DateField.ExifDateTimeOriginal, Stamp, DatePrecision.Second, writeOffset: false);

        plan.Single(a => a.Tag.EndsWith("OffsetTimeOriginal", StringComparison.Ordinal))
            .IsDeletion.ShouldBeTrue();
    }

    /// <summary>
    /// Every EXIF date field carries its own offset and sub-second companions, and each
    /// pairs with a DIFFERENT tag. Getting the pairing wrong writes the right value to the
    /// wrong place.
    /// </summary>
    [Theory]
    [InlineData(DateField.ExifDateTimeOriginal, "OffsetTimeOriginal", "SubSecTimeOriginal")]
    [InlineData(DateField.ExifCreateDate, "OffsetTimeDigitized", "SubSecTimeDigitized")]
    [InlineData(DateField.ExifModifyDate, "OffsetTime", "SubSecTime")]
    public void Each_exif_date_pairs_with_its_own_companions(DateField field, string offsetTag, string subSecondTag)
    {
        IReadOnlyList<TagAssignment> plan = TagWritePlanner.Plan(field, Stamp, DatePrecision.Second);

        plan.ShouldContain(a => a.Tag.EndsWith(offsetTag, StringComparison.Ordinal));
        plan.ShouldContain(a => a.Tag.EndsWith(subSecondTag, StringComparison.Ordinal));
    }

    /// <summary>XMP holds the offset inside the value, so it needs no companion tags.</summary>
    [Fact]
    public void An_xmp_date_is_a_single_iso_write()
    {
        IReadOnlyList<TagAssignment> plan = TagWritePlanner.Plan(
            DateField.XmpDateCreated, Stamp, DatePrecision.Second);

        plan.Count.ShouldBe(1);
        plan[0].Value.ShouldBe("2024-03-15T14:25:30+01:00");
    }

    [Theory]
    [InlineData(0, "+00:00")]
    [InlineData(1, "+01:00")]
    [InlineData(-5, "-05:00")]
    [InlineData(9, "+09:00")]
    [InlineData(14, "+14:00")]
    public void An_offset_is_formatted_the_way_exif_expects(int hours, string expected) =>
        TagWritePlanner.FormatOffset(TimeSpan.FromHours(hours)).ShouldBe(expected);

    /// <summary>India, Nepal and the Chathams are all half or quarter hours.</summary>
    [Fact]
    public void A_half_hour_zone_keeps_its_minutes() =>
        TagWritePlanner.FormatOffset(new TimeSpan(5, 30, 0)).ShouldBe("+05:30");

    [Fact]
    public void A_negative_half_hour_zone_keeps_its_sign_and_minutes() =>
        TagWritePlanner.FormatOffset(new TimeSpan(-3, -30, 0)).ShouldBe("-03:30");

    /// <summary>UTC is "+00:00" in EXIF, never "Z".</summary>
    [Fact]
    public void Utc_is_written_as_a_zero_offset_not_as_z() =>
        TagWritePlanner.FormatOffset(TimeSpan.Zero).ShouldBe("+00:00");

    [Fact]
    public void Asking_for_a_filesystem_field_is_rejected() =>
        Should.Throw<ArgumentException>(() =>
            TagWritePlanner.Plan(DateField.FileCreated, Stamp, DatePrecision.Second));

    /// <summary>Restoring a tag that was absent deletes it rather than blanking it.</summary>
    [Fact]
    public void Restoring_an_absent_tag_deletes_it()
    {
        TagAssignment restore = TagWritePlanner.PlanRestore("ExifIFD:DateTimeOriginal", wasPresent: false, raw: null);

        restore.IsDeletion.ShouldBeTrue();
    }

    /// <summary>
    /// A tag that existed but held an empty value is restored to empty, which is a
    /// different instruction from deleting it even though both produce an empty argument.
    /// </summary>
    [Fact]
    public void Restoring_a_tag_that_was_empty_writes_it_back_empty()
    {
        TagAssignment restore = TagWritePlanner.PlanRestore("ExifIFD:DateTimeOriginal", wasPresent: true, raw: string.Empty);

        restore.Value.ShouldBe(string.Empty);
    }

    /// <summary>Junk round-trips verbatim; a restore is not an opportunity to tidy up.</summary>
    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("2024:03:15 14:25:30")]
    [InlineData("not a date")]
    public void Restoring_a_real_value_is_byte_exact(string raw) =>
        TagWritePlanner.PlanRestore("ExifIFD:DateTimeOriginal", wasPresent: true, raw).Value.ShouldBe(raw);
}

/// <summary>
/// Correction 3: -api QuickTimeUTC must be inferred per file. Applied uniformly it shifts
/// half a library by the UTC offset, in the wrong direction, with no error.
/// </summary>
public class QuickTimeUtcInferenceTests
{
    private static readonly TimeSpan Local = TimeSpan.FromHours(-7);

    [Fact]
    public void A_camera_that_wrote_local_time_is_detected_and_left_alone()
    {
        // The atom holds the same wall-clock reading as the EXIF date, so it is local.
        var exif = new DateTimeOffset(2024, 3, 15, 14, 25, 30, Local);
        var quickTime = new DateTimeOffset(2024, 3, 15, 14, 25, 30, Local);

        TagWritePlanner.ShouldTreatQuickTimeAsUtc(quickTime, exif, Local).ShouldBeFalse();
    }

    [Fact]
    public void A_camera_that_wrote_real_utc_is_detected()
    {
        var exif = new DateTimeOffset(2024, 3, 15, 14, 25, 30, Local);

        // The atom holds the same INSTANT, expressed as UTC: seven hours further on.
        var quickTime = new DateTimeOffset(2024, 3, 15, 21, 25, 30, Local);

        TagWritePlanner.ShouldTreatQuickTimeAsUtc(quickTime, exif, Local).ShouldBeTrue();
    }

    /// <summary>
    /// With nothing to compare against, follow the specification rather than guessing the
    /// camera got it wrong.
    /// </summary>
    [Fact]
    public void With_no_corroborating_date_the_specification_wins() =>
        TagWritePlanner.ShouldTreatQuickTimeAsUtc(
            new DateTimeOffset(2024, 3, 15, 14, 25, 30, TimeSpan.Zero), null, Local).ShouldBeTrue();

    /// <summary>A few seconds of drift between two clocks in one device is normal.</summary>
    [Fact]
    public void A_small_discrepancy_still_counts_as_local()
    {
        var exif = new DateTimeOffset(2024, 3, 15, 14, 25, 30, Local);
        var quickTime = new DateTimeOffset(2024, 3, 15, 14, 25, 41, Local);

        TagWritePlanner.ShouldTreatQuickTimeAsUtc(quickTime, exif, Local).ShouldBeFalse();
    }

    /// <summary>
    /// In a zone with no offset the two readings are identical, so the question does not
    /// arise and the answer must not flip-flop.
    /// </summary>
    [Fact]
    public void In_utc_both_readings_agree_and_the_result_is_stable()
    {
        var exif = new DateTimeOffset(2024, 3, 15, 14, 25, 30, TimeSpan.Zero);
        var quickTime = new DateTimeOffset(2024, 3, 15, 14, 25, 30, TimeSpan.Zero);

        TagWritePlanner.ShouldTreatQuickTimeAsUtc(quickTime, exif, TimeSpan.Zero).ShouldBeFalse();
    }
}
