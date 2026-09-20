// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PaulTechGuy.CN.FileSystem.Native;

/// <summary>
/// The basic information for a file, as SetFileInformationByHandle takes it.
///
/// All four NTFS timestamps live here, ChangeTime included, along with the attributes. That
/// is the whole reason this is the right API: one documented kernel32 call sets everything
/// atomically, so there is no ordering problem between the four times and no need for the
/// undocumented ntdll route.
///
/// A zero in any time field means "leave this one alone".
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileBasicInfo
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

internal static partial class NativeMethods
{
    /// <summary>FileBasicInfo, the FILE_INFO_BY_HANDLE_CLASS value this app uses.</summary>
    internal const int FileBasicInfoClass = 0;

    // Access rights. FILE_WRITE_ATTRIBUTES is a far weaker right than GENERIC_WRITE, and
    // asking only for it is what makes most "access denied" cases simply not happen - a
    // read-only file, or one another process has open, still yields to it.
    internal const uint FileReadAttributes = 0x0080;
    internal const uint FileWriteAttributes = 0x0100;

    internal const uint FileShareAll = 0x00000007;   // read | write | delete
    internal const uint OpenExisting = 3;

    /// <summary>Required to open a directory handle at all.</summary>
    internal const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>Stamp the link itself rather than what it points at.</summary>
    internal const uint FileFlagOpenReparsePoint = 0x00200000;

    /// <summary>
    /// Do not hydrate a cloud placeholder. This is what lets the filesystem half of the app
    /// work on OneDrive files without downloading a byte.
    /// </summary>
    internal const uint FileFlagOpenNoRecall = 0x00100000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int infoClass,
        ref FileBasicInfo info,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int infoClass,
        out FileBasicInfo info,
        uint bufferSize);

    /// <summary>
    /// Buffers are raw pointers rather than StringBuilder, which the source-generated
    /// marshaller does not support, and rather than ref char, which would force
    /// DisableRuntimeMarshalling on the whole assembly for one call.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetVolumeInformationByHandle(
        SafeFileHandle file,
        char* volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        char* fileSystemNameBuffer,
        int fileSystemNameSize);
}
