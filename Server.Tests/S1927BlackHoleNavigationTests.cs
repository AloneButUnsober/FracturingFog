// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Scattering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1927 — box zoom on black hole shadows landed short of the target near the shadow edges.
// Cause: the render used the billiard's 4 / (width · zoom) pixel pitch while every input path
// (box zoom, wheel zoom, pan) goes through ViewCamera, PlaneExtent / (max(w, h) · zoom) with
// PlaneExtent 3.5, so a target landed at 3.5/4 of its distance from the centre. The invariant
// checked here is the camera's own contract ("input and render must agree"): the world point
// the camera assigns a screen pixel is the plane point the render traced there, and a box zoom
// puts the boxed point — and its fate — at the new centre.
public class S1927BlackHoleNavigationTests
{
    private const int W = 400, H = 225;

    private static FractalViewState View(double cx, double cy, double zoom)
        => new() { FractalType = FractalType.BlackHoleShadow, CenterX = cx, CenterY = cy, Zoom = zoom };

    [Theory]
    [InlineData(0.0, 0.0, 0.14, 400, 225)]
    [InlineData(-7.72, 0.0, 6.0, 400, 225)]
    [InlineData(2.5, -1.25, 40.0, 225, 400)]   // portrait: the longer side sets the scale
    public void Camera_And_Render_Agree_On_Every_Pixel(double cx, double cy, double zoom, int w, int h)
    {
        var vs = View(cx, cy, zoom);
        var cam = new ViewCamera(vs);
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1), (w / 3, h / 5), (w / 2, h / 2) })
        {
            var world = cam.WorldFromScreen(x + 0.5, y + 0.5, w, h);
            var (u, v) = BlackHoleShadowCalculator.PlaneAt(x + 0.5, y + 0.5, w, h, cx, cy, zoom);
            double pitch = BlackHoleShadowCalculator.PixelPitch(w, h, zoom);
            Assert.InRange(Math.Abs((double)world.Re - u), 0.0, 1e-9 * pitch);
            Assert.InRange(Math.Abs((double)world.Im - -v), 0.0, 1e-9 * pitch);   // v is up, screen y is down
        }
    }

    [Fact]
    public void Box_Zoom_Lands_On_The_Boxed_Eyebrow()
    {
        // The default binary at the default zoom; box a pixel on the outer eyebrow of hole 1
        // (left side, far from the centre, where the old pitch missed by about a unit).
        var p = new FractalParameters();
        var scene = BlackHoleShadowCalculator.Scene.Build(p);
        double z0 = BlackHoleShadowCalculator.DefaultZoom;
        int Fate(double u, double v)
        {
            var o = scene.Trace(u, v).Outcome;
            return o.Fate == ScatterFate.Captured ? o.Body : o.Fate == ScatterFate.Escaped ? 2 : -1;
        }
        // Find a left-side pixel on the axis whose fate is hole 0 (an eyebrow: hole 0 sits on +u).
        int target = -1;
        for (int x = 0; x < W / 2 && target < 0; x++)
        {
            var (u, v) = BlackHoleShadowCalculator.PlaneAt(x + 0.5, H / 2 + 0.5, W, H, 0, 0, z0);
            if (u < -5.0 && Fate(u, v) == 0) target = x;
        }
        Assert.True(target >= 0, "no eyebrow pixel found on the left of the axis");
        var (tu, tv) = BlackHoleShadowCalculator.PlaneAt(target + 0.5, H / 2 + 0.5, W, H, 0, 0, z0);

        // Box-zoom 8x onto it through the camera, as FractalInputController.ApplyBoxZoom does.
        var vs = View(0, 0, z0);
        new ViewCamera(vs).BoxZoomToPoint(target + 0.5, H / 2 + 0.5, 8.0, W, H, 1e-6, 1e300);
        var (nu, nv) = BlackHoleShadowCalculator.PlaneAt(W * 0.5, H * 0.5, W, H, vs.CenterX, vs.CenterY, vs.Zoom);
        Assert.Equal(tu, nu, 9);
        Assert.Equal(tv, nv, 9);
        Assert.Equal(0, Fate(nu, nv));   // the eyebrow is under the new centre
    }

    // ── S5: zoom-calibrated tolerance (#1927) ─────────────────────────────────

    private static sbyte[] RenderFates(FractalParameters p, double cx, double zoom, int w, int h)
    {
        var c = new BlackHoleShadowCalculator(w, h) { FractalParameters = p, ColorMap = new HsvPalette(), CenterX = cx, Zoom = zoom };
        c.Calculate(TestContext.Current.CancellationToken);
        return c.FateBuffer;
    }

    [Theory]
    [InlineData(1e7)]
    [InlineData(1e9)]
    public void Deep_Views_Keep_The_Reference_Fates(double zoomFactor)
    {
        // A bisected boundary on the outer eyebrow (escaped / hole 1), the calibration's first point.
        const double c = -7.806216050537152;
        double zoom = BlackHoleShadowCalculator.DefaultZoom * zoomFactor;
        var fast = RenderFates(new FractalParameters(), c, zoom, 120, 68);
        var reference = RenderFates(new FractalParameters { BlackHoleTolerance = BlackHoleShadowCalculator.Scene.MinTolerance }, c, zoom, 120, 68);
        Assert.Contains(fast, f => f == 1);
        Assert.Contains(fast, f => f == 2);   // the boundary is in view
        Assert.Equal(reference, fast);
    }

    [Fact]
    public void Tolerance_Follows_The_Pixel_Pitch_Down_To_The_Double_Floor()
    {
        double z = BlackHoleShadowCalculator.DefaultZoom * 1e6;
        double pitch = BlackHoleShadowCalculator.PixelPitch(1920, 1080, z);
        double t = BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-7, z, pitch);
        Assert.True(t == BlackHoleShadowCalculator.Scene.PitchToleranceFactor * pitch);
        // Shallow: the pitch cap does not bite, the zoom tie alone sets it.
        Assert.Equal(BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-7, 1.0),
                     BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-7, 1.0, BlackHoleShadowCalculator.PixelPitch(480, 270, 1.0)), 15);
        // Deep: floored, and flagged as past double precision.
        double deep = BlackHoleShadowCalculator.PixelPitch(1920, 1080, BlackHoleShadowCalculator.DefaultZoom * 1e12);
        Assert.True(BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-7, 1e12, deep) == BlackHoleShadowCalculator.Scene.MinTolerance);
        Assert.True(BlackHoleShadowCalculator.Scene.PastDoublePrecision(deep));
        Assert.False(BlackHoleShadowCalculator.Scene.PastDoublePrecision(pitch));
    }

    [Fact]
    public void Status_Label_Says_When_The_View_Is_Past_Double()
    {
        var c = new BlackHoleShadowCalculator(32, 18) { FractalParameters = new FractalParameters(), ColorMap = new HsvPalette() };
        c.Calculate(TestContext.Current.CancellationToken);
        Assert.StartsWith("fp64 tol", c.LastPrecisionLabel);
        c.CenterX = -7.806216050537152;
        c.Zoom = BlackHoleShadowCalculator.DefaultZoom * 1e13;
        c.FractalParameters = new FractalParameters { BlackHoleMaxSteps = 2000, BlackHoleDdEdges = false };
        c.Calculate(TestContext.Current.CancellationToken);
        Assert.Equal("past fp64: edges approximate", c.LastPrecisionLabel);
        c.FractalParameters = new FractalParameters { BlackHoleMaxSteps = 2000 };   // part 2: DD edges on by default
        c.Calculate(TestContext.Current.CancellationToken);
        Assert.StartsWith("DD edges:", c.LastPrecisionLabel);
    }
}
