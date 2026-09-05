// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S12.2 / S12.3 / S12.4 (#652) — Lens / Edge ink / SSAO on Relief 3D.
// ReliefScreenSpacePost.ApplyStage2 runs the ScreenSpacePost passes over the relief
// buffer + its captured AOVs (HDR beauty for tone map/bloom; normal+depth for
// SSAO/edge; lens is a byte pass needing neither). These lock:
//   * lens changes the buffer with NO AOV (byte pass; works even under froxel);
//   * SSAO + edge ink change the buffer using the relief normal+depth G-buffer;
//   * ApplyStage2 returns true iff a pass ran, false (no-op) when nothing is set.

using System;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefStage2PostTests
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

    private static FractalParameters Relief() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
        Relief2DSupersample = 2,
    };

    // Render relief with a normal+depth G-buffer captured (no HDR needed for lens/edge/ssao).
    private static (uint[] dst, HeightfieldRaymarch2D.ReliefAovBuffers aov, int w, int h) RenderRelief()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        var dst = new uint[w * h];
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h, false, false, false); // normal+depth only
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, Relief(), dst, out double hitFrac, null, aov);
        Assert.True(hitFrac > 0.05 && hitFrac < 0.95, $"need a mixed hit/sky frame (hitFrac={hitFrac})");
        return (dst, aov, w, h);
    }

    private static int Diff(uint[] a, uint[] b)
    {
        int d = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) d++;
        return d;
    }

    [Fact]
    public void Lens_Changes_Output_Without_Aov()
    {
        var (dst, _, w, h) = RenderRelief();
        var before = (uint[])dst.Clone();

        var fx = LightingFxData.CreateDefault();
        fx.Vignette = 0.9;              // corner falloff — a pure byte-buffer lens pass

        // Null AOV: lens needs no HDR / G-buffer (so it works even under froxel).
        bool applied = ReliefScreenSpacePost.ApplyStage2(dst, null, w, h, in fx);

        Assert.True(applied);
        Assert.True(Diff(before, dst) > 0, "vignette must darken the corners");
    }

    [Fact]
    public void Ssao_Changes_Output_From_Depth_Guide()
    {
        var (dst, aov, w, h) = RenderRelief();
        var before = (uint[])dst.Clone();

        var fx = LightingFxData.CreateDefault();
        fx.SsaoSamples = 24;
        fx.SsaoRadius = 0.5;
        fx.SsaoStrength = 1.0;

        bool applied = ReliefScreenSpacePost.ApplyStage2(dst, aov, w, h, in fx);

        Assert.True(applied);
        Assert.True(Diff(before, dst) > 0, "SSAO must darken creviced terrain pixels");
    }

    [Fact]
    public void EdgeInk_Changes_Output_From_Normal_Depth_Guide()
    {
        var (dst, aov, w, h) = RenderRelief();
        var before = (uint[])dst.Clone();

        var fx = LightingFxData.CreateDefault();
        fx.EdgeStrength = 1.0;
        fx.EdgeThreshold = 0.1;

        bool applied = ReliefScreenSpacePost.ApplyStage2(dst, aov, w, h, in fx);

        Assert.True(applied);
        Assert.True(Diff(before, dst) > 0, "edge ink must draw contours on normal/depth discontinuities");
    }

    [Fact]
    public void NoStage2Fx_IsNoOp_ReturnsFalse()
    {
        var (dst, aov, w, h) = RenderRelief();
        var before = (uint[])dst.Clone();

        var fx = LightingFxData.CreateDefault(); // nothing set

        bool applied = ReliefScreenSpacePost.ApplyStage2(dst, aov, w, h, in fx);

        Assert.False(applied);
        Assert.Equal(before, dst);
    }

    [Fact]
    public void WantsGeom_WantsLens_Predicates()
    {
        var none = LightingFxData.CreateDefault();
        Assert.False(ReliefScreenSpacePost.WantsGeom(in none));
        Assert.False(ReliefScreenSpacePost.WantsLens(in none));

        var ssao = LightingFxData.CreateDefault(); ssao.SsaoSamples = 8;
        Assert.True(ReliefScreenSpacePost.WantsGeom(in ssao));

        var edge = LightingFxData.CreateDefault(); edge.EdgeStrength = 0.5;
        Assert.True(ReliefScreenSpacePost.WantsGeom(in edge));

        var lens = LightingFxData.CreateDefault(); lens.ChromaticAberration = 3;
        Assert.True(ReliefScreenSpacePost.WantsLens(in lens));
    }

    // ── S12.5: Stereo on Relief (depth-parallax SBS over the depth G-buffer) ──

    [Fact]
    public void WantsStereo_Predicate()
    {
        var off = LightingFxData.CreateDefault();
        Assert.False(ReliefScreenSpacePost.WantsStereo(in off));

        // Mode set but zero separation → still off (no parallax).
        var noSep = LightingFxData.CreateDefault();
        noSep.StereoMode = StereoMode.Fake; noSep.StereoEyeSeparation = 0.0;
        Assert.False(ReliefScreenSpacePost.WantsStereo(in noSep));

        var fake = LightingFxData.CreateDefault();
        fake.StereoMode = StereoMode.Fake; fake.StereoEyeSeparation = 0.05;
        Assert.True(ReliefScreenSpacePost.WantsStereo(in fake));

        // Relief has no per-eye camera, so True also routes through the warp → wanted.
        var tru = LightingFxData.CreateDefault();
        tru.StereoMode = StereoMode.True; tru.StereoEyeSeparation = 0.05;
        Assert.True(ReliefScreenSpacePost.WantsStereo(in tru));
    }

    [Fact]
    public void Stereo_FullSbs_DoublesWidth_And_Warps_From_Depth()
    {
        var (dst, aov, w, h) = RenderRelief();

        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.Fake;
        fx.StereoEyeSeparation = 0.06;
        fx.StereoLayout = StereoLayout.FullSbs;

        var sbs = ReliefScreenSpacePost.ApplyStereo(dst, aov, w, h, in fx, out int oW, out int oH);

        Assert.NotNull(sbs);
        Assert.Equal(w * 2, oW);
        Assert.Equal(h, oH);
        Assert.Equal(oW * oH, sbs!.Length);
        // Left eye = the original mono buffer, unchanged.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(dst[y * w + x], sbs[y * oW + x]);
        // Right eye = the parallax warp — must differ from a straight copy of the
        // left eye somewhere (near hits shift, sky stays put).
        bool anyShift = false;
        for (int y = 0; y < h && !anyShift; y++)
            for (int x = 0; x < w; x++)
                if (sbs[y * oW + w + x] != dst[y * w + x]) { anyShift = true; break; }
        Assert.True(anyShift, "right eye must show parallax vs the left/mono view");
    }

    [Fact]
    public void Stereo_HalfSbs_KeepsMonoDims()
    {
        var (dst, aov, w, h) = RenderRelief();

        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.Fake;
        fx.StereoEyeSeparation = 0.06;
        fx.StereoLayout = StereoLayout.HalfSbs;

        var sbs = ReliefScreenSpacePost.ApplyStereo(dst, aov, w, h, in fx, out int oW, out int oH);

        Assert.NotNull(sbs);
        Assert.Equal(w, oW);   // anamorphic squeeze → mono dims
        Assert.Equal(h, oH);
        Assert.Equal(oW * oH, sbs!.Length);
    }

    [Fact]
    public void Stereo_Off_ReturnsNull()
    {
        var (dst, aov, w, h) = RenderRelief();
        var fx = LightingFxData.CreateDefault(); // StereoMode.Off
        Assert.Null(ReliefScreenSpacePost.ApplyStereo(dst, aov, w, h, in fx, out _, out _));
    }

    [Fact]
    public void Stereo_NoDepthCapture_ReturnsNull()
    {
        var (dst, _, w, h) = RenderRelief();
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.Fake;
        fx.StereoEyeSeparation = 0.06;
        // No AOV → no depth → the warp has no parallax source, so stereo is skipped.
        Assert.Null(ReliefScreenSpacePost.ApplyStereo(dst, null, w, h, in fx, out _, out _));
    }
}
