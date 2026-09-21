// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using System.IO;
using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.Rules.Tests;

/// <summary>Builds ScannedFile instances without touching a disk.</summary>
internal static class Fake
{
    public static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    public static ScannedFile File(
        string path = @"C:\photos\IMG_20240315_142530.jpg",
        DateTimeOffset? created = null,
        DateTimeOffset? modified = null,
        DateTimeOffset? taken = null,
        MediaKind kind = MediaKind.Jpeg,
        FileTraits traits = FileTraits.None)
    {
        var metadata = new Dictionary<DateField, MetadataValue>();
        if (taken is { } t)
        {
            metadata[DateField.ExifDateTimeOriginal] = new MetadataValue(true, t.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), t);
        }

        return new ScannedFile(
            path,
            Length: 1024,
            kind,
            FileAttributes.Normal,
            new TimestampSet(created, modified, null, null),
            metadata.ToFrozenDictionary(),
            traits);
    }

    public static EvaluationContext Context(
        TimeZoneInfo? zone = null,
        bool engineAvailable = true,
        DstPolicy policy = DstPolicy.Report) =>
        new(new ClockContext(zone ?? TimeZoneInfo.Utc, policy), Now, engineAvailable);

    public static Recipe Recipe(DateSource source, params DateField[] targets) =>
        new([new DateRule(source, targets.ToHashSet(), RuleGuards.None)], ScanFilter.Default);

    public static PlannedChange For(this FilePlan plan, DateField field) =>
        plan.Changes.Single(c => ((ChangeTarget.Field)c.Target).Which == field);
}

public class RuleEvaluatorTests
{
    private static readonly RuleEvaluator Evaluator = new();
    private static readonly DateTimeOffset Stamp = new(2024, 3, 15, 14, 25, 30, TimeSpan.Zero);

