// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1029 — the Distance relief height source: height from the exterior distance
// estimate to the set, in view units. The set is a raised plateau, filaments are
// ridges falling away with distance, and it falls back to Smooth when there is no
// usable distance field. Also the batch / builder / region / dialog parity for the
// height source (which had no batch flag before).

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefDistanceSourceTests
{
    private static float[] Build(float[] smooth, float[] dist, int w, int h, double scale, double falloff = 0.02)
        => ReliefHeightField.Build(smooth, null, w * h, ReliefHeightSource.Distance, 0.5, dist, w, h, scale, falloff);

    [Fact]
    public void Plateau_Ridges_And_TheExactFalloff()
    {
        const int w = 4, h = 2;          // span = pixelScale · min(w,h) = 0.5 · 2 = 1
        var smooth = new float[] { 0, 5, 5, 5, 5, 5, 5, 5 };
        var dist   = new float[] { 0, 0.001f, 0.02f, 0.1f, 0.001f, 0.001f, 0.001f, 0.001f };
        var hgt = Build(smooth, dist, w, h, 0.5);
        Assert.Equal(ReliefHeightField.DistanceHeight, hgt[0]);                      // in the set: plateau
        Assert.True(hgt[1] > hgt[2] && hgt[2] > hgt[3], "height falls with distance");
        Assert.Equal(ReliefHeightField.DistanceHeight * Math.Exp(-1.0), hgt[2], 4);  // d = falloff·span → e^-1
        Assert.True(hgt.All(v => v >= 0f && v <= ReliefHeightField.DistanceHeight));
    }

    [Fact]
    public void FallsBackToSmooth_WithoutAUsableDistanceField()
    {
        var smooth = new float[] { 0, 5, 6, 7 };
        Assert.Same(smooth, ReliefHeightField.Build(smooth, null, 4, ReliefHeightSource.Distance, 0.5, null, 2, 2, 1.0, 0.02));
        // Deep zoom: the float estimate underflowed to 0 for most escaped pixels.
        Assert.Same(smooth, Build(smooth, new float[] { 0, 0, 0, 0.1f }, 2, 2, 1.0));
        // Smooth source ignores the distance field entirely.
        Assert.Same(smooth, ReliefHeightField.Build(smooth, null, 4, ReliefHeightSource.Smooth, 0.5, new float[4], 2, 2, 1.0, 0.02));
    }

    // View units: the same view at two window sizes gives the same height landscape
    // (compare each low-res pixel with the mean of the matching 2×2 high-res block).
    [Fact]
    public void HeightIsInViewUnits_SameAtAnyWindowSize()
    {
        float[] Heights(int w, int h)
        {
            var c = new MandelbrotCalculator(w, h) { CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = 300, ColorMap = new MonoBandMap() };
            c.Calculate(default);
            return Build(c.SmoothBuffer, c.DistanceBuffer, w, h, c.DistancePixelScale);
        }
        const int w = 240, h = 135;
        var lo = Heights(w, h);
        var hi = Heights(2 * w, 2 * h);
        double sumDiff = 0; int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int hx = 2 * x, hy = 2 * y, W2 = 2 * w;
                double m = (hi[hy * W2 + hx] + hi[hy * W2 + hx + 1] + hi[(hy + 1) * W2 + hx] + hi[(hy + 1) * W2 + hx + 1]) / 4.0;
                sumDiff += Math.Abs(lo[y * w + x] - m); n++;
            }
        double meanDiff = sumDiff / n;
        Assert.True(meanDiff < 0.05 * ReliefHeightField.DistanceHeight, $"mean height difference {meanDiff:0.###}");
    }

    // Batch / poster path: below the field floor the poster builds its own hi-res
    // field — it must carry the Distance source (the plateau), not raw counts.
    [Fact]
    public void PosterHiResField_CarriesTheDistanceSource()
    {
        var fp = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DHeightSource = ReliefHeightSource.Distance,
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
        Assert.Equal(ReliefHeightField.DistanceHeight, field!.Max());
        Assert.True(field.All(v => v <= ReliefHeightField.DistanceHeight));
    }

    // ── parity ──────────────────────────────────────────────────────────────

    [Fact]
    public void Batch_Builder_Region_Dialog_CarryTheSource()
    {
        string[] Argv(params string[] extra) => new[] { "FracturingFog", "--batch", "--fractal", "Mandelbrot", "--x", "-0.5", "--y", "0",
                                                        "--zoom", "1", "--relief-raymarch" }.Concat(extra).Concat(new[] { "--out", "o.png" }).ToArray();
        Assert.True(BatchOptions.TryParse(Argv("--relief-height-source", "distance", "--relief-distance-falloff", "0.05"), 2, out var o, out var e), e);
        Assert.Equal(ReliefHeightSource.Distance, o.ReliefHeightSource);
        Assert.Equal(0.05, o.ReliefDistanceFalloff);
        Assert.False(BatchOptions.TryParse(Argv("--relief-height-source", "height"), 2, out _, out var e1));
        Assert.Contains("--relief-height-source", e1);
        Assert.False(BatchOptions.TryParse(Argv("--relief-distance-falloff", "0.9"), 2, out _, out var e2));
        Assert.Contains("--relief-distance-falloff", e2);
        Assert.False(BatchOptions.TryParse(Argv("--relief-height-blend", "2"), 2, out _, out var e3));
        Assert.Contains("--relief-height-blend", e3);

        foreach (var (src, blend, fall) in new[] { (ReliefHeightSource.Distance, 0.5, 0.08), (ReliefHeightSource.Blend, 0.3, 0.02), (ReliefHeightSource.Trap, 0.5, 0.02) })
        {
            var snap = new BatchCommandSnapshot
            {
                Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
                ReliefHeightSource = src, ReliefHeightBlend = blend, ReliefDistanceFalloff = fall,
            };
            var report = BatchCommandBuilder.BuildWithReport(snap);
            var back = new[] { "FracturingFog", "--batch" }.Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "o.png" : a)).ToArray();
            Assert.True(BatchOptions.TryParse(back, 2, out var ob, out var eb), eb);
            Assert.Equal(src, ob.ReliefHeightSource);
            if (src == ReliefHeightSource.Blend) Assert.Equal(blend, ob.ReliefHeightBlend);
            if (src == ReliefHeightSource.Distance) Assert.Equal(fall, ob.ReliefDistanceFalloff);
        }
        Assert.DoesNotContain("--relief-height-source", BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
        }));

        var srcP = new FractalParameters { Relief2DEnabled = true, Relief2DHeightSource = ReliefHeightSource.Distance, Relief2DDistanceFalloff = 0.07 };
        Assert.Equal(0.07, srcP.Clone().Relief2DDistanceFalloff);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<Relief3DSettings>(JsonSerializer.Serialize(Relief3DSettings.Snapshot(srcP)))!.ApplyTo(dst);
        Assert.Equal(ReliefHeightSource.Distance, dst.Relief2DHeightSource);
        Assert.Equal(0.07, dst.Relief2DDistanceFalloff);

        var p = new FractalParameters();
        var vm = new FractalParamsViewModel(FractalType.Mandelbrot, p);
        Assert.False(vm.Relief2DDistanceFalloffApplies);
        vm.Relief2DHeightSource = ReliefHeightSource.Distance;
        Assert.True(vm.Relief2DDistanceFalloffApplies);
        vm.Relief2DDistanceFalloffPercent = 5.0;
        Assert.Equal(0.05, p.Relief2DDistanceFalloff, 12);
        Assert.Contains(ReliefHeightSource.Distance, vm.Relief2DHeightSources.Cast<ReliefHeightSource>());
    }
}
