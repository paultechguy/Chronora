// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// That a session actually takes its ExifTool with it when it goes.
///
/// Reported from a live machine: ten exiftool.exe processes running with Chronora closed,
/// one per session across two days. The disposal code below was never the problem - it was
/// correct all along and simply never ran, because nothing disposed the host that owned the
/// gateway that owned the session.
///
/// So this pins the mechanism the fix now depends on. A -stay_open child blocks on stdin for
/// ever and has no parent to notice it is orphaned, so if this ever stops holding, the leak
/// comes back silently and only shows up as an uninstall that cannot complete.
///
/// These start their own session rather than sharing the class fixture, because the whole
/// point is to dispose it.
/// </summary>
public class SessionLifetimeTests
{
    [Fact]
    public async Task Disposing_a_session_ends_its_exiftool_process()
    {
        Assert.SkipUnless(RealExifTool.IsAvailable, RealExifTool.HowToGetIt);

        ExifToolSession session = ExifToolSession.Start(RealExifTool.ExecutablePath!);
        int pid = session.ProcessId;

        IsRunning(pid).ShouldBeTrue("the session should have started a process");

        await session.DisposeAsync();

        IsRunning(pid).ShouldBeFalse("disposing the session must take ExifTool with it");
    }

    /// <summary>
    /// Disposing twice is what happens when the gateway tidies up and the host disposes it
    /// afterwards, which is exactly the path the fix added. It must not throw.
    /// </summary>
    [Fact]
    public async Task Disposing_a_session_twice_is_harmless()
    {
        Assert.SkipUnless(RealExifTool.IsAvailable, RealExifTool.HowToGetIt);

        ExifToolSession session = ExifToolSession.Start(RealExifTool.ExecutablePath!);
        int pid = session.ProcessId;

        await session.DisposeAsync();
        await session.DisposeAsync();

        IsRunning(pid).ShouldBeFalse();
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws rather than returning null once the pid is gone.
            return false;
        }
    }
}
