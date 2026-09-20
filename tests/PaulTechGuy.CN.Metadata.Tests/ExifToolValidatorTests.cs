// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chronora-exiftool-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public string CreateFakeExe(string name = "exiftool.exe", string content = "not really an executable")
    {
        string full = System.IO.Path.Combine(this.Path, name);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.Path))
            {
                Directory.Delete(this.Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}

/// <summary>
/// The durability behaviour: what happens when an ExifTool that worked last week is
/// upgraded, uninstalled or quarantined between sessions.
/// </summary>
public class ExifToolValidatorTests
{
    private static readonly ExifToolValidator Validator = new();

    private static ExifToolInstall Install(
        string path,
        InstallOwnership ownership = InstallOwnership.Managed,
        string? sha = null,
        string? url = "https://exiftool.org/exiftool.zip") =>
        new(path, ownership, "13.10", sha, url, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Nothing_configured_is_reported_as_such_rather_than_as_an_error()
    {
        EngineStatus status = await Validator.ValidateAsync(null, TestContext.Current.CancellationToken);

        status.Available.ShouldBeFalse();
        status.Fault.ShouldBe(EngineFault.NotConfigured);
        status.CanRepair.ShouldBeFalse();
    }

    /// <summary>Uninstalled between sessions, when Chronora owned the copy.</summary>
    [Fact]
    public async Task A_missing_managed_copy_can_be_repaired()
    {
        using var temp = new TempDir();
        string path = Path.Combine(temp.Path, "exiftool.exe");

        EngineStatus status = await Validator.ValidateAsync(Install(path, InstallOwnership.Managed), TestContext.Current.CancellationToken);

        status.Available.ShouldBeFalse();
        status.Fault.ShouldBe(EngineFault.Missing);
        status.CanRepair.ShouldBeTrue("Chronora installed it, so Chronora can put it back");
        status.Detail.ShouldContain("reinstalled");
    }

    /// <summary>
    /// Uninstalled between sessions, when the USER owned the copy. Chronora must not
    /// quietly download a replacement: that would be installing software they did not ask
    /// for, and if antivirus removed it, a silent re-fetch starts a fight they cannot see.
    /// </summary>
    [Fact]
    public async Task A_missing_external_copy_is_reported_and_not_silently_replaced()
    {
        using var temp = new TempDir();
        string path = Path.Combine(temp.Path, "exiftool.exe");

        EngineStatus status = await Validator.ValidateAsync(Install(path, InstallOwnership.External, url: null), TestContext.Current.CancellationToken);

        status.Available.ShouldBeFalse();
        status.Fault.ShouldBe(EngineFault.Missing);
        status.CanRepair.ShouldBeFalse("the user owns this one, so it is their call");
        status.Detail.ShouldContain("uninstalled or moved");
        status.Detail.ShouldContain(path);
    }

    /// <summary>Antivirus altering a managed copy is caught by the hash.</summary>
    [Fact]
    public async Task A_managed_copy_whose_bytes_changed_is_caught()
    {
        using var temp = new TempDir();
        string path = temp.CreateFakeExe();

        EngineStatus status = await Validator.ValidateAsync(
            Install(path, InstallOwnership.Managed, sha: new string('A', 64)),
            TestContext.Current.CancellationToken);

        status.Available.ShouldBeFalse();
        status.Fault.ShouldBe(EngineFault.HashMismatch);
        status.CanRepair.ShouldBeTrue();
        status.Detail.ShouldContain("antivirus");
    }

    /// <summary>
    /// The upgrade case, and the reason an external copy is never hash-checked: a
    /// legitimate winget upgrade changes the bytes every time, and a check that fails
    /// constantly teaches people to ignore it.
    /// </summary>
    [Fact]
    public async Task An_external_copy_is_not_hash_checked_so_an_upgrade_does_not_break_it()
    {
        using var temp = new TempDir();
        string path = temp.CreateFakeExe();

        var install = Install(path, InstallOwnership.External, sha: new string('A', 64));

        install.ShouldVerifyHash.ShouldBeFalse();

        EngineStatus status = await Validator.ValidateAsync(install, TestContext.Current.CancellationToken);

        // It still fails - the fake exe cannot run - but NOT because of the hash.
        status.Fault.ShouldNotBe(EngineFault.HashMismatch);
    }

    /// <summary>
    /// A file that exists but is not a working ExifTool. The real-world cause is almost
    /// always antivirus, so the message says so rather than reporting a bare exit code.
    /// </summary>
    [Fact]
    public async Task Something_that_will_not_run_is_reported_with_the_likely_cause()
    {
        using var temp = new TempDir();
        string path = temp.CreateFakeExe();

        EngineStatus status = await Validator.ValidateAsync(Install(path, InstallOwnership.External, url: null), TestContext.Current.CancellationToken);

        status.Available.ShouldBeFalse();
        status.Fault.ShouldBe(EngineFault.WillNotStart);
        status.Detail.ShouldContain("antivirus");
    }

    [Fact]
    public void The_managed_path_lives_under_the_data_directory_so_it_survives_an_app_upgrade()
    {
        string path = ExifToolLocator.ManagedPath(@"C:\Users\x\AppData\Local\PaulTechGuy\Chronora");

        path.ShouldBe(@"C:\Users\x\AppData\Local\PaulTechGuy\Chronora\exiftool\exiftool.exe");
    }
}

public class ExifToolVersionTests
{
    [Theory]
    [InlineData("13.10", 13, 10)]
    [InlineData("12.53", 12, 53)]
    [InlineData("13.10\n", 13, 10)]
    [InlineData("  12.99  ", 12, 99)]
    [InlineData("13", 13, 0)]
    public void A_reported_version_is_parsed(string text, int major, int minor)
    {
        ExifToolValidator.TryParseVersion(text, out Version version).ShouldBeTrue();

        version.Major.ShouldBe(major);
        version.Minor.ShouldBe(minor);
    }

    [Fact]
    public void A_development_build_suffix_is_tolerated()
    {
        ExifToolValidator.TryParseVersion("13.11a", out Version version).ShouldBeTrue();

        version.Major.ShouldBe(13);
        version.Minor.ShouldBe(11);
    }

    [Fact]
    public void Nonsense_is_rejected_rather_than_guessed() =>
        ExifToolValidator.TryParseVersion("not a version", out _).ShouldBeFalse();

    /// <summary>
    /// The floor covers all three capabilities that have one: the stay-open status sentinel
    /// (12.15), shifting OffsetTime tags (12.49), and now/Z for OffsetTime (12.53).
    /// </summary>
    [Fact]
    public void The_minimum_version_covers_every_capability_that_has_a_floor()
    {
        ExifToolValidator.MinimumVersion.ShouldBeGreaterThanOrEqualTo(new Version(12, 53));
    }

    [Theory]
    [InlineData("12.15")]
    [InlineData("12.49")]
    [InlineData("11.99")]
    public void A_version_below_the_floor_is_below_the_floor(string text)
    {
        ExifToolValidator.TryParseVersion(text, out Version version).ShouldBeTrue();

        (version < ExifToolValidator.MinimumVersion).ShouldBeTrue();
    }
}

public class ExifToolLocatorTests
{
    /// <summary>
    /// The probe runs before any consent pane. Someone who installed ExifTool through
    /// winget last year should never be asked to download it again.
    /// </summary>
    [Fact]
    public void Probing_an_ordinary_machine_does_not_throw()
    {
        IReadOnlyList<ExifToolCandidate> found = new ExifToolLocator().FindAll();

        found.ShouldNotBeNull();

        foreach (ExifToolCandidate candidate in found)
        {
            File.Exists(candidate.ExecutablePath).ShouldBeTrue();
            candidate.Origin.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void The_same_executable_on_the_path_twice_is_reported_once()
    {
        IReadOnlyList<ExifToolCandidate> found = new ExifToolLocator().FindAll();

        found.Select(c => c.ExecutablePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(found.Count);
    }
}
