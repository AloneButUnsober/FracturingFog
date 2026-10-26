// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1377 — the thread-group shape tuner (#1339) for the relief raymarch and the
// two froxel passes (D3D11 is checked by --gpugroupshapeprobe). Checked:
//   • relief tiles (ReliefGpuTiling.TileSize) for every candidate shape are
//     whole groups, and the tiled dispatch reaches every pixel exactly once,
//     against a brute-force count of the threads each tile's groups run; a tile
//     still stays inside the time budget unless it is a single group row;
//   • GPU timestamp segments (a two-pass kernel reads each pass from them);
//   • the representative scene really is a shadowed relief frame plus a froxel
//     frame (the render took the GPU relief + froxel branch);
//   • on whatever Vulkan device the host has (skipped without one): every
//     candidate shape gives the same colour and AOV bytes as 8×8 for the relief
//     kernel, and the same bytes for each froxel pass, on a frame whose sides
//     are no multiple of any group, with the buffers dirtied by another view
//     first; the froxel pass times add up to the frame's dispatch time; tuning
//     end to end picks the fastest of its own timings for each slot, stores it,
//     and a second kernel reuses it without tuning;
//   • the relief slot is opt-in: "auto" keeps it 8×8 (tuning it costs a compile
//     of the big relief shader per candidate, and 8×8 wins on the GT 710).

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1377RaymarchGroupShapeTests : IDisposable
{
    public void Dispose() => GpuGroupShapes.ResetSettingForThread();

    // ── relief tiles ─────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Shapes() => GpuGroupShapes.Candidates.Select(s => new object[] { s.X, s.Y });

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Tiles_Are_Whole_Groups_And_Run_Each_Pixel_Once(int gx, int gy)
    {
        var shape = new GpuGroupShape(gx, gy);
        const double ns = 3.0;   // a GT 710
        foreach (var (w, h) in new[] { (1920, 1080), (203, 131), (7, 5), (3840, 2160) })
            foreach (double cost in new[] { 0.0, 50.0, 3000.0, 200_000.0 })
            {
                var (rows, cols) = ReliefGpuTiling.TileSize(w, h, cost, ns, shape);
                Assert.True(rows % gy == 0 && cols % gx == 0, $"{shape} {w}x{h} cost {cost}: tile {rows}x{cols} is not whole groups");
                // The tile's in-frame pixels (threads past the frame edge return at once).
                double msPerPixel = cost * ns * 1e-6, tileMs = Math.Min(rows, h) * (double)Math.Min(cols, w) * msPerPixel;
                if (cost > 0)
                    Assert.True(tileMs <= ReliefGpuTiling.BandTargetMs || (rows == gy && cols == gx),
                        $"{shape} {w}x{h} cost {cost}: tile {rows}x{cols} predicts {tileMs:F0} ms");

                // The kernel's loop: each tile dispatches GroupsX × GroupsY groups at
                // (colBase, rowBase); a thread runs its pixel when it is inside the frame.
                var runs = new byte[w * h];
                for (int rowBase = 0; rowBase < h; rowBase += rows)
                for (int colBase = 0; colBase < w; colBase += cols)
                {
                    int r = Math.Min(rows, h - rowBase), c = Math.Min(cols, w - colBase);
                    int tw = (int)shape.GroupsX(c) * gx, th = (int)shape.GroupsY(r) * gy;
                    for (int ty = 0; ty < th; ty++)
                    {
                        int py = rowBase + ty;
                        if (py >= h) break;
                        for (int tx = 0; tx < tw; tx++)
                        {
                            int px = colBase + tx;
                            if (px >= w) break;
                            runs[py * w + px]++;
                        }
                    }
                }
                int wrong = Array.FindIndex(runs, n => n != 1);
                Assert.True(wrong < 0, $"{shape} {w}x{h} cost {cost}: pixel ({wrong % w}, {wrong / w}) ran {(wrong < 0 ? 0 : runs[wrong])} times");
            }
    }

    // ── timestamps ───────────────────────────────────────────────────────────

    [Fact]
    public void Segments_Give_Each_Stamp_Its_Own_Time()
    {
        var stages = new[] { GpuStage.Begin, GpuStage.Dispatch, GpuStage.Dispatch, GpuStage.CopyOut, GpuStage.Begin, GpuStage.Dispatch };
        var ticks = new ulong[] { 0, 10, 25, 26, 100, 140 };   // 1 tick = 1 ms below
        var seg = new List<double>();
        var t = GpuStageTimes.FromStamps(stages, ticks, 1e6, segmentsMs: seg);
        Assert.NotNull(t);
        Assert.Equal(new double[] { 10, 15, 1, 40 }, seg);   // the idle gap 26 → 100 is no segment
        Assert.Equal(65, t.Value.DispatchMs, 9);
        Assert.Equal(1, t.Value.CopyOutMs, 9);
        Assert.Equal(2, t.Value.Packets);
    }

    // ── the setting ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("relief=32x4", GpuGroupShapes.SlotRelief, "32x4")]
    [InlineData("relief=32x4", GpuGroupShapes.SlotFroxelIntegrate, null)]
    [InlineData("froxel-integrate=16x4,froxel-composite=8x8", GpuGroupShapes.SlotFroxelIntegrate, "16x4")]
    [InlineData("froxel-integrate=16x4,froxel-composite=8x8", GpuGroupShapes.SlotFroxelComposite, "8x8")]
    [InlineData("froxel-integrate=16x4", GpuGroupShapes.SlotFroxelComposite, null)]
    [InlineData("16x16", GpuGroupShapes.SlotFroxelComposite, "16x16")]
    [InlineData("off", GpuGroupShapes.SlotRelief, "8x8")]
    // The relief slot is opt-in: "auto" (or no setting) keeps it 8x8.
    [InlineData(null, GpuGroupShapes.SlotRelief, "8x8")]
    [InlineData("auto", GpuGroupShapes.SlotRelief, "8x8")]
    [InlineData("froxel-composite=16x8", GpuGroupShapes.SlotRelief, "8x8")]
    [InlineData("relief=auto", GpuGroupShapes.SlotRelief, null)]
    [InlineData("16x8", GpuGroupShapes.SlotRelief, "16x8")]
    [InlineData(null, GpuGroupShapes.SlotFroxelIntegrate, null)]
    [InlineData("auto", GpuGroupShapes.SlotFroxelComposite, null)]
    public void The_Setting_Names_The_New_Slots(string setting, string slot, string? expected)
    {
        var got = GpuGroupShapes.Fixed(setting, slot);
        Assert.Equal(expected, got?.ToString());
    }

    // ── the tuner's clocks ───────────────────────────────────────────────────

    private sealed class FakeRelief(GpuStageTimes? gpu, double host) : IReliefRaymarchKernel
    {
        public int Runs;
        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
            float[]? aovNormalXyz = null, float[]? aovDepth = null) => Runs++;
        public double LastDispatchMs => host;
        public GpuStageTimes? LastGpuStageTimes => gpu;
        public void Dispose() { }
    }

    private sealed class FakeFroxel(FroxelPassTimes? passes, double host) : IFroxelVolumeKernel
    {
        public int Runs;
        public void Composite(in FroxelGpuUniforms u, uint[] beauty, float[] worldDepth, int w, int h, uint[] dst) => Runs++;
        public double LastDispatchMs => host;
        public FroxelPassTimes? LastPassTimes => passes;
        public void Dispose() { }
    }

    [Fact]
    public void The_Tuner_Times_The_Gpu_Pass_Of_Its_Slot_Else_The_Host()
    {
        var both = new FakeFroxel(new FroxelPassTimes(3, 7), 50);
        Assert.Equal(3, RaymarchGroupShapes.TimeFroxel(both, GpuGroupShapes.SlotFroxelIntegrate));
        Assert.Equal(7, RaymarchGroupShapes.TimeFroxel(both, GpuGroupShapes.SlotFroxelComposite));
        Assert.Equal(2, both.Runs);
        Assert.Equal(50, RaymarchGroupShapes.TimeFroxel(new FakeFroxel(null, 50), GpuGroupShapes.SlotFroxelComposite));

        var relief = new FakeRelief(new GpuStageTimes(0, 4, 1, 3), 40);
        Assert.Equal(4, RaymarchGroupShapes.TimeRelief(relief));   // the dispatch, not the copy-out
        Assert.Equal(1, relief.Runs);
        Assert.Equal(40, RaymarchGroupShapes.TimeRelief(new FakeRelief(null, 40)));
    }

    // ── the representative scene ─────────────────────────────────────────────

    [Fact]
    public void The_Representative_Scene_Is_A_Shadowed_Relief_Frame_And_A_Froxel_Frame()
    {
        var s = RaymarchGroupShapes.Representative;
        Assert.Equal((RaymarchGroupShapes.Width, RaymarchGroupShapes.Height), (s.W, s.H));
        Assert.Equal(s.W, s.Relief.W);
        Assert.Equal(s.H, s.Relief.H);
        Assert.True(s.Relief.ShadowSteps > 0 && s.Relief.AoSamples > 0, "no shadow / AO marches in the relief frame");
        Assert.Equal(0.0, s.Relief.FogDensity);   // the froxel pass carries the fog, as on a live froxel frame
        Assert.True(s.Froxel.Grid.DimX > 0 && s.Froxel.Grid.DimY > 0 && s.Froxel.Grid.DimZ > 0);
        Assert.True(s.Froxel.Medium.BaseDensity > 0, "the froxel medium has no density");
        // A real silhouette: many colours and many depths (not a sky-only or flat frame).
        Assert.True(s.Beauty.Distinct().Count() > 200, $"only {s.Beauty.Distinct().Count()} beauty colours");
        Assert.True(s.Depth.Distinct().Count() > 1000, $"only {s.Depth.Distinct().Count()} depths");
        Assert.Equal(s.Field.Length, s.Relief.Hw * s.Relief.Hh);
    }
}

