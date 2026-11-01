// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1489 — the PrimeTerrace relief height source: escaped pixel with escape count n
// gets π(n) + ramp·exp(−d / (span·falloff)), in-set pixels π(maxIter) + ramp. The
// oracle's π is an independent trial-division count, and the escape counts come from
// the calculator's own IterationBuffer. Plus batch / builder / region / dialog parity.

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefPrimeTerraceSourceTests
{
    private static bool IsPrime(int n)
    {
        if (n < 2) return false;
        for (int d = 2; d * d <= n; d++) if (n % d == 0) return false;
        return true;
    }

    private static int Pi(int n) => Enumerable.Range(2, Math.Max(0, n - 1)).Count(IsPrime);

    private static float[] Build(float[] smooth, int[] iter, int maxIter, float[]? dist, int w, int h, double scale,
                                 double falloff = 0.02, double ramp = 1.0)
        => ReliefHeightField.Build(smooth, null, w * h, ReliefHeightSource.PrimeTerrace, 0.5,
                                   dist, w, h, scale, falloff, iter, maxIter, ramp);

    [Fact]
    public void Treads_StepAtEachPrime_AndTheSetIsAPlateau()
    {
        const int maxIter = 40, w = 41, h = 1;
        var iter = Enumerable.Range(0, w).ToArray();            // n = 0..40; 40 = in set
        var smooth = iter.Select(n => n < maxIter ? n + 0.5f : 0f).ToArray();
        var hgt = Build(smooth, iter, maxIter, null, w, h, 1.0, ramp: 1.0);
        for (int n = 0; n < maxIter; n++)
            Assert.Equal(Pi(n), hgt[n]);                         // no distance field → no ramp
        Assert.Equal(Pi(maxIter) + 1.0f, hgt[maxIter]);          // plateau = top terrace + ramp
        // Tread width = prime gap: the height changes exactly at primes.
        for (int n = 1; n < maxIter; n++)
            Assert.Equal(IsPrime(n), hgt[n] != hgt[n - 1]);
    }

    [Fact]
    public void Ramp_FollowsTheDistanceEstimate()
    {
        const int w = 4, h = 2;          // span = 0.5 · min(w,h) = 1
        var iter = new[] { 7, 7, 7, 7, 7, 7, 7, 7 };
        var dist = new float[] { 0.001f, 0.02f, 0.1f, 0f, 0.001f, 0.001f, 0.001f, 0.001f };
        var hgt = Build(new float[8], iter, 100, dist, w, h, 0.5, falloff: 0.02, ramp: 2.0);
        Assert.Equal(Pi(7) + 2.0 * Math.Exp(-0.001 / 0.02), hgt[0], 4);
        Assert.Equal(Pi(7) + 2.0 * Math.Exp(-1.0), hgt[1], 4);  // d = falloff·span → e^-1
        Assert.Equal(Pi(7), hgt[3], 4);                          // no estimate → no ramp
        Assert.True(hgt[0] > hgt[1] && hgt[1] > hgt[2], "the tread tilts up toward the set");
    }

    [Fact]
    public void FallsBackToSmooth_WithoutEscapeCounts()
    {
        var smooth = new float[] { 0, 5, 6, 7 };
        Assert.Same(smooth, ReliefHeightField.Build(smooth, null, 4, ReliefHeightSource.PrimeTerrace, 0.5, null, 2, 2, 1.0, 0.02));
    }

    // Real render: the calculators expose their escape counts, and every escaped pixel
    // sits on its own tread: π(n) ≤ height ≤ π(n) + ramp.
    [Fact]
    public void MandelbrotRender_EveryPixelOnItsTread()
    {
        const int w = 160, h = 90, maxIter = 200;
        var c = new MandelbrotCalculator(w, h) { CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = maxIter, ColorMap = new MonoBandMap() };
        c.Calculate(default);
        var src = Assert.IsAssignableFrom<IIterationFieldSource>(c);
        var hgt = Build(c.SmoothBuffer, src.IterationBuffer, src.MaxIterations, c.DistanceBuffer, w, h, c.DistancePixelScale, ramp: 1.5);
        int escaped = 0;
        for (int i = 0; i < w * h; i++)
        {
            int n = c.IterationBuffer[i];
            if (n >= maxIter) { Assert.Equal(Pi(maxIter) + 1.5f, hgt[i]); continue; }
            Assert.InRange(hgt[i], Pi(n) - 1e-4f, Pi(n) + 1.5f + 1e-4f);
            escaped++;
        }
        Assert.True(escaped > w * h / 4);
        Assert.IsAssignableFrom<IIterationFieldSource>(new EscapeTimeCalculator(8, 8));
    }

    // Batch / poster path: below the field floor the poster builds its own hi-res
    // field — it must carry the PrimeTerrace source (integer treads), not raw counts.
    [Fact]
    public void PosterHiResField_CarriesTheTerraces()
    {
        var fp = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true,
            Relief2DHeightSource = ReliefHeightSource.PrimeTerrace, Relief2DTerraceRamp = 0.0,
        };
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = 150,
            Width = 160, Height = 90, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard,
            FractalParameters = fp, Path = "unused.png", Format = ImageFileFormat.Png,
        };
        var field = PosterRenderer.ResolveReliefField(req, 160, 90, default, out int fw, out int fh);
        Assert.NotNull(field);
        Assert.True(fw > 160 && fh > 90);
        // Ramp 0: every height is a whole number of steps, topped by the plateau π(150).
        Assert.All(field!, v => Assert.Equal(Math.Round(v), v, 5));
        Assert.Equal(Pi(150), field.Max());
    }

    [Fact]
    public void Batch_Builder_Region_Dialog_CarryTheSource()
    {
        string[] Argv(params string[] extra) => new[] { "FracturingFog", "--batch", "--fractal", "Mandelbrot", "--x", "-0.5", "--y", "0",
                                                        "--zoom", "1", "--relief-raymarch" }.Concat(extra).Concat(new[] { "--out", "o.png" }).ToArray();
        Assert.True(BatchOptions.TryParse(Argv("--relief-height-source", "primeterrace", "--relief-terrace-ramp", "2.5",
                                               "--relief-distance-falloff", "0.05"), 2, out var o, out var e), e);
        Assert.Equal(ReliefHeightSource.PrimeTerrace, o.ReliefHeightSource);
        Assert.Equal(2.5, o.ReliefTerraceRamp);
        Assert.Equal(0.05, o.ReliefDistanceFalloff);
        Assert.False(BatchOptions.TryParse(Argv("--relief-terrace-ramp", "9"), 2, out _, out var e1));
        Assert.Contains("--relief-terrace-ramp", e1);

        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
            ReliefHeightSource = ReliefHeightSource.PrimeTerrace, ReliefTerraceRamp = 3.0, ReliefDistanceFalloff = 0.08,
        };
        var report = BatchCommandBuilder.BuildWithReport(snap);
        var back = new[] { "FracturingFog", "--batch" }.Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "o.png" : a)).ToArray();
        Assert.True(BatchOptions.TryParse(back, 2, out var ob, out var eb), eb);
        Assert.Equal(ReliefHeightSource.PrimeTerrace, ob.ReliefHeightSource);
        Assert.Equal(3.0, ob.ReliefTerraceRamp);
        Assert.Equal(0.08, ob.ReliefDistanceFalloff);
        Assert.DoesNotContain("--relief-terrace-ramp", BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
            ReliefHeightSource = ReliefHeightSource.PrimeTerrace,   // default ramp 1 → not emitted
        }));

        var srcP = new FractalParameters { Relief2DEnabled = true, Relief2DHeightSource = ReliefHeightSource.PrimeTerrace, Relief2DTerraceRamp = 4.0 };
        Assert.Equal(4.0, srcP.Clone().Relief2DTerraceRamp);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<Relief3DSettings>(JsonSerializer.Serialize(Relief3DSettings.Snapshot(srcP)))!.ApplyTo(dst);
        Assert.Equal(ReliefHeightSource.PrimeTerrace, dst.Relief2DHeightSource);
        Assert.Equal(4.0, dst.Relief2DTerraceRamp);

        var p = new FractalParameters();
        var vm = new FractalParamsViewModel(FractalType.Mandelbrot, p);
        Assert.False(vm.Relief2DTerraceRampApplies);
        vm.Relief2DHeightSource = ReliefHeightSource.PrimeTerrace;
        Assert.True(vm.Relief2DTerraceRampApplies);
        Assert.True(vm.Relief2DDistanceFalloffApplies);   // the ramp's reach
        vm.Relief2DTerraceRamp = 2.0;
        Assert.Equal(2.0, p.Relief2DTerraceRamp);
        Assert.Contains(ReliefHeightSource.PrimeTerrace, vm.Relief2DHeightSources.Cast<ReliefHeightSource>());
    }
}
