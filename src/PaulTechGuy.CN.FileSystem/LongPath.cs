// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.FileSystem;

/// <summary>
/// Long-path handling for the native boundary.
///
/// The manifest's longPathAware entry covers BCL calls, but it does nothing for a direct
/// CreateFileW, so every path crossing into P/Invoke goes through here first. Photo libraries
/// reach 300 characters routinely once a Takeout export sits under a long OneDrive root.
/// </summary>
public static class LongPath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const string UncPrefix = @"\\";

    /// <summary>
    /// Below the legacy limit there is no need for the prefix, and avoiding it keeps paths
    /// readable in logs and errors. The threshold is short of 260 to leave room for the
    /// trailing null the API accounts for.
    /// </summary>
    private const int Threshold = 248;

    /// <summary>
    /// The form to hand to a native call.
    ///
    /// Normalisation happens FIRST and deliberately: the extended prefix disables the
    /// normalisation the OS would otherwise apply, so "." and ".." segments and relative
    /// paths would be taken literally and the call would fail on a path that plainly exists.
    /// </summary>
    public static string ToExtended(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path;
        }

        string full = Path.GetFullPath(path);

        if (full.Length < Threshold)
        {
            return full;
        }

        return full.StartsWith(UncPrefix, StringComparison.Ordinal)
            ? string.Concat(ExtendedUncPrefix, full.AsSpan(UncPrefix.Length))
            : ExtendedPrefix + full;
    }

    /// <summary>
    /// Strips the prefix again for anything a person or the journal will see, so one file
    /// has exactly one identity everywhere.
    /// </summary>
    public static string ToDisplay(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
        {
            return string.Concat(UncPrefix, path.AsSpan(ExtendedUncPrefix.Length));
        }

        return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            ? path[ExtendedPrefix.Length..]
            : path;
    }

    /// <summary>True when the path is long enough to need the prefix, used to set a trait.</summary>
    public static bool IsLong(string path) =>
        !string.IsNullOrEmpty(path) && path.Length >= Threshold;
}
