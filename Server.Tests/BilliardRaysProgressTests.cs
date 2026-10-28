// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1511 — Billiard Rays S6: sweep progress, progressive batches, drag preview. Oracles:
//   • a render at progress p draws exactly the first floor(p N) launches: its density
//     total is their in-view length (independent clip), and density only grows with p;
//   • each progressive batch's on-screen image equals a one-shot render at the matching
//     progress, and the last equals the normal render (bit-identical);
//   • a cancel leaves the last batch on screen;
//   • the drag preview is a nearest upscale of a small render of every k-th ray.
public class BilliardRaysProgressTests
{
    private const int W = 80, H = 60;

    private static FractalParameters Params(int rays = 700) => new()
    {
        BilliardRaysCount = rays, BilliardRaysAngleCount = 3, BilliardRaysAngle = 50, BilliardRaysMinBounces = 0,
    };

    private static BilliardRaysCalculator Calc(FractalParameters p, int w = W, int h = H)
        => new(w, h) { FractalParameters = p, Zoom = 0.6 };

    private static BilliardRaysCalculator Render(FractalParameters p, int w = W, int h = H)
    {
        var c = Calc(p, w, h);
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.37, 259)]
    [InlineData(0.3705, 259)]   // 259.35 rays: the fraction is floored
    [InlineData(1.0 / 7.0, 100)]
    [InlineData(1.0, 700)]
    public void Progress_Emits_The_First_Fraction_Of_The_Sweep(double progress, int expectRays)
    {
        var p = Params();
        p.BilliardRaysProgress = progress;
        var c = Render(p);
        Assert.Equal(expectRays, c.LastRaysEmitted);

        var scene = BilliardScene.Build(p);
        var sweep = BilliardRaysCalculator.BuildSweep(p, scene);
        double pitch = 4.0 / W / 0.6;
        double x0 = -(W / 2.0 + 0.5) * pitch, x1 = (W / 2.0 - 0.5) * pitch;
        double y0 = -((H / 2.0 - 0.5) * pitch), y1 = (H / 2.0 + 0.5) * pitch;
        double expect = 0;
        foreach (var l in sweep.Take(expectRays))
        {
            var path = new List<BilliardPoint>();
            if (!BilliardRaysCalculator.Launch(scene, l, path).Valid) continue;
            for (int k = 1; k < path.Count; k++) expect += Clipped(path[k - 1], path[k], x0, x1, y0, y1);
        }
        if (expectRays == 0) Assert.Equal(0.0, c.Density.Sum());
        else Assert.Equal(1.0, c.Density.Sum() / expect, 9);
    }

    [Fact]
    public void Density_Only_Grows_As_Progress_Rises()
    {
        var p = Params();
        double[]? last = null;
        foreach (double f in new[] { 0.1, 0.35, 0.6, 0.85, 1.0 })
        {
            p.BilliardRaysProgress = f;
            var d = Render(p).Density;
            if (last != null)
                for (int i = 0; i < d.Length; i++) Assert.True(d[i] >= last[i], $"pixel {i} lost density at progress {f}");
            last = d;
        }
        p.BilliardRaysProgress = 1.0;
        Assert.Equal(Render(Params()).ColorBuffer, Render(p).ColorBuffer);   // 1 = the normal render
    }

    private static double Clipped(BilliardPoint a, BilliardPoint b, double x0, double x1, double y0, double y1)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, lo = 0, hi = 1;
        foreach (var (p0, d, mn, mx) in new[] { (a.X, dx, x0, x1), (a.Y, dy, y0, y1) })
        {
            if (Math.Abs(d) < 1e-300) { if (p0 < mn || p0 > mx) return 0; continue; }
            double ta = (mn - p0) / d, tb = (mx - p0) / d;
            lo = Math.Max(lo, Math.Min(ta, tb)); hi = Math.Min(hi, Math.Max(ta, tb));
        }
        return hi > lo ? (hi - lo) * Math.Sqrt(dx * dx + dy * dy) : 0;
    }

    [Theory]
    [InlineData(BilliardRayMode.Paths)]
    [InlineData(BilliardRayMode.Both)]
    [InlineData(BilliardRayMode.DivergenceOverPaths)]
    public void Each_Progressive_Batch_Is_The_Render_So_Far(BilliardRayMode mode)
    {
        var p = Params();
        p.BilliardRaysMode = mode;
        var c = Calc(p);
        var frames = new List<(int Done, int Total, uint[] Image)>();
        c.ProgressiveBatchesOverride = 7;
        c.OnBatchComposited = (done, total) => frames.Add((done, total, c.ColorBuffer.ToArray()));
        c.Calculate(TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(1, 7), frames.Select(f => f.Done));
        Assert.All(frames, f => Assert.Equal(7, f.Total));
        for (int k = 1; k <= 7; k++)
        {
            var q = Params();
            q.BilliardRaysMode = mode;
            q.BilliardRaysProgress = k / 7.0;   // exactly 100 k rays
            Assert.Equal(Render(q).ColorBuffer, frames[k - 1].Image);
        }
        Assert.Equal(Render(p).ColorBuffer, c.ColorBuffer);   // progressive final = one-shot
    }

    [Fact]
    public void A_Cancel_Leaves_The_Last_Batch_On_Screen()
    {
        var p = Params();
        var c = Calc(p);
        using var cts = new CancellationTokenSource();
        uint[]? third = null;
        c.ProgressiveBatchesOverride = 7;
        c.OnBatchComposited = (done, _) => { if (done == 3) { third = c.ColorBuffer.ToArray(); cts.Cancel(); } };
        try { c.Calculate(cts.Token); } catch (OperationCanceledException) { }
        Assert.NotNull(third);
        Assert.Equal(third, c.ColorBuffer);
        p.BilliardRaysProgress = 3 / 7.0;
        Assert.Equal(Render(p).ColorBuffer, c.ColorBuffer);
    }

    [Fact]
    public void Drag_Preview_Is_A_Small_Thinned_Render_Upscaled()
    {
        var p = Params(rays: 3200);
        const int w = 160, h = 120;
        var c = Calc(p, w, h);
        c.LowResPreview = true;
        c.LowResPreviewScaleOverride = 0.25;
        c.Calculate(TestContext.Current.CancellationToken);

        var small = new BilliardRaysCalculator(40, 30) { FractalParameters = p, Zoom = 0.6, LaunchStride = 16 };
        small.Calculate(TestContext.Current.CancellationToken);
        Assert.Equal(200, small.LastRaysEmitted);   // 3200 / 16
        var up = new uint[w * h];
        LowResPreview.UpscaleNearest(small.ColorBuffer, 40, 30, up, w, h);
        Assert.Equal(up, c.ColorBuffer);

        // The final frame (preview off) is the full render.
        c.LowResPreview = false;
        c.LowResPreviewScaleOverride = null;
        c.Calculate(TestContext.Current.CancellationToken);
        Assert.Equal(Render(p, w, h).ColorBuffer, c.ColorBuffer);
        Assert.IsAssignableFrom<ILowResPreviewCalculator>(c);
    }

    [Fact]
    public void Emitted_Takes_The_Fraction_Then_Every_Kth()
    {
        var sweep = Enumerable.Range(0, 100).Select(i => new BilliardRayLaunch(BilliardRayLaunch.LaunchKind.FarField, i, 0, 0, 0)).ToList();
        Assert.Equal(Enumerable.Range(0, 50).Select(i => (double)i), BilliardRaysCalculator.Emitted(sweep, 0.5, 1).Select(l => l.A));
        Assert.Equal(new double[] { 0, 4, 8 }, BilliardRaysCalculator.Emitted(sweep, 0.1, 4).Select(l => l.A));
        Assert.Empty(BilliardRaysCalculator.Emitted(sweep, -2, 1));
        Assert.Equal(100, BilliardRaysCalculator.Emitted(sweep, 7, 1).Count);
        Assert.Equal(100, BilliardRaysCalculator.Emitted(sweep, double.NaN, 1).Count);
    }

    [Fact]
    public void Progress_Is_Animatable_Saved_And_In_The_Panel()
    {
        var d = FractalAnimatableParamsMap.For(FractalType.BilliardRays).Single(x => x.ParamName == "BilliardRaysProgress");
        Assert.Equal(AnimatableParamKind.ScalarDouble, d.Kind);
        Assert.Equal(0.0, d.Min);
        Assert.Equal(1.0, d.Max);

        var p = Params();
        p.BilliardRaysProgress = 0.42;
        var snap = RegionFractalParams.Snapshot(FractalType.BilliardRays, p)!;
        var q = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(q);
        Assert.Equal(0.42, q.BilliardRaysProgress);

        var vm = new FractalParamsViewModel(FractalType.BilliardRays, new FractalParameters());
        Assert.Equal(1.0, vm.BilliardRaysProgress);
        vm.BilliardRaysProgress = 3;
        Assert.Equal(1.0, vm.BilliardRaysProgress);
    }
}
