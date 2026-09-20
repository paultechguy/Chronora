// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.FileSystem.Native;

namespace PaulTechGuy.CN.FileSystem;

/// <summary>What one volume can actually store.</summary>
/// <param name="FileSystemName">NTFS, ReFS, exFAT, FAT32, and so on.</param>
/// <param name="SupportsChangeTime">Established by writing and reading back, not by name.</param>
/// <param name="TimeGranularity">
/// How coarsely the volume stores a timestamp. FAT32 rounds to two seconds; applying this
/// before the preview renders is what stops the preview promising a value the disk cannot
/// hold.
/// </param>
public sealed record VolumeCapabilities(
    string FileSystemName,
    bool SupportsChangeTime,
    TimeSpan TimeGranularity)
{
    public static VolumeCapabilities Unknown { get; } = new("unknown", false, TimeSpan.FromSeconds(1));
}

/// <summary>
/// Works out, once per volume, what may be written there.
///
/// The name of the filesystem is a hint and nothing more. SetFileInformationByHandle returns
/// success on volumes that quietly discard ChangeTime, so the only honest answer comes from
/// writing a value and reading it back. Reporting 50,000 successes that changed nothing is
/// worse than reporting a failure.
/// </summary>
public sealed class VolumeProbe(ILogger<VolumeProbe>? logger = null)
{
    private readonly ConcurrentDictionary<string, VolumeCapabilities> _byRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<VolumeProbe> _logger = logger ?? NullLogger<VolumeProbe>.Instance;

    /// <summary>
    /// Capabilities for the volume a path lives on, computed once and cached.
    /// </summary>
    public VolumeCapabilities For(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;

        return this._byRoot.GetOrAdd(root, r => this.Probe(r, path));
    }

    /// <summary>
    /// Records a verified failure discovered during a real write, so that one proven
    /// rejection turns off the capability for the rest of the session rather than being
    /// rediscovered file by file.
    /// </summary>
    public void RecordChangeTimeUnsupported(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;

        _ = this._byRoot.AddOrUpdate(
            root,
            _ => VolumeCapabilities.Unknown,
            (_, existing) => existing with { SupportsChangeTime = false });

        this._logger.LogInformation("ChangeTime is not supported on {Root}; it will not be attempted again this session.", root);
    }

    private VolumeCapabilities Probe(string root, string samplePath)
    {
        string fileSystem = ReadFileSystemName(samplePath) ?? "unknown";

        TimeSpan granularity = fileSystem.ToUpperInvariant() switch
        {
            "FAT32" or "FAT" or "VFAT" => TimeSpan.FromSeconds(2),
            "EXFAT" => TimeSpan.FromMilliseconds(10),
            _ => TimeSpan.FromTicks(1),
        };

        // NTFS and ReFS both honour ChangeTime; the revision 1 plan wrongly lumped ReFS and
        // SMB in with the exceptions. The real exceptions are the FAT family and some
        // network and FUSE mounts, and even that is only a starting guess - the actual
        // answer comes from the read-back probe at write time.
        bool likely = fileSystem.ToUpperInvariant() is "NTFS" or "REFS";

        var capabilities = new VolumeCapabilities(fileSystem, likely, granularity);

        this._logger.LogInformation(
            "Volume {Root} is {FileSystem}; ChangeTime expected {Supported}, granularity {Granularity}.",
            root, fileSystem, likely, granularity);

        return capabilities;
    }

    private static unsafe string? ReadFileSystemName(string path)
    {
        string? directory = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        using SafeFileHandle handle = FileTimeWriter.Open(directory, NativeMethods.FileReadAttributes, isDirectory: true);
        if (handle.IsInvalid)
        {
            return null;
        }

        Span<char> volumeName = stackalloc char[261];
        Span<char> fileSystemName = stackalloc char[261];

        fixed (char* volumePtr = volumeName)
        fixed (char* fsPtr = fileSystemName)
        {
            if (!NativeMethods.GetVolumeInformationByHandle(
                    handle, volumePtr, volumeName.Length, out _, out _, out _, fsPtr, fileSystemName.Length))
            {
                return null;
            }

            return new string(fsPtr);
        }
    }
}
