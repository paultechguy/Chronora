// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Metadata;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// Against a real ExifTool and real bytes on disk.
///
/// This file is deliberately short. It covers only the behaviours the plan's technical
/// review could not verify from documentation - one was read out of Exif.pm because it
/// appears in no HTML docs at all, two came through the Wayback Machine because the
/// upstream forum was offline - plus the byte-exact restore that undo depends on.
/// Everything else about the reader and writer is covered by the unit tests, which do not
/// need a 12 MB download to run.
///
/// The review's warning was specific: anyone re-checking these against the documentation
/// alone would wrongly conclude the original, broken design was fine. Each one fails
/// silently in production - the write succeeds, the preview says it worked, and the date
/// is hours off or the file quietly claims a zone nobody chose.
///
/// Skipped rather than failed when no local copy has been fetched, because a contributor
/// who has not run build/Get-ExifTool.ps1 has not broken anything.
/// </summary>
public class RealExifToolTests(ExifToolFixture fixture) : IClassFixture<ExifToolFixture>, IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "chronora-exiftool-integration", Guid.NewGuid().ToString("N"))).FullName;

    private static readonly TimeZoneInfo Denver =
        TimeZoneInfo.CreateCustomTimeZone("test-mst", TimeSpan.FromHours(-7), "Test MST", "Test MST");

    private ExifToolSession Session
    {
        get
        {
            Assert.SkipUnless(RealExifTool.IsAvailable, RealExifTool.HowToGetIt);
            return fixture.Session!;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this._folder))
            {
                Directory.Delete(this._folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A JPEG carrying a 2019 date, a +09:00 offset and 847 ms.</summary>
    private async Task<string> CreatePhotoAsync()
    {
        string path = RealExifTool.WriteJpeg(Path.Combine(this._folder, "photo.jpg"));

        ExifToolResult result = await this.Session.ExecuteAsync(
            [
                "-overwrite_original_in_place",
                "-ExifIFD:DateTimeOriginal=2019:01:01 14:25:30",
                "-ExifIFD:OffsetTimeOriginal=+09:00",
                "-ExifIFD:SubSecTimeOriginal=847",
                path,
            ],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue(result.StandardError);
        return path;
    }

    private async Task<FileMetadata> ReadAsync(string path)
    {
        IReadOnlyList<FileMetadata> read = await new MetadataReader()
            .ReadAsync(this.Session, [path], Denver, TestContext.Current.CancellationToken);

        return read.Single();
    }

    private static IReadOnlyList<TagAssignment> PlanTaken(DateTimeOffset value) =>
        TagWritePlanner.Plan(DateField.ExifDateTimeOriginal, value, DatePrecision.Second);

    /// <summary>
    /// CLAIM 1, the one read out of Exif.pm rather than any documentation.
    ///
    /// Writing a new date does NOT clear the existing sub-second and offset tags. This
    /// asserts the BROKEN behaviour on purpose: if a future ExifTool starts clearing them,
    /// Chronora's explicit clears become redundant and somebody should find that out here
    /// rather than by guessing.
    /// </summary>
    [Fact]
    public async Task Writing_only_the_date_leaves_a_stale_offset_and_subsecond_behind()
    {
        string path = await this.CreatePhotoAsync();

        ExifToolResult result = await this.Session.ExecuteAsync(
            ["-overwrite_original_in_place", "-ExifIFD:DateTimeOriginal=2024:03:15 10:00:00", path],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue(result.StandardError);

        MetadataValue taken = (await this.ReadAsync(path)).Values[DateField.ExifDateTimeOriginal];

        // The date moved to 2024; the fraction and the zone are still 2019's. Together
        // they describe a moment that never existed.
        taken.Raw.ShouldBe("2024:03:15 10:00:00");
        taken.Parsed!.Value.Offset.ShouldBe(TimeSpan.FromHours(9), "the stale offset tag survived");
        taken.Parsed!.Value.Millisecond.ShouldBe(847, "the stale sub-second tag survived");
    }

    /// <summary>
    /// CLAIM 2, and worse than the plan stated.
    ///
    /// An offset written into the date value is silently discarded - and the file is left
    /// asserting the OLD zone rather than none. Handing ExifTool a DateTimeOffset and
    /// expecting it to survive produces exactly the data loss the design exists to prevent.
    /// </summary>
    [Fact]
    public async Task An_offset_written_into_the_date_value_is_silently_discarded()
    {
        string path = await this.CreatePhotoAsync();

        ExifToolResult result = await this.Session.ExecuteAsync(
            ["-overwrite_original_in_place", "-ExifIFD:DateTimeOriginal=2024:03:15 10:00:00+01:00", path],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue("and that is the problem - it reports success");

        MetadataValue taken = (await this.ReadAsync(path)).Values[DateField.ExifDateTimeOriginal];

        taken.Raw.ShouldBe("2024:03:15 10:00:00", "the +01:00 never reached the tag");
        taken.Parsed!.Value.Offset.ShouldBe(TimeSpan.FromHours(9), "and the file still claims its old zone");
    }

    /// <summary>
    /// Chronora's answer to both of the above: the date, the offset as its own tag, and an
    /// explicit clear of the sub-second the new value does not have.
    /// </summary>
    [Fact]
    public async Task The_planned_write_produces_a_date_that_means_what_it_says()
    {
        string path = await this.CreatePhotoAsync();
        var target = new DateTimeOffset(2024, 3, 15, 10, 0, 0, TimeSpan.FromHours(1));

        MetadataWriteResult written = await new MetadataWriter().WriteAsync(
            this.Session,
            new MetadataWriteRequest(path, MediaKind.Jpeg, PlanTaken(target)),
            keepBackup: false,
            TestContext.Current.CancellationToken);

        written.Succeeded.ShouldBeTrue(written.Detail);

        MetadataValue taken = (await this.ReadAsync(path)).Values[DateField.ExifDateTimeOriginal];

        taken.Parsed!.Value.ShouldBe(target);
        taken.Parsed!.Value.Millisecond.ShouldBe(0, "the stale 847 ms was cleared rather than left behind");
    }

    /// <summary>
    /// CLAIM 3, and the one with the worst consequence.
    ///
    /// -overwrite_original renames a temp file over the original, handing it a NEW creation
    /// time. A tool whose whole job is setting dates must not reset the date it was asked
    /// to set. This is the only check that proves the in-place write on a real volume.
    /// </summary>
    [Fact]
    public async Task An_in_place_write_preserves_the_files_creation_time()
    {
        string path = await this.CreatePhotoAsync();

        var created = new DateTime(2019, 4, 2, 11, 30, 15, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);

        MetadataWriteResult written = await new MetadataWriter().WriteAsync(
            this.Session,
            new MetadataWriteRequest(
                path, MediaKind.Jpeg, PlanTaken(new DateTimeOffset(2024, 3, 15, 10, 0, 0, TimeSpan.Zero))),
            keepBackup: false,
            TestContext.Current.CancellationToken);

        written.Succeeded.ShouldBeTrue(written.Detail);

        File.GetCreationTimeUtc(path).ShouldBe(created, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// What undo actually does: put back the exact string that was there, rather than a
    /// re-rendering of a parsed date. Real files carry values that do not survive a
    /// round trip through DateTime, and re-emitting one is a second edit, not a revert.
    /// </summary>
    [Fact]
    public async Task A_raw_value_round_trips_byte_for_byte()
    {
        string path = await this.CreatePhotoAsync();

        string original = (await this.ReadAsync(path)).Values[DateField.ExifDateTimeOriginal].Raw!;

        _ = await this.Session.ExecuteAsync(
            ["-overwrite_original_in_place", "-ExifIFD:DateTimeOriginal=2024:03:15 10:00:00", path],
            cancellationToken: TestContext.Current.CancellationToken);

        MetadataWriteResult written = await new MetadataWriter().WriteAsync(
            this.Session,
            new MetadataWriteRequest(
                path,
                MediaKind.Jpeg,
                [TagWritePlanner.PlanRestore("ExifIFD:DateTimeOriginal", wasPresent: true, original)]),
            keepBackup: false,
            TestContext.Current.CancellationToken);

        written.Succeeded.ShouldBeTrue(written.Detail);

        (await this.ReadAsync(path)).Values[DateField.ExifDateTimeOriginal].Raw.ShouldBe(original);
    }
}
