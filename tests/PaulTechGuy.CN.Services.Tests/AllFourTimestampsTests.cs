// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Journal;
using Shouldly;

namespace PaulTechGuy.CN.Services.Tests;

/// <summary>
/// Setting every file date at once, and getting the same moment in all of them.
///
/// Reported from use: the dates all moved to the chosen day and the TIMES did not match
/// each other. A run that sets four fields to one value and lands four different values is
/// the preview telling a lie, which is the one thing this app must not do.
/// </summary>
public class AllFourTimestampsTests
{
    private static readonly DateTimeOffset Original = new(2019, 4, 2, 11, 30, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset Target = new(2024, 3, 15, 14, 25, 30, TimeSpan.Zero);

    private static readonly RunHeader Header =
        new(RunKind.Apply, "0.1.0", null, "UTC", "{}", ["test"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The plan is where a divergence would start, so it is checked on its own.</summary>
    [Fact]
    public async Task Every_targeted_field_is_planned_for_the_same_instant()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("a.txt", Original);

        FilePlan plan = await ws.PlanAsync(
            Target,
            [DateField.FileCreated, DateField.FileModified, DateField.FileAccessed, DateField.FileChanged]);

        List<DateTimeOffset?> after = [.. plan.Changes.Where(c => c.WillWrite).Select(c => c.AfterDate)];

        after.Count.ShouldBe(4);
        after.Distinct().Count().ShouldBe(1, "one chosen date means one planned value, to the second");
    }

    /// <summary>
    /// And then what actually lands on disk, which is what was reported.
    /// </summary>
    [Fact]
    public async Task Every_targeted_field_lands_on_the_same_instant()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("a.txt", Original);

        FilePlan plan = await ws.PlanAsync(
            Target,
            [DateField.FileCreated, DateField.FileModified, DateField.FileAccessed, DateField.FileChanged]);

        ApplyOutcome applied = await ws.Apply.ApplyAsync([plan], Header, null, Ct);

        applied.Written.ShouldBe(1);

        TimestampSet times = ws.Read(path);

        times.Created.ShouldBe(Target);
        times.Modified.ShouldBe(Target);
        times.Accessed.ShouldBe(Target);

        // The fourth. NTFS updates ChangeTime whenever a file's metadata changes, and
        // setting the timestamps IS a metadata change - so whether the value asked for
        // survives its own write is a question about the filesystem, not about the code,
        // and it is worth knowing the answer rather than assuming one.
        times.Changed.ShouldBe(Target, "ChangeTime was asked for and should have stuck");
    }

    /// <summary>
    /// Reading a file is entitled to move its Accessed time, on a volume where last-access
    /// updates are switched on. Chronora reads every file it shows - to scan it, and again
    /// to make a thumbnail - so a value that was written correctly can still be different
    /// by the time somebody looks at it in Explorer.
    ///
    /// This does not assert which way it goes, because it differs by machine. It records
    /// what this one does, so a report of "the times do not match" can be checked against
    /// it instead of guessed at.
    /// </summary>
    [Fact]
    public async Task What_reading_a_file_afterwards_does_to_its_accessed_time()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("c.txt", Original);

        FilePlan plan = await ws.PlanAsync(
            Target,
            [DateField.FileCreated, DateField.FileModified, DateField.FileAccessed]);

        _ = await ws.Apply.ApplyAsync([plan], Header, null, Ct);

        TimestampSet straightAfter = ws.Read(path);

        _ = await File.ReadAllTextAsync(path, Ct);

        TimestampSet afterReading = ws.Read(path);

        straightAfter.Accessed.ShouldBe(Target, "the write itself must land");

        // Recorded, not required. If this machine bumps it, so will Explorer.
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Accessed after a read: {afterReading.Accessed:O} (written {Target:O})");
    }

    /// <summary>
    /// The three-field case, which is what the pane offers without opening Advanced and so
    /// is what most runs actually look like.
    /// </summary>
    [Fact]
    public async Task The_three_visible_dates_land_together()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("b.txt", Original);

        FilePlan plan = await ws.PlanAsync(
            Target,
            [DateField.FileCreated, DateField.FileModified, DateField.FileAccessed]);

        _ = await ws.Apply.ApplyAsync([plan], Header, null, Ct);

        TimestampSet times = ws.Read(path);

        times.Created.ShouldBe(Target);
        times.Modified.ShouldBe(Target);
        times.Accessed.ShouldBe(Target);
    }
}
