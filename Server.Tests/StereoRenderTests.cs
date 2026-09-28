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

    [Fact]
    public void OutputDims_MatchLayout()
    {
        Assert.Equal((200, 100), StereoRender.OutputDims(100, 100, StereoLayout.FullSbs));
        Assert.Equal((100, 100), StereoRender.OutputDims(100, 100, StereoLayout.HalfSbs));
    }

    [Fact]
    public void ToHalfSbs_SqueezesEachEye_ToHalfWidth()
    {
        // Full-SBS row: left eye 4 px solid A, right eye 4 px solid B → Half-SBS
        // row is 4 px: [A,A | B,B]. Solid eyes make the 2:1 average exact.
        const int eyeW = 4, h = 1;
        const uint A = 0xFF204060u, B = 0xFF80A0C0u;
        var full = new uint[eyeW * 2 * h];
        for (int x = 0; x < eyeW; x++) { full[x] = A; full[eyeW + x] = B; }

        var half = StereoRender.ToHalfSbs(full, eyeW, h);

        Assert.Equal(eyeW, half.Length);        // W × H, not 2W × H
        Assert.Equal(A, half[0]);
        Assert.Equal(A, half[1]);               // left eye squeezed to [0,2)
        Assert.Equal(B, half[2]);
        Assert.Equal(B, half[3]);               // right eye squeezed to [2,4)
    }

    // Contract the #107 host wiring depends on: RenderTrueStereo drives two
    // renders at eye offsets -IPD/2 then +IPD/2, composites left|right into a
    // 2·W × H buffer, and leaves the calculator mono (offset 0) afterwards.
    [Fact]
    public void RenderTrueStereo_OffsetsEyes_Composites_And_ResetsOffset()
    {
        const int w = 6, h = 3;
        const uint leftColor = 0xFF111111u, rightColor = 0xFF222222u;

        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.True;
        fx.StereoEyeSeparation = 0.1;

        double offset = 0.0;
        var seen = new System.Collections.Generic.List<double>();
        var buf = new uint[w * h];
        // Each "render" paints per the sign of the eye offset set for this pass.
        void RenderOnce(CancellationToken _)
        {
            seen.Add(offset);
            Array.Fill(buf, offset < 0 ? leftColor : rightColor);
        }

        var sbs = StereoRender.RenderTrueStereo(
            in fx, o => offset = o, RenderOnce, () => buf, w, h, CancellationToken.None);

        Assert.NotNull(sbs);
        Assert.Equal(w * 2 * h, sbs!.Length);
        Assert.Equal(new[] { -0.05, 0.05 }, seen);
        for (int y = 0; y < h; y++)
        {
            Assert.Equal(leftColor, sbs[y * (w * 2) + 0]);      // left half = -IPD/2 pass
            Assert.Equal(rightColor, sbs[y * (w * 2) + w]);     // right half = +IPD/2 pass
        }
        Assert.Equal(0.0, offset);                             // back to mono
    }

    [Fact]
    public void RenderTrueStereo_StereoOff_ReturnsNull_WithoutRendering()
    {
        var fx = LightingFxData.CreateDefault(); // StereoMode.Off
        int renders = 0;
        Assert.Null(StereoRender.RenderTrueStereo(
            in fx, _ => { }, _ => renders++, () => new uint[4], 2, 2, CancellationToken.None));
        Assert.Equal(0, renders);
    }

    // #1008 — a faulted eye render must not leave the calculator offset for
    // the next (mono) frame.
    [Fact]
    public void RenderTrueStereo_Fault_ResetsOffset()
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.True;
        fx.StereoEyeSeparation = 0.2;
        double offset = 0.0;
        Assert.Throws<InvalidOperationException>(() => StereoRender.RenderTrueStereo(
            in fx, o => offset = o, _ => throw new InvalidOperationException(),
            () => new uint[4], 2, 2, CancellationToken.None));
        Assert.Equal(0.0, offset);
    }

    [Theory]
    [InlineData(true, StereoMode.True, 0.1, true)]
    [InlineData(false, StereoMode.True, 0.1, false)]   // 2D: no per-eye camera
    [InlineData(true, StereoMode.Fake, 0.1, false)]
    [InlineData(true, StereoMode.Off, 0.1, false)]
    [InlineData(true, StereoMode.True, 0.0, false)]
    public void WantsTrueStereo_Gate(bool is3D, StereoMode mode, double sep, bool expected)
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = mode;
        fx.StereoEyeSeparation = sep;
        Assert.Equal(expected, StereoRender.WantsTrueStereo(is3D, in fx));
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
        fx.StereoLayout = StereoLayout.HalfSbs;

        var round = LightingFxPresetData.FromFx(fx).ToFx();

        Assert.Equal(StereoMode.True, round.StereoMode);
        Assert.Equal(0.08, round.StereoEyeSeparation);
        Assert.Equal(75.0, round.StereoFovDegrees);
        Assert.Equal(0.04, round.StereoConvergence);
        Assert.Equal(0.05, round.StereoMaxDisparity);
        Assert.Equal(StereoLayout.HalfSbs, round.StereoLayout);
    }
}
