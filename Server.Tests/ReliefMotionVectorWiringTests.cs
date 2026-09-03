// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 (3D-Rendering-Roadmap.md, #389 / #398) — the motion-vector AOV
// WIRING. PR #577 shipped the pure ReliefMotionVector operator + the opt-in
// ReliefAovBuffers.Motion channel; this threads the relief raymarch's own current
// camera (exposed as aov.CurrentCamera) and a caller-supplied previous camera into
// the channel. Locks: the render exposes its camera; a still frame (previous ==
// current) yields ~zero motion; a moved camera produces non-zero motion on the
// terrain; sky-miss pixels are exactly zero; and Motion stays unfilled (default)
// when no previous camera is supplied.

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefMotionVectorWiringTests
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

    private static FractalParameters Relief(double azimuthDeg = 25) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = azimuthDeg,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,   // sky background → real silhouette
        Relief2DSupersample = 2,
    };

    private static HeightfieldRaymarch2D.ReliefAovBuffers RenderMotion(
        uint[] albedo, float[] height, int w, int h, FractalParameters p,
        ReliefMotionVector.CameraView? previous)
    {
        var dst = new uint[w * h];
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h, false, true);   // captureMotion
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, p, dst, out _, null, aov,
            previousCamera: previous);
        return aov;
    }

    [Fact]
    public void Render_Exposes_Its_Current_Camera_When_Aov_Supplied()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        var aov = RenderMotion(albedo, height, w, h, Relief(), previous: null);
        Assert.True(aov.CurrentCamera.HasValue, "render did not expose its camera on the AOV buffer");
    }

    [Fact]
    public void No_Previous_Camera_Leaves_Motion_Unfilled_Zero()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        var aov = RenderMotion(albedo, height, w, h, Relief(), previous: null);
        Assert.NotNull(aov.Motion);
        for (int i = 0; i < aov.Motion!.Length; i++)
            Assert.Equal(0f, aov.Motion[i]);
    }

    [Fact]
    public void Still_Frame_Has_Near_Zero_Motion()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        // First render exposes the camera; feed it straight back as "previous".
        var first = RenderMotion(albedo, height, w, h, Relief(), previous: null);
        var aov = RenderMotion(albedo, height, w, h, Relief(), previous: first.CurrentCamera);

        double maxMag = 0.0;
        for (int i = 0; i < w * h; i++)
        {
            double du = aov.Motion![i * 2], dv = aov.Motion[i * 2 + 1];
            maxMag = Math.Max(maxMag, Math.Abs(du) + Math.Abs(dv));
        }
        Assert.True(maxMag < 1e-3, $"identical cameras should give ~zero motion, got max |motion|={maxMag}");
    }

    [Fact]
    public void Moved_Camera_Produces_NonZero_Terrain_Motion()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        // Previous frame sat at a different azimuth than the current frame.
        var prevFrame = RenderMotion(albedo, height, w, h, Relief(azimuthDeg: 18), previous: null);
        var aov = RenderMotion(albedo, height, w, h, Relief(azimuthDeg: 25), previous: prevFrame.CurrentCamera);

        int moved = 0;
        for (int i = 0; i < w * h; i++)
        {
            double du = aov.Motion![i * 2], dv = aov.Motion[i * 2 + 1];
            if (Math.Abs(du) + Math.Abs(dv) > 0.5) moved++;
        }
        Assert.True(moved > (w * h) / 100,
            $"a camera move should shift many terrain pixels, only {moved} moved");
    }

    [Fact]
    public void Sky_Pixels_Have_Exactly_Zero_Motion()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        var prevFrame = RenderMotion(albedo, height, w, h, Relief(azimuthDeg: 18), previous: null);
        var aov = RenderMotion(albedo, height, w, h, Relief(azimuthDeg: 25), previous: prevFrame.CurrentCamera);

        int skyChecked = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (aov.Depth[i] >= 9.9e5)   // the sky-miss sentinel
            {
                Assert.Equal(0f, aov.Motion![i * 2]);
                Assert.Equal(0f, aov.Motion[i * 2 + 1]);
                skyChecked++;
            }
        }
        Assert.True(skyChecked > 0, "expected some sky pixels in a silhouette frame");
    }
}