    [Fact]
    public void An_absolute_date_is_written_to_every_target()
    {
        ScannedFile file = Fake.File(created: Stamp.AddYears(-5), modified: Stamp.AddYears(-5));

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.Absolute(Stamp), DateField.FileCreated, DateField.FileModified),
            Fake.Context());

        plan.For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.WillChange);
        plan.For(DateField.FileCreated).AfterDate.ShouldBe(Stamp);
        plan.For(DateField.FileModified).AfterDate.ShouldBe(Stamp);
    }

    [Fact]
    public void A_value_that_already_matches_is_reported_unchanged_rather_than_rewritten()
    {
        ScannedFile file = Fake.File(created: Stamp);

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.Absolute(Stamp), DateField.FileCreated),
            Fake.Context());

        plan.For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.Unchanged);
        plan.WillWrite.ShouldBeFalse();
    }

    /// <summary>
    /// The product's differentiator: a rule reads a metadata field and writes filesystem
    /// fields. This is "my holiday photos sort wrong in Explorer", and it has to work from
    /// File dates mode, where every TARGET is a file date even though the SOURCE is not.
    /// </summary>
    [Fact]
    public void A_date_can_be_copied_from_metadata_onto_the_file_dates()
    {
        ScannedFile file = Fake.File(
            created: new DateTimeOffset(2026, 9, 19, 8, 0, 0, TimeSpan.Zero),
            taken: Stamp);

        var recipe = new Recipe(
            [new DateRule(
                new DateSource.CopyFrom(Aggregate.FirstPresent, [DateField.ExifDateTimeOriginal]),
                new HashSet<DateField> { DateField.FileCreated, DateField.FileModified },
                RuleGuards.None)],
            ScanFilter.Default,
            AppMode.FileDates);

        FilePlan plan = Evaluator.Evaluate(file, recipe, Fake.Context());

        plan.For(DateField.FileCreated).AfterDate.ShouldBe(Stamp);
        plan.For(DateField.FileModified).AfterDate.ShouldBe(Stamp);

        // And the recipe knows it needs ExifTool even though it writes no metadata, which is
        // what makes the consent pane appear from the simple surface.
        recipe.NeedsMetadataWrite.ShouldBeFalse();
        recipe.NeedsMetadataRead.ShouldBeTrue();
    }

    [Fact]
    public void Copy_from_takes_the_first_field_that_has_a_value()
    {
        ScannedFile file = Fake.File(created: null, modified: Stamp);

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(
                new DateSource.CopyFrom(Aggregate.FirstPresent, [DateField.FileCreated, DateField.FileModified]),
                DateField.FileChanged),
            Fake.Context());

        plan.For(DateField.FileChanged).AfterDate.ShouldBe(Stamp);
    }

    /// <summary>
    /// "Make every date on this file consistent" is the most requested normalisation in this
    /// domain, and a first-non-null CopyFrom cannot express it. This is the gap the reviews
    /// found in the original model.
    /// </summary>
    [Fact]
    public void Copy_from_earliest_picks_the_oldest_candidate()
    {
        DateTimeOffset older = Stamp.AddYears(-3);
        ScannedFile file = Fake.File(created: Stamp, modified: older, taken: Stamp.AddYears(-1));

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(
                new DateSource.CopyFrom(
                    Aggregate.Earliest,
                    [DateField.FileCreated, DateField.FileModified, DateField.ExifDateTimeOriginal]),
                DateField.FileCreated),
            Fake.Context());

        plan.For(DateField.FileCreated).AfterDate.ShouldBe(older);
    }

    [Fact]
    public void Copy_from_latest_picks_the_newest_candidate()
    {
        DateTimeOffset newest = Stamp.AddYears(2);
        ScannedFile file = Fake.File(created: Stamp, modified: newest);

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(
                new DateSource.CopyFrom(Aggregate.Latest, [DateField.FileCreated, DateField.FileModified]),
                DateField.FileChanged),
            Fake.Context());

        plan.For(DateField.FileChanged).AfterDate.ShouldBe(newest);
    }

    [Fact]
    public void A_source_with_no_value_anywhere_is_skipped_and_says_why()
    {
        ScannedFile file = Fake.File(created: null, modified: null);

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(
                new DateSource.CopyFrom(Aggregate.FirstPresent, [DateField.ExifDateTimeOriginal]),
                DateField.FileCreated),
            Fake.Context());

        PlannedChange change = plan.For(DateField.FileCreated);
        change.Status.ShouldBe(ChangeStatus.Skipped);
        change.Problem.ShouldBe(ProblemCode.NoSourceValue);
    }

    [Fact]
    public void A_fallback_runs_when_the_primary_source_finds_nothing()
    {
        ScannedFile file = Fake.File(path: @"C:\p\IMG_20240315_142530.jpg", created: Stamp.AddYears(-5));

        var recipe = new Recipe(
            [new DateRule(
                new DateSource.CopyFrom(Aggregate.FirstPresent, [DateField.ExifDateTimeOriginal]),
                new HashSet<DateField> { DateField.FileCreated },
                RuleGuards.None,
                Fallback: new DateSource.FromFileName(string.Empty))],
            ScanFilter.Default);

        FilePlan plan = Evaluator.Evaluate(file, recipe, Fake.Context());

        plan.For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.WillChange);
        plan.For(DateField.FileCreated).AfterDate!.Value.UtcDateTime.ShouldBe(new DateTime(2024, 3, 15, 14, 25, 30));
    }

    /// <summary>
    /// A later rule targeting the same field wins. This is what lets one run give Created and
    /// Modified different values, which a single-rule model could not express.
    /// </summary>
    [Fact]
    public void A_later_rule_overrides_an_earlier_one_on_the_same_field()
    {
        DateTimeOffset second = Stamp.AddDays(10);
        ScannedFile file = Fake.File(created: Stamp.AddYears(-5));

        var recipe = new Recipe(
            [
                new DateRule(new DateSource.Absolute(Stamp), new HashSet<DateField> { DateField.FileCreated }, RuleGuards.None),
                new DateRule(new DateSource.Absolute(second), new HashSet<DateField> { DateField.FileCreated }, RuleGuards.None),
            ],
            ScanFilter.Default);

        FilePlan plan = Evaluator.Evaluate(file, recipe, Fake.Context());

        plan.Changes.Count.ShouldBe(1);
        plan.For(DateField.FileCreated).AfterDate.ShouldBe(second);
        plan.For(DateField.FileCreated).RuleIndex.ShouldBe(1);
    }

    [Fact]
    public void Different_rules_can_give_different_fields_different_values_in_one_run()
    {
        DateTimeOffset other = Stamp.AddDays(3);
        ScannedFile file = Fake.File(created: Stamp.AddYears(-5), modified: Stamp.AddYears(-5));

        var recipe = new Recipe(
            [
                new DateRule(new DateSource.Absolute(Stamp), new HashSet<DateField> { DateField.FileCreated }, RuleGuards.None),
                new DateRule(new DateSource.Absolute(other), new HashSet<DateField> { DateField.FileModified }, RuleGuards.None),
            ],
            ScanFilter.Default);

        FilePlan plan = Evaluator.Evaluate(file, recipe, Fake.Context());

        plan.For(DateField.FileCreated).AfterDate.ShouldBe(Stamp);
        plan.For(DateField.FileModified).AfterDate.ShouldBe(other);
    }

    [Theory]
    [InlineData(true, false, false, false)]   // OnlyIfTargetEmpty, target has a value
    [InlineData(false, true, false, false)]   // OnlyIfNewer, proposed is older
    [InlineData(false, false, true, true)]    // OnlyIfOlder, proposed is older
    public void Guards_stop_a_rule_touching_a_file_it_should_leave_alone(
        bool onlyIfEmpty, bool onlyIfNewer, bool onlyIfOlder, bool expectWrite)
    {
        ScannedFile file = Fake.File(created: Stamp.AddYears(1));

        var recipe = new Recipe(
            [new DateRule(
                new DateSource.Absolute(Stamp),
                new HashSet<DateField> { DateField.FileCreated },
                new RuleGuards(onlyIfEmpty, onlyIfNewer, onlyIfOlder))],
            ScanFilter.Default);

        FilePlan plan = Evaluator.Evaluate(file, recipe, Fake.Context());

        plan.For(DateField.FileCreated).WillWrite.ShouldBe(expectWrite);
    }

    [Fact]
    public void Only_if_target_empty_writes_when_the_target_really_is_empty()
    {
        ScannedFile file = Fake.File(created: null);

        var recipe = new Recipe(
            [new DateRule(
                new DateSource.Absolute(Stamp),
                new HashSet<DateField> { DateField.FileCreated },
                new RuleGuards(OnlyIfTargetEmpty: true))],
            ScanFilter.Default);

        Evaluator.Evaluate(file, recipe, Fake.Context()).For(DateField.FileCreated)
            .Status.ShouldBe(ChangeStatus.WillChange);
    }

    /// <summary>
    /// The 1904 case: a zeroed QuickTime atom produces a plausible-looking row that is
    /// catastrophically wrong. Without this the run looks clean.
    /// </summary>
    [Fact]
    public void A_date_before_the_plausible_floor_is_flagged_suspicious()
    {
        ScannedFile file = Fake.File(created: Stamp);

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.Absolute(new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero)), DateField.FileCreated),
            Fake.Context());

        PlannedChange change = plan.For(DateField.FileCreated);
        change.Status.ShouldBe(ChangeStatus.Suspicious);
        change.Problem.ShouldBe(ProblemCode.ImplausibleDate);

        // Still written if the user accepts it; Suspicious is a warning, not a block.
        change.WillWrite.ShouldBeTrue();
        plan.IsSuspicious.ShouldBeTrue();
    }

    [Fact]
    public void A_date_in_the_future_is_flagged_suspicious()
    {
        ScannedFile file = Fake.File(created: Stamp);

        Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.Absolute(Fake.Now.AddYears(3)), DateField.FileCreated),
            Fake.Context())
            .For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.Suspicious);
    }

    /// <summary>
    /// The shift-magnitude check exists but is off unless asked for, because the app's most
    /// common legitimate job is a very large move.
    /// </summary>
    [Fact]
    public void A_large_move_is_flagged_only_when_a_shift_limit_was_asked_for()
    {
        ScannedFile file = Fake.File(created: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Recipe recipe = Fake.Recipe(
            new DateSource.Absolute(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            DateField.FileCreated);

        Evaluator.Evaluate(file, recipe, Fake.Context())
            .For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.WillChange);

        var strict = Fake.Context() with { LargestPlausibleShift = TimeSpan.FromDays(365) };

        Evaluator.Evaluate(file, recipe, strict)
            .For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.Suspicious);
    }

    /// <summary>
    /// The regression this protects: a Takeout export stamps every file with the export date,
    /// so restoring the real capture date is a multi-year move on every single row. An
    /// absolute shift limit would flag the whole batch and make the Suspicious count useless
    /// exactly when the user most needs it to mean something.
    /// </summary>
    [Fact]
    public void Restoring_a_years_old_capture_date_is_not_suspicious()
    {
        ScannedFile file = Fake.File(created: Fake.Now);   // as a Takeout export leaves it

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.Absolute(new DateTimeOffset(2019, 4, 2, 11, 30, 0, TimeSpan.Zero)), DateField.FileCreated),
            Fake.Context());

        plan.For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.WillChange);
        plan.IsSuspicious.ShouldBeFalse();
    }

    [Fact]
    public void A_change_time_target_on_a_volume_that_cannot_store_it_is_blocked_with_a_reason()
    {
        ScannedFile file = Fake.File(created: Stamp, traits: FileTraits.NoChangeTimeSupport);

        PlannedChange change = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.Absolute(Stamp.AddDays(1)), DateField.FileChanged),
            Fake.Context()).For(DateField.FileChanged);

        change.Status.ShouldBe(ChangeStatus.Blocked);
        change.Problem.ShouldBe(ProblemCode.ChangeTimeUnsupported);
        change.WillWrite.ShouldBeFalse();
    }

    /// <summary>
    /// "No ExifTool" is a supported state, not an error path. Metadata targets say so; the
    /// filesystem half keeps working untouched.
    /// </summary>
    [Fact]
    public void Without_exiftool_metadata_targets_are_blocked_but_file_dates_still_work()
    {
        ScannedFile file = Fake.File(created: Stamp.AddYears(-1));

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.Absolute(Stamp), DateField.FileCreated, DateField.ExifDateTimeOriginal),
            Fake.Context(engineAvailable: false));

        plan.For(DateField.ExifDateTimeOriginal).Status.ShouldBe(ChangeStatus.Blocked);
        plan.For(DateField.ExifDateTimeOriginal).Problem.ShouldBe(ProblemCode.MetadataEngineUnavailable);
        plan.For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.WillChange);
    }

    /// <summary>
    /// A OneDrive placeholder: filesystem work needs no hydration, metadata does. This
    /// asymmetry is why the simple mode cannot accidentally pull down 200 GB.
    /// </summary>
    [Fact]
    public void A_cloud_placeholder_blocks_metadata_but_not_file_dates()
    {
        ScannedFile file = Fake.File(created: Stamp.AddYears(-1), taken: Stamp.AddYears(-1), traits: FileTraits.CloudDehydrated);

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.Absolute(Stamp), DateField.FileCreated, DateField.ExifDateTimeOriginal),
            Fake.Context());

        plan.For(DateField.ExifDateTimeOriginal).Problem.ShouldBe(ProblemCode.CloudPlaceholderWouldHydrate);
        plan.For(DateField.FileCreated).Status.ShouldBe(ChangeStatus.WillChange);
    }

    /// <summary>
    /// A WhatsApp name knows the day and nothing else. Zeroing the time would flatten a
    /// day's photos onto midnight and destroy their ordering.
    /// </summary>
    [Fact]
    public void A_day_precision_source_keeps_the_existing_time_of_day()
    {
        var existing = new DateTimeOffset(2019, 6, 2, 9, 14, 45, TimeSpan.Zero);
        ScannedFile file = Fake.File(path: @"C:\p\IMG-20240315-WA0001.jpg", created: existing);

        FilePlan plan = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.FromFileName(string.Empty), DateField.FileCreated),
            Fake.Context());

        DateTimeOffset after = plan.For(DateField.FileCreated).AfterDate!.Value;
        after.UtcDateTime.Date.ShouldBe(new DateTime(2024, 3, 15));
        after.UtcDateTime.TimeOfDay.ShouldBe(new TimeSpan(9, 14, 45));
    }

    [Fact]
    public void An_ambiguous_filename_is_blocked_rather_than_guessed()
    {
        ScannedFile file = Fake.File(path: @"C:\p\IMG_20240315_142530 copy of 2019-01-02.jpg", created: Stamp);

        PlannedChange change = Evaluator.Evaluate(
            file,
            Fake.Recipe(new DateSource.FromFileName(string.Empty), DateField.FileCreated),
            Fake.Context()).For(DateField.FileCreated);

        change.Status.ShouldBe(ChangeStatus.Blocked);
        change.Problem.ShouldBe(ProblemCode.AmbiguousPatternMatch);
    }

    [Fact]
    public void The_headline_change_is_the_largest_move()
    {
        ScannedFile file = Fake.File(created: Stamp.AddDays(-1), modified: Stamp.AddYears(-10));

        var recipe = new Recipe(
            [new DateRule(
                new DateSource.Absolute(Stamp),
                new HashSet<DateField> { DateField.FileCreated, DateField.FileModified },
                RuleGuards.None)],
            ScanFilter.Default);

        FilePlan plan = Evaluator.Evaluate(file, recipe, Fake.Context());

        ((ChangeTarget.Field)plan.Headline!.Target).Which.ShouldBe(DateField.FileModified);
    }
}

