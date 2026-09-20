// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Metadata;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// The read path, against ExifTool's real output shapes.
///
/// Everything wrong here is silent. A dropped offset, a sub-second read as a count rather
/// than a fraction, a QuickTime atom taken as UTC when the camera wrote local - each
/// produces a date that looks entirely plausible and is hours or months off.
/// </summary>
public class MetadataReaderTests
{
    /// <summary>A fixed zone, so these assertions do not depend on where the build runs.</summary>
    private static readonly TimeZoneInfo Denver =
        TimeZoneInfo.CreateCustomTimeZone("test-mst", TimeSpan.FromHours(-7), "Test MST", "Test MST");

    private static readonly MetadataReader Reader = new();

    private static IReadOnlyList<FileMetadata> Read(string json) => Reader.Parse(json, Denver);

    /// <summary>
    /// The one that makes the whole feature worth having: EXIF has no room for a zone, so
    /// the offset lives in its own tag and has to be put back together on read.
    /// </summary>
    [Fact]
    public void The_offset_tag_is_recombined_with_the_date()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{
              "SourceFile": "a.jpg",
              "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30",
              "ExifIFD:OffsetTimeOriginal": "+01:00"
            }]
            """);

        MetadataValue taken = files.Single().Values[DateField.ExifDateTimeOriginal];

        taken.Parsed.ShouldBe(new DateTimeOffset(2024, 3, 15, 14, 25, 30, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_negative_offset_is_recombined_with_the_right_sign()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{
              "SourceFile": "a.jpg",
              "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30",
              "ExifIFD:OffsetTimeOriginal": "-05:00"
            }]
            """);

        files.Single().Values[DateField.ExifDateTimeOriginal].Parsed
            .ShouldBe(new DateTimeOffset(2024, 3, 15, 14, 25, 30, TimeSpan.FromHours(-5)));
    }

    /// <summary>
    /// Most photos have no offset tag at all. Assuming UTC would shift the entire library
    /// by the machine's offset, so a naive EXIF date is read as local time.
    /// </summary>
    [Fact]
    public void A_date_with_no_offset_tag_is_read_as_local_time()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.jpg", "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30" }]
            """);

        files.Single().Values[DateField.ExifDateTimeOriginal].Parsed
            .ShouldBe(new DateTimeOffset(2024, 3, 15, 14, 25, 30, TimeSpan.FromHours(-7)));
    }

    /// <summary>
    /// A sub-second tag is a FRACTION. "8" means eight tenths, not eight milliseconds -
    /// read the other way every such photo lands 792 ms early.
    /// </summary>
    [Theory]
    [InlineData("8", 800)]
    [InlineData("84", 840)]
    [InlineData("847", 847)]
    [InlineData("8472", 847)]
    public void The_subsecond_tag_is_read_as_a_fraction(string stored, int expectedMilliseconds)
    {
        IReadOnlyList<FileMetadata> files = Read($$"""
            [{
              "SourceFile": "a.jpg",
              "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30",
              "ExifIFD:SubSecTimeOriginal": "{{stored}}"
            }]
            """);

        files.Single().Values[DateField.ExifDateTimeOriginal].Parsed!.Value.Millisecond
            .ShouldBe(expectedMilliseconds);
    }

    /// <summary>ExifTool emits a bare number for a sub-second tag that looks numeric.</summary>
    [Fact]
    public void A_subsecond_tag_that_arrives_as_a_json_number_is_still_read()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{
              "SourceFile": "a.jpg",
              "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30",
              "ExifIFD:SubSecTimeOriginal": 847
            }]
            """);

        files.Single().Values[DateField.ExifDateTimeOriginal].Parsed!.Value.Millisecond.ShouldBe(847);
    }

    /// <summary>
    /// Real libraries are full of this. It is present, so it must be recorded as present
    /// and preserved byte-exact, but it is not a date and must never be shown as one.
    /// </summary>
    [Fact]
    public void A_zeroed_date_is_present_and_preserved_but_not_parsed()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.jpg", "ExifIFD:DateTimeOriginal": "0000:00:00 00:00:00" }]
            """);

        MetadataValue taken = files.Single().Values[DateField.ExifDateTimeOriginal];

        taken.Present.ShouldBeTrue();
        taken.Raw.ShouldBe("0000:00:00 00:00:00");
        taken.Parsed.ShouldBeNull();
        taken.HasUsableDate.ShouldBeFalse();
    }

    /// <summary>
    /// Raw is what a revert writes back, so it has to be the string that was on disk and
    /// not a tidied version of it.
    /// </summary>
    [Fact]
    public void The_raw_value_is_kept_exactly_as_it_was_read()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.jpg", "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30.847" }]
            """);

        files.Single().Values[DateField.ExifDateTimeOriginal].Raw.ShouldBe("2024:03:15 14:25:30.847");
    }

    [Fact]
    public void A_fraction_inside_the_date_itself_is_read()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.jpg", "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30.847" }]
            """);

        files.Single().Values[DateField.ExifDateTimeOriginal].Parsed!.Value.Millisecond.ShouldBe(847);
    }

    /// <summary>A tag that is not there is absent, not empty - the two revert differently.</summary>
    [Fact]
    public void A_tag_that_is_not_there_does_not_appear_at_all()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.jpg", "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30" }]
            """);

        files.Single().Values.ContainsKey(DateField.ExifCreateDate).ShouldBeFalse();
        files.Single().Values.ContainsKey(DateField.QuickTimeCreateDate).ShouldBeFalse();
    }

    [Fact]
    public void A_tag_that_is_there_but_empty_is_present()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.jpg", "ExifIFD:DateTimeOriginal": "" }]
            """);

        MetadataValue taken = files.Single().Values[DateField.ExifDateTimeOriginal];

        taken.Present.ShouldBeTrue();
        taken.Parsed.ShouldBeNull();
    }

    /// <summary>XMP is ISO-8601 and carries its own offset, so no companion tags apply.</summary>
    [Fact]
    public void An_xmp_date_carries_its_own_offset()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.jpg", "XMP-photoshop:DateCreated": "2024-03-15T14:25:30+02:00" }]
            """);

        files.Single().Values[DateField.XmpDateCreated].Parsed
            .ShouldBe(new DateTimeOffset(2024, 3, 15, 14, 25, 30, TimeSpan.FromHours(2)));
    }

    /// <summary>
    /// The QuickTime question, decided against the EXIF date. Here the two agree as
    /// written, which means the camera put local time in an atom specified as UTC - so the
    /// UTC reading must NOT be applied.
    /// </summary>
    [Fact]
    public void A_quicktime_atom_holding_local_time_is_read_as_local()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{
              "SourceFile": "a.mp4",
              "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30",
              "QuickTime:CreateDate": "2024:03:15 14:25:30"
            }]
            """);

        FileMetadata file = files.Single();

        file.QuickTimeReadAsUtc.ShouldBeFalse();
        file.Values[DateField.QuickTimeCreateDate].Parsed
            .ShouldBe(new DateTimeOffset(2024, 3, 15, 14, 25, 30, TimeSpan.FromHours(-7)));
    }

    /// <summary>
    /// And the other way: the atom is seven hours ahead of the EXIF date, which is exactly
    /// what a correct UTC atom looks like from a -07:00 zone.
    /// </summary>
    [Fact]
    public void A_quicktime_atom_holding_utc_is_read_as_utc()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{
              "SourceFile": "a.mp4",
              "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30",
              "QuickTime:CreateDate": "2024:03:15 21:25:30"
            }]
            """);

        FileMetadata file = files.Single();

        file.QuickTimeReadAsUtc.ShouldBeTrue();
        file.Values[DateField.QuickTimeCreateDate].Parsed!.Value.UtcDateTime
            .ShouldBe(new DateTime(2024, 3, 15, 21, 25, 30, DateTimeKind.Utc));
    }

    /// <summary>
    /// With nothing to check against, the specification wins: the atom is DEFINED as UTC,
    /// so assuming the camera got it wrong would be the more arrogant guess.
    /// </summary>
    [Fact]
    public void A_video_with_no_exif_date_falls_back_to_the_specification()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.mp4", "QuickTime:CreateDate": "2024:03:15 21:25:30" }]
            """);

        files.Single().QuickTimeReadAsUtc.ShouldBeTrue();
    }

    [Fact]
    public void Several_files_come_back_keyed_by_their_own_paths()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [
              { "SourceFile": "a.jpg", "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30" },
              { "SourceFile": "b.jpg", "ExifIFD:DateTimeOriginal": "2019:01:02 08:00:00" }
            ]
            """);

        files.Count.ShouldBe(2);
        files.Select(f => f.Path).ShouldBe(["a.jpg", "b.jpg"]);
    }

    /// <summary>
    /// A wedged or half-written response must cost the batch, never the run. Throwing here
    /// would take down a scan of 50,000 files over one bad record.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[{ \"SourceFile\": ")]
    [InlineData("{ \"SourceFile\": \"a.jpg\" }")]
    public void Unusable_output_yields_nothing_rather_than_throwing(string output)
    {
        Read(output).ShouldBeEmpty();
    }

    [Fact]
    public void A_record_with_no_source_file_is_skipped()
    {
        Read("""[{ "ExifIFD:DateTimeOriginal": "2024:03:15 14:25:30" }]""").ShouldBeEmpty();
    }

    /// <summary>
    /// -G1 qualifies tag names, but a tag unique to one group can come back bare, and
    /// missing it would silently report the file as having no date.
    /// </summary>
    [Fact]
    public void An_unqualified_tag_name_is_still_matched()
    {
        IReadOnlyList<FileMetadata> files = Read("""
            [{ "SourceFile": "a.jpg", "DateTimeOriginal": "2024:03:15 14:25:30" }]
            """);

        files.Single().Values.ShouldContainKey(DateField.ExifDateTimeOriginal);
    }

    /// <summary>
    /// The read has to ask for the offset and sub-second tags, not just the dates. Asking
    /// only for the dates is how the offset gets lost before any of the recombination
    /// above ever runs.
    /// </summary>
    [Fact]
    public async Task The_request_asks_for_the_companion_tags_as_well()
    {
        var session = new FakeExifToolSession().Answers("[]");

        _ = await new MetadataReader().ReadAsync(session, ["a.jpg"], Denver, TestContext.Current.CancellationToken);

        session.LastCommand.ShouldContain("-ExifIFD:DateTimeOriginal");
        session.LastCommand.ShouldContain("-ExifIFD:OffsetTimeOriginal");
        session.LastCommand.ShouldContain("-ExifIFD:SubSecTimeOriginal");
        session.LastCommand.ShouldContain("-j");
        session.LastCommand.ShouldContain("a.jpg");
    }

    [Fact]
    public async Task An_empty_batch_does_not_start_a_command()
    {
        var session = new FakeExifToolSession();

        IReadOnlyList<FileMetadata> files = await new MetadataReader()
            .ReadAsync(session, [], Denver, TestContext.Current.CancellationToken);

        files.ShouldBeEmpty();
        session.Commands.ShouldBeEmpty();
    }

    /// <summary>
    /// Only metadata fields. Asking ExifTool for FileCreated would get an answer, and it
    /// would be the wrong answer - the filesystem layer owns those and reads them without
    /// hydrating a cloud placeholder.
    /// </summary>
    [Fact]
    public void No_filesystem_field_is_ever_requested()
    {
        foreach (DateFieldSpec spec in DateFieldCatalog.All.Where(s => s.Genre == FieldGenre.FileSystem))
        {
            MetadataReader.TagsToRequest.ShouldNotContain(spec.Field.ToString());
        }

        MetadataReader.TagsToRequest.ShouldNotContain(tag => tag.Contains("FileModifyDate", StringComparison.Ordinal));
        MetadataReader.TagsToRequest.ShouldNotContain(tag => tag.Contains("FileCreateDate", StringComparison.Ordinal));
    }
}

