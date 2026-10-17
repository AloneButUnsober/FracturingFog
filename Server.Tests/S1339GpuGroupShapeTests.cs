// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1339 — compute thread-group shapes, tuned per device and kernel slot
// (Engine/Interefaces/GpuGroupShape.cs; D3D11 is checked by --gpugroupshapeprobe).
// Checked:
//   • the shape: parsing, the numthreads rewrite (every entry point, the default
//     source untouched), and group counts that cover every pixel (against a
//     brute-force count of the pixels a dispatch reaches);
//   • the setting (FF_GPU_GROUP_SHAPE): auto / off / one shape / per slot;
//   • the tuner, against a fake timer whose costs the test sets: the
//     measured-fastest shape wins, the default keeps a tie inside MinGain, the
//     best of the repeats counts (not the warm-up), and a shape that fails to
//     build or time is skipped;
//   • the store: a winner is reused by the next process (the file re-read)
//     without timing anything; a different kernel source, device or an
//     unsupported stored shape tunes again; an override never tunes;
//   • band heights for any group height tile the frame, each row once;
//   • on whatever Vulkan device the host has (skipped without one): every
//     candidate shape gives the same bytes as 8×8 for the SP kernel (no palette
//     and a GPU orbit palette, on a frame whose sides aren't a multiple of any
//     group) and for every banded perturbation program, with the buffers dirtied
//     by another view first; bands follow the group height (packet counts);
//     tuning end to end picks the fastest of its own timings, stores it, and a
//     second kernel reuses it without tuning.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FracturingFog.Abstractions;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1339GpuGroupShapeTests : IDisposable
{
    public void Dispose() => GpuGroupShapes.ResetSettingForThread();

    // ── the shape ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("8x8", 8, 8)]
    [InlineData("16x8", 16, 8)]
    [InlineData(" 32X4 ", 32, 4)]
    [InlineData("1024x1", 1024, 1)]
    public void Parses(string text, int x, int y)
    {
        Assert.True(GpuGroupShape.TryParse(text, out var s));
        Assert.Equal(new GpuGroupShape(x, y), s);
        Assert.True(GpuGroupShape.TryParse(s.ToString(), out var back));
        Assert.Equal(s, back);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("16")]
    [InlineData("16x")]
    [InlineData("0x8")]
    [InlineData("-8x8")]
    [InlineData("64x32")]   // 2048 threads
    [InlineData("2048x1")]
    [InlineData("8x8x1")]
    [InlineData("auto")]
    public void Rejects(string? text) => Assert.False(GpuGroupShape.TryParse(text, out _));

    [Fact]
    public void Apply_Rewrites_Every_Entry_Point_And_Leaves_The_Default_Untouched()
    {
        string src = MandelbrotKernelSource.BuildPerturbFloatExp();   // two entry points
        int declared = Count(src, GpuGroupShape.SourceAttribute);
        Assert.True(declared >= 2, "precondition: a source with several entry points");
        Assert.Same(src, GpuGroupShape.Default.Apply(src));
        string s = new GpuGroupShape(32, 4).Apply(src);
        Assert.Equal(0, Count(s, GpuGroupShape.SourceAttribute));
        Assert.Equal(declared, Count(s, "[numthreads(32, 4, 1)]"));
        Assert.Equal(src, s.Replace("[numthreads(32, 4, 1)]", GpuGroupShape.SourceAttribute));
        Assert.Throws<ArgumentException>(() => new GpuGroupShape(16, 8).Apply("void CS() {}"));
    }

    private static int Count(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Group_Counts_Reach_Every_Pixel_And_No_Spare_Group()
    {
        foreach (var shape in GpuGroupShapes.Candidates.Append(new GpuGroupShape(7, 3)))
            foreach (int size in new[] { 1, 2, 7, 8, 9, 31, 32, 33, 100, 1079, 1080, 1920 })
            {
                uint gx = shape.GroupsX(size), gy = shape.GroupsY(size);
                // Brute force: the threads of gx groups reach columns [0, gx·X).
                Assert.True(gx * shape.X >= size, $"{shape}: {gx} groups miss column {size - 1}");
                Assert.True((gx - 1) * shape.X < size, $"{shape}: group {gx - 1} reaches no column of {size}");
                Assert.True(gy * shape.Y >= size, $"{shape}: {gy} groups miss row {size - 1}");
                Assert.True((gy - 1) * shape.Y < size, $"{shape}: group {gy - 1} reaches no row of {size}");
            }
    }

    [Fact]
    public void The_Candidates_Start_With_The_Default_And_Fit_Every_Api()
    {
        Assert.Equal(GpuGroupShape.Default, GpuGroupShapes.Candidates[0]);
        Assert.Equal(GpuGroupShapes.Candidates.Count, GpuGroupShapes.Candidates.Distinct().Count());
        Assert.All(GpuGroupShapes.Candidates, c =>
        {
            Assert.InRange(c.Threads, 64, 256);          // ≤ 256: inside D3D11's 1024 and common Vulkan limits
            Assert.Equal(0, c.Threads % 32);              // whole warps
        });
    }

    // ── the setting ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "sp", null)]
    [InlineData("auto", "sp", null)]
    [InlineData(" AUTO ", "perturb", null)]
    [InlineData("off", "sp", "8x8")]
    [InlineData("0", "perturb", "8x8")]
    [InlineData("16x8", "sp", "16x8")]
    [InlineData("16x8", "perturb-fp32", "16x8")]
    [InlineData("sp=16x8,perturb=32x4", "sp", "16x8")]
    [InlineData("sp=16x8,perturb=32x4", "perturb", "32x4")]
    [InlineData("sp=16x8,perturb=32x4", "perturb-fp32", null)]
    [InlineData("sp=16x8; PERTURB-FP32=16x16", "perturb-fp32", "16x16")]
    [InlineData("sp=auto,perturb=8x8", "sp", null)]
    [InlineData("sp=banana", "sp", null)]
    [InlineData("banana", "sp", null)]
    public void The_Setting_Fixes_Or_Frees_Each_Slot(string? setting, string slot, string? expected)
    {
        var got = GpuGroupShapes.Fixed(setting, slot);
        Assert.Equal(expected, got?.ToString());
    }

    // ── the tuner ────────────────────────────────────────────────────────────

    /// <summary>A fake GPU: each shape costs what the test says, the first call
    /// (the warm-up) of each shape costs 100× more, and <paramref name="noise"/>
    /// adds a deterministic wobble to the timed runs.</summary>
    private sealed class FakeTimer(Dictionary<GpuGroupShape, double> cost, double noise = 0)
    {
        private readonly Dictionary<GpuGroupShape, int> _calls = new();
        public int Calls => _calls.Values.Sum();

        public double Measure(GpuGroupShape s)
        {
            int k = _calls[s] = _calls.GetValueOrDefault(s) + 1;
            if (!cost.TryGetValue(s, out double ms)) throw new InvalidOperationException($"{s} does not build");
            if (k == 1) return ms * 100;                       // warm-up: compile + first dispatch
            return ms * (1 + noise * ((k * 7919 + s.X * 31 + s.Y) % 5 - 2) / 2.0);
        }
    }

    private static Dictionary<GpuGroupShape, double> Costs(params double[] ms) =>
        GpuGroupShapes.Candidates.Zip(ms).ToDictionary(p => p.First, p => p.Second);

    [Fact]
    public void The_Measured_Fastest_Shape_Wins()
    {
        // Every candidate in turn is the fastest by a clear margin.
        for (int fast = 0; fast < GpuGroupShapes.Candidates.Count; fast++)
        {
            var costs = Costs(Enumerable.Repeat(10.0, GpuGroupShapes.Candidates.Count).ToArray());
            costs[GpuGroupShapes.Candidates[fast]] = 8.0;
            var timings = new List<GpuGroupShapes.Timing>();
            var fake = new FakeTimer(costs);
            var won = GpuGroupShapes.PickFastest(GpuGroupShapes.Candidates, fake.Measure, timings);
            Assert.Equal(GpuGroupShapes.Candidates[fast], won);
            Assert.Equal(GpuGroupShapes.Candidates.Count * (1 + GpuGroupShapes.Reps), fake.Calls);
            // The warm-up doesn't count: every recorded best is the shape's real cost.
            Assert.All(timings, t => Assert.Equal(costs[t.Shape], t.BestMs, 9));
        }
    }

    [Theory]
    [InlineData(9.80, false)]   // 2% faster: inside MinGain, the default stays
    [InlineData(9.75, false)]
    [InlineData(9.65, true)]    // 3.5%: wins
    [InlineData(5.00, true)]
    public void The_Default_Keeps_A_Tie_Inside_MinGain(double rival, bool rivalWins)
    {
        var costs = Costs(10, 11, 12, rival, 13, 14);
        var won = GpuGroupShapes.PickFastest(GpuGroupShapes.Candidates, new FakeTimer(costs).Measure);
        Assert.Equal(rivalWins ? GpuGroupShapes.Candidates[3] : GpuGroupShape.Default, won);
    }

    [Fact]
    public void The_Best_Repeat_Counts_So_Noise_Does_Not_Pick_A_Slower_Shape()
    {
        // Up to ±10% wobble on each run; 16x8 is really 20% faster than 8x8.
        var costs = Costs(10, 8, 12, 11, 13, 14);
        foreach (double noise in new[] { 0.02, 0.05, 0.1 })
            Assert.Equal(new GpuGroupShape(16, 8), GpuGroupShapes.PickFastest(GpuGroupShapes.Candidates, new FakeTimer(costs, noise).Measure));
    }

    [Fact]
    public void A_Shape_That_Fails_Is_Skipped()
    {
        var costs = Costs(10, 9, 12, 11, 13, 14);
        costs.Remove(new GpuGroupShape(16, 8));                     // the would-be winner can't build
        var timings = new List<GpuGroupShapes.Timing>();
        Assert.Equal(GpuGroupShape.Default, GpuGroupShapes.PickFastest(GpuGroupShapes.Candidates, new FakeTimer(costs).Measure, timings));
        Assert.DoesNotContain(timings, t => t.Shape == new GpuGroupShape(16, 8));

        costs.Remove(GpuGroupShape.Default);                         // nor the default: the fastest of the rest
        Assert.Equal(new GpuGroupShape(32, 4), GpuGroupShapes.PickFastest(GpuGroupShapes.Candidates, new FakeTimer(costs).Measure));

        // A non-positive / non-finite time is no measurement either.
        var bogus = new Dictionary<GpuGroupShape, double>(Costs(10, 0, double.NaN, -1, 12, 13));
        Assert.Equal(GpuGroupShape.Default, GpuGroupShapes.PickFastest(GpuGroupShapes.Candidates, new FakeTimer(bogus).Measure));
    }

    // ── the store ────────────────────────────────────────────────────────────

    private static string UniqueDevice() => "test|" + Guid.NewGuid().ToString("N");

    [Fact]
    public void A_Stored_Winner_Is_Reused_By_The_Next_Process_Without_Timing()
    {
        GpuGroupShapes.SetSettingForThread("auto");
        string device = UniqueDevice();
        var costs = Costs(10, 7, 12, 11, 13, 14);
        var first = new FakeTimer(costs);
        Assert.Equal(new GpuGroupShape(16, 8), GpuGroupShapes.Resolve(device, "sp", "hash-a", GpuGroupShapes.Candidates, first.Measure));
        Assert.True(first.Calls > 0);
        Assert.True(File.Exists(GpuGroupShapes.StorePath));
        Assert.StartsWith(Path.GetFullPath(AppDataPaths.Root), Path.GetFullPath(GpuGroupShapes.StorePath));   // the test root, never the user's

        GpuGroupShapes.ForgetLoadedStore();                        // a new process: the file is re-read
        var second = new FakeTimer(Costs(5, 50, 50, 50, 50, 50));   // would pick 8x8 if it timed
        Assert.Equal(new GpuGroupShape(16, 8), GpuGroupShapes.Resolve(device, "sp", "hash-a", GpuGroupShapes.Candidates, second.Measure));
        Assert.Equal(0, second.Calls);

        // Another slot, kernel source or device is another key: tuned afresh.
        foreach (var (dev, slot, hash) in new[] { (device, "perturb", "hash-a"), (device, "sp", "hash-b"), (UniqueDevice(), "sp", "hash-a") })
        {
            var again = new FakeTimer(Costs(5, 50, 50, 50, 50, 50));
            Assert.Equal(GpuGroupShape.Default, GpuGroupShapes.Resolve(dev, slot, hash, GpuGroupShapes.Candidates, again.Measure));
            Assert.True(again.Calls > 0, $"{slot}/{hash} reused another key's winner");
        }
    }

    [Fact]
    public void A_Stored_Shape_The_Device_Cannot_Run_Is_Tuned_Again()
    {
        GpuGroupShapes.SetSettingForThread("auto");
        string device = UniqueDevice();
        GpuGroupShapes.Store(GpuGroupShapes.Key(device, "sp", "h"), new GpuGroupShape(16, 16));
        var small = GpuGroupShapes.Candidates.Where(c => c.Threads <= 128).ToList();   // a 128-invocation device
        var fake = new FakeTimer(small.ToDictionary(c => c, c => c == new GpuGroupShape(32, 4) ? 5.0 : 10.0));
        Assert.Equal(new GpuGroupShape(32, 4), GpuGroupShapes.Resolve(device, "sp", "h", small, fake.Measure));
        Assert.True(fake.Calls > 0);
    }

    [Fact]
    public void An_Override_Never_Tunes_Or_Stores()
    {
        string device = UniqueDevice();
        var fake = new FakeTimer(Costs(10, 1, 1, 1, 1, 1));
        GpuGroupShapes.SetSettingForThread("off");
        Assert.Equal(GpuGroupShape.Default, GpuGroupShapes.Resolve(device, "sp", "h", GpuGroupShapes.Candidates, fake.Measure));
        GpuGroupShapes.SetSettingForThread("perturb=32x8");
        Assert.Equal(new GpuGroupShape(32, 8), GpuGroupShapes.Resolve(device, "perturb", "h", GpuGroupShapes.Candidates, fake.Measure));
        Assert.Equal(0, fake.Calls);
        Assert.Null(GpuGroupShapes.Stored(GpuGroupShapes.Key(device, "sp", "h")));
        Assert.Null(GpuGroupShapes.Stored(GpuGroupShapes.Key(device, "perturb", "h")));
    }

    [Fact]
    public void The_Test_Process_Pins_The_Default()
    {
        // TestDataRootIsolation: every other GPU test runs 8x8, as before #1339.
        Assert.Equal("off", GpuGroupShapes.ProcessSetting);
        GpuGroupShapes.ResetSettingForThread();
        Assert.Equal(GpuGroupShape.Default, GpuGroupShapes.Fixed(GpuGroupShapes.Setting, "sp"));
    }

    // ── band heights ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Bands_Of_Any_Group_Height_Run_Each_Row_Once(int groupRows)
    {
        foreach (int height in new[] { 1, 15, 16, 17, 200, 1080 })
            foreach (int maxIter in new[] { 100, 5000, 1_000_000 })
            {
                int rows = MandelbrotKernelSource.PerturbBandRows(1920, height, maxIter, groupRows);
                Assert.True(rows % groupRows == 0 || rows == height, $"{rows} rows, groups of {groupRows}");
                var runs = new int[height];
                for (int rowBase = 0; rowBase < height; rowBase += rows)
                {
                    int r = Math.Min(rows, height - rowBase);
                    int dispatched = (r + groupRows - 1) / groupRows * groupRows;
                    for (int y = rowBase; y < Math.Min(height, rowBase + dispatched); y++) runs[y]++;
                }
                Assert.All(runs, n => Assert.Equal(1, n));
            }
    }
}

/// <summary>#1339 on a real Vulkan device.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes PerturbBudgetMs / PerturbBandRowsOverride
public sealed class S1339VulkanGroupShapeTests : IDisposable
{
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    public S1339VulkanGroupShapeTests() => MandelbrotKernelSource.PerturbBudgetMs = 0;

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotKernelSource.PerturbBandRowsOverride = 0;
        VulkanComputeKernel.ForceFloatExpEntry = false;
        GpuGroupShapes.ResetSettingForThread();
    }

    private const double EscapeR2 = 512.0 * 512.0;

    private static VulkanComputeKernel Kernel()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    private static (double[] Zr, double[] Zi, int Len) Orbit(double cx, double cy, int maxIter)
    {
        var zr = new double[maxIter + 1]; var zi = new double[maxIter + 1];
        double x = 0, y = 0; int len = maxIter;
        for (int n = 0; n < maxIter; n++)
        {
            zr[n] = x; zi[n] = y;
            if (x * x + y * y >= EscapeR2) { len = n; break; }
            double nx = x * x - y * y + cx; y = 2 * x * y + cy; x = nx;
        }
        return (zr, zi, len);
    }

    private static readonly (double[] Zr, double[] Zi, int Len) Seahorse = Orbit(-0.743643887037151, 0.13182590420533, 2000);
    private static readonly (double[] Zr, double[] Zi, int Len) Feigenbaum = Orbit(-1.4011551890920506, 0.0, 2000);

    private static InterpretedOrbitColorMap TrapTheme()
    {
        bool prev = InterpretedOrbitColorMap.GpuEnabled;
        InterpretedOrbitColorMap.GpuEnabled = true;
        try
        {
            var m = InterpretedColorMap.TryCreate("return hsv(saturate(trapMin), 0.9, 1.0);", null, out string? err);
            Assert.Null(err);
            return Assert.IsType<InterpretedOrbitColorMap>(m);
        }
        finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
    }

    private enum Path { Sp, SpPalette, Plain, Orbit, Sa, FloatExp, ScaledF32 }

    // 100 × 75 (SP) / 100 × 200 (perturbation): no side a multiple of any candidate's.
    private static List<Array> Render(VulkanComputeKernel k, Path path, bool dirty)
    {
        bool sp = path is Path.Sp or Path.SpPalette;
        int w = 100, h = sp ? 75 : 200, n = w * h;
        var it = new int[n]; var sm = new float[n]; var zr = new float[n]; var zi = new float[n]; var dr = new float[n]; var di = new float[n];
        var outs = new List<Array> { it, sm, zr, zi, dr, di };
        var o = dirty ? Feigenbaum : Seahorse;
        double zoom = dirty ? 1e3 : 1e5, scale = 3.5 / (w * zoom);
        switch (path)
        {
            case Path.Sp:
                k.Run(w, h, dirty ? -0.5 : -0.7436, dirty ? 0 : 0.1318, (dirty ? 3.0 : 0.01) / w, 1000, 4.0, it, sm, zr, zi, dr, di);
                break;
            case Path.SpPalette:
                var color = new uint[n]; var trap = new float[n];
                k.SetPalette(TrapTheme());
                Assert.True(k.HasGpuPalette);
                try { k.Run(w, h, dirty ? -0.5 : -0.7436, dirty ? 0 : 0.1318, (dirty ? 3.0 : 0.01) / w, 1000, 4.0, it, sm, zr, zi, dr, di, colorDst: color, trapDst: trap); }
                finally { k.SetPalette(null); }
                outs.Add(color); outs.Add(trap);
                break;
            case Path.Plain:
                k.RunPerturb(w, h, scale, 2000, EscapeR2, -w / 2, -h / 2, o.Zr, o.Zi, o.Len, it, sm, zr, zi, dr, di);
                break;
            case Path.Orbit:
                int mask = MandelbrotKernelSource.OrbAll, stride = MandelbrotKernelSource.PerturbOrbitStride(mask);
                var orbit = new float[n * stride];
                k.RunPerturbOrbit(w, h, scale, 2000, EscapeR2, -w / 2, -h / 2, o.Zr, o.Zi, o.Len,
                    mask, -0.743643887037151, 0.13182590420533, it, sm, zr, zi, dr, di, orbit);
                outs.Add(orbit);
                break;
            case Path.Sa:
                var sa = new SeriesApproximation(o.Zr, o.Zi, o.Len);
                var bla = new BlaTable(o.Zr, o.Zi, o.Len, Math.Sqrt(2.0) * 0.5 * Math.Max(w, h) * scale);
                k.RunPerturbSA(w, h, scale, 2000, EscapeR2, -w / 2, -h / 2, o.Zr, o.Zi, o.Len, 1e-3, sa.SafeMax,
                    sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI, it, sm, zr, zi, dr, di, bla.GpuCoefficients, bla.Levels);
                break;
            default:
                VulkanComputeKernel.ForceFloatExpEntry = path == Path.FloatExp;
                try { k.RunPerturbFloatExp(w, h, scale, 2000, EscapeR2, -w / 2, -h / 2, o.Zr, o.Zi, o.Len, it, sm, zr, zi, dr, di); }
                finally { VulkanComputeKernel.ForceFloatExpEntry = false; }
                break;
        }
        return outs;
    }

    private static string Slot(Path p) => p switch
    {
        Path.Sp or Path.SpPalette => GpuGroupShapes.SlotEscape,
        Path.FloatExp or Path.ScaledF32 => GpuGroupShapes.SlotPerturbFp32,
        _ => GpuGroupShapes.SlotPerturb,
    };

    private static string? FirstDifference(List<Array> a, List<Array> b)
    {
        for (int o = 0; o < a.Count; o++)
            for (int i = 0; i < a[o].Length; i++)
                if (!Equals(a[o].GetValue(i), b[o].GetValue(i))) return $"output {o} at {i}";
        return null;
    }

    [Fact]
    public void Every_Candidate_Gives_The_Same_Bytes_As_8x8()
    {
        using var reference = Kernel();
        var paths = Enum.GetValues<Path>().Where(p => p is Path.Sp or Path.SpPalette || reference.SupportsPerturbation).ToList();
        GpuGroupShapes.SetSettingForThread("off");
        var expected = paths.ToDictionary(p => p, p => Render(reference, p, dirty: false));
        foreach (var p in paths)
            Assert.NotNull(FirstDifference(expected[p], Render(reference, p, dirty: true)));   // the dirty view differs

        foreach (var shape in GpuGroupShapes.Candidates.Skip(1))
        {
            GpuGroupShapes.SetSettingForThread(shape.ToString());
            using var k = Kernel();
            foreach (var p in paths)
            {
                Render(k, p, dirty: true);
                var got = Render(k, p, dirty: false);
                Assert.Equal(shape, k.GroupShape(Slot(p)));
                string? diff = FirstDifference(expected[p], got);
                Assert.True(diff is null, $"{shape} {p}: differs from 8x8 at {diff}");
            }
        }
    }

    [Fact]
    public void Bands_Follow_The_Group_Height()
    {
        GpuGroupShapes.SetSettingForThread("16x16");
        using var k = Kernel();
        if (!k.SupportsPerturbation) Assert.Skip("Vulkan device has no shaderFloat64");
        if (!k.SupportsGpuTimestamps) Assert.Skip("no GPU timestamps");
        MandelbrotKernelSource.PerturbBandRowsOverride = 8;          // rounds up to one 16-row group
        Render(k, Path.Plain, dirty: false);
        Assert.Equal(new GpuGroupShape(16, 16), k.GroupShape(GpuGroupShapes.SlotPerturb));
        Assert.Equal((200 + 15) / 16, k.LastGpuStageTimes!.Value.Packets);
    }

    [Fact]
    public void Tuning_Picks_The_Fastest_Of_Its_Own_Timings_Stores_It_And_Is_Reused()
    {
        GpuGroupShapes.SetSettingForThread("auto");
        string storeKeyPrefix;
        int runs = GpuGroupShapes.TuningRuns;
        GpuGroupShape winner;
        using (var k = Kernel())
        {
            storeKeyPrefix = k.GroupShapeDeviceKey + "|" + GpuGroupShapes.SlotEscape + "|";
            Render(k, Path.Sp, dirty: false);
            winner = k.GroupShape(GpuGroupShapes.SlotEscape);
            var t = GpuGroupShapes.LastTuning;
            if (GpuGroupShapes.TuningRuns == runs)
            {
                // An earlier test in this process tuned the slot: reuse is all there is to see.
                Assert.NotNull(GpuGroupShapes.Stored(StoredKey(storeKeyPrefix)));
            }
            else
            {
                Assert.NotNull(t);
                Assert.StartsWith(storeKeyPrefix, t.Value.Key);
                Assert.Equal(winner, t.Value.Winner);
                // The rule, re-applied to the recorded times: fastest, or 8x8 within MinGain.
                var timings = t.Value.Timings;
                Assert.True(timings.Count >= 2, "fewer than two shapes timed");
                double def = timings.First(x => x.Shape == GpuGroupShape.Default).BestMs;
                var fastest = timings.MinBy(x => x.BestMs);
                var expected = fastest.BestMs <= def * (1 - GpuGroupShapes.MinGain) ? fastest.Shape : GpuGroupShape.Default;
                Assert.Equal(expected, winner);
                Assert.Equal(winner, GpuGroupShapes.Stored(t.Value.Key));
            }
        }

        int after = GpuGroupShapes.TuningRuns;
        GpuGroupShapes.ForgetLoadedStore();
        using (var again = Kernel())
        {
            Render(again, Path.Sp, dirty: false);
            Assert.Equal(winner, again.GroupShape(GpuGroupShapes.SlotEscape));
        }
        Assert.Equal(after, GpuGroupShapes.TuningRuns);   // the stored winner, no second tuning
    }

    private static string StoredKey(string prefix)
    {
        var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(GpuGroupShapes.StorePath))!;
        return json.Keys.First(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }
}
