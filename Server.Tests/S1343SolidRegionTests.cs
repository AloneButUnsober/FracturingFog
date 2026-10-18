// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1343 — solid-region skipping (Mariani–Silver) on the CPU Mandelbrot paths.
// Invariants, each against an independent full render (the skip off):
//   • every pixel the skip did not fill is byte-identical in every per-pixel buffer
//     (a masked pass computes exactly what a full frame computes);
//   • a filled pixel is byte-identical whenever the full render finds it in-set; the
//     one failure mode — an escaping filament thinner than a pixel crossing a tile
//     between border samples — is counted and bounded over random views;
//   • an interior theme (it colours each in-set pixel by its own cycle) runs
//     unchanged: no tile border could ever be uniform;
//   • the deep perturbation path fills too, with the same exactness;
//   • off, a frame with its own pixel mask, an orbit theme and a small frame run
//     unchanged; batch parity: the region snapshot / --param / Command builder carry
//     the option, the poster honours it like the live calculator.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1343SolidRegionTests(ITestOutputHelper output)
{
    private const int W = 320, H = 200;

    private static MandelbrotCalculator Calc(double x, double y, double zoom, int iter, bool skip, IColorMap? map = null) => new(W, H)
    {
        CenterX = x, CenterY = y, Zoom = zoom, MaxIterations = iter,
        ColorMap = map ?? new HsvPalette(), UseGpuCompute = false, SolidRegionSkip = skip, CaptureFilledMask = true,
    };

    private static MandelbrotCalculator Run(MandelbrotCalculator c) { c.Calculate(CancellationToken.None); return c; }

    // Every per-pixel output a frame defines at pixel i (the interior ones only for an
    // in-set pixel under an interior theme: otherwise the calculator leaves them
    // uninitialised).
    private static IEnumerable<(string Name, Array A)> Buffers(MandelbrotCalculator c, int i)
    {
        var l = new List<(string, Array)>
        {
            ("Color", c.ColorBuffer), ("Iteration", c.IterationBuffer), ("Smooth", c.SmoothBuffer), ("Distance", c.DistanceBuffer),
            ("NormalX", c.NormalXBuffer), ("NormalY", c.NormalYBuffer), ("FinalZr", c.FinalZrBuffer), ("FinalZi", c.FinalZiBuffer),
            ("FinalDr", c.FinalDrBuffer), ("FinalDi", c.FinalDiBuffer),
        };
        if (c.ColorMap is IInteriorAwareColorMap && c.IterationBuffer[i] >= c.MaxIterations)
            l.AddRange(new (string, Array)[] { ("InteriorPeriod", c.InteriorPeriodBuffer), ("AttractorZr", c.AttractorZrBuffer),
                ("AttractorZi", c.AttractorZiBuffer), ("MultiplierMag", c.MultiplierMagBuffer) });
        return l;
    }

    // The name of the first buffer whose pixel i differs (null = identical).
    private static string? PixelDiff(MandelbrotCalculator a, MandelbrotCalculator b, int i)
    {
        foreach (var ((name, x), (_, y)) in Buffers(a, i).Zip(Buffers(b, i)))
        {
            if (x.Length != y.Length) return $"{name} length {x.Length} vs {y.Length}";
            if (x.Length <= i) continue;
            if (!Equals(x.GetValue(i), y.GetValue(i))) return $"{name} {x.GetValue(i)} vs {y.GetValue(i)}";
        }
        return null;
    }

    // (filled, wrong = filled but escaping in the full render); asserts everything else is exact.
    private (long Filled, long Wrong, long InSet) Compare(MandelbrotCalculator off, MandelbrotCalculator on, int maxIt)
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
    [InlineData(-0.75, 0, 1, 1000)]                        // whole set
    [InlineData(-1.7548776662466927, 0, 80, 2000)]          // period-3 minibrot
    [InlineData(-0.1592, 1.0317, 40, 2000)]                 // a satellite bulb region
    [InlineData(-1.75, 0, 1e5, 20000)]                      // parabolic cusp: slow interior
    [InlineData(-0.7436, 0.1318, 60, 2000)]                 // seahorse valley
    public void Shallow_Frames_Equal_The_Full_Render_Except_Rare_Filled_Filaments(double x, double y, double zoom, int iter)
    {
        var off = Run(Calc(x, y, zoom, iter, false));
        var on = Run(Calc(x, y, zoom, iter, true));
        Assert.Equal("SP", on.LastPrecisionLabel);
        var (filled, wrong, inSet) = Compare(off, on, iter);
        output.WriteLine($"({x}, {y}) zoom {zoom}: in-set {inSet}, filled {filled} in {on.LastFrameSolidPasses} passes, filled but escaping {wrong}");
        Assert.True(on.LastFrameSolidPasses > 0);
        Assert.True(wrong <= W * H / 2000, $"{wrong} filled pixels escape in the full render");   // ≤ 0.05 %
    }

    [Fact]
    public void Random_Views_Near_The_Boundary_Keep_Wrong_Fills_Rare()
    {
        var rng = new Random(1343);
        long totalFilled = 0, totalWrong = 0, frames = 0;
        for (int n = 0; n < 12; n++)
        {
            // A point near the boundary: walk from inside the cardioid outward until a pixel escapes.
            double th = rng.NextDouble() * 2 * Math.PI;
            double cx = Math.Cos(th) / 2 - Math.Cos(2 * th) / 4, cy = Math.Sin(th) / 2 - Math.Sin(2 * th) / 4;
            double zoom = Math.Pow(10, 1 + rng.NextDouble() * 4);
            var off = Run(Calc(cx, cy, zoom, 3000, false));
            var on = Run(Calc(cx, cy, zoom, 3000, true));
            var (f, wr, _) = Compare(off, on, 3000);
            totalFilled += f; totalWrong += wr; frames++;
        }
        output.WriteLine($"{frames} views: filled {totalFilled}, filled but escaping {totalWrong} ({totalWrong / (double)(frames * W * H):P4} of all pixels)");
        Assert.True(totalFilled > W * H);                                  // precondition: the skip fills
        Assert.True(totalWrong <= frames * W * H / 2000, $"{totalWrong} wrong fills");
    }

    [Fact]
    public void The_Deep_Path_Fills_With_The_Same_Exactness()
    {
        double th = 2 * Math.PI * 0.6180339887498949;
        double sx = Math.Cos(th) / 2 - Math.Cos(2 * th) / 4, sy = Math.Sin(th) / 2 - Math.Sin(2 * th) / 4;
        foreach (var (x, y) in new[] { (sx, sy), (-1.7548776662466927, 0.0) })
        {
            var off = Run(Calc(x, y, 1e14, 6000, false));
            var on = Run(Calc(x, y, 1e14, 6000, true));
            Assert.NotEqual("SP", on.LastPrecisionLabel);   // precondition: the deep path
            var (filled, wrong, _) = Compare(off, on, 6000);
            output.WriteLine($"deep ({x}, {y}) [{on.LastPrecisionLabel}]: filled {filled}, wrong {wrong}");
            Assert.True(filled > W * H / 2);
            Assert.Equal(0, wrong);
        }
    }

    [Fact]
    public void An_Interior_Theme_Runs_Unchanged()
    {
        // The multiplier theme colours each in-set pixel by its own cycle.
        var off = Run(Calc(-0.75, 0, 1, 1000, false, new MultiplierMap()));
        var on = Run(Calc(-0.75, 0, 1, 1000, true, new MultiplierMap()));
        var inSetColours = Enumerable.Range(0, W * H).Where(i => off.IterationBuffer[i] >= 1000).Select(i => off.ColorBuffer[i]).Distinct().Count();
        Assert.True(inSetColours > 100, $"precondition: the interior colour varies ({inSetColours})");
        Assert.Equal(0, on.LastFrameSolidPasses);
        Assert.Equal(off.ColorBuffer, on.ColorBuffer);
    }

    [Fact]
    public void Whole_Frame_Passes_Run_Once_On_The_Finished_Frame()
    {
        // Interior alpha multiplies the in-set alpha: run per masked pass it would compound.
        var off = Calc(-1.7548776662466927, 0, 80, 2000, false); off.InteriorAlpha = 128; Run(off);
        var on = Calc(-1.7548776662466927, 0, 80, 2000, true); on.InteriorAlpha = 128; Run(on);
        Assert.True(on.LastFrameFilledPixels > 0);
        var (_, wrong, _) = Compare(off, on, 2000);
        Assert.Equal(0, wrong);
        Assert.Contains(on.ColorBuffer, c => c >> 24 == 128);   // precondition: the stamp ran

        // A neighbourhood post-process theme (edge detection over the finished colours)
        // must see the whole frame once: the frame equals the full one exactly.
        var pOff = Run(Calc(-1.7548776662466927, 0, 80, 2000, false, new EntropyEdgeMap()));
        var pOn = Run(Calc(-1.7548776662466927, 0, 80, 2000, true, new EntropyEdgeMap()));
        Assert.True(pOn.LastFrameFilledPixels > 0);
        Assert.Equal(pOff.ColorBuffer, pOn.ColorBuffer);
    }

    [Fact]
    public void Off_A_Masked_Frame_An_Orbit_Theme_And_A_Small_Frame_Run_Unchanged()
    {
        var off = Run(Calc(-0.75, 0, 1, 1000, false));
        Assert.Equal(0, off.LastFrameSolidPasses);
        Assert.Null(off.LastFilledMask);

        // A frame with its own pixel mask (#1344 adaptive AA / #1330 pan) is left to it.
        var mask = new byte[W * H]; for (int i = 0; i < mask.Length; i += 7) mask[i] = 1;
        var a = Calc(-0.75, 0, 1, 1000, false); a.PixelMask = mask; Run(a);
        var b = Calc(-0.75, 0, 1, 1000, true); b.PixelMask = mask; Run(b);
        Assert.Equal(0, b.LastFrameSolidPasses);
        for (int i = 0; i < mask.Length; i++)   // unmarked pixels keep whatever the buffers held
            if (mask[i] != 0) Assert.Equal(a.ColorBuffer[i], b.ColorBuffer[i]);

        // Orbit themes take a path without the mask.
        var orbit = Run(Calc(-0.75, 0, 1, 500, true, new OrbitTrapPointMap()));
        Assert.Equal(0, orbit.LastFrameSolidPasses);

        // Too small for the 64-px grid.
        var small = new MandelbrotCalculator(100, 80) { CenterX = -0.75, Zoom = 1, MaxIterations = 500, ColorMap = new HsvPalette(), SolidRegionSkip = true };
        small.Calculate(CancellationToken.None);
        Assert.Equal(0, small.LastFrameSolidPasses);
    }

    // ── Batch parity ───────────────────────────────────────────────────────────

    [Fact]
    public void The_Snapshot_Param_And_Command_Builder_Carry_The_Option()
    {
        Assert.Null(RegionFractalParams.Snapshot(FractalType.Mandelbrot, new FractalParameters()));
        var p = new FractalParameters { SolidRegionSkip = true };
        Assert.True(p.Clone().SolidRegionSkip);
        var snap = RegionFractalParams.Snapshot(FractalType.Mandelbrot, p)!;
        var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
        var q = new FractalParameters();
        back.ApplyTo(q);
        Assert.True(q.SolidRegionSkip);
        RegionFractalParams.ResetFamilyToDefaults(FractalType.Mandelbrot, q);
        Assert.False(q.SolidRegionSkip);

        var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, Parameters = p,
            FamilyParams = snap.ToKeyValues(),
        };
        string cmd = FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap);
        Assert.Contains("SolidRegionSkip=", cmd);
        var args = new List<string> { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
        var overlay = RegionFractalParams.FromKeyValues(opts.Params, FractalType.Mandelbrot, out var error);
        Assert.True(overlay != null, error);
        var fresh = new FractalParameters();
        overlay!.ApplyTo(fresh);
        Assert.True(fresh.SolidRegionSkip);
    }

    [Fact]
    public void The_Poster_Honours_It_Like_The_Live_Calculator()
    {
        // A boundary view where the skip fills a few escaping filament pixels, so on and
        // off differ and the poster's choice is visible.
        const double X = 0.14300170536992562, Y = -0.5808130077977705, Z = 73664.51036716906;
        FracturingFog.Imaging.PosterRequest Req(bool skip) => new()
        {
            FractalType = FractalType.Mandelbrot, Width = W, Height = H,
            CenterX = X, CenterY = Y, Zoom = Z, MaxIterations = 3000, Quality = QualityPreset.Standard,
            ColorMap = new HsvPalette(), FractalParameters = new FractalParameters { SolidRegionSkip = skip }, Path = string.Empty,
        };
        MandelbrotCalculator Live(bool skip) { var c = Calc(X, Y, Z, 3000, skip); c.Quality = QualityPreset.Standard; return Run(c); }
        var liveOn = Live(true);
        var liveOff = Live(false);
        Assert.NotEqual(liveOff.ColorBuffer, liveOn.ColorBuffer);   // precondition
        Assert.Equal(liveOn.ColorBuffer, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(true), CancellationToken.None, out _, out _));
        Assert.Equal(liveOff.ColorBuffer, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(false), CancellationToken.None, out _, out _));
    }

    [Fact]
    public void The_View_Model_Forwards_The_Option()
    {
        var fp = new FractalParameters();
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(FractalType.Mandelbrot, fp);
        Assert.False(vm.SolidRegionSkip);
        vm.SolidRegionSkip = true;
        Assert.True(fp.SolidRegionSkip);
    }
}
