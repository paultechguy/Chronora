// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// One ExifTool process for the whole test class.
///
/// It was one per test to begin with, which was a mistake worth recording: ExifTool takes
/// about a second to boot its Perl runtime and up to five more to shut down politely, so
/// five tests cost half a minute of waiting for a suite whose unit tests finish in 250 ms.
/// Sharing the process is also what the app itself does, and for the same reason.
/// </summary>
public sealed class ExifToolFixture : IAsyncDisposable
{
    public ExifToolFixture() =>
        this.Session = RealExifTool.IsAvailable ? ExifToolSession.Start(RealExifTool.ExecutablePath!) : null;

    /// <summary>Null when nobody has fetched a local copy; the tests then skip.</summary>
    public ExifToolSession? Session { get; }

    public async ValueTask DisposeAsync()
    {
        if (this.Session is not null)
        {
            await this.Session.DisposeAsync();
        }
    }
}

/// <summary>
/// Finds the developer copy of ExifTool, and builds the sample files to run against.
///
/// The copy lives in tools/exiftool/, which is gitignored and never reaches the build
/// output: this repository is Apache-2.0 and ExifTool is GPL/Artistic, so it is fetched
/// rather than committed. Run build/Get-ExifTool.ps1 to put it there.
/// </summary>
public static class RealExifTool
{
    /// <summary>
    /// The smallest thing that is genuinely a JPEG: one pixel, and a real JFIF header, so
    /// ExifTool will write EXIF into it rather than refusing.
    ///
    /// Embedded rather than committed as a file because a binary in the tree is one more
    /// thing to explain, and because a test that builds its own input cannot drift from
    /// what it claims to be testing.
    /// </summary>
    private const string OnePixelJpegBase64 =
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIx"
        + "wcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMj"
        + "IyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAA"
        + "AAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0f"
        + "AkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJip"
        + "KTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8"
        + "QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQ"
        + "dhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2"
        + "hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4u"
        + "Pk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD3+iiigD//2Q==";

    /// <summary>The developer copy, or null when nobody has fetched one.</summary>
    public static string? ExecutablePath { get; } = Find();

    public static bool IsAvailable => ExecutablePath is not null;

    public const string HowToGetIt =
        "No local ExifTool. Run: pwsh .\\build\\Get-ExifTool.ps1";

    /// <summary>Writes the one-pixel JPEG to a path and returns it.</summary>
    public static string WriteJpeg(string path)
    {
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelJpegBase64));
        return path;
    }

    /// <summary>
    /// Walks up from the test assembly looking for tools/exiftool/exiftool.exe. Walking
    /// rather than a fixed relative path because the output folder's depth depends on the
    /// target framework and the runtime identifier.
    /// </summary>
    private static string? Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tools", "exiftool", "exiftool.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
