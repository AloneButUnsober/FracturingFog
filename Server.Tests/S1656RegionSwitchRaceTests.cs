// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1656 / #1657 — recalling a region while the live view was still refining (a
// jittered MSAA / TAA pass in flight on the calc thread) could render the PREVIOUS
// view's centre at the new zoom: Trigger cancels, then writes the new view on the UI
// thread (ApplyView), while the pass restored the centre it had captured after its
// Calculate. The next frame rendered that old centre (flat or black at depth, grainy
// mush or a ghosted overlay elsewhere), TAA settled there, and each further zoom step
// during refinement restored it again. Locks, each against an independent reference:
//   • CentreJitter.TryApply refuses once the token is cancelled or the centre moved,
//     and RestoreIfOwned leaves a centre someone else wrote;
//   • a region switch during refinement settles on the new view's centre, byte for
//     byte the exported still of that view (fresh calculator, #1380 path);
//   • a zoom step during the next refinement also lands on the view state's centre.
// Before the fix the switch settled on the old centre in 6/6 runs (scratch harness,
// see the issue).

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1656CentreJitterOwnershipTests
{
    private static MandelbrotCalculator Calc() => new(8, 8)
    {
        CenterX = -0.75, CenterXLo = 1e-17, CenterX2 = 1e-34, CenterY = 0.1, CenterYLo = -1e-18, Zoom = 1e20,
    };

    [Fact]
    public void TryApply_Jitters_A_Live_View_And_RestoreIfOwned_Puts_It_Back()
    {
        var c = Calc();
        var orig = CentreLimbs.Capture(c);
        var owned = orig;
        Assert.True(CentreJitter.TryApply(c, in orig, 0.25, -0.25, 1e-22, CancellationToken.None, ref owned));
        Assert.NotEqual(orig, CentreLimbs.Capture(c));
        Assert.Equal(owned, CentreLimbs.Capture(c));
        // A multi-sample pass jitters again from orig without restoring in between.
        Assert.True(CentreJitter.TryApply(c, in orig, -0.25, 0.25, 1e-22, CancellationToken.None, ref owned));
        Assert.Equal(owned, CentreLimbs.Capture(c));
        CentreJitter.RestoreIfOwned(c, in orig, in owned);
        Assert.Equal(orig, CentreLimbs.Capture(c));
    }

    [Fact]
    public void TryApply_Refuses_A_Cancelled_Token_Or_A_Moved_Centre()
    {
        var c = Calc();
        var orig = CentreLimbs.Capture(c);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var owned = orig;
        Assert.False(CentreJitter.TryApply(c, in orig, 0.25, 0.25, 1e-22, cts.Token, ref owned));
        Assert.Equal(orig, CentreLimbs.Capture(c));

        c.CenterX7 = 3e-120;   // the UI moved the view (any limb)
        var moved = CentreLimbs.Capture(c);
        Assert.False(CentreJitter.TryApply(c, in orig, 0.25, 0.25, 1e-22, CancellationToken.None, ref owned));
        Assert.Equal(moved, CentreLimbs.Capture(c));
    }

    [Fact]
    public void RestoreIfOwned_Leaves_A_Centre_Written_After_The_Jitter()
    {
        var c = Calc();
        var orig = CentreLimbs.Capture(c);
        var jittered = orig;
        Assert.True(CentreJitter.TryApply(c, in orig, 0.5, 0.5, 1e-22, CancellationToken.None, ref jittered));
        // ApplyView of the next region lands while the pass is calculating.
        c.CenterX = 0.34370735950670833; c.CenterXLo = 2.282346286510163e-17; c.CenterY = -0.050349769895588446;
        var next = CentreLimbs.Capture(c);
        CentreJitter.RestoreIfOwned(c, in orig, in jittered);
        Assert.Equal(next, CentreLimbs.Capture(c));
    }
}

