// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// The unpacking, against synthetic archives shaped like the real distribution.
///
/// These run without network on purpose: unpacking is where the quiet failures live, and a
/// test that needs the internet is a test that does not run.
/// </summary>
public class ExifToolInstallerTests
{
    private static readonly ExifToolManifest Manifest =
        new("13.10", "https://exiftool.org/exiftool.zip", new string('a', 64), 12_000_000);

    /// <summary>Builds a zip with the given entries, each holding trivial content.</summary>
    private static string MakeArchive(string directory, params string[] entries)
    {
        string path = Path.Combine(directory, "download.zip");

        using (FileStream stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (string entry in entries)
            {
                ZipArchiveEntry created = zip.CreateEntry(entry);

                if (!entry.EndsWith('/'))
                {
                    using StreamWriter writer = new(created.Open());
                    writer.Write("x");
                }
            }
        }

        return path;
    }

    private static InstallResult Unpack(TempDir temp, params string[] entries)
    {
        string archive = MakeArchive(temp.Path, entries);
        string target = Path.Combine(temp.Path, "installed");

        return ExifToolInstaller.Unpack(archive, target, Manifest, NullLogger.Instance);
    }

    /// <summary>
    /// The real distribution ships "exiftool(-k).exe", where -k means "pause before
    /// exiting". Left under that name it waits for a keypress that never comes, and under
    /// the stay-open protocol that is a process which starts and never answers - which
    /// looks exactly like antivirus having eaten it.
    /// </summary>
    [Fact]
    public void The_k_suffixed_executable_is_renamed()
    {
        using var temp = new TempDir();

        InstallResult result = Unpack(temp, "exiftool(-k).exe", "exiftool_files/perl.dll");

        result.Succeeded.ShouldBeTrue(result.Detail);
        result.Install!.ExecutablePath.ShouldEndWith("exiftool.exe");
        File.Exists(result.Install.ExecutablePath).ShouldBeTrue();
    }

    [Fact]
    public void An_already_correctly_named_executable_is_left_alone()
    {
        using var temp = new TempDir();

        InstallResult result = Unpack(temp, "exiftool.exe", "exiftool_files/perl.dll");

        result.Succeeded.ShouldBeTrue(result.Detail);
        File.Exists(result.Install!.ExecutablePath).ShouldBeTrue();
    }

    /// <summary>
    /// Distributions have shipped both flat and inside a single wrapper folder, so the
    /// executable is found rather than assumed to be at the root.
    /// </summary>
    [Fact]
    public void An_archive_with_a_wrapper_folder_is_handled()
    {
        using var temp = new TempDir();

        InstallResult result = Unpack(temp, "exiftool-13.10_64/exiftool(-k).exe", "exiftool-13.10_64/exiftool_files/perl.dll");

        result.Succeeded.ShouldBeTrue(result.Detail);
        File.Exists(result.Install!.ExecutablePath).ShouldBeTrue();
        Directory.Exists(Path.Combine(Path.GetDirectoryName(result.Install.ExecutablePath)!, "exiftool_files")).ShouldBeTrue();
    }

    /// <summary>
    /// The exe is useless without exiftool_files beside it: that folder carries the whole
    /// Perl runtime, and without it the program starts and exits immediately. Catching it
    /// here beats letting the user meet it as "ExifTool would not start".
    /// </summary>
    [Fact]
    public void An_archive_missing_the_runtime_folder_is_rejected()
    {
        using var temp = new TempDir();

        InstallResult result = Unpack(temp, "exiftool.exe");

        result.Succeeded.ShouldBeFalse();
        result.Detail.ShouldContain("exiftool_files");
    }

    [Fact]
    public void An_archive_with_no_executable_is_rejected()
    {
        using var temp = new TempDir();

        InstallResult result = Unpack(temp, "readme.txt", "exiftool_files/perl.dll");

        result.Succeeded.ShouldBeFalse();
        result.Detail.ShouldContain("executable");
    }

    /// <summary>A successful install records everything the consent needs to be meaningful.</summary>
    [Fact]
    public void A_successful_install_records_the_consent_details()
    {
        using var temp = new TempDir();

        InstallResult result = Unpack(temp, "exiftool(-k).exe", "exiftool_files/perl.dll");

        ExifToolInstall install = result.Install!;
        install.Ownership.ShouldBe(InstallOwnership.Managed);
        install.Version.ShouldBe("13.10");
        install.SourceUrl.ShouldBe(Manifest.Url);
        install.ConsentedUtc.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));

        // The hash recorded is the EXECUTABLE's, not the archive's: the executable is what
        // later goes missing or gets altered, and what the validator re-checks.
        install.Sha256.ShouldNotBeNullOrWhiteSpace();
        install.ShouldVerifyHash.ShouldBeTrue();
        install.IsRepairable.ShouldBeTrue();
    }

    /// <summary>A failed unpack must not leave a half-installed folder where a good one was.</summary>
    [Fact]
    public void A_failed_unpack_leaves_no_staging_folder_behind()
    {
        using var temp = new TempDir();
        string target = Path.Combine(temp.Path, "installed");

        InstallResult result = ExifToolInstaller.Unpack(
            MakeArchive(temp.Path, "exiftool.exe"), target, Manifest, NullLogger.Instance);

        result.Succeeded.ShouldBeFalse();
        Directory.Exists(target + ".new").ShouldBeFalse();
    }
}

public class ExifToolManifestTests
{
    private static ExifToolManifest Make(string? version = "13.10", string? url = "https://exiftool.org/x.zip", string? sha = null) =>
        new(version!, url!, sha ?? new string('a', 64), 12_000_000);

    [Fact]
    public void A_complete_manifest_is_usable() =>
        ExifToolManifestSource.IsUsable(Make()).ShouldBeTrue();

    /// <summary>
    /// A manifest missing any of this is worse than none: it would let the app download
    /// something it cannot verify.
    /// </summary>
    [Theory]
    [InlineData("", "https://exiftool.org/x.zip", null)]
    [InlineData("13.10", "", null)]
    [InlineData("13.10", "http://exiftool.org/x.zip", null)]
    [InlineData("13.10", "https://exiftool.org/x.zip", "tooshort")]
    [InlineData("13.10", "https://exiftool.org/x.zip", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void An_incomplete_or_insecure_manifest_is_rejected(string version, string url, string? sha) =>
        ExifToolManifestSource.IsUsable(Make(version, url, sha)).ShouldBeFalse();

    /// <summary>Plain http is refused: the hash is only as trustworthy as the channel naming it.</summary>
    [Fact]
    public void Plain_http_is_refused() =>
        ExifToolManifestSource.IsUsable(Make(url: "http://exiftool.org/x.zip")).ShouldBeFalse();

    [Fact]
    public void The_size_is_stated_in_words_a_person_reads() =>
        Make().SizeText.ShouldContain("MB");

    [Fact]
    public void An_unknown_size_says_so_rather_than_showing_zero() =>
        new ExifToolManifest("13.10", "https://x/y.zip", new string('a', 64), 0).SizeText.ShouldBe("unknown size");
}
