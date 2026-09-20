// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.FileSystem.Tests;

/// <summary>
/// The round trip the whole undo story rests on, exercised against real files.
///
/// These live in the FileSystem suite rather than the Journal one deliberately: the journal
/// tests prove the bookkeeping in isolation, and these prove that what the bookkeeping
/// records actually puts a real file back where it was.
/// </summary>
public class ApplyRoundTripTests
{
    private static readonly DateTimeOffset Original = new(2019, 4, 2, 11, 30, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset Applied = new(2024, 3, 15, 14, 25, 30, TimeSpan.Zero);

    [Fact]
    public void A_write_then_a_restore_returns_the_file_to_its_original_dates()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile("photo.jpg");
        var writer = new FileTimeWriter();

        // As found.
        _ = writer.Write(file, new TimestampSet(Original, Original, null, null), isDirectory: false);
        writer.TryRead(file, false, out TimestampSet before, out _).ShouldBeTrue();

        // As a run would leave it.
        _ = writer.Write(file, new TimestampSet(Applied, Applied, null, null), isDirectory: false);
        writer.TryRead(file, false, out TimestampSet after, out _).ShouldBeTrue();
        after.Created!.Value.ShouldBe(Applied);

        // As an undo would put it back, from the recorded prior values.
        _ = writer.Write(file, new TimestampSet(before.Created, before.Modified, null, null), isDirectory: false);
        writer.TryRead(file, false, out TimestampSet restored, out _).ShouldBeTrue();

        restored.Created!.Value.ShouldBe(Original);
        restored.Modified!.Value.ShouldBe(Original);
    }

    /// <summary>
    /// Writing only Created must leave Modified exactly as it was. A restore relies on this:
    /// putting one field back must not disturb a field the run never touched.
    /// </summary>
    [Fact]
    public void Restoring_one_field_does_not_disturb_the_others()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile("photo.jpg");
        var writer = new FileTimeWriter();

        var modified = new DateTimeOffset(2021, 7, 8, 9, 10, 11, TimeSpan.Zero);
        _ = writer.Write(file, new TimestampSet(Original, modified, null, null), isDirectory: false);

        _ = writer.Write(file, new TimestampSet(Applied, null, null, null), isDirectory: false);

        writer.TryRead(file, false, out TimestampSet after, out _).ShouldBeTrue();
        after.Created!.Value.ShouldBe(Applied);
        after.Modified!.Value.ShouldBe(modified);
    }

    /// <summary>
    /// The drift check compares what is on disk now against what the run left there. If
    /// something else edited the file since, the values will not match and the undo must
    /// leave it alone.
    /// </summary>
    [Fact]
    public void A_file_edited_after_the_run_no_longer_holds_what_the_run_wrote()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile("photo.jpg");
        var writer = new FileTimeWriter();

        _ = writer.Write(file, new TimestampSet(Applied, Applied, null, null), isDirectory: false);

        // Something else comes along afterwards.
        var somethingElse = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _ = writer.Write(file, new TimestampSet(somethingElse, null, null, null), isDirectory: false);

        writer.TryRead(file, false, out TimestampSet now, out _).ShouldBeTrue();

        now.Created!.Value.ShouldNotBe(Applied);
        now.Created!.Value.ShouldBe(somethingElse);
    }

    [Fact]
    public void Reading_a_file_that_has_since_been_deleted_fails_rather_than_throwing()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile("gone.jpg");
        var writer = new FileTimeWriter();

        File.Delete(file);

        writer.TryRead(file, false, out _, out _).ShouldBeFalse();
    }

    /// <summary>
    /// A run writes all four; the undo has to put all four back, ChangeTime included, or
    /// the file is only mostly restored.
    /// </summary>
    [Fact]
    public void All_four_timestamps_survive_a_full_round_trip()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile("photo.jpg");
        var writer = new FileTimeWriter();

        var before = new TimestampSet(
            Original,
            Original.AddHours(1),
            Original.AddHours(2),
            Original.AddHours(3));

        _ = writer.Write(file, before, isDirectory: false);
        writer.TryRead(file, false, out TimestampSet captured, out _).ShouldBeTrue();

        _ = writer.Write(file, new TimestampSet(Applied, Applied, Applied, Applied), isDirectory: false);
        _ = writer.Write(file, captured, isDirectory: false);

        writer.TryRead(file, false, out TimestampSet restored, out _).ShouldBeTrue();

        restored.Created!.Value.ShouldBe(before.Created!.Value);
        restored.Modified!.Value.ShouldBe(before.Modified!.Value);
        restored.Accessed!.Value.ShouldBe(before.Accessed!.Value);
        restored.Changed!.Value.ShouldBe(before.Changed!.Value);
    }
}
