// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Presentation;

/// <summary>
/// What a candidate filename pattern would do to the files already loaded.
///
/// The count is the reassurance the chip builder is for: having clicked year, month and
/// day on one file, "matches 1,284 of 1,284" is what makes it safe to press on - and
/// "matches 1 of 1,284" is what stops somebody applying a pattern that only ever fitted
/// the file they built it from.
/// </summary>
/// <param name="Matched">How many loaded files yield a plausible date.</param>
/// <param name="Total">How many are loaded.</param>
/// <param name="Samples">Up to three worked examples, so the answer can be checked.</param>
public sealed record FilenamePatternPreview(int Matched, int Total, IReadOnlyList<string> Samples);
