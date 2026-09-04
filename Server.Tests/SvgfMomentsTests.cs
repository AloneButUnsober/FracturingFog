// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 (3D-Rendering-Roadmap.md, #389 / #402) — SVGF temporal luminance
// moments. Contract: the first frame (no history) is a single sample (m1 = l, m2 =
// l², length 1); a subsequent frame blends toward the reprojected history and grows
// the per-pixel length; a per-pixel-constant signal converges to zero variance; an
// off-frame reprojection resets the length; deterministic.

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SvgfMomentsTests
{
    private static uint[] Flat(int n, uint v)
    {
        var b = new uint[n];
        for (int i = 0; i < n; i++) b[i] = v;
        return b;
    }

    // Rec.601 luma of a grey level (R=G=B=v).
    private static double GreyLuma(int v) => v / 255.0;

    [Fact]
    public void First_Frame_Is_A_Single_Sample()
    {
        int w = 8, h = 8, n = w * h;
        var cur = Flat(n, 0xFF404040u);   // grey 64
        var (m1, m2, len) = SvgfMoments.Accumulate(cur, null, null, null, null, w, h, 0.8);

        double l = GreyLuma(0x40);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal((float)l, m1[i], 5);
            Assert.Equal((float)(l * l), m2[i], 5);
            Assert.Equal(1, len[i]);
        }
    }

    [Fact]
    public void Second_Frame_Blends_Toward_History_And_Grows_Length()
    {
        int w = 8, h = 8, n = w * h;
        var cur = Flat(n, 0xFF808080u);   // grey 128
        var histM1 = new float[n];
        var histM2 = new float[n];
        var histLen = new byte[n];
        for (int i = 0; i < n; i++) { histM1[i] = 0.2f; histM2[i] = 0.05f; histLen[i] = 3; }

        double l = GreyLuma(0x80), a = 0.8;
        var (m1, m2, len) = SvgfMoments.Accumulate(cur, histM1, histM2, histLen, null, w, h, a);

        Assert.Equal((float)(l * (1 - a) + 0.2 * a), m1[0], 5);
        Assert.Equal((float)(l * l * (1 - a) + 0.05 * a), m2[0], 5);
        Assert.Equal(4, len[0]);   // 3 + 1
    }

    [Fact]
    public void Null_History_Length_Starts_At_Two_On_Reuse()
    {
        int w = 4, h = 4, n = w * h;
        var cur = Flat(n, 0xFF505050u);
        var histM1 = new float[n]; var histM2 = new float[n];
        for (int i = 0; i < n; i++) { histM1[i] = 0.3f; histM2[i] = 0.09f; }
        // Moments present but no length buffer → the reused sample counts as 2.
        var (_, _, len) = SvgfMoments.Accumulate(cur, histM1, histM2, null, null, w, h, 0.5);
        Assert.Equal(2, len[0]);
    }

    [Fact]
    public void Constant_Signal_Converges_To_Zero_Variance()
    {
        int w = 8, h = 8, n = w * h;
        var cur = Flat(n, 0xFF909090u);   // the same grey every frame
        float[]? m1 = null, m2 = null; byte[]? len = null;
        for (int frame = 0; frame < 5; frame++)
            (m1, m2, len) = SvgfMoments.Accumulate(cur, m1, m2, len, null, w, h, 0.8);

        var variance = SvgfVariance.FromMoments(m1!, m2!, w, h);
        foreach (var v in variance) Assert.True(v < 1e-6f, $"constant signal variance not ~0: {v}");
        Assert.True(len![0] >= 5, $"length did not accumulate: {len[0]}");
    }

    [Fact]
    public void Off_Frame_Reprojection_Resets_Length()
    {
        int w = 8, h = 8, n = w * h;
        var cur = Flat(n, 0xFF808080u);
        var histM1 = new float[n]; var histM2 = new float[n]; var histLen = new byte[n];
        for (int i = 0; i < n; i++) { histM1[i] = 0.5f; histM2[i] = 0.25f; histLen[i] = 9; }
        var motion = new float[n * 2];
        for (int i = 0; i < n; i++) { motion[i * 2] = -1000f; motion[i * 2 + 1] = 0f; }

        var (m1, _, len) = SvgfMoments.Accumulate(cur, histM1, histM2, histLen, motion, w, h, 0.9);
        double l = GreyLuma(0x80);
        Assert.Equal((float)l, m1[0], 5);   // reset to the current single sample
        Assert.Equal(1, len[0]);
    }

    [Fact]
    public void Is_Deterministic()
    {
        int w = 12, h = 12, n = w * h;
        var cur = Flat(n, 0xFF707070u);
        var histM1 = new float[n]; var histM2 = new float[n]; var histLen = new byte[n];
        for (int i = 0; i < n; i++) { histM1[i] = 0.4f; histM2[i] = 0.2f; histLen[i] = 2; }
        var a = SvgfMoments.Accumulate(cur, histM1, histM2, histLen, null, w, h, 0.7);
        var b = SvgfMoments.Accumulate(cur, histM1, histM2, histLen, null, w, h, 0.7);
        Assert.Equal(a.m1, b.m1);
        Assert.Equal(a.m2, b.m2);
        Assert.Equal(a.length, b.length);
    }
}