/// <summary>
/// The argfile framing, which has exactly one rule worth a test of its own.
/// </summary>
public class ExifToolArgFileTests
{
    /// <summary>
    /// A line beginning with '#' is a COMMENT in an ExifTool argfile, and stdin under
    /// "-@ -" is an argfile.
    ///
    /// The status sentinel was "##cn1:${status}", so it was dropped before ExifTool ever
    /// saw it; -echo4 then consumed the following "-execute1" as its argument, the command
    /// never ran, no {ready} was emitted, and every metadata operation hung for five
    /// minutes without reporting anything. A one-character mistake that disabled the
    /// entire feature silently.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4096)]
    public void The_status_sentinel_is_never_read_as_a_comment(int commandNumber)
    {
        string prefix = InvokeStatusPrefix(commandNumber);

        prefix.ShouldNotStartWith("#");
        prefix.ShouldContain(commandNumber.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Each command needs its own tag, or two replies cannot be told apart.</summary>
    [Fact]
    public void Each_command_gets_its_own_sentinel()
    {
        InvokeStatusPrefix(1).ShouldNotBe(InvokeStatusPrefix(2));
    }

    private static string InvokeStatusPrefix(int number) => (string)typeof(ExifToolSession)
        .GetMethod("StatusPrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
        .Invoke(null, [number])!;
}
