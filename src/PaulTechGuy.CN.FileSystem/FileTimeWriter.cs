// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.FileSystem.Native;

namespace PaulTechGuy.CN.FileSystem;

/// <summary>What a write attempt did.</summary>
/// <param name="Succeeded">Whether the call reported success AND, when probed, actually took.</param>
/// <param name="Problem">Why not, when it did not.</param>
/// <param name="Detail">The Win32 message, for the log rather than the user.</param>
public readonly record struct WriteResult(bool Succeeded, ProblemCode Problem, string? Detail)
{
    public static WriteResult Ok => new(true, ProblemCode.None, null);
}

/// <summary>
/// Writes the four filesystem timestamps and the attributes.
///
/// One SetFileInformationByHandle call does all of it atomically, which is why there is no
/// "ChangeTime must be written last" rule here: with a single call there is no sequence to
/// get wrong. ChangeTime is simply set to what was asked for.
/// </summary>
public sealed class FileTimeWriter(ILogger<FileTimeWriter>? logger = null)
{
    private static readonly uint BasicInfoSize = (uint)Marshal.SizeOf<FileBasicInfo>();

    private readonly ILogger<FileTimeWriter> _logger = logger ?? NullLogger<FileTimeWriter>.Instance;

    /// <summary>
    /// Applies a set of timestamps and, optionally, attributes.
    ///
    /// Any time left null is passed as zero, which the API reads as "leave this one alone",
    /// so a rule that touches only Modified does not disturb the other three.
    /// </summary>
    /// <param name="path">Display path; the extended form is applied here.</param>
    /// <param name="times">The values to set. Nulls are left alone.</param>
    /// <param name="isDirectory">Directories need backup semantics to open at all.</param>
    /// <param name="attributes">New attributes, or null to leave them.</param>
    /// <param name="verify">
    /// Read the values back and confirm they took. A success return does not prove anything
    /// on a volume that silently ignores ChangeTime, and reporting 50,000 successes that
    /// changed nothing is worse than failing.
    /// </param>
    public WriteResult Write(
        string path,
        TimestampSet times,
        bool isDirectory,
        System.IO.FileAttributes? attributes = null,
        bool verify = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Read-only blocks the write, so it is cleared and put back. Blocking the operation
        // instead would refuse work the app can plainly do.
        System.IO.FileAttributes? restore = null;
        try
        {
            System.IO.FileAttributes current = System.IO.File.GetAttributes(path);
            if (current.HasFlag(System.IO.FileAttributes.ReadOnly) && attributes is null)
            {
                restore = current;
                System.IO.File.SetAttributes(path, current & ~System.IO.FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
        {
            return new WriteResult(false, ProblemCode.AccessDenied, ex.Message);
        }

        try
        {
            using SafeFileHandle handle = Open(path, NativeMethods.FileWriteAttributes | NativeMethods.FileReadAttributes, isDirectory);
            if (handle.IsInvalid)
            {
                return FromLastError();
            }

            var info = new FileBasicInfo
            {
                CreationTime = ToFileTime(times.Created),
                LastAccessTime = ToFileTime(times.Accessed),
                LastWriteTime = ToFileTime(times.Modified),
                ChangeTime = ToFileTime(times.Changed),

                // Zero means "change no attributes", which is what we want unless asked.
                FileAttributes = attributes is { } a ? (uint)a : 0,
            };

            if (!NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileBasicInfoClass, ref info, BasicInfoSize))
            {
                return FromLastError();
            }

            if (verify && times.Changed is { } wanted)
            {
                if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileBasicInfoClass, out FileBasicInfo read, BasicInfoSize))
                {
                    return FromLastError();
                }

                // A second of slack: FAT-family volumes round, and a mismatch of a few ticks
                // is storage granularity rather than the value being ignored outright.
                DateTimeOffset actual = DateTimeOffset.FromFileTime(read.ChangeTime);
                if ((actual - wanted).Duration() > TimeSpan.FromSeconds(2))
                {
                    this._logger.LogWarning(

                        "ChangeTime did not take on {Path}: read back {Actual}, expected {Wanted}. The volume probably does not support it.",

                        path, actual, wanted);


                    return new WriteResult(false, ProblemCode.ChangeTimeUnsupported,

                        $"ChangeTime read back as {actual:O}, expected {wanted:O}.");
                }
            }

            return WriteResult.Ok;
        }
        finally
        {
            if (restore is { } original)
            {
                try
                {
                    System.IO.File.SetAttributes(path, original);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException)
                {
                    // The write itself succeeded, so this stays a log line rather than turning a

                    // good result into a bad one.

                    this._logger.LogWarning(ex, "Could not restore the read-only flag on {Path}.", path);
                }
            }
        }
    }

    /// <summary>Reads all four timestamps and the attributes in one call.</summary>
    public bool TryRead(string path, bool isDirectory, out TimestampSet times, out System.IO.FileAttributes attributes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        times = default;
        attributes = default;

        using SafeFileHandle handle = Open(path, NativeMethods.FileReadAttributes, isDirectory);
        if (handle.IsInvalid)
        {
            // Logged rather than thrown: one unreadable file in a 50,000-file scan should
            // cost that file and not the scan. Without the line there is nothing to
            // diagnose "why is this file missing from the list" with.
            this._logger.LogDebug(
                "Could not open {Path} to read its timestamps: {Error}",
                path,
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);

            return false;
        }

        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileBasicInfoClass, out FileBasicInfo info, BasicInfoSize))
        {
            this._logger.LogDebug(
                "Opened {Path} but could not read its basic information: {Error}",
                path,
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);

            return false;
        }

        times = new TimestampSet(
            FromFileTime(info.CreationTime),
            FromFileTime(info.LastWriteTime),
            FromFileTime(info.LastAccessTime),
            FromFileTime(info.ChangeTime));

        attributes = (System.IO.FileAttributes)info.FileAttributes;
        return true;
    }

    internal static SafeFileHandle Open(string path, uint access, bool isDirectory)
    {
        uint flags = NativeMethods.FileFlagOpenReparsePoint | NativeMethods.FileFlagOpenNoRecall;
        if (isDirectory)
        {
            flags |= NativeMethods.FileFlagBackupSemantics;
        }

        return NativeMethods.CreateFile(
            LongPath.ToExtended(path),
            access,
            NativeMethods.FileShareAll,
            nint.Zero,
            NativeMethods.OpenExisting,
            flags,
            nint.Zero);
    }

    private static long ToFileTime(DateTimeOffset? value) => value?.ToFileTime() ?? 0;

    private static DateTimeOffset? FromFileTime(long value) =>
        value > 0 ? DateTimeOffset.FromFileTime(value) : null;

    private static WriteResult FromLastError()
    {
        int error = Marshal.GetLastWin32Error();
        var ex = new System.ComponentModel.Win32Exception(error);

        ProblemCode code = error switch
        {
            5 => ProblemCode.AccessDenied,            // ERROR_ACCESS_DENIED
            32 or 33 => ProblemCode.FileLocked,       // SHARING_VIOLATION, LOCK_VIOLATION
            _ => ProblemCode.AccessDenied,
        };

        return new WriteResult(false, code, ex.Message);
    }
}
