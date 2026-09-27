// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.FileSystem;
using PaulTechGuy.CN.Journal;
using Shouldly;

namespace PaulTechGuy.CN.Services.Tests;

/// <summary>
/// Removing personal details through the apply path. What matters is what the run REPORTS,
/// because nothing about it can be undone: a file called cleaned must be clean, its own dates
/// must survive, and no road may lead back to an undo that has nothing to restore.
/// </summary>
public class PrivacyStripTests
{
    private static readonly DateTimeOffset Original = new(2019, 4, 2, 11, 30, 15, TimeSpan.Zero);

    private static readonly RunHeader StripHeader =
        new(RunKind.PrivacyStrip, "0.1.0", null, "UTC", "{}", ["test"]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<FilePlan> PlanStripAsync(Workspace ws, params PrivacyCategory[] categories)
    {
        var scanner = new FileScanner(ws.Writer);
        ScannedFile file = null!;

        await foreach (ScannedFile f in scanner.ScanAsync(ws.Files, ScanFilter.Default, Ct))
        {
            file = f;
        }

        return new FilePlan(file, [.. categories.Select(c => new PlannedChange(
            new ChangeTarget.Privacy(c), Before: null, new FieldWrite.Delete(), ChangeStatus.WillChange))]);
    }

    [Fact]
    public async Task A_clean_file_is_stripped_and_keeps_its_own_dates()
    {
        using var ws = new Workspace();
        string path = ws.CreateFile("photo.jpg", Original);

        // Imitate what ExifTool does to the file: rewriting it moves Modified to now.
        var engine = new RecordingGateway { OnWrite = p => File.AppendAllText(p, "rewritten") };

        ApplyOutcome outcome = await ws.ApplyWith(engine).ApplyAsync(
            [await PlanStripAsync(ws, PrivacyCategory.Location, PrivacyCategory.Thumbnail)], StripHeader, null, Ct);

        outcome.Written.ShouldBe(1);
        engine.Strips.Single().ShouldBe([PrivacyCategory.Location, PrivacyCategory.Thumbnail], ignoreOrder: true);

        TimestampSet after = ws.Read(path);
        after.Created.ShouldBe(Original);
        after.Modified.ShouldBe(Original, "a privacy clean must not move every photo's Modified to today");
    }

    [Fact]
    public async Task A_detail_still_there_after_the_strip_is_a_failure_that_names_it()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);

        var engine = new RecordingGateway
        {
            AfterStrip = new PrivacyFindings(
                new Dictionary<PrivacyCategory, IReadOnlyList<string>> { [PrivacyCategory.Location] = ["Panasonic:City"] }.ToFrozenDictionary(),
                HasEmbeddedMedia: false),
        };

        ApplyOutcome outcome = await ws.ApplyWith(engine).ApplyAsync(
            [await PlanStripAsync(ws, PrivacyCategory.Location)], StripHeader, null, Ct);

        outcome.Written.ShouldBe(0, "ExifTool said yes; the file says otherwise, and the file wins");
        outcome.Failed.ShouldBe(1);
        outcome.FailureReasons.Single().ShouldContain("Panasonic:City");
    }

    [Fact]
    public async Task A_strip_is_journaled_as_one_and_cannot_be_reverted()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);
        ApplyService apply = ws.ApplyWith(new RecordingGateway());

        ApplyOutcome outcome = await apply.ApplyAsync([await PlanStripAsync(ws, PrivacyCategory.Location)], StripHeader, null, Ct);

        JournalRun run = ws.Journal.ListRuns().Single();
        run.Kind.ShouldBe(RunKind.PrivacyStrip);
        run.ChangeCount.ShouldBe(0, "no values are journaled - they are what was removed");

        ws.Journal.ReadRevertable(outcome.RunId).ShouldBeEmpty();
        _ = await Should.ThrowAsync<InvalidOperationException>(
            () => apply.RevertAsync(outcome.RunId, StripHeader with { Kind = RunKind.Apply }, cancellationToken: Ct));
        ws.Journal.ListRuns().Count.ShouldBe(1, "refusing must not leave an empty undo run in History");
    }

    [Fact]
    public async Task Privacy_and_date_changes_never_share_a_run()
    {
        using var ws = new Workspace();
        _ = ws.CreateFile("photo.jpg", Original);
        ApplyService apply = ws.ApplyWith(new RecordingGateway());
        FilePlan strip = await PlanStripAsync(ws, PrivacyCategory.Location);

        _ = await Should.ThrowAsync<ArgumentException>(
            () => apply.ApplyAsync([strip], StripHeader with { Kind = RunKind.Apply }, null, Ct));
    }
}
