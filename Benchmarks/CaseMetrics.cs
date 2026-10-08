// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// CaseMetrics.cs — per-benchmark-case metrics measured in [GlobalSetup] and
// shown as extra summary columns.
//
// BenchmarkDotNet's own diagnosers only see what happens inside the timed
// method. Some numbers worth tracking are taken once per case during setup:
//   • Footprint (#1048) — managed memory a calculator retains after
//     construction + a warm frame. MemoryDiagnoser's per-op Allocated can't
//     show it: the 17 pinned per-pixel buffers (~68 B/px) and the lazily built
//     ref-orbit / SA / BLA state are all allocated in setup.
//   • DeviceAlloc/op, Device (#1162) — ILGPU device bytes per frame and the
//     accelerator that ran it, for the GPU calculator bench.
//   • Resident (#1166) — device memory a D3D11 / Vulkan kernel holds after a
//     warm frame (persistent, frame-sized buffers).
//
// Setup records a formatted value keyed by (metric, case parameters); the
// column reads it back at summary time. This hand-off works only because the
// harness uses InProcessEmitToolchain (setup and summary share one process).
// Under an out-of-process toolchain nothing is recorded and columns print "-".

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace FracturingFog.Benchmarks;

public static class CaseMetrics
{
    public const string Footprint = "Footprint";
    public const string DeviceAllocPerOp = "DeviceAlloc/op";
    public const string Device = "Device";
    public const string Resident = "Resident";

    private static readonly ConcurrentDictionary<(string Metric, string Key), string> s_values = new();

    /// <summary>Stable key for one benchmark case: its parameters sorted by
    /// name, as "Name=Value" pairs. Used by both the recording and the
    /// reading side so they agree without depending on declaration order.</summary>
    public static string Key(IEnumerable<(string Name, object? Value)> parameters) =>
        string.Join(";", parameters
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={Convert.ToString(p.Value, CultureInfo.InvariantCulture)}"));

    public static void Record(string metric, string key, string value) => s_values[(metric, key)] = value;

    public static bool TryGet(string metric, string key, out string value) =>
        s_values.TryGetValue((metric, key), out value!);

    public static string FormatMegabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>Managed heap size after a forced, blocking full collection
    /// (includes SOH, LOH and the pinned object heap).</summary>
    public static long SettledHeapBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}

public sealed class CaseMetricColumn : IColumn
{
    private readonly string _metric;

    public CaseMetricColumn(string metric, string legend, bool isNumeric, UnitType unitType)
    {
        _metric = metric;
        Legend = legend;
        IsNumeric = isNumeric;
        UnitType = unitType;
    }

    public string Id => nameof(CaseMetricColumn) + "." + _metric;
    public string ColumnName => _metric;
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Metric;
    public int PriorityInCategory => 0;
    public bool IsNumeric { get; }
    public UnitType UnitType { get; }
    public string Legend { get; }

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, summary.Style);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        string key = CaseMetrics.Key(
            benchmarkCase.Parameters.Items.Select(p => (p.Name, (object?)p.Value)));
        return CaseMetrics.TryGet(_metric, key, out string value) ? value : "-";
    }

    public override string ToString() => ColumnName;
}
