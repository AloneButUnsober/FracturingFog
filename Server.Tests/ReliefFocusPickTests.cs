// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S3 click-to-focus (#400) — HeightfieldRaymarch2D.FocusDistanceAtPixel returns the
// camera-to-surface distance at a clicked output pixel, ready to drop into
// Relief2DDofFocusDistance. These lock: the raymarch-off / off-frame / sky-miss
// cases return NoFocus (0), a terrain pixel returns a finite positive distance, and
// the pick is deterministic.

using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Server.Tests;

public sealed class ReliefFocusPickTests
{
    private static (uint[] albedo, float[] height) Mandelbrot(int w, int h)
    {
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 400,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    }

    // Relief raymarch, perspective, no ground plane → a real silhouette (both
    // terrain hits and sky misses in frame).
    private static FractalParameters Relief() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
        Relief2DSupersample = 1,
    };

    [Fact]
    public void NonRelief_Params_Return_NoFocus()
    {
        const int w = 96, h = 72;
        var (albedo, height) = Mandelbrot(w, h);
        var p = Relief();
        p.Relief2DRaymarch = false;   // relief on but not the raymarch path
        Assert.Equal(HeightfieldRaymarch2D.NoFocus,
            HeightfieldRaymarch2D.FocusDistanceAtPixel(albedo, height, w, h, w, h, p, w / 2, h / 2));

        p.Relief2DEnabled = false;
        Assert.Equal(HeightfieldRaymarch2D.NoFocus,
            HeightfieldRaymarch2D.FocusDistanceAtPixel(albedo, height, w, h, w, h, p, w / 2, h / 2));
    }

    [Fact]
    public void OffFrame_Pixel_Returns_NoFocus()
    {
        const int w = 96, h = 72;
        var (albedo, height) = Mandelbrot(w, h);
        var p = Relief();
        Assert.Equal(HeightfieldRaymarch2D.NoFocus,
            HeightfieldRaymarch2D.FocusDistanceAtPixel(albedo, height, w, h, w, h, p, w + 5, h / 2));
        Assert.Equal(HeightfieldRaymarch2D.NoFocus,
            HeightfieldRaymarch2D.FocusDistanceAtPixel(albedo, height, w, h, w, h, p, w / 2, -1));
    }

    [Fact]
    public void Terrain_Pixel_Returns_Positive_Finite_Distance_Deterministically()
    {
        const int w = 128, h = 96;
        var (albedo, height) = Mandelbrot(w, h);
        var p = Relief();

        // The fractal body sits around the frame centre for this view; the centre
        // pixel is on terrain, so the pick is a real camera-to-surface distance.
        double d1 = HeightfieldRaymarch2D.FocusDistanceAtPixel(albedo, height, w, h, w, h, p, w / 2, h / 2);
        double d2 = HeightfieldRaymarch2D.FocusDistanceAtPixel(albedo, height, w, h, w, h, p, w / 2, h / 2);

        Assert.True(d1 > 0.0, "centre pixel should hit terrain");
        Assert.True(double.IsFinite(d1) && d1 < 9.9e5, $"distance out of range: {d1}");
        Assert.Equal(d1, d2);   // deterministic (CPU trace, fixed seed)
    }

    [Fact]
    public void Silhouette_Has_Both_Hits_And_SkyMisses()
    {
        // With the ground plane off, some in-frame pixels hit terrain (>0) and some
        // miss to sky (NoFocus) — proving the miss sentinel is honoured, not just a
        // blanket non-zero.
        const int w = 128, h = 96;
        var (albedo, height) = Mandelbrot(w, h);
        var p = Relief();

        int hits = 0, misses = 0;
        for (int py = 0; py < h; py += 6)
            for (int px = 0; px < w; px += 6)
            {
                double d = HeightfieldRaymarch2D.FocusDistanceAtPixel(albedo, height, w, h, w, h, p, px, py);
                if (d > 0.0) hits++; else misses++;
            }

        Assert.True(hits > 0, "expected some terrain hits");
        Assert.True(misses > 0, "expected some sky misses (ground plane is off)");
    }
}
