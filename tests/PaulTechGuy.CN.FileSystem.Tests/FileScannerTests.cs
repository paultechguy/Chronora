// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.FileSystem.Tests;

public class FileScannerTests
{
    private static async Task<List<ScannedFile>> ScanAsync(string root, ScanFilter filter)
    {
        var scanner = new FileScanner();
        var results = new List<ScannedFile>();

        await foreach (ScannedFile file in scanner.ScanAsync(root, filter))
        {
            results.Add(file);
        }

        return results;
    }

    [Fact]
    public async Task A_scan_finds_the_files_in_a_folder()
    {
        using var temp = new TempFolder();
        _ = temp.CreateFile("a.jpg");
        _ = temp.CreateFile("b.png");

        List<ScannedFile> found = await ScanAsync(temp.Path, ScanFilter.Default);

        found.Count.ShouldBe(2);
        found.Select(f => f.FileName).OrderBy(n => n).ShouldBe(["a.jpg", "b.png"]);
    }

    /// <summary>
    /// Semicolon-separated wildcards, the form FileTouch used and people already expect.
    /// </summary>
    [Fact]
    public async Task Several_patterns_can_be_given_at_once()
    {
        using var temp = new TempFolder();
        _ = temp.CreateFile("a.jpg");
        _ = temp.CreateFile("b.png");
        _ = temp.CreateFile("c.txt");

        List<ScannedFile> found = await ScanAsync(temp.Path, new ScanFilter(["*.jpg", "*.png"]));

        found.Select(f => f.FileName).OrderBy(n => n).ShouldBe(["a.jpg", "b.png"]);
    }

