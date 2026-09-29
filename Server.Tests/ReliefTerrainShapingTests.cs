// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1035 — terrain shaping for Real height. Terrain smoothing (view-relative, default
// 2 % of the short axis) turns the needle walls an iteration-count field forms
// around the set into cliffs and mounds; Robust caps needles with a soft knee
// instead of letting them rise above full height. Checked from the render's depth
// AOV and hit positions reconstructed from the camera alone, not from the prepass.

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefTerrainShapingTests
{
    private static (uint[] Albedo, float[] Field) Mandel(int w, int h)
    {
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 300, ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    }

    private static FractalParameters P(bool trueHeight = true, double smoothing = 0.02,
                                       ReliefHeightMode mode = ReliefHeightMode.Peak) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DGpuRaymarch = false,
        Relief2DSupersample = 1,
        Relief2DGroundPlane = false,
        Relief2DTrueHeight = trueHeight,
        Relief2DTerrainSmoothing = smoothing,
        Relief2DHeightMode = mode,
    };

    private sealed record Trace(uint[] Image, float[] Depth, float[] HitY, double HitFraction);

    // Render and reconstruct each terrain hit's world height from the camera alone
    // (Peak / full field: sy·maxH = 0.35·HeightScale, whatever the content).
    private static Trace Render(int w, int h, FractalParameters p)
    {
        var (alb, field) = Mandel(w, h);
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h);
        var dst = new uint[w * h];
        HeightfieldRaymarch2D.Render(alb, field, w, h, w, h, p, dst, out double hf, null, aov);
        double aspect = (double)w / h;
        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(w, h, aspect, 0.35 * p.Relief2DHeightScale, 1.0, p);
        var hitY = new float[w * h];
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                int i = py * w + px;
                float d = aov.Depth[i];
                if (!(d > 0f && d < 9.9e5f)) { hitY[i] = float.NaN; continue; }
                double b = (1.0 - 2.0 * (py + 0.5) / h) * cam.TanHalf;
                double a = (2.0 * (px + 0.5) / w - 1.0) * aspect * cam.TanHalf;
                double rx = cam.FX + cam.RX * a + cam.UX * b, ry = cam.FY + cam.UY * b, rz = cam.FZ + cam.RZ * a + cam.UZ * b;
                double il = 1.0 / Math.Sqrt(rx * rx + ry * ry + rz * rz);
                hitY[i] = (float)(cam.CamY + ry * il * d);
            }
        return new Trace(dst, aov.Depth, hitY, hf);
    }

    // Mean |second difference| of depth along rows over terrain hits — geometric
    // roughness, independent of colour and lighting.
    private static double Roughness(float[] depth, int w, int h)
    {
        double sum = 0; int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 1; x < w - 1; x++)
            {
                float a = depth[y * w + x - 1], b = depth[y * w + x], c = depth[y * w + x + 1];
                if (!(a < 9.9e5f && b < 9.9e5f && c < 9.9e5f) || a <= 0 || b <= 0 || c <= 0) continue;
                sum += Math.Abs(a - 2 * b + c); n++;
            }
        return n > 0 ? sum / n : 0;
    }

    [Fact]
    public void DefaultPlateLook_IgnoresTerrainSmoothing()
    {
        var a = Render(320, 180, P(trueHeight: false, smoothing: 0.0));
        var b = Render(320, 180, P(trueHeight: false, smoothing: 0.05));
        Assert.Equal(a.Image, b.Image);
    }

    [Fact]
    public void Smoothing_MakesTheTerrainMarkedlyLessRough()
    {
        var raw = Render(480, 270, P(smoothing: 0.0));
        var smooth = Render(480, 270, P());
        double r0 = Roughness(raw.Depth, 480, 270), r1 = Roughness(smooth.Depth, 480, 270);
        Assert.True(r1 < r0 * 0.6, $"roughness raw {r0:0.#####} → smoothed {r1:0.#####}");
    }

    // View-relative smoothing: a small and a large window show the same shape —
    // the silhouette coverage agrees.
    [Fact]
    public void Smoothing_KeepsTheShape_AcrossWindowSizes()
    {
        var small = Render(480, 270, P());
        var large = Render(960, 540, P());
        Assert.True(Math.Abs(small.HitFraction - large.HitFraction) < 0.03,
            $"hit fraction 480px {small.HitFraction:0.###} vs 960px {large.HitFraction:0.###}");
    }

    // Robust caps the needles: its highest terrain point stays within the knee's
    // headroom above full height (0.35·HeightScale), where Peak by definition peaks.
    [Fact]
    public void Robust_CapsTheNeedlesAtFullHeight()
    {
        double full = 0.35 * new FractalParameters().Relief2DHeightScale;
        var peak = Render(480, 270, P(smoothing: 0.0));
        var robust = Render(480, 270, P(smoothing: 0.0, mode: ReliefHeightMode.Robust));
        double peakTop = peak.HitY.Where(float.IsFinite).Max();
        double robustTop = robust.HitY.Where(float.IsFinite).Max();
        Assert.True(robustTop <= full * 1.22, $"Robust top {robustTop:0.####} above the knee cap ({full * 1.2:0.####})");
        Assert.True(peakTop > full * 0.9, $"Peak top {peakTop:0.####} should reach full height {full:0.####}");
    }

    // ── parity ──────────────────────────────────────────────────────────────

    [Fact]
    public void Batch_Builder_Region_Dialog_CarryTheSmoothing()
    {
        string[] argv = { "FracturingFog", "--batch", "--fractal", "Mandelbrot", "--x", "-0.5", "--y", "0", "--zoom", "1",
                          "--relief-raymarch", "--relief-true-height", "--relief-terrain-smoothing", "0.035", "--out", "o.png" };
        Assert.True(BatchOptions.TryParse(argv, 2, out var o, out var e), e);
        Assert.Equal(0.035, o.ReliefTerrainSmoothing);
        argv[^3] = "0.2";
        Assert.False(BatchOptions.TryParse(argv, 2, out _, out var err));
        Assert.Contains("--relief-terrain-smoothing", err);

        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
            ReliefTrueHeight = true, ReliefTerrainSmoothing = 0.005,
        };
        var report = BatchCommandBuilder.BuildWithReport(snap);
        var back = new[] { "FracturingFog", "--batch" }.Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "o.png" : a)).ToArray();
        Assert.True(BatchOptions.TryParse(back, 2, out var o2, out var e2), e2);
        Assert.Equal(0.005, o2.ReliefTerrainSmoothing);
        Assert.DoesNotContain("--relief-terrain-smoothing", BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true, ReliefTrueHeight = true,
        }));

        var src = new FractalParameters { Relief2DEnabled = true, Relief2DTerrainSmoothing = 0.04 };
        Assert.Equal(0.04, src.Clone().Relief2DTerrainSmoothing);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<Relief3DSettings>(JsonSerializer.Serialize(Relief3DSettings.Snapshot(src)))!.ApplyTo(dst);
        Assert.Equal(0.04, dst.Relief2DTerrainSmoothing);

        var p = new FractalParameters();
        var vm = new FractalParamsViewModel(FractalType.Mandelbrot, p) { Relief2DTerrainSmoothingPercent = 3.0 };
        Assert.Equal(0.03, p.Relief2DTerrainSmoothing, 12);
        Assert.Equal(0.02, new FractalParameters().Relief2DTerrainSmoothing);
    }
}
