// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.FileSystem.Tests;

/// <summary>A real directory under TEMP, removed when the test finishes.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chronora-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public string CreateFile(string name = "sample.jpg", string content = "x")
    {
        string full = System.IO.Path.Combine(this.Path, name);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDirectory(string name)
    {
        string full = System.IO.Path.Combine(this.Path, name);
        _ = Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.Path))
            {
                // Read-only files are deliberately created by some tests.
                foreach (string file in Directory.EnumerateFiles(this.Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(this.Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}

public class FileTimeWriterTests
{
    private static readonly DateTimeOffset Created = new(2019, 4, 2, 11, 30, 15, TimeSpan.Zero);
    private static readonly DateTimeOffset Modified = new(2020, 5, 3, 12, 31, 16, TimeSpan.Zero);
    private static readonly DateTimeOffset Accessed = new(2021, 6, 4, 13, 32, 17, TimeSpan.Zero);
    private static readonly DateTimeOffset Changed = new(2022, 7, 5, 14, 33, 18, TimeSpan.Zero);

    /// <summary>
    /// The claim the whole FileSystem layer rests on: one call sets all four, ChangeTime
    /// included, with no ordering rule needed. If this fails the design is wrong.
    /// </summary>
    [Fact]
    public void All_four_timestamps_round_trip_through_one_call()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile();
        var writer = new FileTimeWriter();

        WriteResult result = writer.Write(file, new TimestampSet(Created, Modified, Accessed, Changed), isDirectory: false);

        result.Succeeded.ShouldBeTrue(result.Detail);

        writer.TryRead(file, isDirectory: false, out TimestampSet read, out _).ShouldBeTrue();

        read.Created!.Value.ShouldBe(Created);
        read.Modified!.Value.ShouldBe(Modified);
        read.Accessed!.Value.ShouldBe(Accessed);
        read.Changed!.Value.ShouldBe(Changed);
    }

    /// <summary>
    /// A null means "leave this one alone", which is what lets a rule touch Modified without
    /// disturbing the other three.
    /// </summary>
    [Fact]
    public void A_null_leaves_that_timestamp_untouched()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile();
        var writer = new FileTimeWriter();

        _ = writer.Write(file, new TimestampSet(Created, Modified, Accessed, Changed), isDirectory: false);
        _ = writer.Write(file, new TimestampSet(null, Modified.AddYears(1), null, null), isDirectory: false);

        writer.TryRead(file, isDirectory: false, out TimestampSet read, out _).ShouldBeTrue();

        read.Created!.Value.ShouldBe(Created);
        read.Modified!.Value.ShouldBe(Modified.AddYears(1));
        read.Accessed!.Value.ShouldBe(Accessed);
    }

    /// <summary>
    /// Setting the other three updates ChangeTime as a side effect, so writing it in the same
    /// call is the only way to control it. This is the test that would have caught the
    /// original "write ChangeTime last" design being unnecessary.
    /// </summary>
    [Fact]
    public void Change_time_survives_being_written_alongside_the_others()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile();
        var writer = new FileTimeWriter();

        WriteResult result = writer.Write(
            file,
            new TimestampSet(Created, Modified, Accessed, Changed),
            isDirectory: false,
            verify: true);

        result.Succeeded.ShouldBeTrue(result.Detail);

        writer.TryRead(file, isDirectory: false, out TimestampSet read, out _).ShouldBeTrue();
        read.Changed!.Value.ShouldBe(Changed);
    }

    [Fact]
    public void A_directory_can_be_stamped_too()
    {
        using var temp = new TempFolder();
        string dir = temp.CreateDirectory("album");
        var writer = new FileTimeWriter();

        WriteResult result = writer.Write(dir, new TimestampSet(Created, Modified, null, null), isDirectory: true);

        result.Succeeded.ShouldBeTrue(result.Detail);

        writer.TryRead(dir, isDirectory: true, out TimestampSet read, out _).ShouldBeTrue();
        read.Created!.Value.ShouldBe(Created);
        read.Modified!.Value.ShouldBe(Modified);
    }

    /// <summary>
    /// Read-only is cleared for the write and put back. Refusing would decline work the app
    /// can plainly do, and a great many photos in a real library carry the flag.
    /// </summary>
    [Fact]
    public void A_read_only_file_is_written_and_left_read_only()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile();
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var writer = new FileTimeWriter();
        WriteResult result = writer.Write(file, new TimestampSet(Created, Modified, null, null), isDirectory: false);

        result.Succeeded.ShouldBeTrue(result.Detail);

        writer.TryRead(file, isDirectory: false, out TimestampSet read, out FileAttributes attributes).ShouldBeTrue();
        read.Created!.Value.ShouldBe(Created);
        attributes.HasFlag(FileAttributes.ReadOnly).ShouldBeTrue("the flag should have been put back");
    }

    /// <summary>
    /// A file another process holds open must still be stampable. FILE_WRITE_ATTRIBUTES is a
    /// far weaker right than GENERIC_WRITE, which is exactly why it is what we ask for.
    /// </summary>
    [Fact]
    public void A_file_held_open_by_someone_else_can_still_be_stamped()
    {
        using var temp = new TempFolder();
        string file = temp.CreateFile();

        using (FileStream holder = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var writer = new FileTimeWriter();
            WriteResult result = writer.Write(file, new TimestampSet(Created, null, null, null), isDirectory: false);

            result.Succeeded.ShouldBeTrue(result.Detail);
        }
    }

    [Fact]
    public void Writing_a_file_that_does_not_exist_reports_rather_than_throws()
    {
        using var temp = new TempFolder();
        var writer = new FileTimeWriter();

        WriteResult result = writer.Write(
            Path.Combine(temp.Path, "missing.jpg"),
            new TimestampSet(Created, null, null, null),
            isDirectory: false);

        result.Succeeded.ShouldBeFalse();
    }
}

public class LongPathTests
{
    [Fact]
    public void A_short_path_is_left_alone()
    {
        string result = LongPath.ToExtended(@"C:\photos\a.jpg");

        result.ShouldBe(@"C:\photos\a.jpg");
    }

    [Fact]
    public void A_long_path_gets_the_extended_prefix()
    {
        string longPath = @"C:\" + new string('a', 300) + @"\b.jpg";

        LongPath.ToExtended(longPath).ShouldStartWith(@"\\?\C:\");
    }

    [Fact]
    public void An_already_extended_path_is_not_prefixed_twice()
    {
        string once = @"\\?\C:\" + new string('a', 300);

        LongPath.ToExtended(once).ShouldBe(once);
    }

    /// <summary>
    /// The prefix disables normalisation, so a relative or dotted path has to be resolved
    /// before it is applied or the call fails on a path that plainly exists.
    /// </summary>
    [Fact]
    public void A_dotted_segment_is_normalised_before_the_prefix_is_applied()
    {
        string result = LongPath.ToExtended(@"C:\photos\..\photos\a.jpg");

        result.ShouldBe(@"C:\photos\a.jpg");
        result.ShouldNotContain("..");
    }

    [Fact]
    public void The_display_form_strips_the_prefix_again()
    {
        LongPath.ToDisplay(@"\\?\C:\photos\a.jpg").ShouldBe(@"C:\photos\a.jpg");
        LongPath.ToDisplay(@"\\?\UNC\server\share\a.jpg").ShouldBe(@"\\server\share\a.jpg");
    }

    [Fact]
    public void A_display_path_without_a_prefix_is_unchanged()
    {
        LongPath.ToDisplay(@"C:\photos\a.jpg").ShouldBe(@"C:\photos\a.jpg");
    }
}
