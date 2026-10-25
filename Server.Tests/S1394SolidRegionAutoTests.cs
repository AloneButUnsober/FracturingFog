// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1394 — solid-region skipping, part 3: Auto mode, and no pixel computed twice.
// Invariants, each against an independent full render (the skip off):
//   • a frame Auto declines is byte-identical to the full render in every buffer;
//   • a frame Auto subdivides is byte-identical to the forced skip (Auto only adds the
//     decision), so the #1343 exactness carries over — checked again here;
//   • the share Auto decides on equals one this file computes from the full render's
//     grid rows with its own cardioid / bulb test, and the decision is share < limit;
//   • no SIMD batch computes a pixel twice, and every pixel a batch computed (a fill
//     must agree with it) equals the full render;
//   • batch parity: Auto defaults on, the region snapshot / --param / Command builder
//     carry it when off, the poster and the live host honour it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1394SolidRegionAutoTests(ITestOutputHelper output)
{
    private const int W = 640, H = 360;   // multiples of 8: every pixel on a SIMD batch path

    private static MandelbrotCalculator Calc(double x, double y, double zoom, int iter, bool skip, bool auto = true) => new(W, H)
    {
        CenterX = x, CenterY = y, Zoom = zoom, MaxIterations = iter,
        ColorMap = new HsvPalette(), UseGpuCompute = false, SolidRegionSkip = skip, SolidRegionAuto = auto,
        CaptureFilledMask = true,
    };

    private static MandelbrotCalculator Run(MandelbrotCalculator c) { c.Calculate(CancellationToken.None); return c; }

    private static IEnumerable<(string Name, Array A)> Buffers(MandelbrotCalculator c) => new (string, Array)[]
    {
        ("Color", c.ColorBuffer), ("Iteration", c.IterationBuffer), ("Smooth", c.SmoothBuffer), ("Distance", c.DistanceBuffer),
        ("NormalX", c.NormalXBuffer), ("NormalY", c.NormalYBuffer), ("FinalZr", c.FinalZrBuffer), ("FinalZi", c.FinalZiBuffer),
        ("FinalDr", c.FinalDrBuffer), ("FinalDi", c.FinalDiBuffer),
    };

    private static string? PixelDiff(MandelbrotCalculator a, MandelbrotCalculator b, int i)
    {
        foreach (var ((name, x), (_, y)) in Buffers(a).Zip(Buffers(b)))
        {
            if (x.Length != y.Length) return $"{name} length {x.Length} vs {y.Length}";
            if (x.Length <= i) continue;
            if (!Equals(x.GetValue(i), y.GetValue(i))) return $"{name} {x.GetValue(i)} vs {y.GetValue(i)}";
        }
        return null;
    }

    private static void AssertFrameEqual(MandelbrotCalculator expected, MandelbrotCalculator actual, string what)
    {
        for (int i = 0; i < W * H; i++)
            if (PixelDiff(expected, actual, i) is { } d) Assert.Fail($"{what}: pixel {i} differs: {d}");
    }

    // The test's own copy of the closed-form in-set tests (main cardioid, period-2 bulb).
    private static bool Free(double cx, double cy)
    {
        if ((cx + 1) * (cx + 1) + cy * cy <= 1.0 / 16) return true;
        double xm = cx - 0.25, q = xm * xm + cy * cy;
        return q * (q + xm) <= cy * cy / 4;
    }

    // From the full render: the share of the grid rows' pixels in runs of ≥ 2·SolidMinTile
    // in-set pixels outside the cardioid / bulb.
    private static double ExpectedShare(MandelbrotCalculator off)
    {
        var rows = new List<int>();
        for (int v = 0; v < H - 1; v += MandelbrotCalculator.SolidTileSize) rows.Add(v);
        rows.Add(H - 1);
        double s = 3.5 / Math.Max(W, H) / off.Zoom;
        long n = 0;
        foreach (int y in rows)
        {
            int run = 0;
            for (int x = 0; x <= W; x++)
            {
                if (x < W && off.IterationBuffer[y * W + x] >= off.MaxIterations
                    && !Free(off.CenterX + (x - W / 2.0) * s, off.CenterY + (y - H / 2.0) * s)) { run++; continue; }
                if (run >= 2 * MandelbrotCalculator.SolidMinTile) n += run;
                run = 0;
            }
        }
        return n / (double)(rows.Count * W);
    }

    private static readonly (string Name, double X, double Y, double Zoom, int Iter)[] FreeSet =
    {
        ("whole set", -0.75, 0, 1, 1000),
        ("seahorse valley", -0.7436, 0.1318, 60, 2000),
        ("elephant valley", 0.2925, 0.0149, 100, 2000),
        ("satellite bulb region", -0.1592, 1.0317, 40, 2000),
        ("small minibrot", -1.7548776662466927, 0, 15, 5000),
    };

    private static readonly (string Name, double X, double Y, double Zoom, int Iter)[] CostlySet =
    {
        ("period-3 minibrot", -1.7548776662466927, 0, 80, 2000),
        ("period-3 minibrot at 45", -1.7548776662466927, 0, 45, 2000),
        ("parabolic cusp", -1.75, 0, 1e5, 20000),
        ("deep minibrot", -1.7548776662466927, 0, 1e14, 6000),
    };

    private static TheoryData<string, double, double, double, int> Data(IEnumerable<(string, double, double, double, int)> views)
    {
        var d = new TheoryData<string, double, double, double, int>();
        foreach (var v in views) d.Add(v.Item1, v.Item2, v.Item3, v.Item4, v.Item5);
        return d;
    }

    public static TheoryData<string, double, double, double, int> FreeViews => Data(FreeSet);
    public static TheoryData<string, double, double, double, int> CostlyViews => Data(CostlySet);

    [Theory]
    [MemberData(nameof(FreeViews))]
    public void Auto_Declines_A_View_Whose_Interior_Is_Free_And_Renders_It_Exactly(string name, double x, double y, double zoom, int iter)
    {
        var off = Run(Calc(x, y, zoom, iter, false));
        var forced = Run(Calc(x, y, zoom, iter, true, auto: false));
        var auto = Run(Calc(x, y, zoom, iter, true));
        output.WriteLine($"{name}: share {auto.LastFrameSolidCostlyShare:P2}, forced fills {forced.LastFrameFilledPixels}");
        Assert.True(forced.LastFrameFilledPixels > 0, "precondition: the forced skip fills here");
        Assert.True(auto.LastFrameSolidDeclined);
        Assert.Equal(0, auto.LastFrameFilledPixels);
        Assert.Equal(2, auto.LastFrameSolidPasses);   // the rows, then the rest
        AssertFrameEqual(off, auto, name);
    }

    [Theory]
    [MemberData(nameof(CostlyViews))]
    public void Auto_Subdivides_A_View_With_A_Costly_Interior_Exactly_Like_The_Forced_Skip(string name, double x, double y, double zoom, int iter)
    {
        var off = Run(Calc(x, y, zoom, iter, false));
        var forced = Run(Calc(x, y, zoom, iter, true, auto: false));
        var auto = Run(Calc(x, y, zoom, iter, true));
        output.WriteLine($"{name} [{auto.LastPrecisionLabel}]: share {auto.LastFrameSolidCostlyShare:P2}, filled {auto.LastFrameFilledPixels}");
        Assert.False(auto.LastFrameSolidDeclined);
        Assert.True(auto.LastFrameFilledPixels > W * H / 20, "precondition: it fills");
        AssertFrameEqual(forced, auto, name);
        Assert.Equal(forced.LastFrameFilledPixels, auto.LastFrameFilledPixels);

        // The #1343 exactness: unfilled pixels are the full render's; so are filled in-set ones.
        var fm = auto.LastFilledMask!;
        long wrong = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (fm[i] == 0 || off.IterationBuffer[i] >= iter) Assert.True(PixelDiff(off, auto, i) is null, $"pixel {i}: {PixelDiff(off, auto, i)}");
            else wrong++;
        }
        Assert.True(wrong <= W * H / 2000, $"{wrong} filled pixels escape in the full render");
    }

    [Fact]
    public void The_Share_Auto_Decides_On_Is_Measured_On_The_Grid_Rows()
    {
        bool sawDecline = false, sawSubdivide = false;
        foreach (var (name, x, y, zoom, iter) in FreeSet.Concat(CostlySet.Take(2)))
        {
            var off = Run(Calc(x, y, zoom, iter, false));
            var auto = Run(Calc(x, y, zoom, iter, true));
            double expected = ExpectedShare(off);
            output.WriteLine($"{name}: expected {expected:P3}, measured {auto.LastFrameSolidCostlyShare:P3}");
            Assert.Equal(expected, auto.LastFrameSolidCostlyShare, 12);
            Assert.Equal(expected < MandelbrotCalculator.SolidAutoMinCostlyShare, auto.LastFrameSolidDeclined);
            sawDecline |= auto.LastFrameSolidDeclined;
            sawSubdivide |= !auto.LastFrameSolidDeclined;
        }
        Assert.True(sawDecline && sawSubdivide, "precondition: both decisions");
        // Forced, nothing is measured.
        Assert.True(double.IsNaN(Run(Calc(-0.75, 0, 1, 1000, true, auto: false)).LastFrameSolidCostlyShare));
    }

    [Theory]
    [InlineData(-1.7548776662466927, 0, 80, 2000, false)]   // shallow SIMD
    [InlineData(-1.7548776662466927, 0, 1e14, 6000, false)] // deep perturbation
    [InlineData(-0.75, 0, 1, 1000, false)]                  // forced on a cardioid view: many outright tiles
    [InlineData(-0.75, 0, 1, 1000, true)]                   // Auto declines: rows, then the rest
    public void No_Batch_Computes_A_Pixel_Twice_And_Every_Pixel_Is_Computed_Or_Filled(double x, double y, double zoom, int iter, bool auto)
    {
        var on = Run(Calc(x, y, zoom, iter, true, auto));
        var count = on.LastBatchComputeCount!;
        var fm = on.LastFilledMask!;
        long sideLanes = 0, bad = 0;
        for (int i = 0; i < W * H; i++)
        {
            Assert.True(count[i] <= 1, $"pixel {i} computed {count[i]} times");
            if ((count[i] == 1) == (fm[i] == 1)) bad++;   // neither, or both
        }
        Assert.Equal(0, bad);
        // Precondition: the grid's 1-px columns did leave batch neighbours computed.
        int col = MandelbrotCalculator.SolidTileSize;
        for (int yy = 1; yy < H - 1; yy++) if (count[yy * W + col + 1] == 1) sideLanes++;
        Assert.True(auto || sideLanes > 0, "precondition: batch neighbours of the grid columns");
    }

    [Fact]
    public void Every_Pixel_A_Batch_Computed_Equals_The_Full_Render_Even_Inside_A_Filled_Tile()
    {
        // Boundary views, forced: a fill must agree with every computed pixel inside its
        // tile (an escaping one there is a channel the border samples missed).
        var rng = new Random(1394);
        long insideFilledTiles = 0;
        for (int n = 0; n < 10; n++)
        {
            double th = rng.NextDouble() * 2 * Math.PI;
            double cx = Math.Cos(th) / 2 - Math.Cos(2 * th) / 4, cy = Math.Sin(th) / 2 - Math.Sin(2 * th) / 4;
            double zoom = Math.Pow(10, 1 + rng.NextDouble() * 4);
            var off = Run(Calc(cx, cy, zoom, 3000, false));
            var on = Run(Calc(cx, cy, zoom, 3000, true, auto: false));
            var count = on.LastBatchComputeCount!;
            for (int i = 0; i < W * H; i++)
            {
                if (count[i] == 0) continue;
                Assert.True(PixelDiff(off, on, i) is null, $"view {n}: computed pixel {i} differs: {PixelDiff(off, on, i)}");
                int x = i % W;
                if (x > 0 && x + 1 < W && on.LastFilledMask![i - 1] == 1) insideFilledTiles++;
            }
        }
        output.WriteLine($"computed pixels next to a fill: {insideFilledTiles}");
        Assert.True(insideFilledTiles > 0, "precondition: computed pixels sit inside filled tiles");
    }

    // ── Batch parity ───────────────────────────────────────────────────────────

    [Fact]
    public void Auto_Defaults_On_And_The_Snapshot_Param_And_Command_Builder_Carry_It_Off()
    {
        Assert.True(new FractalParameters().SolidRegionAuto);
        Assert.True(new MandelbrotCalculator(64, 64).SolidRegionAuto);
        Assert.Null(RegionFractalParams.Snapshot(FractalType.Mandelbrot, new FractalParameters { SolidRegionSkip = true })!.SolidRegionAuto);

        var p = new FractalParameters { SolidRegionSkip = true, SolidRegionAuto = false };
        Assert.False(p.Clone().SolidRegionAuto);
        var snap = RegionFractalParams.Snapshot(FractalType.Mandelbrot, p)!;
        var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
        var q = new FractalParameters();
        back.ApplyTo(q);
        Assert.False(q.SolidRegionAuto);
        RegionFractalParams.ResetFamilyToDefaults(FractalType.Mandelbrot, q);
        Assert.True(q.SolidRegionAuto);

        var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, Parameters = p,
            FamilyParams = snap.ToKeyValues(),
        };
        string cmd = FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap);
        Assert.Contains("SolidRegionAuto=", cmd);
        var args = new List<string> { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
        var overlay = RegionFractalParams.FromKeyValues(opts.Params, FractalType.Mandelbrot, out var error);
        Assert.True(overlay != null, error);
        var fresh = new FractalParameters();
        overlay!.ApplyTo(fresh);
        Assert.False(fresh.SolidRegionAuto);
        Assert.True(fresh.SolidRegionSkip);
    }

    [Fact]
    public void The_Poster_Honours_Auto_Like_The_Live_Calculator()
    {
        // A boundary view (near elephant valley) Auto declines where the forced skip fills
        // a few escaping filament pixels, so the two differ and the poster's choice shows.
        const double X = 0.30441637880733, Y = 0.030001579209094215, Z = 12306.750158217876;
        FracturingFog.Imaging.PosterRequest Req(bool auto) => new()
        {
            FractalType = FractalType.Mandelbrot, Width = W, Height = H,
            CenterX = X, CenterY = Y, Zoom = Z, MaxIterations = 2000, Quality = QualityPreset.Standard,
            ColorMap = new HsvPalette(), FractalParameters = new FractalParameters { SolidRegionSkip = true, SolidRegionAuto = auto },
            Path = string.Empty,
        };
        MandelbrotCalculator Live(bool auto) { var c = Calc(X, Y, Z, 2000, true, auto); c.Quality = QualityPreset.Standard; return Run(c); }
        var liveAuto = Live(true);
        var liveForced = Live(false);
        Assert.NotEqual(liveForced.ColorBuffer, liveAuto.ColorBuffer);   // precondition
        Assert.Equal(liveAuto.ColorBuffer, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(true), CancellationToken.None, out _, out _));
        Assert.Equal(liveForced.ColorBuffer, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(false), CancellationToken.None, out _, out _));
    }

    [Fact]
    public void The_View_Model_Forwards_It()
    {
        var fp = new FractalParameters();
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(FractalType.Mandelbrot, fp);
        Assert.True(vm.SolidRegionAuto);
        vm.SolidRegionAuto = false;
        Assert.False(fp.SolidRegionAuto);
    }
}

