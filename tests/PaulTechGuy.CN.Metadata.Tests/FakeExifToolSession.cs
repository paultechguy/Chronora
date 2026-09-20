// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Metadata;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// An ExifTool that answers with whatever the test says, and records what it was asked.
///
/// The arguments matter as much as the answers here: -overwrite_original_in_place instead
/// of -overwrite_original is the difference between preserving a file's Created time and
/// silently replacing it, and nothing about the result distinguishes the two.
/// </summary>
internal sealed class FakeExifToolSession : IExifToolSession
{
    private readonly Queue<ExifToolResult> _answers = new();

    /// <summary>Every command, in order, exactly as it was handed over.</summary>
    public List<IReadOnlyList<string>> Commands { get; } = [];

    /// <summary>The most recent command's arguments.</summary>
    public IReadOnlyList<string> LastCommand => this.Commands[^1];

    public FakeExifToolSession Answers(string standardOutput, string standardError = "", int status = 0)
    {
        this._answers.Enqueue(new ExifToolResult(standardOutput, standardError, status));
        return this;
    }

    public Task<ExifToolResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        this.Commands.Add([.. arguments]);

        return Task.FromResult(this._answers.Count > 0
            ? this._answers.Dequeue()
            : new ExifToolResult(string.Empty, string.Empty, 0));
    }
}
