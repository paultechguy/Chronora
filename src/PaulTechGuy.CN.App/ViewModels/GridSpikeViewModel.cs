// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace PaulTechGuy.CN.App.ViewModels;

/// <summary>
/// One grid row: the SIMPLE shape, a materialised observable object per file.
///
/// The plan asserted that 50,000 of these must never be materialised and that a lazy
/// IItemsRangeInfo source with a recycling pool was required. Measured rather than assumed,
/// 50,000 rows cost 14 ms and 7.6 MB, so that machinery is not built.
/// </summary>
public sealed partial class SpikeRow : ObservableObject
{
    public SpikeRow(string name, DateTimeOffset before)
    {
        this.Name = name;
        this.Before = before;
        this.After = before;
        this.Diff = string.Empty;
        this.Status = string.Empty;
    }

    public string Name { get; }

    public DateTimeOffset Before { get; }

    /// <summary>
    /// What the row will become. Recompute sets only this struct; the template formats it
    /// through FormatDiff, so a string is produced for the dozen or so realised rows rather
    /// than for all 50,000.
    /// </summary>
    [ObservableProperty]
    public partial DateTimeOffset After { get; set; }

    /// <summary>
    /// The eager alternative, kept only so the benchmark can price it. Formatting every row
    /// up front costs 50,000 allocations per keystroke, which is what makes the naive
    /// version allocation-bound rather than CPU-bound.
    /// </summary>
    [ObservableProperty]
    public partial string Diff { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Called from x:Bind, so it runs only for rows actually on screen.</summary>
    public static string FormatDiff(DateTimeOffset before, DateTimeOffset after) =>
        string.Create(CultureInfo.CurrentCulture, $"{before:yyyy-MM-dd HH:mm} → {after:yyyy-MM-dd HH:mm}");
}

/// <summary>
/// Throwaway spike closing milestone 1.
///
/// Does a uniform two-line ListView row survive 50,000 items, and what does a full
/// re-evaluation cost on every keystroke? Measured at two sizes and three strategies,
/// because one data point from fast hardware says nothing about a budget laptop.
/// </summary>
public sealed partial class GridSpikeViewModel : ObservableObject
{
    private const int DisplayRowCount = 50_000;
    private const int StressRowCount = 200_000;

    private static readonly string[] Extensions = [".jpg", ".heic", ".mp4", ".nef", ".png", ".txt"];

    private int _shiftDays;

    public GridSpikeViewModel()
    {
        var sw = Stopwatch.StartNew();
        long before = GC.GetTotalMemory(forceFullCollection: true);

        this.Rows = BuildRows(DisplayRowCount);

        long after = GC.GetTotalMemory(forceFullCollection: true);
        sw.Stop();

        this.BuildReport = string.Create(
            CultureInfo.InvariantCulture,
            $"Built {DisplayRowCount:N0} rows in {sw.ElapsedMilliseconds:N0} ms · {(after - before) / (1024.0 * 1024.0):N1} MB heap");

        Log.Information("SPIKE build: {Report}", this.BuildReport);

        this.Recompute();
        this.Benchmark();
    }

    public IReadOnlyList<SpikeRow> Rows { get; }

    [ObservableProperty]
    public partial string BuildReport { get; set; }

    [ObservableProperty]
    public partial string RecomputeReport { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BenchmarkReport { get; set; } = "Benchmark not run.";

    /// <summary>Simulates one option change on the live, bound list.</summary>
    [RelayCommand]
    public void Recompute()
    {
        this._shiftDays++;

        var sw = Stopwatch.StartNew();
        ApplyValuesOnly(this.Rows, this._shiftDays);
        sw.Stop();

        this.RecomputeReport = string.Create(
            CultureInfo.InvariantCulture,
            $"Recompute #{this._shiftDays}: {this.Rows.Count:N0} bound rows in {sw.ElapsedMilliseconds:N0} ms (budget 50 ms)");

        Log.Information("SPIKE {Report}", this.RecomputeReport);
    }

    /// <summary>
    /// Prices three strategies at two sizes. The interesting comparison is eager-serial
    /// against values-only: a large gap means the cost was never the arithmetic, it was
    /// formatting strings nobody is looking at.
    /// </summary>
    [RelayCommand]
    public void Benchmark()
    {
        var lines = new List<string>();
        int[] sizes = [DisplayRowCount, StressRowCount];

        foreach (int count in sizes)
        {
            IReadOnlyList<SpikeRow> rows = count == DisplayRowCount ? this.Rows : BuildRows(count);

            // Warm up so the first timing does not also pay for JIT.
            ApplyEagerSerial(rows, 1);
            ApplyEagerParallel(rows, 1);
            ApplyValuesOnly(rows, 1);

            var sw = Stopwatch.StartNew();
            ApplyEagerSerial(rows, 2);
            long serial = sw.ElapsedMilliseconds;

            sw.Restart();
            ApplyEagerParallel(rows, 3);
            long parallel = sw.ElapsedMilliseconds;

            sw.Restart();
            ApplyValuesOnly(rows, 4);
            long valuesOnly = sw.ElapsedMilliseconds;

            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{count,7:N0} rows: eager-serial {serial,4:N0} ms, eager-parallel {parallel,4:N0} ms, values-only {valuesOnly,4:N0} ms"));
        }

        this.BenchmarkReport = string.Join(Environment.NewLine, lines);

        foreach (string line in lines)
        {
            Log.Information("SPIKE bench: {Line}", line);
        }

        Log.Information("SPIKE bench: cores={Cores}", Environment.ProcessorCount);
    }

    private static List<SpikeRow> BuildRows(int count)
    {
        var rows = new List<SpikeRow>(count);
        var seed = new DateTimeOffset(2019, 1, 1, 14, 25, 30, TimeSpan.Zero);
        var rng = new Random(20260919);

        for (int i = 0; i < count; i++)
        {
            string ext = Extensions[i % Extensions.Length];
            string name = string.Create(
                CultureInfo.InvariantCulture,
                $"IMG_{20240000 + (i % 9999):D8}_{i:D6}{ext}");

            rows.Add(new SpikeRow(name, seed.AddMinutes(rng.Next(0, 3_000_000))));
        }

        return rows;
    }

    /// <summary>The chosen strategy: set the value, let the template format what is visible.</summary>
    private static void ApplyValuesOnly(IReadOnlyList<SpikeRow> rows, int shiftDays)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            SpikeRow row = rows[i];
            row.After = row.Before.AddDays(shiftDays);
            row.Status = row.After.Year < 1990 ? "⚠ odd" : string.Empty;
        }
    }

    private static void ApplyEagerSerial(IReadOnlyList<SpikeRow> rows, int shiftDays)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            EvaluateEager(rows[i], shiftDays);
        }
    }

    private static void ApplyEagerParallel(IReadOnlyList<SpikeRow> rows, int shiftDays) =>
        Parallel.For(0, rows.Count, i => EvaluateEager(rows[i], shiftDays));

    private static void EvaluateEager(SpikeRow row, int shiftDays)
    {
        DateTimeOffset after = row.Before.AddDays(shiftDays);

        row.Diff = string.Create(
            CultureInfo.CurrentCulture,
            $"{row.Before:yyyy-MM-dd HH:mm} → {after:yyyy-MM-dd HH:mm}");

        row.Status = after.Year < 1990 ? "⚠ odd" : string.Empty;
    }
}