/// <summary>#1394 — the live host hands Auto to its calculator.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1394SolidRegionAutoHostTests
{
    private sealed class Recorder : IFractalRenderer
    {
        private int _n;
        public int Count => Volatile.Read(ref _n);
        public void UpdateTexture(uint[] colorBuffer, int width, int height) => Interlocked.Increment(ref _n);
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "recorder";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static bool Settle(Recorder r, int ms = 60000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int last = -1; long stableSince = 0;
        while (sw.ElapsedMilliseconds < ms)
        {
            int n = r.Count;
            if (n != last) { last = n; stableSince = sw.ElapsedMilliseconds; }
            else if (n >= 1 && sw.ElapsedMilliseconds - stableSince > 1500) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Host_Renders_The_Whole_Set_With_The_Views_Auto_Setting(bool auto)
    {
        var s = new FractalViewState { FractalType = FractalType.Mandelbrot, CenterX = -0.75, Zoom = 1.0, Quality = QualityPreset.Standard };
        s.FractalParameters.SolidRegionSkip = true;
        s.FractalParameters.SolidRegionAuto = auto;
        var r = new Recorder();
        using var host = new FractalRenderHost(r, s, 512, 384, ColorPalette.BuiltIns[0]);
        host.UseGpuCompute = false;
        host.Trigger(progressive: false);
        Assert.True(Settle(r), "no frame");
        var c = host.Mandelbrot;
        Assert.True(c.SolidRegionSkip);
        Assert.Equal(auto, c.SolidRegionAuto);
        Assert.True(c.LastFrameSolidPasses > 0, "precondition: the skip ran");
        Assert.Equal(auto, c.LastFrameSolidDeclined);   // the whole set: Auto declines
    }
}