[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1656RegionSwitchHostTests
{
    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private const int W = 120, H = 70;

    // The user's saved regions (centre limbs X0..X7 / Y0..Y7, zoom, iterations).
    // A: "Mandelbrot - Julia Morph P-2920 Z-2.27E50".
    private static readonly double[] AX = { 0.34370735950670833, 2.282346286510163e-17, 7.932326910602696e-34, 5.4401276235633795e-50, 6.284063154961764e-67, -1.985572306602123e-83, -1.0377884187455825e-99, -3.2792343773204467e-116 };
    private static readonly double[] AY = { -0.050349769895588446, -2.7181764697723013e-18, 1.9007460651095413e-34, -1.9865328190517915e-51, -6.233284332956233e-69, 1.3263718270036998e-85, -4.290051330756198e-102, -2.8796016064491864e-118 };
    private const double AZoom = 2.2702867439547516e+50;
    // B: "Mandelbrot - Julia Morpj P-2635 Z-1.039E49" (the fuzzy one, #1657).
    private static readonly double[] BX = { -0.774696091724181, 3.5556493435759257e-17, -1.9553571560734845e-33, -1.7183717390852757e-50, 8.796554554634337e-67, -3.9780036141418855e-83, -5.467415462815407e-101, 2.2376297359348503e-117 };
    private static readonly double[] BY = { -0.12422383643139207, -1.5049571086365649e-18, 2.3757984112672922e-35, -2.266284944760178e-53, -2.3519011006983258e-70, 8.190455495091457e-87, 2.0129611967467234e-104, -1.4221396332082148e-120 };
    private const double BZoom = 1.0388265985585207e+49;
    private const int Iter = 131072;

    private static void Set(FractalViewState s, double[] x, double[] y, double zoom)
    {
        s.CenterX = x[0]; s.CenterXLo = x[1]; s.CenterX2 = x[2]; s.CenterX3 = x[3];
        s.CenterX4 = x[4]; s.CenterX5 = x[5]; s.CenterX6 = x[6]; s.CenterX7 = x[7];
        s.CenterY = y[0]; s.CenterYLo = y[1]; s.CenterY2 = y[2]; s.CenterY3 = y[3];
        s.CenterY4 = y[4]; s.CenterY5 = y[5]; s.CenterY6 = y[6]; s.CenterY7 = y[7];
        s.Zoom = zoom; s.PreferredIterations = Iter; s.Quality = QualityPreset.Extreme;
        s.FractalType = FractalType.Mandelbrot;
    }

    private static uint[] Still(double[] x, double[] y, double zoom, FracturingFog.Interefaces.IColorMap map)
    {
        var req = new FracturingFog.Imaging.PosterRequest
        {
            FractalType = FractalType.Mandelbrot, Width = W, Height = H,
            CenterX = x[0], CenterXLo = x[1], CenterX2 = x[2], CenterX3 = x[3],
            CenterX4 = x[4], CenterX5 = x[5], CenterX6 = x[6], CenterX7 = x[7],
            CenterY = y[0], CenterYLo = y[1], CenterY2 = y[2], CenterY3 = y[3],
            CenterY4 = y[4], CenterY5 = y[5], CenterY6 = y[6], CenterY7 = y[7],
            Zoom = zoom, MaxIterations = Iter, Quality = QualityPreset.Extreme, ColorMap = map,
            FractalParameters = new FractalParameters(), TemporalAa = true, Path = string.Empty,
        };
        return FracturingFog.Imaging.PosterRenderer.RenderToPixels(req, CancellationToken.None, out _, out _).Take(W * H).ToArray();
    }

    private static bool CentreMatchesState(MandelbrotCalculator c, FractalViewState s)
        => CentreLimbs.Capture(c) == new CentreLimbs(
            s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3, s.CenterX4, s.CenterX5, s.CenterX6, s.CenterX7,
            s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3, s.CenterY4, s.CenterY5, s.CenterY6, s.CenterY7);

    private static int Differ(uint[] a, uint[] b) => Enumerable.Range(0, a.Length).Count(i => a[i] != b[i]);

    [Fact]
    public void A_Region_Recalled_While_The_View_Refines_Settles_On_The_New_Centre()
    {
        var state = new FractalViewState();
        using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var map = host.Mandelbrot.ColorMap!;
        var stillB = Still(BX, BY, BZoom, map);
        // Precondition: the old centre at B's zoom is a different picture, so landing
        // there would be caught (this is the frame the race produced).
        var wrong = Still(AX, AY, BZoom, map);
        Assert.True(Differ(stillB, wrong) > W * H / 2, "precondition: the old centre renders a different frame");

        for (int trial = 0; trial < 3; trial++)
        {
            var refining = new ManualResetEventSlim(false);   // not disposed: late events may Set them
            var done = new ManualResetEventSlim(false);
            EventHandler<RefineProgressInfo> onRefine = (_, e) =>
            {
                if (e.Done) done.Set();
                else if (e.Sample >= 2) refining.Set();
            };
            host.RefineProgress += onRefine;
            try
            {
                Set(state, AX, AY, AZoom);
                host.Trigger();
                Assert.True(refining.Wait(TimeSpan.FromSeconds(300)), "view A never started refining");
                Thread.Sleep(trial * 5);   // land inside a TAA sample
                done.Reset();
                Set(state, BX, BY, BZoom);   // the region pick (ShellViewModel.JumpToRegion → Trigger)
                host.Trigger();
                Assert.True(done.Wait(TimeSpan.FromSeconds(600)), "view B never settled");
            }
            finally { host.RefineProgress -= onRefine; }
            Thread.Sleep(200);

            var c = host.Mandelbrot;
            Assert.True(CentreMatchesState(c, state), $"trial {trial}: calc centre {CentreLimbs.Capture(c)} is not the view's (X0 {state.CenterX:R})");
            Assert.Equal(BZoom, c.Zoom);
            Assert.Equal(stillB, c.ColorBuffer.Take(W * H).ToArray());
        }
    }

    [Fact]
    public void A_Zoom_Step_During_The_Next_Refinement_Keeps_The_View_Centre()
    {
        var state = new FractalViewState();
        using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var refining = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);
        EventHandler<RefineProgressInfo> onRefine = (_, e) =>
        {
            if (e.Done) done.Set();
            else if (e.Sample >= 2) refining.Set();
        };
        host.RefineProgress += onRefine;
        try
        {
            Set(state, AX, AY, AZoom);
            host.Trigger();
            Assert.True(refining.Wait(TimeSpan.FromSeconds(300)));
            refining.Reset();
            Set(state, BX, BY, BZoom);
            host.Trigger();
            Assert.True(refining.Wait(TimeSpan.FromSeconds(300)), "view B never started refining");
            // A wheel step about the centre while B refines (zoom only; the
            // iteration override clears as FractalInputController does).
            done.Reset();
            state.Zoom = BZoom * 1.06;
            state.PreferredIterations = 0;
            host.Trigger();
            Assert.True(done.Wait(TimeSpan.FromSeconds(600)), "the zoomed view never settled");
        }
        finally { host.RefineProgress -= onRefine; }
        Thread.Sleep(200);
        var c = host.Mandelbrot;
        Assert.True(CentreMatchesState(c, state), $"calc centre {c.CenterX:R} is not the view's {state.CenterX:R}");
        Assert.Equal(BZoom * 1.06, c.Zoom);
    }
}