/// <summary>#1377 on a real Vulkan device.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]   // tunes: TuningRuns / LastTuning are process-wide (S1339 reads them)
public sealed class S1377VulkanRaymarchGroupShapeTests : IDisposable
{
    private const int W = 203, H = 131;   // no multiple of any group side

    private readonly VulkanContext? _ctx;

    public S1377VulkanRaymarchGroupShapeTests()
    {
        try
        {
            var ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return; }
            ctx.CreateComputeDevice();
            _ctx = ctx;
        }
        catch { _ctx = null; }
    }

    public void Dispose()
    {
        GpuGroupShapes.ResetSettingForThread();
        _ctx?.Dispose();
    }

    private VulkanContext Ctx => _ctx ?? throw new InvalidOperationException();

    private void RequireDevice()
    {
        if (_ctx is null) Assert.Skip("no Vulkan device on this host");
    }

    private static readonly Lazy<RaymarchGroupShapes.Scene> s_scene = new(() => RaymarchGroupShapes.Capture(W, H));

    // Another view at the same size: its frame leaves every buffer dirty.
    private static readonly Lazy<RaymarchGroupShapes.Scene> s_dirty = new(() =>
    {
        var p = RaymarchGroupShapes.Params();
        p.Relief2DCameraAzimuthDeg = 140;
        p.Relief2DCameraElevationDeg = 55;
        var fx = p.Lighting; fx.FogDensity = 1.5; p.Lighting = fx;
        return RaymarchGroupShapes.Capture(W, H, p);
    });

    private sealed record ReliefOut(uint[] Color, float[] Normal, float[] Depth);

    private static ReliefOut Relief(IReliefRaymarchKernel k, RaymarchGroupShapes.Scene s)
    {
        var o = new ReliefOut(new uint[W * H], new float[W * H * 3], new float[W * H]);
        k.Run(s.Relief, s.Field, s.Keep, s.Albedo, o.Color, o.Normal, o.Depth);
        return o;
    }

    private static uint[] Froxel(IFroxelVolumeKernel k, RaymarchGroupShapes.Scene s)
    {
        var dst = new uint[W * H];
        k.Composite(s.Froxel, s.Beauty, s.Depth, W, H, dst);
        return dst;
    }

    private static string? FirstDifference(ReliefOut a, ReliefOut b)
    {
        for (int i = 0; i < a.Color.Length; i++) if (a.Color[i] != b.Color[i]) return $"colour at ({i % W}, {i / W})";
        for (int i = 0; i < a.Normal.Length; i++) if (BitConverter.SingleToInt32Bits(a.Normal[i]) != BitConverter.SingleToInt32Bits(b.Normal[i])) return $"normal {i % 3} at ({i / 3 % W}, {i / 3 / W})";
        for (int i = 0; i < a.Depth.Length; i++) if (BitConverter.SingleToInt32Bits(a.Depth[i]) != BitConverter.SingleToInt32Bits(b.Depth[i])) return $"depth at ({i % W}, {i / W})";
        return null;
    }

    private static string? FirstDifference(uint[] a, uint[] b)
    {
        if (a.Length != b.Length) return "the length";
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return $"({i % W}, {i / W})";
        return null;
    }

    [Fact]
    public void Every_Candidate_Gives_The_Relief_Bytes_Of_8x8()
    {
        RequireDevice();
        GpuGroupShapes.SetSettingForThread("off");
        ReliefOut expected;
        using (var reference = new ReliefRaymarchVulkanKernel(Ctx))
        {
            expected = Relief(reference, s_scene.Value);
            Assert.Equal(GpuGroupShape.Default, reference.GroupShape);
            Assert.NotNull(FirstDifference(expected, Relief(reference, s_dirty.Value)));   // the dirty view differs
        }

        foreach (var shape in VulkanCandidates().Skip(1))
        {
            GpuGroupShapes.SetSettingForThread(shape.ToString());
            using var k = new ReliefRaymarchVulkanKernel(Ctx);
            Relief(k, s_dirty.Value);
            var got = Relief(k, s_scene.Value);
            Assert.Equal(shape, k.GroupShape);
            string? diff = FirstDifference(expected, got);
            Assert.True(diff is null, $"{shape}: relief differs from 8x8 at {diff}");
        }
    }

    [Fact]
    public void Every_Candidate_Gives_The_Froxel_Bytes_Of_8x8()
    {
        RequireDevice();
        GpuGroupShapes.SetSettingForThread("off");
        uint[] expected;
        using (var reference = new FroxelVolumeVulkanKernel(Ctx))
        {
            expected = Froxel(reference, s_scene.Value);
            Assert.NotNull(FirstDifference(expected, Froxel(reference, s_dirty.Value)));
        }

        foreach (var shape in VulkanCandidates().Skip(1))
            foreach (bool integrate in new[] { true, false })
            {
                string slot = integrate ? GpuGroupShapes.SlotFroxelIntegrate : GpuGroupShapes.SlotFroxelComposite;
                string other = integrate ? GpuGroupShapes.SlotFroxelComposite : GpuGroupShapes.SlotFroxelIntegrate;
                GpuGroupShapes.SetSettingForThread($"{slot}={shape},{other}=8x8");
                using var k = new FroxelVolumeVulkanKernel(Ctx);
                Froxel(k, s_dirty.Value);
                var got = Froxel(k, s_scene.Value);
                Assert.Equal(shape, k.GroupShape(slot));
                Assert.Equal(GpuGroupShape.Default, k.GroupShape(other));
                string? diff = FirstDifference(expected, got);
                Assert.True(diff is null, $"{slot} {shape}: froxel differs from 8x8 at {diff}");
            }
    }

    [Fact]
    public void The_Froxel_Pass_Times_Add_Up_To_The_Dispatch_Time()
    {
        RequireDevice();
        if (Ctx.TimestampValidBits == 0) Assert.Skip("no GPU timestamps");
        GpuGroupShapes.SetSettingForThread("off");
        using var k = new FroxelVolumeVulkanKernel(Ctx);
        Froxel(k, s_scene.Value);
        Assert.NotNull(k.LastGpuStageTimes);
        Assert.NotNull(k.LastPassTimes);
        var p = k.LastPassTimes!.Value;
        Assert.True(p.IntegrateMs > 0 && p.CompositeMs > 0, $"pass times {p}");
        Assert.Equal(k.LastGpuStageTimes!.Value.DispatchMs, p.IntegrateMs + p.CompositeMs, 9);

        // Which pass is which: sixteen times the pixels makes the per-pixel composite
        // pass slower, while the per-column integrate pass (same grid) is not.
        var s = s_scene.Value;
        var big = new uint[W * H * 16];
        var bigDepth = new float[big.Length];
        for (int i = 0; i < big.Length; i++) { big[i] = s.Beauty[i % s.Beauty.Length]; bigDepth[i] = s.Depth[i % s.Depth.Length]; }
        double small = double.MaxValue, smallI = double.MaxValue, large = double.MaxValue, largeI = double.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            k.Composite(s.Froxel, s.Beauty, s.Depth, W, H, new uint[W * H]);
            small = Math.Min(small, k.LastPassTimes!.Value.CompositeMs); smallI = Math.Min(smallI, k.LastPassTimes!.Value.IntegrateMs);
            k.Composite(s.Froxel, big, bigDepth, W * 4, H * 4, new uint[big.Length]);
            large = Math.Min(large, k.LastPassTimes!.Value.CompositeMs); largeI = Math.Min(largeI, k.LastPassTimes!.Value.IntegrateMs);
        }
        Assert.True(large > 4 * small, $"composite {small:F3} ms at {W}x{H}, {large:F3} ms at 16x the pixels");
        Assert.True(largeI < 2 * smallI, $"integrate {smallI:F3} ms vs {largeI:F3} ms: the pass times are swapped");

        using var r = new ReliefRaymarchVulkanKernel(Ctx);
        Relief(r, s_scene.Value);
        Assert.True(r.LastGpuStageTimes is { DispatchMs: > 0, Packets: 1 }, $"relief GPU times {r.LastGpuStageTimes}");
    }

    [Fact]
    public void Tuning_Picks_The_Fastest_Of_Its_Own_Timings_Stores_It_And_Is_Reused()
    {
        RequireDevice();
        GpuGroupShapes.SetSettingForThread("relief=auto");   // opt-in; the froxel slots tune under it too
        string device = VulkanGroupShapesKey();
        var winners = new Dictionary<string, GpuGroupShape>();
        var timingsBySlot = new Dictionary<string, IReadOnlyList<GpuGroupShapes.Timing>>();
        int runs = GpuGroupShapes.TuningRuns;
        using (var relief = new ReliefRaymarchVulkanKernel(Ctx))
        {
            Relief(relief, s_scene.Value);
            winners[GpuGroupShapes.SlotRelief] = relief.GroupShape;
            if (GpuGroupShapes.LastTuning is { } t && t.Key.Contains("|relief|", StringComparison.Ordinal))
                timingsBySlot[GpuGroupShapes.SlotRelief] = t.Timings;
        }
        using (var froxel = new FroxelVolumeVulkanKernel(Ctx))
        {
            // Each slot's tuning is the last one recorded while the next is timed: catch them in turn.
            Froxel(froxel, s_scene.Value);
            winners[GpuGroupShapes.SlotFroxelIntegrate] = froxel.GroupShape(GpuGroupShapes.SlotFroxelIntegrate);
            winners[GpuGroupShapes.SlotFroxelComposite] = froxel.GroupShape(GpuGroupShapes.SlotFroxelComposite);
            if (GpuGroupShapes.LastTuning is { } t && t.Key.Contains("|froxel-composite|", StringComparison.Ordinal))
                timingsBySlot[GpuGroupShapes.SlotFroxelComposite] = t.Timings;
        }

        foreach (var (slot, winner) in winners)
        {
            string key = GpuGroupShapes.Key(device, slot, SourceHash(slot));
            Assert.Equal(winner, GpuGroupShapes.Stored(key));   // stored under this device + slot + source
        }
        if (GpuGroupShapes.TuningRuns > runs)
            foreach (var (slot, timings) in timingsBySlot)
            {
                // The rule, re-applied to the recorded times: fastest, or 8x8 within MinGain.
                Assert.True(timings.Count >= 2, $"{slot}: fewer than two shapes timed");
                double def = timings.First(x => x.Shape == GpuGroupShape.Default).BestMs;
                var fastest = timings.MinBy(x => x.BestMs);
                var expected = fastest.BestMs <= def * (1 - GpuGroupShapes.MinGain) ? fastest.Shape : GpuGroupShape.Default;
                Assert.Equal(expected, winners[slot]);
            }

        int after = GpuGroupShapes.TuningRuns;
        GpuGroupShapes.ForgetLoadedStore();   // a new process: the file is re-read
        using (var relief = new ReliefRaymarchVulkanKernel(Ctx))
        using (var froxel = new FroxelVolumeVulkanKernel(Ctx))
        {
            Relief(relief, s_scene.Value);
            Froxel(froxel, s_scene.Value);
            Assert.Equal(winners[GpuGroupShapes.SlotRelief], relief.GroupShape);
            Assert.Equal(winners[GpuGroupShapes.SlotFroxelIntegrate], froxel.GroupShape(GpuGroupShapes.SlotFroxelIntegrate));
            Assert.Equal(winners[GpuGroupShapes.SlotFroxelComposite], froxel.GroupShape(GpuGroupShapes.SlotFroxelComposite));
        }
        Assert.Equal(after, GpuGroupShapes.TuningRuns);   // the stored winners, no second tuning
    }

    private List<GpuGroupShape> VulkanCandidates() =>
        GpuGroupShapes.Candidates.Where(c => c.Threads <= Ctx.MaxComputeWorkGroupInvocations
            && c.X <= Ctx.MaxComputeWorkGroupSizeX && c.Y <= Ctx.MaxComputeWorkGroupSizeY).ToList();

    private string VulkanGroupShapesKey()
    {
        using var k = new ReliefRaymarchVulkanKernel(Ctx);
        return k.GroupShapeDeviceKey;
    }

    private static string SourceHash(string slot) => GpuGroupShapes.SourceHash("vulkan-spv\n" +
        (slot == GpuGroupShapes.SlotRelief ? ReliefRaymarchKernelSource.Build() : FroxelKernelSource.Build()));
}
