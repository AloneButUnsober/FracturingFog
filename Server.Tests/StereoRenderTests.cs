// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Threading;
using Xunit;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Server.Tests;

// #108 — SBS stereo comfort. Locks in the convergence (HIT) shift, the
// parallax comfort clamp on the Fake warp, and the SuggestEyeSeparation math.
// Anaglyph is deliberately out of scope (#106): the owner is red-green
// colorblind and SBS relies on zero color discrimination.
public class StereoRenderTests
{
    private static LightingFxData Fx(double eyeSep, double conv, double maxDisp)
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoEyeSeparation = eyeSep;
        fx.StereoConvergence = conv;
        fx.StereoMaxDisparity = maxDisp;
        fx.StereoFovDegrees = 60.0;
        return fx;
    }

    [Fact]
    public void StereoOff_ReturnsNull()
    {
        var color = new uint[16];
        var depth = new float[16];
        Assert.Null(StereoRender.ApplyStereoSideBySide(color, depth, 4, 4, Fx(0, 0, 0.03)));
    }

    [Fact]
    public void Output_IsDoubledWidth_And_LeftEye_IsSource()
    {
        const int w = 8, h = 4;
        var color = new uint[w * h];
        var depth = new float[w * h];
        for (int i = 0; i < color.Length; i++) { color[i] = 0xFF000000u | (uint)i; depth[i] = ScreenSpacePost.DepthMiss; }

        var outBuf = StereoRender.ApplyStereoSideBySide(color, depth, w, h, Fx(0.05, 0, 0.03));

        Assert.NotNull(outBuf);
        Assert.Equal(w * 2 * h, outBuf!.Length);
        // Sky-only depth ⇒ no parallax; left half is a verbatim copy of source.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(color[y * w + x], outBuf[y * (w * 2) + x]);
    }

    [Fact]
    public void ApplyConvergence_Zero_IsNoOp()
    {
        const int w = 8, h = 2, outW = w * 2;
        var buf = new uint[outW * h];
        for (int i = 0; i < buf.Length; i++) buf[i] = (uint)(1000 + i);
        var copy = (uint[])buf.Clone();

        StereoRender.ApplyConvergence(buf, outW, w, h, 0.0);

        Assert.Equal(copy, buf);
    }

    [Fact]
    public void ApplyConvergence_Positive_ShiftsEyesOppositely_EdgeClamped()
    {
        // width 8, conv 0.5 ⇒ half = round(0.5·8·0.5) = 2. Left eye shifts +2
        // (content right), right eye shifts −2 (content left); edges replicate.
        const int w = 8, h = 1, outW = w * 2;
        var buf = new uint[outW];
        for (int x = 0; x < w; x++) buf[x] = (uint)(100 + x);        // left eye
        for (int x = 0; x < w; x++) buf[w + x] = (uint)(200 + x);    // right eye

        StereoRender.ApplyConvergence(buf, outW, w, h, 0.5);

        // Left eye shifted right by 2: cols 0,1 clamp to source col 0.
        Assert.Equal(100u, buf[0]);
        Assert.Equal(100u, buf[1]);
        Assert.Equal(100u, buf[2]); // src col 0
        Assert.Equal(101u, buf[3]); // src col 1
        Assert.Equal(105u, buf[7]); // src col 5
        // Right eye shifted left by 2: high cols clamp to source col 7.
        Assert.Equal(202u, buf[w + 0]); // src col 2
        Assert.Equal(207u, buf[w + 5]); // src col 7
        Assert.Equal(207u, buf[w + 6]); // clamp
        Assert.Equal(207u, buf[w + 7]); // clamp
    }

    [Fact]
    public void MaxDisparity_Clamps_NearPixel_Shift()
    {
        // One very-near marker among far pixels. Unclamped its parallax shift
        // would run off-screen; the guard caps it to maxDisparity·width.
        const int w = 32, h = 1;
        const uint marker = 0xFF00FF00u;
        var color = new uint[w * h];
        var depth = new float[w * h];
        for (int x = 0; x < w; x++) { color[x] = 0xFF000000u; depth[x] = 1000f; } // far, opaque black
        color[16] = marker; depth[16] = 0.01f;                                    // very near

        // focalPx = 16/tan(30°) ≈ 27.7; near shift ≈ 0.1·27.7/0.01 ≈ 277 px.
        // maxDisparity 0.1 ⇒ cap 3.2 px ⇒ round 3 ⇒ lands at col 16−3 = 13.
        var outBuf = StereoRender.ApplyStereoSideBySide(color, depth, w, h, Fx(0.1, 0, 0.1));

        Assert.NotNull(outBuf);
        Assert.Equal(marker, outBuf![w + 13]);    // clamped landing column (right eye)
        Assert.NotEqual(marker, outBuf[w + 16]);  // would-be origin: no marker (hole-filled)
    }

    [Fact]
    public void SuggestEyeSeparation_HitsDisparityBudget()
    {
        const int w = 64, h = 1;
        var depth = new float[w * h];
        Array.Fill(depth, ScreenSpacePost.DepthMiss);
        depth[10] = 2.5f; // nearest finite hit

        var fx = Fx(0, 0, 0.03);
        double sep = StereoRender.SuggestEyeSeparation(depth, w, h, fx);

        double focalPx = (w * 0.5) / Math.Tan(fx.StereoFovDegrees * Math.PI / 180.0 * 0.5);
        double disparityPx = sep * focalPx / 2.5;   // disparity of the nearest hit
        Assert.Equal(fx.StereoMaxDisparity * w, disparityPx, 3); // ≈ within 1e-3
    }

    [Fact]
    public void SuggestEyeSeparation_AllSky_ReturnsZero()
    {
        const int w = 16, h = 1;
        var depth = new float[w * h];
        Array.Fill(depth, ScreenSpacePost.DepthMiss);
        Assert.Equal(0.0, StereoRender.SuggestEyeSeparation(depth, w, h, Fx(0, 0, 0.03)));
    }

    // Contract the #107 host wiring depends on: RenderTrueStereo drives two
    // renders at eye offsets -IPD/2 then +IPD/2, composites left|right into a
    // 2·W × H buffer, and restores Lighting afterwards (EyeOffset back to 0).
    [Fact]
    public void RenderTrueStereo_OffsetsEyes_Composites_And_Restores()
    {
        const int w = 6, h = 3;
        const uint leftColor = 0xFF111111u, rightColor = 0xFF222222u;

        var fp = new FractalParameters();
        var lf = LightingFxData.CreateDefault();
        lf.StereoMode = StereoMode.True;
        lf.StereoEyeSeparation = 0.1;
        fp.Lighting = lf;

        var buf = new uint[w * h];
        // Each "render" paints per the sign of the transient eye offset that
        // RenderTrueStereo set for this pass.
        void RenderOnce(CancellationToken _)
        {
            double off = fp.Lighting.StereoEyeOffset;
            Array.Fill(buf, off < 0 ? leftColor : rightColor);
        }

        var sbs = StereoRender.RenderTrueStereo(
            fp, RenderOnce, () => buf, w, h, CancellationToken.None);

        Assert.NotNull(sbs);
        Assert.Equal(w * 2 * h, sbs!.Length);
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(leftColor, sbs[y * (w * 2) + 0]);      // left half = -IPD/2 pass
            Assert.Equal(rightColor, sbs[y * (w * 2) + w]);     // right half = +IPD/2 pass
        }
        // Lighting restored — no leaked stereo offset.
        Assert.Equal(0.0, fp.Lighting.StereoEyeOffset);
        Assert.Equal(StereoMode.True, fp.Lighting.StereoMode);
    }

    [Fact]
    public void RenderTrueStereo_StereoOff_ReturnsNull()
    {
        var fp = new FractalParameters(); // default Lighting = StereoMode.Off
        Assert.Null(StereoRender.RenderTrueStereo(
            fp, _ => { }, () => new uint[4], 2, 2, CancellationToken.None));
    }

    // #107 — stereo settings must survive a scene/preset save-load so a saved
    // scene reopens in stereo, not mono.
    [Fact]
    public void PresetRoundTrip_PreservesStereoFields()
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.True;
        fx.StereoEyeSeparation = 0.08;
        fx.StereoFovDegrees = 75.0;
        fx.StereoConvergence = 0.04;
        fx.StereoMaxDisparity = 0.05;

        var round = LightingFxPresetData.FromFx(fx).ToFx();

        Assert.Equal(StereoMode.True, round.StereoMode);
        Assert.Equal(0.08, round.StereoEyeSeparation);
        Assert.Equal(75.0, round.StereoFovDegrees);
        Assert.Equal(0.04, round.StereoConvergence);
        Assert.Equal(0.05, round.StereoMaxDisparity);
    }
}
