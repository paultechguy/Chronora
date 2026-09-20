// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Metadata;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// The write path. Most of what is asserted here is about the arguments rather than the
/// outcome, because the dangerous mistakes all succeed.
/// </summary>
public class MetadataWriterTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "chronora-writer-tests", Guid.NewGuid().ToString("N"));

    private static readonly TagAssignment Taken =
        new("ExifIFD:DateTimeOriginal", "2024:03:15 14:25:30");

    public MetadataWriterTests() => Directory.CreateDirectory(this._folder);

    private string CreateFile(string name)
    {
        string path = Path.Combine(this._folder, name);
        File.WriteAllText(path, "pretend this is a photo");
        return path;
    }

    private static MetadataWriteRequest Request(string path, MediaKind kind = MediaKind.Jpeg) =>
        new(path, kind, [Taken]);

    /// <summary>
    /// The correction that started all of this. -overwrite_original renames a temp file
    /// over the original, which hands the file a brand new Created time - so the tool the
    /// user opened to set Created would silently reset it instead.
    /// </summary>
    [Fact]
    public async Task The_write_is_in_place_so_the_created_time_survives()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string path = this.CreateFile("a.jpg");

        _ = await new MetadataWriter().WriteAsync(
            session, Request(path), keepBackup: false, TestContext.Current.CancellationToken);

        session.LastCommand.ShouldContain("-overwrite_original_in_place");
        session.LastCommand.ShouldNotContain("-overwrite_original");
    }

    [Fact]
    public async Task The_tag_assignments_are_passed_through_verbatim()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string path = this.CreateFile("a.jpg");

        _ = await new MetadataWriter().WriteAsync(
            session, Request(path), keepBackup: false, TestContext.Current.CancellationToken);

        session.LastCommand.ShouldContain("-ExifIFD:DateTimeOriginal=2024:03:15 14:25:30");
        session.LastCommand[^1].ShouldBe(path);
    }

    /// <summary>
    /// ExifTool must never be allowed near the filesystem dates. It would write them, and
    /// they belong to the one call that sets all four atomically, afterwards.
    /// </summary>
    [Fact]
    public async Task Exiftool_is_never_asked_to_write_a_file_timestamp()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string path = this.CreateFile("a.jpg");

        _ = await new MetadataWriter().WriteAsync(
            session, Request(path), keepBackup: false, TestContext.Current.CancellationToken);

        session.LastCommand.ShouldNotContain(a => a.StartsWith("-FileModifyDate", StringComparison.Ordinal));
        session.LastCommand.ShouldNotContain(a => a.StartsWith("-FileCreateDate", StringComparison.Ordinal));
    }

    /// <summary>
    /// The journal restores field values and cannot repair a container a write corrupted,
    /// so a byte rewrite gets a copy first.
    /// </summary>
    [Fact]
    public async Task A_copy_is_taken_before_the_bytes_are_rewritten()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string path = this.CreateFile("a.jpg");

        MetadataWriteResult result = await new MetadataWriter().WriteAsync(
            session, Request(path), keepBackup: true, TestContext.Current.CancellationToken);

        result.BackupPath.ShouldBe(path + "_original");
        File.Exists(path + "_original").ShouldBeTrue();
        File.ReadAllText(path + "_original").ShouldBe("pretend this is a photo");
    }

    [Fact]
    public async Task No_copy_is_taken_when_the_caller_declines_one()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string path = this.CreateFile("a.jpg");

        MetadataWriteResult result = await new MetadataWriter().WriteAsync(
            session, Request(path), keepBackup: false, TestContext.Current.CancellationToken);

        result.BackupPath.ShouldBeNull();
        File.Exists(path + "_original").ShouldBeFalse();
    }

    /// <summary>
    /// If the backup cannot be made, the file is left alone. Writing anyway would mean
    /// taking the risk the backup exists to cover, at the moment it is known to be
    /// uncovered.
    /// </summary>
    [Fact]
    public async Task A_file_is_left_alone_when_its_backup_cannot_be_made()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string missing = Path.Combine(this._folder, "not-here.jpg");

        MetadataWriteResult result = await new MetadataWriter().WriteAsync(
            session, Request(missing), keepBackup: true, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.Detail.ShouldNotBeNull();
        session.Commands.ShouldBeEmpty("nothing should have been sent to ExifTool");
    }

    /// <summary>A failed write keeps the copy, because the copy is now the good version.</summary>
    [Fact]
    public async Task A_failed_write_keeps_its_backup()
    {
        var session = new FakeExifToolSession().Answers(string.Empty, "Error: Not a valid JPEG", status: 1);
        string path = this.CreateFile("a.jpg");

        MetadataWriteResult result = await new MetadataWriter().WriteAsync(
            session, Request(path), keepBackup: true, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.BackupPath.ShouldNotBeNull();
        File.Exists(result.BackupPath).ShouldBeTrue();
    }

    [Fact]
    public async Task Exiftools_complaint_is_reported_rather_than_a_status_number()
    {
        var session = new FakeExifToolSession().Answers(string.Empty, "Error: Not a valid JPEG (looks more like a PNG)", status: 1);
        string path = this.CreateFile("a.jpg");

        MetadataWriteResult result = await new MetadataWriter().WriteAsync(
            session, Request(path), keepBackup: false, TestContext.Current.CancellationToken);

        result.Detail!.ShouldContain("looks more like a PNG");
    }

    [Fact]
    public async Task A_failure_with_nothing_to_say_still_produces_a_sentence()
    {
        var session = new FakeExifToolSession().Answers(string.Empty, string.Empty, status: 1);
        string path = this.CreateFile("a.jpg");

        MetadataWriteResult result = await new MetadataWriter().WriteAsync(
            session, Request(path), keepBackup: false, TestContext.Current.CancellationToken);

        result.Detail.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_request_with_nothing_to_write_does_not_start_a_command()
    {
        var session = new FakeExifToolSession();
        string path = this.CreateFile("a.jpg");

        MetadataWriteResult result = await new MetadataWriter().WriteAsync(
            session, new MetadataWriteRequest(path, MediaKind.Jpeg, []), true, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        session.Commands.ShouldBeEmpty();
        File.Exists(path + "_original").ShouldBeFalse("nothing was going to be written");
    }

    /// <summary>
    /// Proprietary raw is a vendor format nobody outside the vendor fully understands, and
    /// the file is the negative. DNG is the deliberate exception: an open specification
    /// designed to be written to.
    /// </summary>
    [Theory]
    [InlineData(MediaKind.RawProprietary, WriteDestination.Sidecar)]
    [InlineData(MediaKind.Dng, WriteDestination.Embedded)]
    [InlineData(MediaKind.Jpeg, WriteDestination.Embedded)]
    [InlineData(MediaKind.Heic, WriteDestination.Embedded)]
    [InlineData(MediaKind.Video, WriteDestination.Embedded)]
    public void Only_proprietary_raw_goes_to_a_sidecar(MediaKind kind, WriteDestination expected)
    {
        MetadataWriter.DestinationFor(kind).ShouldBe(expected);
    }

    [Fact]
    public async Task A_raw_file_gets_a_sidecar_and_is_not_touched_itself()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string path = this.CreateFile("IMG_1234.CR2");

        MetadataWriteResult result = await new MetadataWriter().WriteAsync(
            session, Request(path, MediaKind.RawProprietary), true, TestContext.Current.CancellationToken);

        result.Destination.ShouldBe(WriteDestination.Sidecar);
        File.Exists(path + "_original").ShouldBeFalse("the raw file's bytes were never at risk");

        session.LastCommand.ShouldContain("-o");
        session.LastCommand.ShouldContain(Path.Combine(this._folder, "IMG_1234.xmp"));
    }

    /// <summary>An EXIF tag name in an .xmp is silently dropped, so it has to be translated.</summary>
    [Fact]
    public async Task A_sidecar_write_moves_the_tags_into_the_xmp_namespace()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string path = this.CreateFile("IMG_1234.CR2");

        _ = await new MetadataWriter().WriteAsync(
            session, Request(path, MediaKind.RawProprietary), true, TestContext.Current.CancellationToken);

        session.LastCommand.ShouldContain("-XMP-photoshop:DateCreated=2024:03:15 14:25:30");
        session.LastCommand.ShouldNotContain(a => a.StartsWith("-ExifIFD:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_existing_sidecar_is_updated_in_place_rather_than_recreated()
    {
        var session = new FakeExifToolSession().Answers(string.Empty);
        string path = this.CreateFile("IMG_1234.CR2");
        string sidecar = Path.Combine(this._folder, "IMG_1234.xmp");
        File.WriteAllText(sidecar, "<x:xmpmeta/>");

        _ = await new MetadataWriter().WriteAsync(
            session, Request(path, MediaKind.RawProprietary), true, TestContext.Current.CancellationToken);

        session.LastCommand.ShouldNotContain("-o");
        session.LastCommand.ShouldContain("-overwrite_original_in_place");
        session.LastCommand[^1].ShouldBe(sidecar);
    }

    /// <summary>
    /// RAW+JPEG is an ordinary camera mode, and both files map to one sidecar under the
    /// convention every reader expects. Left undetected, one photo's dates end up
    /// describing the other.
    /// </summary>
    [Fact]
    public void Two_files_that_would_share_one_sidecar_are_reported()
    {
        MetadataWriteRequest raw = new(@"C:\photos\IMG_1234.CR2", MediaKind.RawProprietary, [Taken]);
        MetadataWriteRequest other = new(@"C:\photos\IMG_1234.NEF", MediaKind.RawProprietary, [Taken]);

        IReadOnlyDictionary<string, IReadOnlyList<string>> clashes =
            MetadataWriter.FindSidecarCollisions([raw, other]);

        clashes.Count.ShouldBe(1);
        clashes.Single().Value.Count.ShouldBe(2);
    }

    /// <summary>
    /// A raw beside its own JPEG is the common case and is NOT a clash: only the raw uses
    /// a sidecar, the JPEG is written in place.
    /// </summary>
    [Fact]
    public void A_raw_beside_its_jpeg_is_not_a_collision()
    {
        MetadataWriteRequest raw = new(@"C:\photos\IMG_1234.CR2", MediaKind.RawProprietary, [Taken]);
        MetadataWriteRequest jpeg = new(@"C:\photos\IMG_1234.JPG", MediaKind.Jpeg, [Taken]);

        MetadataWriter.FindSidecarCollisions([raw, jpeg]).ShouldBeEmpty();
    }

    [Fact]
    public void Files_with_distinct_names_never_collide()
    {
        MetadataWriteRequest a = new(@"C:\photos\IMG_1234.CR2", MediaKind.RawProprietary, [Taken]);
        MetadataWriteRequest b = new(@"C:\photos\IMG_1235.CR2", MediaKind.RawProprietary, [Taken]);

        MetadataWriter.FindSidecarCollisions([a, b]).ShouldBeEmpty();
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
}
