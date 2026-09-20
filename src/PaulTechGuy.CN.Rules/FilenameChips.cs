// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Rules;

/// <summary>What a run of digits in a filename means.</summary>
public enum ChipRole
{
    /// <summary>Not part of the date. Matched by position and ignored.</summary>
    None,

    Year,

    /// <summary>A two-digit year. Separate from <see cref="Year" /> because the width differs.</summary>
    ShortYear,

    Month,
    Day,
    Hour,
    Minute,
    Second,
}

/// <summary>One clickable piece of a filename.</summary>
/// <param name="Text">The characters themselves.</param>
/// <param name="IsDigits">Only digit runs can be given a role.</param>
/// <param name="Role">What it means, once someone says.</param>
public sealed record FilenameChip(string Text, bool IsDigits, ChipRole Role);

/// <summary>
/// Turns "click the year, click the month" into a pattern.
///
/// This is the escape hatch for a filename the built-ins do not recognise, and it exists
/// instead of a regex box because the person who needs the escape hatch is by definition
/// the one the built-ins failed - and there is no connection whatsoever between "my camera
/// writes odd filenames" and "I can write a named capture group". A live match preview
/// helps somebody debug a regex they could already have written; it does nothing for
/// somebody who cannot write one.
///
/// Regex is also the one place a user can quietly produce WRONG results rather than no
/// results: a pattern that matches 0001 as a year is perfectly valid and shows green.
///
/// A role shorter than the run it is dropped on splits that run, so 20240315 becomes year,
/// then month, then day by clicking three times - which is the case that actually turns up,
/// since a camera writes the date as one long number.
/// </summary>
public sealed class FilenameChipBuilder
{
    private readonly List<FilenameChip> _chips;

    public FilenameChipBuilder(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        this._chips = [.. Split(fileName)];
    }

    /// <summary>The pieces, in order. Rebuilt as roles are assigned.</summary>
    public IReadOnlyList<FilenameChip> Chips => this._chips;

    /// <summary>How many digits each role consumes.</summary>
    public static int WidthOf(ChipRole role) => role switch
    {
        ChipRole.Year => 4,
        ChipRole.None => 0,
        _ => 2,
    };

    /// <summary>
    /// Gives a role to a run, splitting it when the role is narrower than the run.
    ///
    /// Assigning from the left is the whole interaction: on 20240315 you name the year and
    /// are left with 0315 sitting next to it, ready to be named month and then day.
    /// </summary>
    /// <returns>False when the run is too short for the role, or is not digits.</returns>
    public bool Assign(int index, ChipRole role)
    {
        if (index < 0 || index >= this._chips.Count)
        {
            return false;
        }

        FilenameChip chip = this._chips[index];

        if (!chip.IsDigits)
        {
            return false;
        }

        if (role == ChipRole.None)
        {
            this._chips[index] = chip with { Role = ChipRole.None };
            this.Merge();
            return true;
        }

        int width = WidthOf(role);

        if (chip.Text.Length < width)
        {
            return false;
        }

        this._chips[index] = new FilenameChip(chip.Text[..width], true, role);

        if (chip.Text.Length > width)
        {
            this._chips.Insert(index + 1, new FilenameChip(chip.Text[width..], true, ChipRole.None));
        }

        return true;
    }

    /// <summary>Takes a role off a run and heals the split it caused.</summary>
    public void Clear(int index) => this.Assign(index, ChipRole.None);

    /// <summary>Every role currently assigned.</summary>
    public IReadOnlySet<ChipRole> AssignedRoles =>
        this._chips.Select(c => c.Role).Where(r => r != ChipRole.None).ToHashSet();

