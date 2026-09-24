// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Rules.Tests;

/// <summary>
/// The one wildcard grammar. Each case here is a way a pattern once meant something other
/// than what was typed.
/// </summary>
public class NamePatternsTests
{
    [Theory]
    [InlineData("xmp", "*.xmp")]
    [InlineData(".xmp", "*.xmp")]
    [InlineData("*.xmp", "*.xmp")]

    // An exact name, not an extension. The old rule made this "*.Thumbs.db".
    [InlineData("Thumbs.db", "Thumbs.db")]

    // Under simple matching "*.*" needs a dot, and would drop every extensionless file.
    [InlineData("*.*", "*")]
    public void A_file_pattern_means_what_was_typed(string typed, string expected) =>
        NamePatterns.Parse(typed, files: true).ShouldBe([expected]);

    [Fact]
    public void Folder_patterns_are_never_treated_as_extensions() =>
        NamePatterns.Parse("@eaDir; .thumbnails", files: false).ShouldBe(["@eaDir", ".thumbnails"]);

    [Fact]
    public void Semicolons_and_newlines_both_separate_and_duplicates_go() =>
        NamePatterns.Parse(" *.aae ;\r\n*.AAE\n\n*.xmp ", files: true).ShouldBe(["*.aae", "*.xmp"]);

    /// <summary>
    /// A pane cleared to nothing and a pane saying "*" are the same setting. If they were
    /// not, clearing a pane would light "Read the folders again" over no change at all.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("*.*")]
    [InlineData("*.jpg; *")]
    public void An_include_that_lets_everything_through_is_everything(string typed) =>
        NamePatterns.ParseInclude(typed, files: true).ShouldBe(NamePatterns.Everything);

    [Fact]
    public void Comparison_ignores_order_and_case() =>
        NamePatterns.SameAs(["*.JPG", "@eaDir"], ["@eadir", "*.jpg"]).ShouldBeTrue();
}
