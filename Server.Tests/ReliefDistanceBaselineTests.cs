// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1041 — the Distance height source skips the automatic baseline. On a dense view
// (the in-set plateau covering over 40% of the cells) the 60th-percentile baseline
// landed on the plateau itself, so only a sliver of the height range stayed above
// ground and normalisation blew it up to full height. The invariant: the ground-to-
// plateau span survives the prepass — the plateau renders a full plateau height
// above the ground.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefDistanceBaselineTests
{
    private static MandelbrotCalculator Calc(double cx, double cy, double zoom, int w = 640, int h = 360)
    {
        var c = new MandelbrotCalculator(w, h) { CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = 500, ColorMap = new MonoBandMap() };
        c.Calculate(default);
        return c;
    }

    private static FractalParameters P(ReliefHeightSource source = ReliefHeightSource.Distance,
                                       ReliefHeightMode mode = ReliefHeightMode.Peak) => new()
    {
        Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = false, Relief2DSupersample = 1,
        Relief2DTrueHeight = true, Relief2DHeightSource = source, Relief2DHeightMode = mode,
    };

    private static float[] DistanceField(MandelbrotCalculator c, FractalParameters p)
        => ReliefHeightField.Build(c.SmoothBuffer, null, c.Width * c.Height, ReliefHeightSource.Distance, 0.5,
                                   c.DistanceBuffer, c.Width, c.Height, c.DistancePixelScale, p.Relief2DDistanceFalloff);

    // The plateau's height after the default Log tone curve.
    private static readonly double PlateauLog = Math.Log(1.0 + ReliefHeightField.DistanceHeight);

    [Theory]
    [InlineData(-0.7453, 0.1127, 60.0)]   // seahorse — dense
    [InlineData(0.2925, 0.0149, 60.0)]    // elephant — dense
    [InlineData(-0.5, 0.0, 1.0)]          // whole set
    public void ThePlateauStandsAFullPlateauHeightAboveTheGround(double cx, double cy, double zoom)
    {
        var c = Calc(cx, cy, zoom);
        var p = P();
        var f = DistanceField(c, p);
        // Precondition: the dense views really are plateau-dominated.
        if (zoom > 1) Assert.True(f.Count(v => v >= ReliefHeightField.DistanceHeight) > f.Length * 0.3);

        foreach (var mode in new[] { ReliefHeightMode.Peak, ReliefHeightMode.Robust })
        {
            p.Relief2DHeightMode = mode;
            var m = HeightfieldRaymarch2D.MeasureHeightNormalization(f, c.Width, c.Height, p);
            Assert.NotNull(m);
            Assert.True(m!.Value.Reference > 0.9 * PlateauLog,
                $"{mode}: only {m.Value.Reference:0.####} of the {PlateauLog:0.####} ground-to-plateau span survives (baseline {m.Value.Baseline:0.####})");
        }
    }

    // A Distance selection that fell back to the smooth counts (no distance estimate)
    // keeps the automatic baseline — exactly as the Smooth source.
    [Fact]
    public void FallbackToSmooth_KeepsTheAutomaticBaseline()
    {
        var c = Calc(-0.7453, 0.1127, 60.0);
        var smooth = c.SmoothBuffer;
        var fallback = ReliefHeightField.Build(smooth, null, smooth.Length, ReliefHeightSource.Distance, 0.5,
                                               null, c.Width, c.Height, 0.0, 0.02);
        Assert.Same(smooth, fallback);
        var a = HeightfieldRaymarch2D.MeasureHeightNormalization(fallback, c.Width, c.Height, P());
        var b = HeightfieldRaymarch2D.MeasureHeightNormalization(smooth, c.Width, c.Height, P(ReliefHeightSource.Smooth));
        Assert.Equal(b, a);
        Assert.True(a!.Value.Baseline > 0);
    }

    [Fact]
    public void OnlyTheDistanceSourceSkipsTheBaseline()
    {
        var f = DistanceField(Calc(-0.7453, 0.1127, 60.0), P());
        Assert.True(HeightfieldRaymarch2D.IsDistanceField(f, f.Length, P()));
        Assert.False(HeightfieldRaymarch2D.IsDistanceField(f, f.Length, P(ReliefHeightSource.Smooth)));
        Assert.False(HeightfieldRaymarch2D.IsDistanceField(f, f.Length, P(ReliefHeightSource.Trap)));
    }

    // "Lock current height" on a Distance view locks the zero baseline and the
    // plateau reference, and Fixed then reproduces the frame exactly.
    [Fact]
    public void LockOnADistanceView_ReproducesTheFrame()
    {
        var c = Calc(-0.7453, 0.1127, 60.0, 320, 180);
        var p = P(mode: ReliefHeightMode.Robust);
        var f = DistanceField(c, p);
        var m = HeightfieldRaymarch2D.MeasureHeightNormalization(f, c.Width, c.Height, p)!.Value;
        Assert.Equal(0.0, m.Baseline);

        var live = new uint[320 * 180];
        HeightfieldRaymarch2D.Render(c.ColorBuffer, f, 320, 180, p, live);
        var fixedP = P(mode: ReliefHeightMode.Fixed);
        fixedP.Relief2DHeightBaseline = m.Baseline;
        fixedP.Relief2DHeightRef = m.Reference;
        var locked = new uint[320 * 180];
        HeightfieldRaymarch2D.Render(c.ColorBuffer, f, 320, 180, fixedP, locked);
        Assert.Equal(live, locked);
    }
}
