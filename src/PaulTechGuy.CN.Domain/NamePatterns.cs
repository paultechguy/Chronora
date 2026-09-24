// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.IO.Enumeration;

namespace PaulTechGuy.CN.Domain;

/// <summary>
/// The one wildcard grammar in the app: what the Type filter and the scan filters accept.
///
/// Names only, "*" and "?", matched with <see cref="FileSystemName.MatchesSimpleExpression(ReadOnlySpan{char}, ReadOnlySpan{char}, bool)" />
/// - the matcher the scanner has always used. Win32 matching would quietly change what
/// IMG_????.CR2 means.
/// </summary>
public static class NamePatterns
{
    /// <summary>The canonical "everything" list. An include list is exactly this or narrowed.</summary>
    public static IReadOnlyList<string> Everything { get; } = ["*"];

    /// <summary>
    /// Splits on ';' and newlines, trims, drops empties and case-insensitive duplicates.
    ///
    /// For file names, a word with no dot in it ("xmp") or a lone ".xmp" is taken as an
    /// extension and becomes "*.xmp". ONLY those: the rule used to be "anything without a
    /// wildcard", which made "Thumbs.db" into "*.Thumbs.db" - a pattern that cannot match
    /// the file it was typed to find.
    ///
    /// "*.*" becomes "*". Under simple matching "*.*" demands a dot in the name, so the
    /// Beyond Compare default would silently drop every extensionless file; measured
    /// 2026-09-23, MatchesSimpleExpression("*.*", "README") is false.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text, bool files)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in (text ?? string.Empty).Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string pattern = raw == "*.*" ? "*" : raw;

            if (files && IsBareExtension(pattern))
            {
                pattern = "*" + (pattern.StartsWith('.') ? pattern : "." + pattern);
            }

            if (seen.Add(pattern))
            {
                result.Add(pattern);
            }
        }

        return result;
    }

    /// <summary>
    /// An include list in canonical form: empty, or anything containing "*", is exactly
    /// <see cref="Everything" />. Without this, clearing a pane and leaving "*" in it would
    /// count as different settings and light "Read the folders again" over nothing.
    /// </summary>
    public static IReadOnlyList<string> ParseInclude(string? text, bool files)
    {
        IReadOnlyList<string> parsed = Parse(text, files);
        return parsed.Count == 0 || parsed.Contains("*") ? Everything : parsed;
    }

    /// <summary>Whether a list leaves nothing out.</summary>
    public static bool IsEverything(IReadOnlyList<string> patterns) =>
        patterns.Count == 0 || patterns.Contains("*");

    /// <summary>Whether a name matches any of the patterns. Case-insensitive.</summary>
    public static bool MatchesAny(IReadOnlyList<string> patterns, ReadOnlySpan<char> name)
    {
        foreach (string pattern in patterns)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text a pane shows and settings store.</summary>
    public static string Format(IReadOnlyList<string> patterns) => string.Join("; ", patterns);

    /// <summary>Same patterns, ignoring order and case. Records compare lists by reference.</summary>
    public static bool SameAs(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private static bool IsBareExtension(string pattern) =>
        !pattern.Contains('*', StringComparison.Ordinal)
        && !pattern.Contains('?', StringComparison.Ordinal)
        && pattern.LastIndexOf('.') <= 0;
}
