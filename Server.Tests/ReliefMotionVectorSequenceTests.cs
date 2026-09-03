// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 (3D-Rendering-Roadmap.md, #389 / #398) — the motion-vector AOV
// SEQUENCE seam. The pure operator (PR #577) + the render wiring (PR #638) fill the
// Motion channel from a previous-frame camera; this threads that previous camera
// through PosterRequest → PosterRenderer → ApplyReliefIfEnabled → the relief render,
// so the offline sequence renderers (which all build PosterRequests) can carry a
// per-frame previous camera. Mirrors the PR #468 FroxelHistory seam. Locks: a
// motion-capturing AOV + a supplied PreviousCamera fills motion on the relief path;
// an identical previous camera gives ~zero; no previous camera leaves motion zero.

using System;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefMotionVectorSequenceTests
{
    private static PosterRequest ReliefRequest(double azimuthDeg = 25,
        ReliefMotionVector.CameraView? previous = null, double motionBlur = 0.0)
    {
        var fp = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = azimuthDeg,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,   // sky background → real silhouette
            Relief2DMotionBlurStrength = motionBlur,
        };
        return new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1.0,
            MaxIterations = 150,
            Width = 96, Height = 72,
            ColorMap = ColorPalette.BuiltIns[0],
            Quality = QualityPreset.Standard,
            FractalParameters = fp,
            Path = "unused.png",
            Format = ImageFileFormat.Png,
            PreviousCamera = previous,
        };
    }

    private static HeightfieldRaymarch2D.ReliefAovBuffers RenderCapturingMotion(PosterRequest req)
    {
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(96, 72, false, true);   // captureMotion
        PosterRenderer.RenderToPixels(req, default, out _, out _, aov);
        return aov;
    }

    [Fact]
    public void Render_Sets_CurrentCamera_On_The_Capture()
    {
        var aov = RenderCapturingMotion(ReliefRequest());
        Assert.True(aov.CurrentCamera.HasValue, "the relief render did not expose its camera through PosterRenderer");
    }

    [Fact]
    public void No_Previous_Camera_Leaves_Motion_Zero()
    {
        var aov = RenderCapturingMotion(ReliefRequest(previous: null));
        Assert.NotNull(aov.Motion);
        for (int i = 0; i < aov.Motion!.Length; i++)
            Assert.Equal(0f, aov.Motion[i]);
    }

    [Fact]
    public void Identical_Previous_Camera_Gives_Near_Zero_Motion()
    {
        var first = RenderCapturingMotion(ReliefRequest());
        var aov = RenderCapturingMotion(ReliefRequest(previous: first.CurrentCamera));

        double maxMag = 0.0;
        for (int i = 0; i < 96 * 72; i++)
            maxMag = Math.Max(maxMag, Math.Abs(aov.Motion![i * 2]) + Math.Abs(aov.Motion[i * 2 + 1]));
        Assert.True(maxMag < 1e-3, $"identical cameras should give ~zero motion, got {maxMag}");
    }

    [Fact]
    public void Moved_Previous_Camera_Fills_NonZero_Motion()
    {
        // The previous frame sat at a different azimuth than the current one.
        var prev = RenderCapturingMotion(ReliefRequest(azimuthDeg: 18));
        var aov = RenderCapturingMotion(ReliefRequest(azimuthDeg: 25, previous: prev.CurrentCamera));

        int moved = 0;
        for (int i = 0; i < 96 * 72; i++)
            if (Math.Abs(aov.Motion![i * 2]) + Math.Abs(aov.Motion[i * 2 + 1]) > 0.5) moved++;
        Assert.True(moved > (96 * 72) / 100,
            $"a camera move should shift many terrain pixels through the seam, only {moved} moved");
    }

    // S1 (#398) consumer — vector motion blur end-to-end through PosterRenderer.
    // A moved previous camera + a non-zero strength smears the beauty (output
    // differs from the un-blurred render); with no previous camera (a still frame)
    // the same strength changes nothing.
    private static uint[] RenderBeauty(PosterRequest req)
        => PosterRenderer.RenderToPixels(req, default, out _, out _);

    [Fact]
    public void MotionBlur_Changes_The_Render_When_Camera_Moved()
    {
        var prev = RenderCapturingMotion(ReliefRequest(azimuthDeg: 16));
        var sharp = RenderBeauty(ReliefRequest(azimuthDeg: 25, previous: prev.CurrentCamera, motionBlur: 0.0));
        var blurred = RenderBeauty(ReliefRequest(azimuthDeg: 25, previous: prev.CurrentCamera, motionBlur: 1.0));

        Assert.Equal(sharp.Length, blurred.Length);
        int diff = 0;
        for (int i = 0; i < sharp.Length; i++) if (sharp[i] != blurred[i]) diff++;
        Assert.True(diff > sharp.Length / 100, $"motion blur changed too few pixels ({diff})");
    }

    [Fact]
    public void MotionBlur_Is_Identity_On_A_Still_Frame()
    {
        // No previous camera → the motion AOV is zero → the strength does nothing,
        // and the GPU-fast-path beauty is preserved (byte-identical to strength 0).
        var sharp = RenderBeauty(ReliefRequest(previous: null, motionBlur: 0.0));
        var still = RenderBeauty(ReliefRequest(previous: null, motionBlur: 1.0));
        Assert.Equal(sharp, still);
    }
}
