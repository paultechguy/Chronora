// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Rules;

/// <summary>How a pattern is written.</summary>
public enum PatternMode
{
    /// <summary>Tokens such as IMG_{yyyy}{MM}{dd}. The shareable, authorable form.</summary>
    Tokens,

    /// <summary>A raw .NET regex with named groups. The advanced escape hatch.</summary>
    Regex,
}

/// <summary>Which part of the path a pattern is matched against.</summary>
public enum PatternScope
{
    FileNameWithoutExtension,
    FileName,
    ParentFolderName,
}

/// <summary>
/// One filename date pattern.
///
/// Tokens are the authoring and storage format, compiled to regex underneath. Storing raw
/// regex as the user-facing form was the revision 1 design and it fails exactly the person
/// who needs it: someone whose camera uses an odd filename has no reason to know named
/// capture groups, and a regex that matches "0001" as a year is valid and shows green.
/// </summary>
/// <param name="Id">Stable identifier, referenced by DateSource.FromFileName.</param>
/// <param name="Name">What the user sees.</param>
/// <param name="Mode">Tokens or raw regex.</param>
/// <param name="Pattern">The token string or the regex source.</param>
/// <param name="Scope">Which part of the path to match.</param>
/// <param name="Precision">The best precision this pattern can yield.</param>
/// <param name="IsBuiltIn">Built-ins are read-only and may be duplicated, not edited.</param>
/// <param name="IsEnabled">
/// Off by default for patterns that match too much. Bare {yyyy}{MM}{dd} happily matches
/// inside IMG_20240315_142530 before the specific pattern gets a chance, and it matches
/// serial numbers, so default-enablement is as much of the design as the pattern itself.
/// </param>
/// <param name="Order">Evaluation order. First plausible match wins.</param>
public sealed record FilenamePattern(
    string Id,
    string Name,
    PatternMode Mode,
    string Pattern,
    PatternScope Scope,
    DatePrecision Precision,
    bool IsBuiltIn = false,
    bool IsEnabled = true,
    int Order = 0);

/// <summary>A date recovered from a filename.</summary>
/// <param name="PatternId">Which pattern matched.</param>
/// <param name="Value">The date, with whatever precision the pattern supplied.</param>
/// <param name="Precision">How much of the value is real, as opposed to defaulted.</param>
/// <param name="Offset">A UTC offset, when the filename carried one.</param>
/// <param name="MatchStart">Where the match began, used by the ambiguity check.</param>
/// <param name="MatchLength">How long the match was.</param>
public readonly record struct FilenameDateMatch(
    string PatternId,
    DateTime Value,
    DatePrecision Precision,
    TimeSpan? Offset,
    int MatchStart,
    int MatchLength);

/// <summary>
/// Compiles token patterns to regex, once, with the safety options that matter.
/// </summary>
public static class PatternCompiler
{
    /// <summary>
    /// A user-supplied regex is untrusted input running 50,000 times. NonBacktracking makes
    /// catastrophic backtracking impossible; it rejects lookaround and backreferences, so a
    /// pattern needing those falls back to Compiled with a hard timeout instead.
    /// </summary>
    private const RegexOptions BaseOptions =
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static Regex Compile(FilenamePattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        string source = pattern.Mode == PatternMode.Tokens
            ? TokensToRegex(pattern.Pattern)
            : pattern.Pattern;

        try
        {
            return new Regex(source, BaseOptions | RegexOptions.NonBacktracking, MatchTimeout);
        }
        catch (NotSupportedException)
        {
            // Lookaround or backreferences. Allowed, but now it needs the timeout to be safe.
            return new Regex(source, BaseOptions | RegexOptions.Compiled, MatchTimeout);
        }
    }

    /// <summary>
    /// Translates the token vocabulary into a regex with named groups.
    ///
    /// Anything that is not a token is escaped, so a pattern is literal by default and a user
    /// cannot accidentally write regex metacharacters into what they meant as plain text.
    /// </summary>
    public static string TokensToRegex(string tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var sb = new StringBuilder(tokens.Length * 4);
        int i = 0;

        while (i < tokens.Length)
        {
            if (tokens[i] == '{')
            {
                int close = tokens.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new FormatException($"Unclosed token starting at position {i}.");
                }

                string token = tokens[(i + 1)..close];
                sb.Append(TranslateToken(token));
                i = close + 1;
                continue;
            }

            sb.Append(Regex.Escape(tokens[i].ToString()));
            i++;
        }

        return sb.ToString();
    }

    private static string TranslateToken(string token) => token switch
    {
        "yyyy" => @"(?<y>\d{4})",
        "yy" => @"(?<yy>\d{2})",
        "MM" => @"(?<M>\d{2})",
        "M" => @"(?<M>\d{1,2})",
        "dd" => @"(?<d>\d{2})",
        "d" => @"(?<d>\d{1,2})",
        "HH" => @"(?<H>\d{2})",
        "H" => @"(?<H>\d{1,2})",
        "hh" => @"(?<h>\d{1,2})",
        "mm" => @"(?<m>\d{2})",
        "ss" => @"(?<s>\d{2})",
        "fff" => @"(?<f>\d{3})",
        "tt" => @"(?<t>[AaPp]\.?[Mm]\.?)",
        "z" => @"(?<z>Z|[+-]\d{2}:?\d{2})",
        "unix" => @"(?<unix>\d{10})",
        "unixms" => @"(?<unixms>\d{13})",

        // Structural helpers. None of these capture.
        "*" => ".*?",
        "?" => ".",
        "#" => @"\d+",

        // The Google-style duplicate marker, matched and discarded.
        "(n)" => @"(?:\(\d+\))?",

        _ => throw new FormatException($"Unknown token '{{{token}}}'."),
    };
}
