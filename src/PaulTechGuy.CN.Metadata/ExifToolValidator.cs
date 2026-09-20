// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaulTechGuy.CN.Metadata;

/// <summary>
/// Decides, on every connect, whether the configured ExifTool is actually usable.
///
/// The recorded install is a starting point and never a guarantee. A machine-wide copy can
/// be upgraded by winget, uninstalled, or quarantined by antivirus between one session and
/// the next, so trusting the record would mean reporting success against a binary that is
/// no longer there.
/// </summary>
public sealed class ExifToolValidator(ILogger<ExifToolValidator>? logger = null)
{
    /// <summary>
    /// The floor, and it is hard rather than a warning.
    ///
    /// Below this the app does not fail, it silently does less: the offset tags are not
    /// written, an offset shift is a no-op, and the preview reports success anyway. 12.53
    /// is where OffsetTime handling is complete; the stay-open status sentinel needs 12.15
    /// and offset shifting needs 12.49, so this covers all three.
    /// </summary>
    public static readonly Version MinimumVersion = new(12, 53);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<ExifToolValidator> _logger = logger ?? NullLogger<ExifToolValidator>.Instance;

    /// <summary>
    /// Checks a configured install end to end: present, intact, runnable, recent enough.
    /// </summary>
    public async Task<EngineStatus> ValidateAsync(ExifToolInstall? install, CancellationToken cancellationToken = default)
    {
        if (install is null)
        {
            return EngineStatus.NotConfigured;
        }

        if (!File.Exists(install.ExecutablePath))
        {
            // The two cases read differently to the user and are recovered differently, so
            // the message names which one this is.
            string detail = install.Ownership == InstallOwnership.Managed
                ? "Chronora's copy of ExifTool is missing. It can be reinstalled."
                : $"ExifTool is no longer at {install.ExecutablePath}. It may have been uninstalled or moved.";

            this._logger.LogWarning("ExifTool is missing at {Path} ({Ownership}).", install.ExecutablePath, install.Ownership);

            return new EngineStatus(false, install, EngineFault.Missing, detail, new HashSet<string>());
        }

        // Only a copy Chronora downloaded is hash-checked. An external one legitimately
        // changes every time the user upgrades it, and a check that fails on every winget
        // upgrade would teach people to ignore the warning.
        if (install.ShouldVerifyHash)
        {
            string actual = await ComputeSha256Async(install.ExecutablePath, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actual, install.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                this._logger.LogWarning(
                    "ExifTool at {Path} does not match the approved hash. Expected {Expected}, found {Actual}.",
                    install.ExecutablePath, install.Sha256, actual);

                return new EngineStatus(
                    false,
                    install,
                    EngineFault.HashMismatch,
                    "Chronora's copy of ExifTool has changed since it was installed, most often because antivirus "
                    + "altered or removed part of it. It can be reinstalled.",
                    new HashSet<string>());
            }
        }

        (string? version, string? error) = await this.ProbeVersionAsync(install.ExecutablePath, cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            return new EngineStatus(
                false,
                install,
                EngineFault.WillNotStart,
                "ExifTool is installed but would not start. This is usually antivirus: the Windows build unpacks a "
                + $"Perl runtime and runs as a background process. {error}".TrimEnd(),
                new HashSet<string>());
        }

        if (!TryParseVersion(version, out Version parsed) || parsed < MinimumVersion)
        {
            return new EngineStatus(
                false,
                install,
                EngineFault.TooOld,
                $"ExifTool {version} is older than the {MinimumVersion} Chronora needs. Older builds do not write "
                + "time-zone information, and would appear to succeed while silently leaving it out.",
                new HashSet<string>());
        }

        IReadOnlySet<string> writable = await this.ProbeWritableFormatsAsync(install.ExecutablePath, cancellationToken).ConfigureAwait(false);

        this._logger.LogInformation(
            "ExifTool {Version} at {Path} ({Ownership}) is ready; {Count} writable formats.",
            version, install.ExecutablePath, install.Ownership, writable.Count);

        return new EngineStatus(
            true,
            install with { Version = version },
            EngineFault.None,
            $"ExifTool {version}",
            writable);
    }

    /// <summary>Runs -ver, which is also the cheapest possible "does this thing work at all".</summary>
    private async Task<(string? Version, string? Error)> ProbeVersionAsync(string exe, CancellationToken cancellationToken)
    {
        try
        {
            (int code, string stdout, string stderr) = await RunAsync(exe, ["-ver"], cancellationToken).ConfigureAwait(false);

            string trimmed = stdout.Trim();

            return code == 0 && trimmed.Length > 0
                ? (trimmed, null)
                : (null, string.IsNullOrWhiteSpace(stderr) ? $"It exited with code {code}." : stderr.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            this._logger.LogWarning(ex, "ExifTool at {Path} could not be started.", exe);
            return (null, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return (null, "It did not respond in time.");
        }
    }

    /// <summary>
    /// The runtime writability matrix. Consulting this beats a hard-coded extension list,
    /// because which formats a given build can write genuinely varies.
    /// </summary>
    private async Task<IReadOnlySet<string>> ProbeWritableFormatsAsync(string exe, CancellationToken cancellationToken)
    {
        try
        {
            (int code, string stdout, _) = await RunAsync(exe, ["-listwf"], cancellationToken).ConfigureAwait(false);

            if (code != 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // Output is a wrapped, space-separated list of extensions after a header line.
            IEnumerable<string> tokens = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.Contains(':', StringComparison.Ordinal))
                .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            this._logger.LogDebug(ex, "Could not read the writable-format list from {Path}.", exe);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string exe,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // UTF-8 without a BOM on every stream. Without it ExifTool falls back to the
            // ANSI code page and cannot see a file named with non-Latin characters.
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false),
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        _ = process.Start();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A process that starts and never answers is the antivirus signature. Killing
            // it and reporting beats hanging the app's startup.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw;
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// ExifTool reports versions as "13.10", and occasionally with a trailing letter on a
    /// development build.
    /// </summary>
    internal static bool TryParseVersion(string text, out Version version)
    {
        string cleaned = new([.. text.Trim().TakeWhile(c => char.IsDigit(c) || c == '.')]);

        if (Version.TryParse(cleaned, out Version? parsed))
        {
            version = parsed;
            return true;
        }

        // A bare "13" is a valid answer and Version.TryParse rejects it.
        if (int.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out int major))
        {
            version = new Version(major, 0);
            return true;
        }

        version = new Version(0, 0);
        return false;
    }
}