    /// <summary>
    /// What is still missing, in plain words, or null when the pattern is usable.
    ///
    /// A day and a month without a year is not a date, and a pattern that silently
    /// defaulted the year to this one would put four thousand holiday photos in 2026.
    /// </summary>
    public string? Problem
    {
        get
        {
            IReadOnlySet<ChipRole> roles = this.AssignedRoles;

            if (roles.Count == 0)
            {
                return "Click a number in the file name and say what it is.";
            }

            if (!roles.Contains(ChipRole.Year) && !roles.Contains(ChipRole.ShortYear))
            {
                return "A year is needed. Without one Chronora would have to guess it.";
            }

            if (!roles.Contains(ChipRole.Month))
            {
                return "A month is needed.";
            }

            if (!roles.Contains(ChipRole.Day))
            {
                return "A day is needed.";
            }

            // Minutes without an hour is not a time, and seconds without minutes is not
            // one either. Each is only meaningful on top of the one above it.
            if (roles.Contains(ChipRole.Minute) && !roles.Contains(ChipRole.Hour))
            {
                return "An hour is needed before minutes mean anything.";
            }

            if (roles.Contains(ChipRole.Second) && !roles.Contains(ChipRole.Minute))
            {
                return "Minutes are needed before seconds mean anything.";
            }

            return null;
        }
    }

    /// <summary>
    /// How much of the result is real.
    ///
    /// Load-bearing rather than informational: a day-precision match combined with a
    /// date-and-time target has to set the date and leave the existing time of day alone,
    /// not zero it to midnight.
    /// </summary>
    public DatePrecision Precision
    {
        get
        {
            IReadOnlySet<ChipRole> roles = this.AssignedRoles;

            if (roles.Contains(ChipRole.Second))
            {
                return DatePrecision.Second;
            }

            return roles.Contains(ChipRole.Minute) ? DatePrecision.Minute : DatePrecision.Day;
        }
    }

    /// <summary>
    /// The token pattern these chips describe.
    ///
    /// Unassigned digit runs become a fixed-width matcher rather than {#}. The difference
    /// matters: {#} is one-or-more and greedy, so having named the year in 20240315 it
    /// would happily swallow the month and day as well.
    /// </summary>
    public string ToTokens()
    {
        var tokens = new StringBuilder();

        foreach (FilenameChip chip in this._chips)
        {
            _ = tokens.Append(TokenFor(chip));
        }

        return tokens.ToString();
    }

    private static string TokenFor(FilenameChip chip) => chip switch
    {
        { Role: ChipRole.Year } => "{yyyy}",
        { Role: ChipRole.ShortYear } => "{yy}",
        { Role: ChipRole.Month } => "{MM}",
        { Role: ChipRole.Day } => "{dd}",
        { Role: ChipRole.Hour } => "{HH}",
        { Role: ChipRole.Minute } => "{mm}",
        { Role: ChipRole.Second } => "{ss}",

        { IsDigits: true } => string.Create(CultureInfo.InvariantCulture, $"{{#{chip.Text.Length}}}"),

        // Literal text, escaped by the compiler, so a filename full of dots and brackets
        // cannot become regex by accident.
        _ => chip.Text,
    };

    /// <summary>
    /// Rejoins neighbouring unassigned digit runs, so clearing a role leaves the filename
    /// looking the way it did before it was assigned rather than as two stuck-together
    /// fragments nobody can reassemble.
    /// </summary>
    private void Merge()
    {
        for (int i = this._chips.Count - 2; i >= 0; i--)
        {
            if (this._chips[i] is { IsDigits: true, Role: ChipRole.None }
                && this._chips[i + 1] is { IsDigits: true, Role: ChipRole.None })
            {
                this._chips[i] = new FilenameChip(
                    this._chips[i].Text + this._chips[i + 1].Text,
                    true,
                    ChipRole.None);

                this._chips.RemoveAt(i + 1);
            }
        }
    }

    /// <summary>Breaks a name into alternating runs of digits and everything else.</summary>
    private static IEnumerable<FilenameChip> Split(string name)
    {
        int i = 0;

        while (i < name.Length)
        {
            bool digits = char.IsAsciiDigit(name[i]);
            int start = i;

            while (i < name.Length && char.IsAsciiDigit(name[i]) == digits)
            {
                i++;
            }

            yield return new FilenameChip(name[start..i], digits, ChipRole.None);
        }
    }
}
