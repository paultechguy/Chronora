// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Rules;

/// <summary>
/// Everything the evaluator needs that is not the file or the recipe. Established once per
/// preview pass and held constant across it, so every row is judged by the same standard.
/// </summary>
/// <param name="Clock">The zone and DST policy in force.</param>
/// <param name="Now">Injected rather than read, so tests can pin the clock.</param>
/// <param name="MetadataEngineAvailable">
/// False when ExifTool has not been consented to. Metadata targets are then reported blocked
/// with a reason rather than silently dropped, because the app must be coherent in that state.
/// </param>
/// <param name="EarliestPlausible">Below this, a result is Suspicious rather than accepted quietly.</param>
/// <param name="LargestPlausibleShift">
/// When set, a move larger than this is Suspicious.
///
/// Deliberately OFF by default, which is the opposite of what it looks like it should be. A
/// large move is the app's most common legitimate operation: restoring a 2019 capture date
/// onto a file the exporter stamped with today is a seven-year shift, and flagging every row
/// of a Takeout repair would make the Suspicious count useless exactly when it matters most.
///
/// The genuinely informative version of this check is relative rather than absolute - "this
/// row moved very differently from the other 4,000" - and that belongs at the batch level,
/// where the peers are known. Per-row, absolute plausibility is all that can be judged
/// honestly.
/// </param>
public sealed record EvaluationContext(
    ClockContext Clock,
    DateTimeOffset Now,
    bool MetadataEngineAvailable = true,
    DateTimeOffset? EarliestPlausible = null,
    TimeSpan? LargestPlausibleShift = null)
{
    public DateTimeOffset Earliest => this.EarliestPlausible ?? new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// The pure heart of the app: (file, recipe) becomes a list of planned changes, with no I/O
/// of any kind.
///
/// That purity is load-bearing rather than tidy. It is what lets the preview re-run on every
/// keystroke without touching the disk, and it is why the rule matrix can be table-tested
/// exhaustively without a filesystem.
/// </summary>
public sealed class RuleEvaluator
{
    private readonly FilenameDateParser _filenames;

    public RuleEvaluator(FilenameDateParser? filenameParser = null) =>
        this._filenames = filenameParser ?? new FilenameDateParser();

    public FilePlan Evaluate(ScannedFile file, Recipe recipe, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(context);

        // Keyed by target so a later rule wins, which is what makes a recipe a list rather
        // than a single rule: FileTouch let Created and Modified take different values in
        // one pass, and that has to survive.
        var byField = new Dictionary<DateField, PlannedChange>();

        for (int ruleIndex = 0; ruleIndex < recipe.Rules.Count; ruleIndex++)
        {
            DateRule rule = recipe.Rules[ruleIndex];

            foreach (DateField target in rule.Targets)
            {
                // A field this kind of file simply does not have produces nothing at all,
                // rather than a blocked row. One template covering photos and videos is
                // the point - a phone folder holds both - and reporting "no QuickTime
                // date" against every JPEG would bury the real problems in noise about a
                // field nobody asked for on that file.
                if (!DateFieldCatalog.AppliesTo(target, file.Kind))
                {
                    continue;
                }

                PlannedChange change = this.EvaluateOne(file, rule, target, ruleIndex, context);
                byField[target] = change;
            }
        }

        // A stable order, so the preview does not reshuffle between recomputes.
        List<PlannedChange> ordered = byField
            .OrderBy(kv => (int)kv.Key)
            .Select(kv => kv.Value)
            .ToList();

        return new FilePlan(file, ordered);
    }

    private PlannedChange EvaluateOne(
        ScannedFile file,
        DateRule rule,
        DateField target,
        int ruleIndex,
        EvaluationContext context)
    {
        var targetRef = new ChangeTarget.Field(target);
        DateTimeOffset? current = file.Current(target);
        FieldWrite? before = current is { } c
            ? new FieldWrite.SetDate(c, DatePrecision.Second)
            : null;

        // Blocked beats everything: there is no point computing a value for a field that
        // cannot be written, and saying so is more useful than a silent skip.
        if (TryBlock(file, target, context, out ProblemCode blockReason))
        {
            return new PlannedChange(targetRef, before, After: null, ChangeStatus.Blocked, blockReason, ruleIndex);
        }

        ResolvedDate? resolved = this.Resolve(file, rule.Source, target, context)
            ?? (rule.Fallback is not null ? this.Resolve(file, rule.Fallback, target, context) : null);

        if (resolved is not { } value)
        {
            return new PlannedChange(targetRef, before, After: null, ChangeStatus.Skipped, ProblemCode.NoSourceValue, ruleIndex);
        }

        if (value.Problem != ProblemCode.None && value.Blocking)
        {
            return new PlannedChange(targetRef, before, After: null, ChangeStatus.Blocked, value.Problem, ruleIndex);
        }

        // A day-precision source must not invent a time of day. Keeping the existing clock
        // reading is the difference between "correct the date" and "flatten 4,000 photos to
        // midnight", and WhatsApp names make this the common case rather than a corner one.
        DateTimeOffset final = value.Precision == DatePrecision.Day && current is { } existing
            ? CombineDateWithExistingTime(value.Value, existing, context)
            : value.Value;

        if (!PassesGuards(rule.Guards, current, final))
        {
            return new PlannedChange(targetRef, before, After: null, ChangeStatus.Skipped, ProblemCode.None, ruleIndex);
        }

        var after = new FieldWrite.SetDate(final, value.Precision);

        if (current is { } existingValue && existingValue == final)
        {
            return new PlannedChange(targetRef, before, after, ChangeStatus.Unchanged, ProblemCode.None, ruleIndex);
        }

        ChangeStatus status = Judge(final, current, context, value.Problem, out ProblemCode problem);

        return new PlannedChange(targetRef, before, after, status, problem, ruleIndex);
    }

    /// <summary>
    /// Reasons a field cannot be written, established from traits captured during the scan
    /// rather than discovered at apply time.
    ///
    /// Read-only is deliberately NOT one of them: the writer clears the flag, writes, and
    /// restores it, so blocking here would refuse work the app can do.
    /// </summary>
    private static bool TryBlock(ScannedFile file, DateField target, EvaluationContext context, out ProblemCode reason)
    {
        bool isMetadata = DateFieldCatalog.GenreOf(target) == FieldGenre.Metadata;

        if (target == DateField.FileChanged && file.Traits.HasFlag(FileTraits.NoChangeTimeSupport))
        {
            reason = ProblemCode.ChangeTimeUnsupported;
            return true;
        }

        if (isMetadata && !context.MetadataEngineAvailable)
        {
            reason = ProblemCode.MetadataEngineUnavailable;
            return true;
        }

        // Filesystem work needs no hydration thanks to FILE_FLAG_OPEN_NO_RECALL, so only
        // metadata is blocked here. That asymmetry is why the simple mode stays safe on
        // OneDrive.
        if (isMetadata && file.Traits.HasFlag(FileTraits.CloudDehydrated))
        {
            reason = ProblemCode.CloudPlaceholderWouldHydrate;
            return true;
        }

        if (isMetadata && file.IsDirectory)
        {
            reason = ProblemCode.FieldNotWritableForFormat;
            return true;
        }

        reason = ProblemCode.None;
        return false;
    }

    private ResolvedDate? Resolve(ScannedFile file, DateSource source, DateField target, EvaluationContext context) =>
        source switch
        {
            DateSource.Absolute a => new ResolvedDate(a.Value, DatePrecision.Second),

            DateSource.Shift s => file.Current(target) is { } cur
                ? new ResolvedDate(context.Clock.Shift(cur, s.Delta, s.Basis), DatePrecision.Second)
                : null,

            DateSource.ZoneChange z => file.Current(target) is { } cur
                ? new ResolvedDate(ClockContext.ReinterpretZone(cur, z.From, z.To), DatePrecision.Second)
                : null,

            DateSource.CopyFrom copy => ResolveCopy(file, copy),

            DateSource.FromFileName fn => this.ResolveFilename(file, fn, context),

            _ => null,
        };

    private static ResolvedDate? ResolveCopy(ScannedFile file, DateSource.CopyFrom copy)
    {
        // The list may cross the filesystem/metadata boundary in either direction. That is
        // the product's differentiator, and it costs nothing here because ScannedFile.Current
        // already hides which side a field lives on.
        List<DateTimeOffset> candidates = copy.Fields
            .Select(file.Current)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        DateTimeOffset chosen = copy.How switch
        {
            Aggregate.Earliest => candidates.Min(),
            Aggregate.Latest => candidates.Max(),
            _ => candidates[0],
        };

        return new ResolvedDate(chosen, DatePrecision.Second);
    }

    private ResolvedDate? ResolveFilename(ScannedFile file, DateSource.FromFileName source, EvaluationContext context)
    {
        FilenameParseResult parsed = string.IsNullOrEmpty(source.PatternId)
            ? this._filenames.Parse(file.FullPath)
            : this._filenames.ParseWith(file.FullPath, source.PatternId);

        if (parsed.Outcome == FilenameMatchOutcome.Ambiguous)
        {
            // Two credible dates in one name. Refusing is the point.
            return new ResolvedDate(default, DatePrecision.Day, ProblemCode.AmbiguousPatternMatch, Blocking: true);
        }

        if (!parsed.Success)
        {
            return null;
        }

        FilenameDateMatch m = parsed.Match;

        // A filename carries a wall-clock reading, not an instant, unless it said otherwise.
        if (m.Offset is { } offset)
        {
            return new ResolvedDate(new DateTimeOffset(m.Value, offset), m.Precision);
        }

        ClockConversion conversion = context.Clock.ToInstant(m.Value, out DateTimeOffset instant);

        ProblemCode problem = conversion switch
        {
            ClockConversion.Invalid => ProblemCode.InvalidLocalTime,
            ClockConversion.Ambiguous => ProblemCode.AmbiguousLocalTime,
            _ => ProblemCode.None,
        };

        bool blocking = problem != ProblemCode.None && context.Clock.Policy == DstPolicy.Report;

        return new ResolvedDate(instant, m.Precision, problem, blocking);
    }

    /// <summary>
    /// Keeps the existing time of day and replaces only the date part, in the user's zone so
    /// that "same time, different day" means what it looks like on screen.
    /// </summary>
    private static DateTimeOffset CombineDateWithExistingTime(
        DateTimeOffset dateOnly,
        DateTimeOffset existing,
        EvaluationContext context)
    {
        DateTime existingWall = context.Clock.ToWallClock(existing);
        DateTime newWall = dateOnly.Date.Add(existingWall.TimeOfDay);

        _ = context.Clock.ToInstant(newWall, out DateTimeOffset combined);
        return combined;
    }

    private static bool PassesGuards(RuleGuards guards, DateTimeOffset? current, DateTimeOffset proposed)
    {
        if (guards.OnlyIfTargetEmpty && current.HasValue)
        {
            return false;
        }

        if (guards.OnlyIfNewer && current is { } newerThan && proposed <= newerThan)
        {
            return false;
        }

        if (guards.OnlyIfOlder && current is { } olderThan && proposed >= olderThan)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Decides whether a change should be trusted quietly or flagged.
    ///
    /// This is what stops a run where 63 videos pick up a 1904 date from a zeroed QuickTime
    /// atom looking exactly like a clean run.
    /// </summary>
    private static ChangeStatus Judge(
        DateTimeOffset proposed,
        DateTimeOffset? current,
        EvaluationContext context,
        ProblemCode carried,
        out ProblemCode problem)
    {
        if (carried != ProblemCode.None)
        {
            problem = carried;
            return ChangeStatus.Suspicious;
        }

        if (proposed < context.Earliest || proposed > context.Now.AddDays(1))
        {
            problem = ProblemCode.ImplausibleDate;
            return ChangeStatus.Suspicious;
        }

        // Only when a caller has explicitly asked for it. See the note on the context.
        if (context.LargestPlausibleShift is { } limit
            && current is { } from
            && (proposed - from).Duration() > limit)
        {
            problem = ProblemCode.ImplausibleDate;
            return ChangeStatus.Suspicious;
        }

        problem = ProblemCode.None;
        return ChangeStatus.WillChange;
    }

    /// <summary>A date a source produced, plus anything questionable about how it was produced.</summary>
    private readonly record struct ResolvedDate(
        DateTimeOffset Value,
        DatePrecision Precision,
        ProblemCode Problem = ProblemCode.None,
        bool Blocking = false);
}
