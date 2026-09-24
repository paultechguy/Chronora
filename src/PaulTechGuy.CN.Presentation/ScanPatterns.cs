// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Presentation;

/// <summary>
/// The four name filters from "More filters…", as one value.
///
/// A record, but never compare two with ==: the members are lists, and records compare
/// lists by reference, so two identical filters built a second apart are "different".
/// That exact mistake once lit "Read the folders again" the instant the folders were read.
/// Use <see cref="SameAs" />.
/// </summary>
public sealed record ScanPatterns(
    IReadOnlyList<string> IncludeFiles,
    IReadOnlyList<string> ExcludeFiles,
    IReadOnlyList<string> IncludeFolders,
    IReadOnlyList<string> ExcludeFolders)
{
    /// <summary>Nothing left out: what a fresh install and Clear both mean.</summary>
    public static ScanPatterns None { get; } = new(NamePatterns.Everything, [], NamePatterns.Everything, []);

    /// <summary>
    /// What Suggest merges in. Short on purpose: things photo libraries actually carry
    /// that nobody wants dated. Thumbs.db and desktop.ini are not here because they are
    /// hidden or system files and are already skipped unless asked for; node_modules and
    /// *.tmp were in the first draft and are not photo junk.
    /// </summary>
    public static IReadOnlyList<string> SuggestedExcludeFolders { get; } = ["@eaDir", ".thumbnails", "*.lrdata"];

    /// <inheritdoc cref="SuggestedExcludeFolders" />
    public static IReadOnlyList<string> SuggestedExcludeFiles { get; } = ["*.aae"];

    /// <summary>Parses the four panes' text, each in the canonical form SameAs relies on.</summary>
    public static ScanPatterns FromText(string? includeFiles, string? excludeFiles, string? includeFolders, string? excludeFolders) =>
        new(
            NamePatterns.ParseInclude(includeFiles, files: true),
            NamePatterns.Parse(excludeFiles, files: true),
            NamePatterns.ParseInclude(includeFolders, files: false),
            NamePatterns.Parse(excludeFolders, files: false));

    /// <summary>
    /// Whether anything is left out, given whether subfolders are read. Include folders
    /// means nothing without them - every file is in the root - so it does not count then.
    /// </summary>
    public bool IsNarrowed(bool recurse) =>
        !NamePatterns.IsEverything(this.IncludeFiles)
        || this.ExcludeFiles.Count > 0
        || this.ExcludeFolders.Count > 0
        || (recurse && !NamePatterns.IsEverything(this.IncludeFolders));

    public bool SameAs(ScanPatterns other) =>
        NamePatterns.SameAs(this.IncludeFiles, other.IncludeFiles)
        && NamePatterns.SameAs(this.ExcludeFiles, other.ExcludeFiles)
        && NamePatterns.SameAs(this.IncludeFolders, other.IncludeFolders)
        && NamePatterns.SameAs(this.ExcludeFolders, other.ExcludeFolders);

    /// <summary>One line per non-default pane, for a tooltip and the journal.</summary>
    public string Describe()
    {
        var lines = new List<string>();

        if (!NamePatterns.IsEverything(this.IncludeFiles))
        {
            lines.Add("Include files named " + NamePatterns.Format(this.IncludeFiles));
        }

        if (this.ExcludeFiles.Count > 0)
        {
            lines.Add("Skip files named " + NamePatterns.Format(this.ExcludeFiles));
        }

        if (!NamePatterns.IsEverything(this.IncludeFolders))
        {
            lines.Add("Include folders named " + NamePatterns.Format(this.IncludeFolders));
        }

        if (this.ExcludeFolders.Count > 0)
        {
            lines.Add("Skip folders named " + NamePatterns.Format(this.ExcludeFolders));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
