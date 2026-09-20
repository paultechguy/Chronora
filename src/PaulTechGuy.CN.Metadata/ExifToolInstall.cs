// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Metadata;

/// <summary>Who is responsible for an ExifTool install, which decides how failures are handled.</summary>
public enum InstallOwnership
{
    /// <summary>
    /// Chronora downloaded it into its own folder. Nothing outside the app can upgrade,
    /// move or remove it, so a failure here is Chronora's to repair.
    /// </summary>
    Managed,

    /// <summary>
    /// Already on the machine: winget, Chocolatey, or somewhere the user pointed at. The
    /// user owns its lifecycle, so an upgrade or an uninstall is their decision and
    /// Chronora reports rather than silently replacing it.
    /// </summary>
    External,
}

/// <summary>Why the engine is not usable.</summary>
public enum EngineFault
{
    None,

    /// <summary>Never set up. The first-use consent pane belongs here.</summary>
    NotConfigured,

    /// <summary>The recorded path no longer exists.</summary>
    Missing,

    /// <summary>
    /// A managed copy whose bytes no longer match what was approved. Usually antivirus or
    /// a partial write rather than anything sinister, but either way it is not what the
    /// user consented to.
    /// </summary>
    HashMismatch,

    /// <summary>
    /// The process launched and then died, or never answered. Overwhelmingly this is
    /// antivirus: the Windows build unpacks a Perl tree and runs as a long-lived child
    /// process, which is a textbook heuristic trigger.
    /// </summary>
    WillNotStart,

    /// <summary>
    /// Older than the floor. Reported rather than tolerated, because an old build does not
    /// fail - it silently does less, and the preview would report success.
    /// </summary>
    TooOld,
}

/// <summary>
/// What Chronora knows about the ExifTool it is configured to use.
///
/// Recorded in settings and revalidated on every connect. The record is a starting point,
/// never a guarantee: a machine-wide copy can be upgraded or uninstalled between sessions.
/// </summary>
/// <param name="ExecutablePath">Where it was last seen.</param>
/// <param name="Ownership">Managed or external, which decides how a failure is recovered.</param>
/// <param name="Version">The version last observed, from -ver.</param>
/// <param name="Sha256">
/// The hash approved at install time. Only meaningful for a managed copy: an external one
/// legitimately changes whenever the user upgrades it, so checking it would fail constantly
/// and teach people to ignore the warning.
/// </param>
/// <param name="SourceUrl">Where a managed copy came from, so Repair can fetch it again.</param>
/// <param name="ConsentedUtc">When the user agreed, and to what.</param>
public sealed record ExifToolInstall(
    string ExecutablePath,
    InstallOwnership Ownership,
    string? Version,
    string? Sha256,
    string? SourceUrl,
    DateTimeOffset ConsentedUtc)
{
    /// <summary>A hash is only checked for a copy Chronora itself downloaded.</summary>
    public bool ShouldVerifyHash => this.Ownership == InstallOwnership.Managed && this.Sha256 is not null;

    /// <summary>
    /// Whether a missing binary is Chronora's to silently put back. Only ever true for a
    /// managed copy, and even then it is offered as a one-click Repair rather than done
    /// unasked.
    /// </summary>
    public bool IsRepairable => this.Ownership == InstallOwnership.Managed && this.SourceUrl is not null;
}

/// <summary>The engine's current usability, recomputed on every connect.</summary>
/// <param name="Available">Whether metadata work can happen at all.</param>
/// <param name="Install">What is configured, if anything.</param>
/// <param name="Fault">Why not, when it is unavailable.</param>
/// <param name="Detail">A sentence for the user, already in plain language.</param>
/// <param name="WritableFormats">
/// From -listwf, so the preview can block a field for a format this build cannot write
/// rather than discovering it at apply time.
/// </param>
public sealed record EngineStatus(
    bool Available,
    ExifToolInstall? Install,
    EngineFault Fault,
    string Detail,
    IReadOnlySet<string> WritableFormats)
{
    public static EngineStatus NotConfigured { get; } = new(
        false,
        null,
        EngineFault.NotConfigured,
        "Chronora needs a free helper to read photo dates.",
        new HashSet<string>());

    /// <summary>
    /// Offering Repair only makes sense for a copy Chronora owns. For an external one the
    /// honest move is to say it has gone and let the user decide, because downloading a
    /// replacement would be installing software they did not ask for.
    /// </summary>
    public bool CanRepair =>
        this.Install is { } install
        && install.IsRepairable
        && this.Fault is EngineFault.Missing or EngineFault.HashMismatch;
}