    /// <summary>
    /// Two patterns can match the same file. The user asked for the file once, so a run
    /// must not plan two conflicting writes to it.
    /// </summary>
    [Fact]
    public async Task A_file_matching_two_patterns_appears_only_once()
    {
        using var temp = new TempFolder();
        _ = temp.CreateFile("photo.jpg");

        List<ScannedFile> found = await ScanAsync(temp.Path, new ScanFilter(["*.jpg", "photo.*", "*"]));

        found.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Recursion_can_be_turned_off()
    {
        using var temp = new TempFolder();
        _ = temp.CreateFile("top.jpg");
        _ = temp.CreateFile(Path.Combine("nested", "deep.jpg"));

        List<ScannedFile> shallow = await ScanAsync(temp.Path, ScanFilter.Default with { Recurse = false });
        List<ScannedFile> deep = await ScanAsync(temp.Path, ScanFilter.Default with { Recurse = true });

        shallow.Select(f => f.FileName).ShouldContain("top.jpg");
        shallow.Select(f => f.FileName).ShouldNotContain("deep.jpg");
        deep.Select(f => f.FileName).ShouldContain("deep.jpg");
    }

    /// <summary>
    /// The root folder itself is a separate request from its contents. FileTouch separated
    /// these correctly and it is easy to miss.
    /// </summary>
    [Fact]
    public async Task The_root_folder_is_included_only_when_asked_for()
    {
        using var temp = new TempFolder();
        _ = temp.CreateFile("a.jpg");

        List<ScannedFile> without = await ScanAsync(temp.Path, ScanFilter.Default);
        List<ScannedFile> with = await ScanAsync(temp.Path, ScanFilter.Default with { IncludeRootDirectory = true });

        without.Any(f => f.FullPath.Equals(temp.Path, StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        with.Any(f => f.FullPath.Equals(temp.Path, StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_scanned_file_carries_its_timestamps()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile("a.jpg");

        var stamp = new DateTimeOffset(2019, 4, 2, 11, 30, 15, TimeSpan.Zero);
        _ = new FileTimeWriter().Write(file, new TimestampSet(stamp, stamp, null, null), isDirectory: false);

        List<ScannedFile> found = await ScanAsync(temp.Path, ScanFilter.Default);

        ScannedFile scanned = found.Single();
        scanned.Current(DateField.FileCreated).ShouldBe(stamp);
        scanned.Current(DateField.FileModified).ShouldBe(stamp);

        // Metadata is a separate, much slower pass, so it is empty at this point rather
        // than absent-and-unknowable.
        scanned.Current(DateField.ExifDateTimeOriginal).ShouldBeNull();
    }

    [Fact]
    public async Task A_read_only_file_is_marked_as_such()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile("a.jpg");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        List<ScannedFile> found = await ScanAsync(temp.Path, ScanFilter.Default);

        found.Single().Traits.HasFlag(FileTraits.ReadOnly).ShouldBeTrue();
    }

    [Theory]
    [InlineData("a.jpg", MediaKind.Jpeg)]
    [InlineData("a.JPEG", MediaKind.Jpeg)]
    [InlineData("a.heic", MediaKind.Heic)]
    [InlineData("a.png", MediaKind.Png)]
    [InlineData("a.tif", MediaKind.Tiff)]
    [InlineData("a.dng", MediaKind.Dng)]
    [InlineData("a.NEF", MediaKind.RawProprietary)]
    [InlineData("a.cr3", MediaKind.RawProprietary)]
    [InlineData("a.mp4", MediaKind.Video)]
    [InlineData("a.MOV", MediaKind.Video)]
    [InlineData("a.txt", MediaKind.Other)]
    [InlineData("a", MediaKind.Other)]
    public void A_file_is_classified_by_its_extension(string name, MediaKind expected) =>
        FileScanner.KindOf(name).ShouldBe(expected);

    /// <summary>
    /// DNG is an open spec designed to be written in place; the proprietary formats default
    /// to an XMP sidecar instead. Keeping them distinct is what makes that safe.
    /// </summary>
    [Fact]
    public void Dng_is_not_lumped_in_with_proprietary_raw() =>
        FileScanner.KindOf("a.dng").ShouldNotBe(FileScanner.KindOf("a.cr3"));

    [Fact]
    public async Task Cancelling_a_scan_stops_it()
    {
        using var temp = new TempFolder();
        for (int i = 0; i < 20; i++)
        {
            _ = temp.CreateFile($"file{i}.jpg");
        }

        using var cts = new CancellationTokenSource();
        var scanner = new FileScanner();
        var seen = new List<ScannedFile>();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (ScannedFile file in scanner.ScanAsync(temp.Path, ScanFilter.Default, cts.Token))
            {
                seen.Add(file);
                if (seen.Count == 3)
                {
                    await cts.CancelAsync();
                }
            }
        });

        seen.Count.ShouldBeLessThan(20);
    }
}

public class VolumeProbeTests
{
    [Fact]
    public void The_temp_volume_reports_a_filesystem_name()
    {
        using var temp = new TempFolder();

        VolumeCapabilities caps = new VolumeProbe().For(temp.Path);

        caps.FileSystemName.ShouldNotBeNullOrWhiteSpace();
        caps.FileSystemName.ShouldNotBe("unknown");
    }

    /// <summary>
    /// ReFS and SMB DO honour ChangeTime; only the FAT family and some network and FUSE
    /// mounts do not. Revision 1 of the plan had this backwards.
    /// </summary>
    [Fact]
    public void Ntfs_and_refs_are_expected_to_support_change_time()
    {
        using var temp = new TempFolder();

        VolumeCapabilities caps = new VolumeProbe().For(temp.Path);

        if (caps.FileSystemName.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
            || caps.FileSystemName.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
        {
            caps.SupportsChangeTime.ShouldBeTrue();
        }
    }

    [Fact]
    public void A_proven_failure_turns_the_capability_off_for_the_session()
    {
        using var temp = new TempFolder();
        var probe = new VolumeProbe();

        _ = probe.For(temp.Path);
        probe.RecordChangeTimeUnsupported(temp.Path);

        probe.For(temp.Path).SupportsChangeTime.ShouldBeFalse();
    }

    [Fact]
    public void Capabilities_are_computed_once_per_volume()
    {
        using var temp = new TempFolder();
        var probe = new VolumeProbe();

        VolumeCapabilities first = probe.For(temp.Path);
        VolumeCapabilities second = probe.For(Path.Combine(temp.Path, "nested", "deeper"));

        second.ShouldBeSameAs(first);
    }
}
