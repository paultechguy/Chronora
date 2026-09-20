// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Metadata;

namespace PaulTechGuy.CN.Services;

/// <summary>
/// The little of ExifTool that the apply path needs to know about.
///
/// This exists so the apply ORDER can be tested. Metadata is written before the filesystem
/// timestamps because ExifTool rewrites the file and moves them, and anything the rewrite
/// disturbed that the user did not ask to change is put back - which is the difference
/// between "set the photo's Taken date" leaving the file's Modified date alone and
/// silently moving it to now.
///
/// None of that can be checked against a real ExifTool in a unit test, and all of it is
/// silent when it breaks. So the apply path depends on this rather than on the concrete
/// gateway, and the tests hand it a writer that records what it was asked for.
/// </summary>
public interface IMetadataWriteGateway
{
    /// <summary>Whether metadata work can happen at all right now.</summary>
    bool Available { get; }

    /// <summary>Writes one file's tags.</summary>
    Task<MetadataWriteResult> WriteAsync(
        MetadataWriteRequest request,
        bool keepBackup = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one file's date tags as they are right now.
    ///
    /// Undo needs this, and its absence was a real bug rather than an omission. The drift
    /// check asks "does this file still hold what the run wrote?", and it could only read
    /// filesystem timestamps - so a recorded photo date always came back as nothing, which
    /// the check read as "somebody changed it" and refused to undo. Every run that touched
    /// a photo or video date was permanently un-undoable.
    /// </summary>
    Task<FileMetadata?> ReadOneAsync(string path, CancellationToken cancellationToken = default);
}
