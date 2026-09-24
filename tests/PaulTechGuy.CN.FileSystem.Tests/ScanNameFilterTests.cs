// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;
using Shouldly;

namespace PaulTechGuy.CN.FileSystem.Tests;

/// <summary>
/// The four name filters behind "More filters…": which files and folders a scan reads.
/// </summary>
public class ScanNameFilterTests
{
    private static async Task<List<string>> ScanAsync(string root, ScanFilter filter)
    {
        var results = new List<string>();

        await foreach (ScannedFile file in new FileScanner().ScanAsync(root, filter, TestContext.Current.CancellationToken))
        {
            results.Add(Path.GetRelativePath(root, file.FullPath) + (file.IsDirectory ? "\\" : string.Empty));
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>
    /// Pruned, not walked and then filtered. From the files that come back the two look
    /// identical; only pruning saves the walk through a Synology share's thousands of
    /// @eaDir folders, so the decision itself is tested.
    /// </summary>
    [Fact]
    public async Task An_excluded_folder_is_never_entered()
    {
        ScanFilter filter = ScanFilter.Default with { ExcludeFolders = ["@eaDir"] };

        FileScanner.ShouldRecurse("@eaDir", FileAttributes.Directory, filter).ShouldBeFalse();
        FileScanner.ShouldRecurse("@EADIR", FileAttributes.Directory, filter).ShouldBeFalse("names match ignoring case");
        FileScanner.ShouldRecurse("2019", FileAttributes.Directory, filter).ShouldBeTrue();
        FileScanner.ShouldRecurse("link", FileAttributes.Directory | FileAttributes.ReparsePoint, ScanFilter.Default)
            .ShouldBeFalse("the junction guard still comes first");

        using var temp = new TempFolder();
        _ = temp.CreateFile("a.jpg");
        _ = temp.CreateFile(Path.Combine("@eaDir", "a.jpg", "SYNOPHOTO_THUMB_M.jpg"));

        (await ScanAsync(temp.Path, filter)).ShouldBe(["a.jpg"]);
    }

    /// <summary>
    /// "Match anywhere, subtree follows" - Paul's call over Beyond Compare's literal rule,
    /// which would lose 2019\Jan because Jan is not itself named 2019*. Files sitting
    /// directly in the dropped folder are always in.
    /// </summary>
    [Fact]
    public async Task Include_folders_takes_the_whole_subtree_of_a_match_at_any_depth()
    {
        using var temp = new TempFolder();
        _ = temp.CreateFile("x.jpg");
        _ = temp.CreateFile(Path.Combine("2019", "Jan", "a.jpg"));
        _ = temp.CreateFile(Path.Combine("2020", "b.jpg"));
        _ = temp.CreateFile(Path.Combine("Archive", "2019", "c.jpg"));

        ScanFilter filter = ScanFilter.Default with { IncludeFolders = ["2019*"] };

        (await ScanAsync(temp.Path, filter)).ShouldBe(["2019\\Jan\\a.jpg", "Archive\\2019\\c.jpg", "x.jpg"]);

        // Folder rows follow the same rule: 2019\Jan is a row because 2019 is, Archive is
        // not a row though it had to be walked to find Archive\2019.
        List<string> withRows = await ScanAsync(temp.Path, filter with { IncludeDirectories = true });

        withRows.Where(p => p.EndsWith('\\')).ShouldBe(["2019\\", "2019\\Jan\\", "Archive\\2019\\"]);
    }

    [Fact]
    public async Task Exclude_beats_include_and_the_dropped_root_is_never_excluded()
    {
        using var temp = new TempFolder();
        string root = temp.CreateDirectory("@eaDir");
        _ = temp.CreateFile(Path.Combine("@eaDir", "keep.jpg"));
        _ = temp.CreateFile(Path.Combine("@eaDir", "edit.aae"));
        _ = temp.CreateFile(Path.Combine("@eaDir", "2019", "skip.jpg"));

        ScanFilter filter = ScanFilter.Default with
        {
            Patterns = ["*.jpg", "*.aae"],
            ExcludeFiles = ["*.aae"],
            IncludeFolders = ["2019"],
            ExcludeFolders = ["@eaDir", "2019"],
        };

        (await ScanAsync(root, filter)).ShouldBe(["keep.jpg"], "dropping @eaDir on purpose means it");
    }

    /// <summary>
    /// The slicing trap: the enumerator trims a trailing separator from its root, except at
    /// a drive root, so slicing by the string the caller passed was off by one - and threw
    /// for a file sitting in a root given as "...\photos\".
    /// </summary>
    [Fact]
    public async Task A_root_given_with_a_trailing_slash_is_read_the_same()
    {
        using var temp = new TempFolder();
        _ = temp.CreateFile("x.jpg");
        _ = temp.CreateFile(Path.Combine("2019", "a.jpg"));
        _ = temp.CreateFile(Path.Combine("2020", "b.jpg"));

        ScanFilter filter = ScanFilter.Default with { IncludeFolders = ["2019"] };

        (await ScanAsync(temp.Path + "\\", filter)).ShouldBe(["2019\\a.jpg", "x.jpg"]);

        // What a drive root hands over: no leading separator.
        FileScanner.IsWithinIncludedFolders("2019", ["2019"]).ShouldBeTrue();
        FileScanner.IsWithinIncludedFolders("\\Archive\\2019\\Jan", ["2019"]).ShouldBeTrue();
        FileScanner.IsWithinIncludedFolders("\\2020", ["2019"]).ShouldBeFalse();
        FileScanner.IsWithinIncludedFolders(string.Empty, ["2019"]).ShouldBeTrue("the root's own files are always in");
    }

    /// <summary>With recursion off every file is in the root, and Include folders is inert.</summary>
    [Fact]
    public async Task Include_folders_does_nothing_without_subfolders()
    {
        using var temp = new TempFolder();
        _ = temp.CreateFile("x.jpg");
        _ = temp.CreateDirectory("2020");

        ScanFilter filter = ScanFilter.Default with { Recurse = false, IncludeFolders = ["2019"], IncludeDirectories = true };

        (await ScanAsync(temp.Path, filter)).ShouldBe(["2020\\", "x.jpg"]);
    }
}
