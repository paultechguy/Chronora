// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace PaulTechGuy.CN.Metadata;

/// <summary>
/// A Win32 job object that every ExifTool child is put into, so none of them can outlive
/// Chronora.
///
/// <see cref="ExifToolSession.DisposeAsync" /> already asks a session to stop and kills it
/// if it will not, and that covers every ORDERLY exit. It covers nothing else. A
/// <c>-stay_open</c> child has no parent to notice: it blocks on stdin for ever, so a crash,
/// a Stop-Process, or stopping the debugger leaks one that nothing will ever clean up.
///
/// Measured, on a developer machine with Chronora not running: ten live exiftool.exe
/// processes, one per session across two days, every one of them holding a lock on the
/// ExifTool folder under LOCALAPPDATA. That is not untidiness. It makes Repair fail with
/// "the unpacked files could not be moved into place", and it would make an uninstall
/// unrecoverable - the uninstaller removes the Add/Remove Programs entry and the program
/// folder BEFORE it deletes the data folder, then throws on the locked DLL, leaving no app,
/// no uninstaller and no way back.
///
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE ties the children to THIS process's lifetime instead.
/// When the last handle to the job closes, Windows kills everything still in it - and the
/// kernel closes that handle for us however the process dies, including ways no finally
/// block would survive. The handle is therefore deliberately never closed here: closing it
/// is exactly the event that does the killing.
/// </summary>
internal static class ChildProcessJob
{
    /// <summary>JobObjectExtendedLimitInformation.</summary>
    private const int ExtendedLimitInformation = 9;

    /// <summary>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.</summary>
    private const uint KillOnJobClose = 0x2000;

    /// <summary>
    /// Created once, held for the life of the process. Zero means the job could not be
    /// created, in which case adoption quietly does nothing: the orderly shutdown path is
    /// unaffected and is still the one that does the work almost every time.
    /// </summary>
    private static readonly nint Job = Create();

    /// <summary>
    /// Puts a freshly started child into the job. Failure is logged and swallowed - a child
    /// that could not be adopted still shuts down normally through DisposeAsync, and
    /// refusing to start ExifTool over it would trade a rare leak for a certain outage.
    /// </summary>
    public static void Adopt(Process process, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(logger);

        if (Job == 0)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(Job, process.Handle))
            {
                logger.LogDebug(
                    "ExifTool {Pid} could not be put into the job object (win32 {Error}).",
                    process.Id,
                    Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NotSupportedException)
        {
            // The process exited between Start and here, which is not a problem worth
            // raising: there is nothing left to leak.
            logger.LogDebug(ex, "ExifTool exited before it could be put into the job object.");
        }
    }

    private static nint Create()
    {
        nint job = CreateJobObject(0, null);

        if (job == 0)
        {
            return 0;
        }

        var limits = default(JobObjectExtendedLimitInformation);
        limits.BasicLimitInformation.LimitFlags = KillOnJobClose;

        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            _ = CloseHandle(job);

            return 0;
        }

        return job;
    }

    // DllImport rather than LibraryImport, and deliberately. The LibraryImport generator
    // emits unsafe code, and AllowUnsafeBlocks is confined to the FileSystem project by a
    // decision written down in its csproj. The App project already reaches Win32 this way
    // for the same reason, so this follows the house precedent instead of quietly widening
    // an unsafe switch into a second layer.
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint job,
        int infoClass,
        ref JobObjectExtendedLimitInformation info,
        int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

// The kernel reads the whole structure, so every field has to be here and laid out exactly,
// even though only LimitFlags is ever set. They are written by the kernel rather than by us,
// which is what CS0649 is complaining about.
#pragma warning disable CS0649

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectBasicLimitInformation
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectIoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JobObjectExtendedLimitInformation
{
    public JobObjectBasicLimitInformation BasicLimitInformation;
    public JobObjectIoCounters IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

#pragma warning restore CS0649