public class ClockContextTests
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    /// <summary>
    /// 02:30 on 10 March 2024 does not exist in US Eastern. The BCL throws; we move forward
    /// past the gap and say that we did.
    /// </summary>
    [Fact]
    public void A_time_inside_the_spring_forward_gap_is_reported_and_moved_forward()
    {
        var clock = new ClockContext(Eastern);

        ClockConversion result = clock.ToInstant(new DateTime(2024, 3, 10, 2, 30, 0), out DateTimeOffset instant);

        result.ShouldBe(ClockConversion.Invalid);
        instant.Offset.ShouldBe(TimeSpan.FromHours(-4));   // already in daylight time
    }

    /// <summary>
    /// 01:30 on 3 November 2024 happens twice in US Eastern. The BCL silently picks standard
    /// time; we say it was ambiguous so the user can decide.
    /// </summary>
    [Fact]
    public void A_time_inside_the_fall_back_overlap_is_reported()
    {
        var clock = new ClockContext(Eastern);

        ClockConversion result = clock.ToInstant(new DateTime(2024, 11, 3, 1, 30, 0), out _);

        result.ShouldBe(ClockConversion.Ambiguous);
    }

    [Fact]
    public void An_ambiguous_time_resolves_to_daylight_or_standard_on_request()
    {
        var wall = new DateTime(2024, 11, 3, 1, 30, 0);

        _ = new ClockContext(Eastern, DstPolicy.PreferFirst).ToInstant(wall, out DateTimeOffset first);
        _ = new ClockContext(Eastern, DstPolicy.PreferSecond).ToInstant(wall, out DateTimeOffset second);

        first.Offset.ShouldBe(TimeSpan.FromHours(-4));    // daylight
        second.Offset.ShouldBe(TimeSpan.FromHours(-5));   // standard
        second.ShouldBeGreaterThan(first);
    }

    [Fact]
    public void An_ordinary_time_converts_without_complaint()
    {
        var clock = new ClockContext(Eastern);

        clock.ToInstant(new DateTime(2024, 6, 15, 12, 0, 0), out DateTimeOffset instant)
            .ShouldBe(ClockConversion.Ok);

        instant.Offset.ShouldBe(TimeSpan.FromHours(-4));
    }

    /// <summary>
    /// The difference the ShiftBasis enum exists for. Shifting across the spring-forward
    /// boundary: the wall clock reads one day later either way, but the instants differ by
    /// the hour that DST removed.
    /// </summary>
    [Fact]
    public void A_wall_clock_shift_and_an_instant_shift_differ_across_a_dst_boundary()
    {
        var clock = new ClockContext(Eastern);
        var before = new DateTimeOffset(2024, 3, 9, 12, 0, 0, TimeSpan.FromHours(-5));

        DateTimeOffset wall = clock.Shift(before, TimeSpan.FromDays(1), ShiftBasis.WallClock);
        DateTimeOffset instant = clock.Shift(before, TimeSpan.FromDays(1), ShiftBasis.Instant);

        clock.ToWallClock(wall).TimeOfDay.ShouldBe(new TimeSpan(12, 0, 0));
        clock.ToWallClock(instant).TimeOfDay.ShouldBe(new TimeSpan(13, 0, 0));
        wall.ShouldNotBe(instant);
    }

    /// <summary>
    /// "The camera was still set to New York while I was in Tokyo": the digits on the photo
    /// stay, the instant they represent moves.
    /// </summary>
    [Fact]
    public void Reinterpreting_the_zone_keeps_the_reading_and_moves_the_instant()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        var asRecorded = new DateTimeOffset(2024, 6, 15, 14, 0, 0, TimeSpan.FromHours(-4));

        DateTimeOffset corrected = ClockContext.ReinterpretZone(asRecorded, Eastern, tokyo);

        corrected.DateTime.TimeOfDay.ShouldBe(new TimeSpan(14, 0, 0));
        corrected.Offset.ShouldBe(TimeSpan.FromHours(9));
        corrected.UtcDateTime.ShouldNotBe(asRecorded.UtcDateTime);
    }
}
