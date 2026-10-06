// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FootprintColumn.cs — #1048: resident memory footprint column for --bench.
//
// MemoryDiagnoser's "Allocated" is per-op: it shows what one Calculate()
// allocates. The calculator's big per-pixel buffers (17 pinned arrays on the
// POH, ~68 B/px) plus lazily built tables (reference orbit, SA, BLA) are
// allocated at construction / first frame — i.e. in [GlobalSetup] — so no
// standard column shows how much memory a calculator actually holds.
//
// The benchmark measures the settled managed heap around construct + one warm
// frame and records it here; the column reads it back at summary time. This
// hand-off works only because the harness uses InProcessEmitToolchain
// (setup and summary share one process). Under an out-of-process toolchain
// nothing is recorded and the column prints "-".
//
// FF has no native heap allocations on this path (no NativeMemory /
// AllocHGlobal; GPU paths are off under --bench), so the managed heap is the
// whole story here. See #1048.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace FracturingFog.Benchmarks;

public static class FootprintRegistry
{
    private static readonly ConcurrentDictionary<string, long> s_bytes = new();

    /// <summary>Stable key for one benchmark case: its parameters sorted by
    /// name, as "Name=Value" pairs. Used by both the recording and the
    /// reading side so they agree without depending on declaration order.</summary>
    public static string Key(IEnumerable<(string Name, object? Value)> parameters) =>
        string.Join(";", parameters
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={Convert.ToString(p.Value, CultureInfo.InvariantCulture)}"));

    public static void Record(string key, long bytes) => s_bytes[key] = bytes;

    public static bool TryGet(string key, out long bytes) => s_bytes.TryGetValue(key, out bytes);

    /// <summary>Managed heap size after a forced, blocking full collection
    /// (includes SOH, LOH and the pinned object heap).</summary>
    public static long SettledHeapBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}

public sealed class FootprintColumn : IColumn
{
    public string Id => nameof(FootprintColumn);
    public string ColumnName => "Footprint";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Metric;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Size;
    public string Legend =>
        "Managed memory the calculator retains after construction + one warm frame " +
        "(settled heap delta around GlobalSetup; in-process toolchain only)";

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, summary.Style);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        string key = FootprintRegistry.Key(
            benchmarkCase.Parameters.Items.Select(p => (p.Name, (object?)p.Value)));
        if (!FootprintRegistry.TryGet(key, out long bytes))
            return "-";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    public override string ToString() => ColumnName;
}
