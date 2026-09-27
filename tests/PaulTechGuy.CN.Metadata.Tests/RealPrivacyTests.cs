// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Metadata;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// The strip against a real ExifTool: write personal details into every place they live,
/// remove them, and read back with plain ExifTool rather than with Chronora's own reader - a
/// reader that shares the catalog's blind spots would pass a strip that shares them too.
/// </summary>
public class RealPrivacyTests(ExifToolFixture fixture) : IClassFixture<ExifToolFixture>, IDisposable
{
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAEAAAAAwCAYAAAChS3wfAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAABwSURBVGhD7dAxAQAgEIDAD2slY34G3akAwy2MzLn7zIbBpgEMNg1gsGkAg00DGGwawGDTAAabBjDYNIDBpgEMNg1gsGkAg00DGGwawGDTAAabBjDYNIDBpgEMNg1gsGkAg00DGGwawGDTAAabBjDYfOV7vf9EzZkiAAAAAElFTkSuQmCC";

    /// <summary>Ships with every Windows install, so no profile needs committing.</summary>
    private static readonly string SrgbProfile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color", "sRGB Color Space Profile.icm");

    private readonly string _folder = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "chronora-privacy-integration", Guid.NewGuid().ToString("N"))).FullName;

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
            Directory.Delete(this._folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("image.png")]
    public async Task Every_detail_goes_and_the_date_orientation_colour_and_copyright_stay(string name)
    {
        string path = Path.Combine(this._folder, name);

        if (name.EndsWith(".png", StringComparison.Ordinal))
        {
            File.WriteAllBytes(path, Convert.FromBase64String(TinyPngBase64));
        }
        else
        {
            _ = RealExifTool.WriteJpeg(path);
        }

        string thumbnail = RealExifTool.WriteJpeg(Path.Combine(this._folder, "thumb.jpg"));
        bool hasProfile = File.Exists(SrgbProfile);

        List<string> seed =
        [
            "-overwrite_original_in_place",
            "-GPSLatitude=48.8584", "-GPSLatitudeRef=N", "-XMP-exif:GPSLatitude=48.8584 N",
            "-XMP-iptcCore:Location=Champ de Mars", "-XMP-photoshop:City=Paris", "-IPTC:City=Paris",
            "-SerialNumber=SN123", "-XMP-aux:SerialNumber=AUX789", "-OwnerName=Jane", "-Artist=Jane", "-XMP-dc:Creator=Jane",
            "-Software=Photoshop", "-HostComputer=JANES-PC", "-XMP-xmpMM:DocumentID=xmp.did:1",
            "-XMP-crs:Exposure2012=+0.5", "-Copyright=(c) Jane",
            "-ThumbnailImage<=" + thumbnail,
            "-DateTimeOriginal=2024:03:15 09:12:00", "-Orientation#=6",
        ];

        if (hasProfile)
        {
            seed.Add("-ICC_Profile<=" + SrgbProfile);
        }

        seed.Add(path);

        (await this.Session.ExecuteAsync(seed, cancellationToken: TestContext.Current.CancellationToken)).Succeeded.ShouldBeTrue();

        MetadataWriteResult result = await new MetadataWriter().StripAsync(
            this.Session, path, [.. PrivacyCategoryNames.All], TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue(result.Detail);

        // Plain ExifTool, every group, every duplicate.
        ExifToolResult after = await this.Session.ExecuteAsync(
            ["-j", "-a", "-u", path], cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<MetadataTag> tags = new MetadataReader().ParseAll(after.StandardOutput);
        string[] keys = [.. tags.Select(t => $"{t.Group}:{t.Name}")];

        keys.Where(k => PrivacyTagCatalog.CategoryOf(k) is not null).ShouldBeEmpty();

        keys.ShouldContain("ExifIFD:DateTimeOriginal");
        keys.ShouldContain("IFD0:Orientation");
        keys.ShouldContain(k => k.EndsWith(":Copyright", StringComparison.Ordinal));
        keys.ShouldContain("XMP-crs:Exposure2012", "Lightroom edit settings are kept");

        if (hasProfile)
        {
            keys.ShouldContain("ICC_Profile:ProfileDescription");
        }

        // And the reader Chronora will use to check its own work agrees.
        IReadOnlyList<FilePrivacy> read = await new MetadataReader().ReadPrivacyAsync(
            this.Session, [path], TestContext.Current.CancellationToken);

        read.Single().Findings.Found.ShouldBeEmpty();
    }
}
