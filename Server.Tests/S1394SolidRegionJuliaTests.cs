// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1394 part 2 — solid-region skipping for Julia and Multibrot (EscapeTimeCalculator).
// Invariants, each against an independent full render (the skip off):
//   • every pixel the skip did not fill is byte-identical in every per-pixel buffer
//     (a masked pass of the SIMD or the scalar core computes exactly what a full
//     frame computes);
//   • a filled pixel is byte-identical whenever the full render finds it in-set; the
//     one failure mode (an escaping channel thinner than a pixel) is counted and
//     bounded over random Julia constants;
//   • no SIMD batch computes a pixel twice, and every pixel is computed or filled;
//   • Auto decides on the share this file measures on the full render's grid rows
//     (neither family has a free in-set test), and a frame it declines is the full one;
//   • the families the fill is not safe for (Burning Ship, Tricorn, Phoenix, Magnet,
//     Glynn, Spider), the domain warp, a per-row cap and a small frame run unchanged;
//   • batch parity: the Julia / Multibrot region snapshot, --param and the Command
//     builder carry the option, the poster and the live host honour it, the view
//     model shows it for these families.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1394SolidRegionJuliaTests(ITestOutputHelper output)
{
    private const int W = 320, H = 200;   // multiples of 8: every pixel on a SIMD batch path

    private static readonly Complex Rabbit = new(-0.123, 0.745);

    private static EscapeTimeCalculator Calc(FractalType type, Complex c, int d, double x, double y, double zoom, int iter,
        bool skip, bool auto = false) => new(W, H)
    {
        CenterX = x, CenterY = y, Zoom = zoom, MaxIterations = iter,
        ColorMap = new HsvPalette(), UseGpuCompute = false, FractalType = type, CaptureFilledMask = true,
        FractalParameters = new FractalParameters { JuliaC = c, MultibrotExponent = d, SolidRegionSkip = skip, SolidRegionAuto = auto },
    };

    private static EscapeTimeCalculator Julia(Complex c, double x, double y, double zoom, int iter, bool skip, bool auto = false)
        => Calc(FractalType.Julia, c, 2, x, y, zoom, iter, skip, auto);

    private static EscapeTimeCalculator Run(EscapeTimeCalculator c) { c.Calculate(CancellationToken.None); return c; }

    private static IEnumerable<(string Name, Array A)> Buffers(EscapeTimeCalculator c) => new (string, Array)[]
    {
        ("Color", c.ColorBuffer), ("Iteration", c.IterationBuffer), ("Smooth", c.SmoothBuffer), ("Distance", c.DistanceBuffer),
        ("NormalX", c.NormalXBuffer), ("NormalY", c.NormalYBuffer), ("FinalZr", c.FinalZrBuffer), ("FinalZi", c.FinalZiBuffer),
        ("FinalDr", c.FinalDrBuffer), ("FinalDi", c.FinalDiBuffer),
    };

    private static string? PixelDiff(EscapeTimeCalculator a, EscapeTimeCalculator b, int i)
    {
        foreach (var ((name, x), (_, y)) in Buffers(a).Zip(Buffers(b)))
        {
            if (x.Length != y.Length) return $"{name} length {x.Length} vs {y.Length}";
            if (x.Length <= i) continue;
            if (!Equals(x.GetValue(i), y.GetValue(i))) return $"{name} {x.GetValue(i)} vs {y.GetValue(i)}";
        }
        return null;
    }

    private static void AssertFrameEqual(EscapeTimeCalculator expected, EscapeTimeCalculator actual, string what)
    {
        for (int i = 0; i < W * H; i++)
            if (PixelDiff(expected, actual, i) is { } d) Assert.Fail($"{what}: pixel {i} differs: {d}");
    }

    // (filled, wrong = filled but escaping in the full render); asserts everything else is exact.
    private static (long Filled, long Wrong, long InSet) Compare(EscapeTimeCalculator off, EscapeTimeCalculator on, int maxIt)
    {
        var fm = on.LastFilledMask!;
        long filled = 0, wrong = 0, inSet = 0;
        for (int i = 0; i < W * H; i++)
        {
            bool offIn = off.IterationBuffer[i] >= maxIt;
            if (offIn) inSet++;
            if (fm[i] == 0)
            {
                Assert.True(PixelDiff(off, on, i) is null, $"computed pixel {i} differs from the full render: {PixelDiff(off, on, i)}");
                continue;
            }
            filled++;
            if (offIn) Assert.True(PixelDiff(off, on, i) is null, $"filled in-set pixel {i} differs from the full render: {PixelDiff(off, on, i)}");
            else wrong++;
        }
        Assert.Equal(filled, on.LastFrameFilledPixels);
        return (filled, wrong, inSet);
    }

    [Theory]
    [InlineData("rabbit", FractalType.Julia, -0.123, 0.745, 2, 0.0, 0.0, 1.0, 1000)]
    [InlineData("rabbit at 20", FractalType.Julia, -0.123, 0.745, 2, -0.0884, 0.4, 20.0, 2000)]
    [InlineData("basilica", FractalType.Julia, -1.0, 0.0, 2, 0.0, 0.0, 1.0, 1000)]
    [InlineData("San Marco", FractalType.Julia, -0.75, 0.0, 2, 0.0, 0.0, 1.0, 2000)]
    [InlineData("Multibrot d=3 (SIMD)", FractalType.Multibrot, 0.0, 0.0, 3, 0.0, 0.0, 1.0, 1000)]
    [InlineData("Multibrot d=5 (SIMD)", FractalType.Multibrot, 0.0, 0.0, 5, 0.0, 0.0, 1.0, 1000)]
    [InlineData("Multibrot d=8 (scalar)", FractalType.Multibrot, 0.0, 0.0, 8, 0.0, 0.0, 1.0, 500)]
    public void Frames_Equal_The_Full_Render_Except_Rare_Filled_Channels(
        string name, FractalType type, double cr, double ci, int d, double x, double y, double zoom, int iter)
    {
        var off = Run(Calc(type, new(cr, ci), d, x, y, zoom, iter, false));
        var on = Run(Calc(type, new(cr, ci), d, x, y, zoom, iter, true));
        var (filled, wrong, inSet) = Compare(off, on, iter);
        output.WriteLine($"{name}: in-set {inSet}, filled {filled} in {on.LastFrameSolidPasses} passes, filled but escaping {wrong}");
        Assert.True(on.LastFrameSolidPasses > 0);
        Assert.True(filled > inSet / 10, $"precondition: the skip fills ({filled} of {inSet} in-set)");
        Assert.True(wrong <= W * H / 2000, $"{wrong} filled pixels escape in the full render");   // ≤ 0.05 %
    }

    [Fact]
    public void The_Scalar_Core_Computes_Only_The_Marked_Pixels()
    {
        // Multibrot d = 8 runs the scalar core. At this view the forced skip fills 4
        // pixels a full render finds escaping: a pass that recomputed unmarked pixels
        // would overwrite those fills with the full render's bytes.
        const double X = 0.7605099599367112, Y = -0.24824302777129925, Z = 40.96934627362424;
        var off = Run(Calc(FractalType.Multibrot, default, 8, X, Y, Z, 600, false));
        var on = Run(Calc(FractalType.Multibrot, default, 8, X, Y, Z, 600, true));
        var (filled, wrong, _) = Compare(off, on, 600);
        output.WriteLine($"scalar Multibrot: filled {filled}, filled but escaping {wrong}");
        Assert.True(wrong > 0, "a filled channel survived the later passes");
    }

    [Fact]
    public void Random_Julia_Constants_Keep_Wrong_Fills_Rare()
    {
        // Connected Julia sets with an attracting cycle (c inside the main cardioid or the
        // period-2 bulb), so there is an inside to fill, at a few zooms.
        var rng = new Random(13942);
        long totalFilled = 0, totalWrong = 0, frames = 0;
        for (int n = 0; n < 12; n++)
        {
            double th = rng.NextDouble() * 2 * Math.PI, r = 0.8 + 0.19 * rng.NextDouble();
            var c = n % 3 == 2
                ? new Complex(-1 + r / 4 * Math.Cos(th), r / 4 * Math.Sin(th))
                : r * new Complex(Math.Cos(th) / 2 - Math.Cos(2 * th) / 4, Math.Sin(th) / 2 - Math.Sin(2 * th) / 4);
            double zoom = Math.Pow(10, rng.NextDouble() * 1.5);
            double x = (rng.NextDouble() - 0.5) * 0.6, y = (rng.NextDouble() - 0.5) * 0.6;
            var off = Run(Julia(c, x, y, zoom, 2000, false));
            var on = Run(Julia(c, x, y, zoom, 2000, true));
            var (f, wr, _) = Compare(off, on, 2000);
            totalFilled += f; totalWrong += wr; frames++;
        }
        output.WriteLine($"{frames} views: filled {totalFilled}, filled but escaping {totalWrong} ({totalWrong / (double)(frames * W * H):P4} of all pixels)");
        Assert.True(totalFilled > W * H);                                  // precondition: the skip fills
        Assert.True(totalWrong <= frames * W * H / 2000, $"{totalWrong} wrong fills");
    }

    [Theory]
    [InlineData(FractalType.Julia, 2, false)]
    [InlineData(FractalType.Julia, 2, true)]
    [InlineData(FractalType.Multibrot, 4, false)]
    [InlineData(FractalType.Multibrot, 8, false)]   // the scalar core
    public void No_Batch_Computes_A_Pixel_Twice_And_Every_Pixel_Is_Computed_Or_Filled(FractalType type, int d, bool auto)
    {
        var c = type == FractalType.Julia ? Rabbit : default;
        var on = Run(Calc(type, c, d, 0, 0, 1, 1000, true, auto));
        var count = on.LastBatchComputeCount!;
        var fm = on.LastFilledMask!;
        Assert.True(on.LastFrameFilledPixels > 0, "precondition: the skip fills");
        for (int i = 0; i < W * H; i++)
        {
            Assert.True(count[i] <= 1, $"pixel {i} computed {count[i]} times");
            Assert.True(count[i] == 1 ^ fm[i] == 1, $"pixel {i}: computed {count[i]}, filled {fm[i]}");
        }
        if (d == 8) return;   // the scalar core computes no batch neighbours
        // The batch neighbours of a 1-px grid column were computed with it, not again.
        int sideLanes = 0, col = MandelbrotCalculator.SolidTileSize;
        for (int yy = 1; yy < H - 1; yy++) if (count[yy * W + col + 1] == 1) sideLanes++;
        Assert.True(sideLanes > 0, "precondition: batch neighbours of the grid columns");
    }

    [Fact]
    public void The_Simd_Tail_Pixels_Of_An_Odd_Width_Are_Computed_Once_And_Exactly()
    {
        // 323 px: the last 3 (AVX2) or 3–7 columns of each row run the SIMD core's scalar tail.
        const int w = 323;
        EscapeTimeCalculator Make(bool skip) => new(w, H)
        {
            Zoom = 1, MaxIterations = 1000, ColorMap = new HsvPalette(), UseGpuCompute = false,
            FractalType = FractalType.Julia, CaptureFilledMask = true,
            FractalParameters = new FractalParameters { JuliaC = new(-1, 0), SolidRegionSkip = skip, SolidRegionAuto = false },
        };
        var off = Make(false); off.Calculate(CancellationToken.None);
        var on = Make(true); on.Calculate(CancellationToken.None);
        Assert.True(on.LastFrameFilledPixels > 0, "precondition: the skip fills");
        var count = on.LastBatchComputeCount!;
        var fm = on.LastFilledMask!;
        for (int i = 0; i < w * H; i++)
        {
            Assert.True(count[i] == 1 ^ fm[i] == 1, $"pixel ({i % w}, {i / w}): computed {count[i]}, filled {fm[i]}");
            if (fm[i] == 0 || off.IterationBuffer[i] >= 1000)
                Assert.Equal(off.ColorBuffer[i], on.ColorBuffer[i]);
        }
    }

    // From the full render: the share of the grid rows' pixels in runs of ≥ 2·MinTile
    // in-set pixels (Julia / Multibrot have no free in-set test).
    private static double ExpectedShare(EscapeTimeCalculator off)
    {
        var rows = new List<int>();
        for (int v = 0; v < H - 1; v += MandelbrotCalculator.SolidTileSize) rows.Add(v);
        rows.Add(H - 1);
        long costly = 0;
        foreach (int y in rows)
        {
            int run = 0;
            for (int x = 0; x <= W; x++)
            {
                if (x < W && off.IterationBuffer[y * W + x] >= off.MaxIterations) { run++; continue; }
                if (run >= 2 * MandelbrotCalculator.SolidMinTile) costly += run;
                run = 0;
            }
        }
        return costly / (double)(rows.Count * W);
    }

    [Theory]
    [InlineData(-0.123, 0.745)]     // rabbit: subdivides
    [InlineData(0.0, 1.0)]          // dendrite (c = i): no inside, declines
    [InlineData(0.5, 0.0)]          // Cantor dust: declines
    [InlineData(-0.7, 0.27015)]     // the default constant: a thin inside
    public void Auto_Decides_On_The_Full_Renders_Grid_Rows_And_A_Declined_Frame_Is_The_Full_One(double cr, double ci)
    {
        var off = Run(Julia(new(cr, ci), 0, 0, 1, 1000, false));
        var on = Run(Julia(new(cr, ci), 0, 0, 1, 1000, true, auto: true));
        double expected = ExpectedShare(off);
        output.WriteLine($"c = {cr} + {ci}i: share {expected:P2}, declined {on.LastFrameSolidDeclined}");
        Assert.Equal(expected, on.LastFrameSolidCostlyShare, 12);
        Assert.Equal(expected < MandelbrotCalculator.SolidAutoMinCostlyShare, on.LastFrameSolidDeclined);
        if (on.LastFrameSolidDeclined)
        {
            Assert.Equal(0, on.LastFrameFilledPixels);
            AssertFrameEqual(off, on, "declined frame");
        }
        else Compare(off, on, 1000);
    }

    [Fact]
    public void Auto_Declines_The_Dendrite_And_Keeps_The_Rabbit()
    {
        Assert.True(Run(Julia(new(0, 1), 0, 0, 1, 1000, true, auto: true)).LastFrameSolidDeclined);
        Assert.False(Run(Julia(Rabbit, 0, 0, 1, 1000, true, auto: true)).LastFrameSolidDeclined);
    }

    [Fact]
    public void Whole_Frame_Passes_Run_Once_On_The_Finished_Frame()
    {
        // Interior alpha multiplies the in-set alpha: run per masked pass it would compound.
        var off = Julia(Rabbit, 0, 0, 1, 1000, false); off.InteriorAlpha = 128; Run(off);
        var on = Julia(Rabbit, 0, 0, 1, 1000, true); on.InteriorAlpha = 128; Run(on);
        Assert.True(on.LastFrameFilledPixels > 0);
        var (_, wrong, _) = Compare(off, on, 1000);
        Assert.Equal(0, wrong);
        Assert.Contains(on.ColorBuffer, c => c >> 24 == 128);   // precondition: the stamp ran
    }

    [Theory]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Tricorn)]
    [InlineData(FractalType.Phoenix)]
    [InlineData(FractalType.Magnet1)]
    [InlineData(FractalType.Glynn)]
    [InlineData(FractalType.Spider)]
    public void Families_The_Fill_Is_Not_Safe_For_Run_Unchanged(FractalType type)
    {
        var off = Run(Calc(type, Rabbit, 3, 0, 0, 1, 500, false));
        var on = Run(Calc(type, Rabbit, 3, 0, 0, 1, 500, true));
        Assert.Equal(0, on.LastFrameSolidPasses);   // not even the grid (it runs whatever the inside)
        AssertFrameEqual(off, on, type.ToString());
    }

    [Fact]
    public void Off_The_Domain_Warp_A_Per_Row_Cap_And_A_Small_Frame_Run_Unchanged()
    {
        var off = Run(Julia(Rabbit, 0, 0, 1, 1000, false));
        Assert.Equal(0, off.LastFrameSolidPasses);
        Assert.Null(off.LastFilledMask);

        // #253 — a warped pixel no longer maps linearly to its plane point.
        var warp = Julia(Rabbit, 0, 0, 1, 1000, true);
        warp.FractalParameters.DomainWarpEnabled = true;
        warp.FractalParameters.DomainWarpStrength = 0.3;
        Run(warp);
        Assert.Equal(0, warp.LastFrameSolidPasses);

        // A per-row iteration cap (video): border and interior would be different sets.
        var capped = Julia(Rabbit, 0, 0, 1, 1000, true);
        capped.PerRowMaxIter = Enumerable.Repeat(1000, H).ToArray();
        Run(capped);
        Assert.Equal(0, capped.LastFrameSolidPasses);

        // Too small for the 64-px grid.
        var small = new EscapeTimeCalculator(100, 80)
        {
            Zoom = 1, MaxIterations = 500, ColorMap = new HsvPalette(), FractalType = FractalType.Julia,
            FractalParameters = new FractalParameters { JuliaC = Rabbit, SolidRegionSkip = true },
        };
        small.Calculate(CancellationToken.None);
        Assert.Equal(0, small.LastFrameSolidPasses);
    }

    // ── Batch parity ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.Multibrot)]
    public void The_Snapshot_Param_And_Command_Builder_Carry_The_Option(FractalType type)
    {
        var stock = RegionFractalParams.Snapshot(type, new FractalParameters())!;
        Assert.Null(stock.SolidRegionSkip);
        Assert.Null(stock.SolidRegionAuto);
        var p = new FractalParameters { SolidRegionSkip = true, SolidRegionAuto = false };
        var snap = RegionFractalParams.Snapshot(type, p)!;
        var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
        var q = new FractalParameters();
        back.ApplyTo(q);
        Assert.True(q.SolidRegionSkip);
        Assert.False(q.SolidRegionAuto);
        RegionFractalParams.ResetFamilyToDefaults(type, q);
        Assert.False(q.SolidRegionSkip);
        Assert.True(q.SolidRegionAuto);

        var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot
        {
            Fractal = type, Parameters = p,
            FamilyParams = snap.ToKeyValues(),
        };
        string cmd = FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap);
        Assert.Contains("SolidRegionSkip=", cmd);
        Assert.Contains("SolidRegionAuto=", cmd);
        var args = new List<string> { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
        var overlay = RegionFractalParams.FromKeyValues(opts.Params, type, out var error);
        Assert.True(overlay != null, error);
        var fresh = new FractalParameters();
        overlay!.ApplyTo(fresh);
        Assert.True(fresh.SolidRegionSkip);
        Assert.False(fresh.SolidRegionAuto);
    }

    [Fact]
    public void The_Poster_Honours_It_Like_The_Live_Calculator()
    {
        // A Julia view (c near the cardioid) where the skip fills 3 escaping channel pixels,
        // so on and off differ and the poster's choice is visible.
        var c = new Complex(-0.11100247693438625, 0.6445276781094681);
        const double X = -0.2009093201723459, Y = -0.15974818345147568, Z = 1.2799534108965132;
        FracturingFog.Imaging.PosterRequest Req(bool skip) => new()
        {
            FractalType = FractalType.Julia, Width = W, Height = H,
            CenterX = X, CenterY = Y, Zoom = Z, MaxIterations = 3000, Quality = QualityPreset.Standard,
            ColorMap = new HsvPalette(), FractalParameters = new FractalParameters { JuliaC = c, SolidRegionSkip = skip, SolidRegionAuto = false },
            Path = string.Empty,
        };
        EscapeTimeCalculator Live(bool skip) { var e = Julia(c, X, Y, Z, 3000, skip); e.Quality = QualityPreset.Standard; return Run(e); }
        var liveOn = Live(true);
        var liveOff = Live(false);
        Assert.NotEqual(liveOff.ColorBuffer, liveOn.ColorBuffer);   // precondition
        Assert.Equal(liveOn.ColorBuffer, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(true), CancellationToken.None, out _, out _));
        Assert.Equal(liveOff.ColorBuffer, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(false), CancellationToken.None, out _, out _));
    }

    [Fact]
    public void The_View_Model_Shows_It_For_The_Families_With_A_Full_Inside()
    {
        foreach (var t in new[] { FractalType.Mandelbrot, FractalType.Julia, FractalType.Multibrot })
            Assert.True(new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(t, new FractalParameters()).HasSolidRegionSkip, t.ToString());
        foreach (var t in new[] { FractalType.BurningShip, FractalType.Tricorn, FractalType.Phoenix })
            Assert.False(new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(t, new FractalParameters()).HasSolidRegionSkip, t.ToString());
        var fp = new FractalParameters();
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(FractalType.Julia, fp);
        vm.SolidRegionSkip = true;
        Assert.True(fp.SolidRegionSkip);
    }
}

/// <summary>#1394 part 2 — the live host hands the option to its Julia calculator.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1394SolidRegionJuliaHostTests
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
    public void The_Host_Renders_A_Julia_Set_With_The_Views_Setting(bool skip)
    {
        var s = new FractalViewState { FractalType = FractalType.Julia, CenterX = 0, Zoom = 1.0, Quality = QualityPreset.Standard };
        s.FractalParameters.JuliaC = new Complex(-0.123, 0.745);
        s.FractalParameters.SolidRegionSkip = skip;
        var r = new Recorder();
        using var host = new FractalRenderHost(r, s, 512, 384, ColorPalette.BuiltIns[0]);
        host.UseGpuCompute = false;
        host.Trigger(progressive: false);
        Assert.True(Settle(r), "no frame");
        var e = Assert.IsType<EscapeTimeCalculator>(host.ActiveAltCalculator);
        Assert.Equal(skip, e.LastFrameSolidPasses > 0);
        Assert.Equal(skip, e.LastFrameFilledPixels > 0);
    }
}
