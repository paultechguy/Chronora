// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Metadata;

/// <summary>
/// One running ExifTool, as everything above it sees it.
///
/// The reader and the writer are the two places where a mistake is silent - a tag that
/// comes back unparsed, an offset that is dropped, a stale sub-second left behind - so they
/// have to be testable against canned ExifTool output rather than only against a real
/// install. That is what this seam is for; there is exactly one real implementation.
/// </summary>
public interface IExifToolSession
{
    /// <summary>
    /// Runs one command and waits for its sentinel. Arguments go without the common ones
    /// and without -execute, which the implementation adds.
    /// </summary>
    Task<ExifToolResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
