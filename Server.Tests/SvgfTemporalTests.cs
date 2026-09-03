// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 (3D-Rendering-Roadmap.md, #389 / #402) — SVGF temporal
// accumulation. Contract: a null history / alpha 0 is the identity (byte-identical
// default); accumulating a noisy frame toward a clean history reduces variance;
// an off-frame reprojection keeps the current pixel (disocclusion); a normal or
// depth guide that disagrees rejects the history; deterministic; alpha carried.

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SvgfTemporalTests
{
    private static uint[] Flat(int n, uint v)
    {
        var b = new uint[n];
        for (int i = 0; i < n; i++) b[i] = v;
        return b;
    }

    private static uint[] NoisyGray(int w, int h, int mean, int amp, int seed)
    {
        var buf = new uint[w * h];
        uint s = (uint)seed | 1u;
        for (int i = 0; i < buf.Length; i++)
        {
            s = s * 1664525u + 1013904223u;
            int noise = (int)((s >> 8) % (uint)(2 * amp + 1)) - amp;
            int v = Math.Clamp(mean + noise, 0, 255);
            buf[i] = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v;
        }
        return buf;
    }

    private static double StdDevR(uint[] buf)
    {
        double m = 0;
        foreach (var c in buf) m += (c >> 16) & 0xFF;
        m /= buf.Length;
        double v = 0;
        foreach (var c in buf) { double d = ((c >> 16) & 0xFF) - m; v += d * d; }
        return Math.Sqrt(v / buf.Length);
    }

    private static int R(uint c) => (int)((c >> 16) & 0xFF);

    [Fact]
    public void NullHistory_Is_Identity()
    {
        int w = 16, h = 16;
        var cur = NoisyGray(w, h, 128, 40, 3);
        var outp = SvgfTemporal.Accumulate(cur, null, null, w, h, 0.9);
        Assert.Equal(cur, outp);
    }

    [Fact]
    public void ZeroAlpha_Is_Identity()
    {
        int w = 16, h = 16, n = w * h;
        var cur = NoisyGray(w, h, 128, 40, 5);
        var hist = Flat(n, 0xFF404040u);
        var outp = SvgfTemporal.Accumulate(cur, hist, null, w, h, 0.0);
        Assert.Equal(cur, outp);
    }

    [Fact]
    public void Identical_History_No_Motion_Is_Identity()
    {
        int w = 16, h = 16;
        var cur = NoisyGray(w, h, 100, 20, 7);
        var hist = (uint[])cur.Clone();
        var outp = SvgfTemporal.Accumulate(cur, hist, null, w, h, 0.5);
        Assert.Equal(cur, outp);   // blend of equal values = same
    }

    [Fact]
    public void Accumulation_Reduces_Noise_Toward_History()
    {
        int w = 48, h = 48, n = w * h;
        var cur = NoisyGray(w, h, 128, 40, 11);
        var hist = Flat(n, 0xFF808080u);   // clean converged history at the mean
        var outp = SvgfTemporal.Accumulate(cur, hist, null, w, h, 0.8);

        double before = StdDevR(cur), after = StdDevR(outp);
        Assert.True(after < before * 0.35, $"temporal accumulation did not reduce noise ({before:F1} → {after:F1})");
    }

    [Fact]
    public void OffFrame_Reprojection_Keeps_Current()
    {
        int w = 16, h = 16, n = w * h;
        var cur = Flat(n, 0xFF000000u);
        var hist = Flat(n, 0xFFFFFFFFu);
        // Motion pushes every reprojection far off-frame → history rejected everywhere.
        var motion = new float[n * 2];
        for (int i = 0; i < n; i++) { motion[i * 2] = -1000f; motion[i * 2 + 1] = 0f; }
        var outp = SvgfTemporal.Accumulate(cur, hist, motion, w, h, 0.9);
        Assert.Equal(cur, outp);
    }

    [Fact]
    public void Normal_Disocclusion_Rejects_History()
    {
        int w = 16, h = 16, n = w * h;
        var cur = Flat(n, 0xFF000000u);
        var hist = Flat(n, 0xFFFFFFFFu);
        var curN = new float[n * 3];
        var histN = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            curN[i * 3 + 2] = 1f;   // current faces +Z
            histN[i * 3] = 1f;      // history faces +X → dot 0 < threshold
        }
        var reject = SvgfTemporal.Accumulate(cur, hist, null, w, h, 0.9, curN, histN);
        Assert.Equal(cur, reject);   // history rejected → stays black

        var accept = SvgfTemporal.Accumulate(cur, hist, null, w, h, 0.9);   // no guides → blends
        Assert.True(R(accept[0]) > 200, "without a normal guide the history should blend in");
    }

    [Fact]
    public void Depth_Disocclusion_Rejects_History()
    {
        int w = 16, h = 16, n = w * h;
        var cur = Flat(n, 0xFF000000u);
        var hist = Flat(n, 0xFFFFFFFFu);
        var curD = new float[n];
        var histD = new float[n];
        for (int i = 0; i < n; i++) { curD[i] = 1.0f; histD[i] = 5.0f; }   // 400% jump
        var outp = SvgfTemporal.Accumulate(cur, hist, null, w, h, 0.9,
            null, null, curD, histD);
        Assert.Equal(cur, outp);
    }

    [Fact]
    public void Is_Deterministic_And_Keeps_Alpha()
    {
        int w = 24, h = 24, n = w * h;
        var cur = NoisyGray(w, h, 128, 30, 13);
        for (int i = 0; i < n; i++) cur[i] = (cur[i] & 0x00FFFFFFu) | 0x80000000u;   // alpha 0x80
        var hist = Flat(n, 0xFF303030u);
        var a = SvgfTemporal.Accumulate(cur, hist, null, w, h, 0.7);
        var b = SvgfTemporal.Accumulate(cur, hist, null, w, h, 0.7);
        Assert.Equal(a, b);
        Assert.Equal(0x80u, (a[0] >> 24) & 0xFF);   // current alpha carried
    }
}
