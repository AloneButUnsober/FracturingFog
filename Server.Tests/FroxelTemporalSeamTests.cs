// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S6 (#408) — froxel temporal persistence seam. A sequence renderer can hand
// the SAME FroxelHistory to every frame via PosterRequest.FroxelHistory so animated
// fog blends across frames. These lock that the history threads all the way through
// PosterRenderer → ApplyReliefIfEnabled → HeightfieldRaymarch2D (the froxel CPU
// post-pass): a second frame rendered with a shared, seeded history differs from the
// same frame rendered fresh (null history), and a null history is byte-identical to
// the single-frame render (default off).

using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FroxelTemporalSeamTests
{
    // A Mandelbrot relief-raymarch scene with froxel fog + temporal on. `fog` scales
    // the fog density so successive frames can "animate" the volume.
    private static PosterRequest ReliefFogReq(double fog, FroxelHistory? history)
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = fog;
        fx.VolumeSteps = 12;
        fx.Light1.Intensity = 1.0;
        fx.ShowSkyBackdrop = true;

        var fp = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DGpuRaymarch = false,   // CPU froxel post-pass (the temporal path)
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,
            Relief2DFroxelVolumetrics = true,
            Relief2DFroxelTemporal = true,
            Relief2DFroxelTemporalFeedback = 0.85,
            Lighting = fx,
        };

        return new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0,
            Zoom = 1.0,
            MaxIterations = 300,
            Width = 96, Height = 72,
            ColorMap = ColorPalette.BuiltIns[0],
            Quality = QualityPreset.Standard,
            FractalParameters = fp,
            FroxelHistory = history,
        };
    }

    [Fact]
    public void SharedHistory_ThreadsThrough_AndBlendsAcrossFrames()
    {
        // Frame 1 (dense fog) seeds the shared history.
        var history = new FroxelHistory();
        var f1 = PosterRenderer.RenderToPixels(ReliefFogReq(0.8, history), default, out int w, out int h);
        Assert.True(w > 0 && h > 0);

        // Frame 2 (thin fog) WITH the shared, seeded history → temporally blended.
        var f2Temporal = PosterRenderer.RenderToPixels(ReliefFogReq(0.15, history), default, out _, out _);

        // The SAME frame 2 rendered fresh (no history) → single-frame.
        var f2Single = PosterRenderer.RenderToPixels(ReliefFogReq(0.15, null), default, out _, out _);

        // The seeded history must have pulled the temporal frame away from the fresh
        // one — proving PosterRequest.FroxelHistory reached the froxel post-pass.
        bool differs = false;
        for (int i = 0; i < f2Temporal.Length; i++)
            if (f2Temporal[i] != f2Single[i]) { differs = true; break; }
        Assert.True(differs, "shared froxel history should blend frame 2 away from the fresh render");
    }

    [Fact]
    public void NullHistory_IsSingleFrame_Deterministic()
    {
        // No history → the froxel post-pass is single-frame; two independent renders of
        // the same scene are byte-identical (deterministic, no cross-frame state).
        var a = PosterRenderer.RenderToPixels(ReliefFogReq(0.6, null), default, out _, out _);
        var b = PosterRenderer.RenderToPixels(ReliefFogReq(0.6, null), default, out _, out _);
        Assert.Equal(a, b);
    }
}
