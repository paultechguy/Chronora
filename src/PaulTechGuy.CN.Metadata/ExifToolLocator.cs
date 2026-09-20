// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaulTechGuy.CN.Metadata;

/// <summary>One place ExifTool might already be, and how it got there.</summary>
/// <param name="ExecutablePath">The exe.</param>
/// <param name="Origin">How it was found, for the consent pane to explain.</param>
public sealed record ExifToolCandidate(string ExecutablePath, string Origin);

/// <summary>
/// Finds an ExifTool that is already on the machine.
///
/// This runs BEFORE any consent pane appears. Someone who installed ExifTool through winget
/// last year should not be asked to download it again - silent success is the best possible
/// consent flow, and asking for something they already have reads as the app not paying
/// attention.
/// </summary>
public sealed class ExifToolLocator(ILogger<ExifToolLocator>? logger = null)
{
    private const string ExeName = "exiftool.exe";

    private readonly ILogger<ExifToolLocator> _logger = logger ?? NullLogger<ExifToolLocator>.Instance;

    /// <summary>
    /// Everywhere worth looking, in order of confidence. Returns every hit rather than the
    /// first, so the consent pane can say which one it means.
    /// </summary>
    public IReadOnlyList<ExifToolCandidate> FindAll()
    {
        var found = new List<ExifToolCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? path, string origin)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full) && seen.Add(full))
                {
                    found.Add(new ExifToolCandidate(full, origin));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                // A malformed PATH entry is common and is not worth failing the probe over.
                this._logger.LogDebug(ex, "Ignoring unusable candidate path {Path}.", path);
            }
        }

        foreach (string directory in PathDirectories())
        {
            Consider(Path.Combine(directory, ExeName), "on PATH");
        }

        // winget's shim directory, which is on PATH for most users but not all - a fresh
        // install does not take effect in already-running processes.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Consider(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", ExeName), "installed with winget");

        Consider(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "chocolatey", "bin", ExeName), "installed with Chocolatey");

        foreach (Environment.SpecialFolder programs in (Environment.SpecialFolder[])
                 [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86])
        {
            Consider(Path.Combine(Environment.GetFolderPath(programs), "ExifTool", ExeName), "in Program Files");
        }

        if (found.Count > 0)
        {
            this._logger.LogInformation(
                "Found {Count} existing ExifTool installation(s); the first is {Path} ({Origin}).",
                found.Count, found[0].ExecutablePath, found[0].Origin);
        }

        return found;
    }

    /// <summary>The best existing candidate, or null when there is none.</summary>
    public ExifToolCandidate? Find()
    {
        IReadOnlyList<ExifToolCandidate> all = this.FindAll();
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// Where a copy Chronora owns lives. Under LOCALAPPDATA rather than the install folder,
    /// so it survives an upgrade of the app itself along with the consent that approved it.
    /// </summary>
    public static string ManagedPath(string dataDirectory) =>
        Path.Combine(dataDirectory, "exiftool", ExeName);

    private static IEnumerable<string> PathDirectories()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return entry;
        }
    }
}
