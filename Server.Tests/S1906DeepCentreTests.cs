// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1906 (epic #1253, D1) — the full centre reaches every non-Mandelbrot calculator that takes it
// (IDeepCentre). Only the main live frame (Generated Z2–Z5, TearDrop) and the poster copied QD
// limbs, per type; the shared sync (preview sidecars, slideshow cross-fade, re-renders) and the
// video engine copied the Hi limb only, Generated Tricorn / Burning Ship and Compile & Load were
// named nowhere, and the poster gave User Equation none. Checks:
//   • every IDeepCentre calculator reads back the sixteen limbs and the exact centre it was given,
//     and drops the exact centre once its limbs move;
//   • the live host and the video engine: Generated Z2 is z² + c, so at the p448 view (1e30 live,
//     1e25 for video, which clamps to 5e27; its QD perturbation tier) its iteration counts must match MandelbrotCalculator's on the same view
//     (an independent engine). The Hi limb alone puts the view elsewhere: the counts disagree;
//   • the poster / batch / cluster / explore-probe path hands every IDeepCentre calculator the
//     request's sixteen limbs and exact centre (User Equation included);
//   • the explorer's per-family limit (FamilyDepth) follows the calculators' tiers.
// Mutants (by hand): the host sync without the IDeepCentre call — caught (live case; the main
// frame's per-type copies are gone, so the sync is the only route); the video sync without it —
// caught; the poster without it — caught.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1906DeepCentreTests(ITestOutputHelper output)
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

    private const int W = 96, H = 64, Iter = 6000;
    private const double Zoom = 1e30, VideoZoom = 1e25;   // video zooms clamp to the Ultra cap (5e27, #1323)
    // The bench-kit p448 minibrot centre (S1660 / S1782 / S1791).
    private static readonly OD Re = new(-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50, 0, 0, 0, 0);
    private static readonly OD Im = new(-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50, 0, 0, 0, 0);

    private static FractalViewState State(FractalType t, double zoom) => new()
    {
        FractalType = t, Quality = QualityPreset.Ultra, Zoom = zoom, PreferredIterations = Iter, IterLocked = true, LockedIterations = Iter,
        CenterX = Re.X0, CenterXLo = Re.X1, CenterX2 = Re.X2, CenterX3 = Re.X3,
        CenterY = Im.X0, CenterYLo = Im.X1, CenterY2 = Im.X2, CenterY3 = Im.X3,
    };

    private static int[] Oracle(double zoom)
    {
        var m = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Ultra, Zoom = zoom, MaxIterations = Iter, ColorMap = ColorPalette.BuiltIns[0],
            CenterX = Re.X0, CenterXLo = Re.X1, CenterX2 = Re.X2, CenterX3 = Re.X3,
            CenterY = Im.X0, CenterYLo = Im.X1, CenterY2 = Im.X2, CenterY3 = Im.X3,
        };
        m.Calculate(CancellationToken.None);
        return m.IterationBuffer.Take(W * H).ToArray();
    }

    private static double Agree(int[] a, int[] b) => Enumerable.Range(0, W * H).Count(i => a[i] == b[i]) / (double)(W * H);

    private static int[] Iterations(IFractalCalculator c) => ((dynamic)c).IterationBuffer is int[] it ? it.Take(W * H).ToArray() : throw new InvalidOperationException();

    [Fact]
    public void Every_Deep_Centre_Calculator_Reads_Back_What_It_Was_Given()
    {
        var c = new DeepComplex(
            new OD(-1.75, 1.2e-17, -3.4e-34, 5.6e-51, -7.8e-68, 9.1e-85, -2.3e-101, 4.5e-118),
            new OD(0.01, -6.7e-19, 8.9e-36, -1.1e-52, 2.2e-69, -3.3e-86, 4.4e-103, -5.5e-120));
        var exact = ExactCentre.FromDeep(c, 512);
        IDeepCentre[] calcs =
        [
            new TearDropCalculator(8, 8), new UserEquationCalculator(8, 8),
            new FracturingFog.Calculators.Generated.MandelbrotZ2Calculator(8, 8),
            new FracturingFog.Calculators.Generated.BurningShipCalculator(8, 8),
        ];
        foreach (var d in calcs)
        {
            d.SetDeepCentre(c, exact);
            Assert.True(ExactCentre.SameLimbs(c, d.GetDeepCentre()), $"{d.GetType().Name}: limbs");
            Assert.Equal(c.Re.X0, ((IFractalCalculator)d).CenterX);
            Assert.NotNull(d.CenterExact);
            ((IFractalCalculator)d).CenterX += 1e-3;   // a limb-only writer moved the view
            Assert.Null(d.CenterExact);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_Live_Host_And_The_Video_Engine_Give_The_Generated_Family_The_Full_Centre(bool video)
    {
        double zoom = video ? VideoZoom : Zoom;
        var want = Oracle(zoom);
        var state = State(FractalType.GeneratedMandelbrotZ2, zoom);
        // The video pans to the view from ~3 view-widths away, so a calculator still holding the
        // start's limbs (from the host's earlier sync) renders the wrong place.
        if (video) state.CenterXLo += 1e-24;
        using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var done = new ManualResetEventSlim(false);   // not disposed: a late event may still Set it
        if (video)
        {
            host.Stopped += (_, _) => done.Set();
            host.StartVideo(new VideoZoomRequest
            {
                StartFromCurrentView = true,
                TargetCXHi = Re.X0, TargetCXLo = Re.X1, TargetCX2 = Re.X2, TargetCX3 = Re.X3,
                TargetCYHi = Im.X0, TargetCYLo = Im.X1, TargetCY2 = Im.X2, TargetCY3 = Im.X3,
                TargetZoom = zoom, Seconds = 0.2, TaaSmoothing = 0,
            });
        }
        else
        {
            host.FrameCompleted += (_, _) => done.Set();
            host.Trigger(progressive: false);
        }
        Assert.True(done.Wait(TimeSpan.FromSeconds(180)), "the frame never finished");
        Thread.Sleep(200);
        var alt = host.ActiveAltCalculator!;
        Assert.IsType<FracturingFog.Calculators.Generated.MandelbrotZ2Calculator>(alt);
        var got = Iterations(alt);
        double agree = Agree(want, got);

        // The Hi limb alone (the old sync): the same calculator, the centre's X0 only.
        var hi = new FracturingFog.Calculators.Generated.MandelbrotZ2Calculator(W, H)
        {
            CenterX = Re.X0, CenterY = Im.X0, Zoom = zoom, MaxIterations = Iter, Quality = QualityPreset.Ultra,
            ColorMap = ColorPalette.BuiltIns[0], UsePerturbation = true, UseBla = true,
        };
        hi.Calculate(CancellationToken.None);
        double agreeHi = Agree(want, hi.IterationBuffer.Take(W * H).ToArray());
        output.WriteLine($"{(video ? "video" : "live")}: Generated Z2 vs Mandelbrot at {zoom:G2}: {agree:P1} equal counts [{((dynamic)alt).LastPrecisionLabel}]; Hi limb only: {agreeHi:P1}");
        Assert.True(agreeHi < 0.5, $"precondition: the Hi limb alone renders elsewhere ({agreeHi:P1})");
        Assert.True(agree >= 0.95, $"Generated Z2 through the {(video ? "video engine" : "live host")} agrees with Mandelbrot on {agree:P1} of pixels");
    }

    [Theory]
    [InlineData(FractalType.TearDrop)]
    [InlineData(FractalType.UserEquation)]
    [InlineData(FractalType.GeneratedMandelbrotZ3)]
    [InlineData(FractalType.GeneratedTricorn)]
    public void The_Poster_Path_Hands_Over_The_Full_Centre(FractalType t)
    {
        var c = new DeepComplex(
            new OD(-0.75, 1.2e-17, -3.4e-34, 5.6e-51, -7.8e-68, 9.1e-85, -2.3e-101, 4.5e-118),
            new OD(0.1, -6.7e-19, 8.9e-36, -1.1e-52, 2.2e-69, -3.3e-86, 4.4e-103, -5.5e-120));
        var exact = ExactCentre.FromDeep(c, 512);
        var req = new PosterRequest
        {
            FractalType = t, Width = 8, Height = 8, Zoom = 1e20, MaxIterations = 64,
            ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard, FractalParameters = new FractalParameters(),
            CenterX = c.Re.X0, CenterXLo = c.Re.X1, CenterX2 = c.Re.X2, CenterX3 = c.Re.X3,
            CenterX4 = c.Re.X4, CenterX5 = c.Re.X5, CenterX6 = c.Re.X6, CenterX7 = c.Re.X7,
            CenterY = c.Im.X0, CenterYLo = c.Im.X1, CenterY2 = c.Im.X2, CenterY3 = c.Im.X3,
            CenterY4 = c.Im.X4, CenterY5 = c.Im.X5, CenterY6 = c.Im.X6, CenterY7 = c.Im.X7,
            CenterExact = exact,
        };
        var calc = PosterRenderer.BuildCaptureCalculator(req);
        var d = Assert.IsAssignableFrom<IDeepCentre>(calc);
        Assert.True(ExactCentre.SameLimbs(c, d.GetDeepCentre()), $"{t}: the limbs");
        Assert.Equal(exact, d.CenterExact);
    }

    [Fact]
    public void The_Explorer_Limit_Follows_The_Calculators_Tiers()
    {
        foreach (var t in new[] { FractalType.Mandelbrot,
                                  FractalType.Julia, FractalType.Multibrot, FractalType.Tricorn, FractalType.BurningShip, FractalType.Magnet1, FractalType.Magnet2, FractalType.Glynn })   // #1921
            Assert.Equal(QualityPreset.Deepest.ZoomMax, AutoExplorer.MaxZoomFor(t));
        foreach (var t in new[] { FractalType.TearDrop,   // #1918
                                  FractalType.Phoenix, FractalType.Spider,   // #1912
                                  FractalType.GeneratedMandelbrotZ2, FractalType.GeneratedMandelbrotZ5, FractalType.GeneratedTricorn, FractalType.GeneratedBurningShip })   // #1917
            Assert.Equal(FamilyDepth.EngineDoubleDeltaMaxZoom, AutoExplorer.MaxZoomFor(t));
        foreach (var t in new[] { FractalType.UserEquation })
            Assert.Equal(AutoExplorer.DoubleMaxZoom, AutoExplorer.MaxZoomFor(t));
    }
}
