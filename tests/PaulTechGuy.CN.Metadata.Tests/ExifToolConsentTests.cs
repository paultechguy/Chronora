// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// The consent record: what gets remembered, and what happens when it is damaged.
/// </summary>
public class ExifToolConsentTests
{
    private static ConsentRecord Sample(InstallOwnership ownership = InstallOwnership.Managed) =>
        new(
            @"C:\Users\x\AppData\Local\PaulTechGuy\Chronora\exiftool\exiftool.exe",
            ownership,
            "13.10",
            new string('a', 64),
            "https://exiftool.org/exiftool.zip",
            new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
            "downloaded by Chronora");

    [Fact]
    public void A_consent_round_trips()
    {
        using var temp = new TempDir();
        ConsentRecord written = Sample();

        ExifToolService.WriteConsent(temp.Path, written, NullLogger.Instance);
        ConsentRecord? read = ExifToolService.ReadConsent(temp.Path, NullLogger.Instance);

        read.ShouldNotBeNull();
        read.ExecutablePath.ShouldBe(written.ExecutablePath);
        read.Ownership.ShouldBe(InstallOwnership.Managed);
        read.Version.ShouldBe("13.10");
        read.SourceUrl.ShouldBe(written.SourceUrl);
        read.ConsentedUtc.ShouldBe(written.ConsentedUtc);
        read.Route.ShouldBe("downloaded by Chronora");
    }

    /// <summary>
    /// Ownership is the field everything else keys off, so it has to survive the round
    /// trip exactly: it decides whether the hash is checked and whether a missing copy
    /// may be silently replaced.
    /// </summary>
    [Theory]
    [InlineData(InstallOwnership.Managed)]
    [InlineData(InstallOwnership.External)]
    public void Ownership_survives_the_round_trip(InstallOwnership ownership)
    {
        using var temp = new TempDir();

        ExifToolService.WriteConsent(temp.Path, Sample(ownership), NullLogger.Instance);

        ExifToolService.ReadConsent(temp.Path, NullLogger.Instance)!.Ownership.ShouldBe(ownership);
    }

    [Fact]
    public void No_record_means_nothing_has_been_agreed_to()
    {
        using var temp = new TempDir();

        ExifToolService.ReadConsent(temp.Path, NullLogger.Instance).ShouldBeNull();
    }

    /// <summary>
    /// A damaged record is treated as no record. The worst outcome is being asked once
    /// more, which beats refusing to start over a file the user never sees.
    /// </summary>
    [Fact]
    public void A_damaged_record_is_treated_as_absent()
    {
        using var temp = new TempDir();
        File.WriteAllText(ExifToolService.ConsentPath(temp.Path), "{ this is not json");

        ExifToolService.ReadConsent(temp.Path, NullLogger.Instance).ShouldBeNull();
    }

    /// <summary>
    /// Written to a neighbour and swapped, so a crash mid-write cannot leave a truncated
    /// record that reads as "never consented" while the binary is sitting right there.
    /// </summary>
    [Fact]
    public void Writing_leaves_no_temporary_file_behind()
    {
        using var temp = new TempDir();

        ExifToolService.WriteConsent(temp.Path, Sample(), NullLogger.Instance);

        File.Exists(ExifToolService.ConsentPath(temp.Path) + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void Writing_twice_replaces_rather_than_appends()
    {
        using var temp = new TempDir();

        ExifToolService.WriteConsent(temp.Path, Sample(), NullLogger.Instance);
        ExifToolService.WriteConsent(temp.Path, Sample() with { Version = "13.11" }, NullLogger.Instance);

        ExifToolService.ReadConsent(temp.Path, NullLogger.Instance)!.Version.ShouldBe("13.11");
    }

    /// <summary>
    /// The record lives in the data directory, not beside the executable, so an upgrade of
    /// Chronora itself does not ask the user to agree all over again.
    /// </summary>
    [Fact]
    public void The_record_lives_with_the_user_data()
    {
        string path = ExifToolService.ConsentPath(@"C:\Users\x\AppData\Local\PaulTechGuy\Chronora");

        path.ShouldBe(@"C:\Users\x\AppData\Local\PaulTechGuy\Chronora\exiftool-consent.json");
    }
}
