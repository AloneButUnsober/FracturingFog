// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 (3D-Rendering-Roadmap.md, #389 / #402) — SVGF variance guiding.
// Two contracts: the variance ESTIMATOR (flat → ~0, noisy → >0, moments math) and
// the variance-GUIDED À-Trous (null variance / scale 0 = byte-identical to the plain
// denoise; a high variance estimate + scale > 0 smooths a noisy region MORE than the
// plain filter at the same colour sigma).

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SvgfVarianceTests
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

    // ── estimator ──────────────────────────────────────────────────────────

    [Fact]
    public void Flat_Region_Has_Zero_Variance()
    {
        int w = 16, h = 16, n = w * h;
        var v = SvgfVariance.EstimateSpatial(Flat(n, 0xFF808080u), w, h, 1);
        foreach (var x in v) Assert.True(x < 1e-6f, $"flat region variance not ~0: {x}");
    }

    [Fact]
    public void Noisy_Region_Has_Positive_Variance()
    {
        int w = 24, h = 24;
        var v = SvgfVariance.EstimateSpatial(NoisyGray(w, h, 128, 50, 3), w, h, 1);
        double mean = 0;
        foreach (var x in v) mean += x;
        mean /= v.Length;
        Assert.True(mean > 1e-3, $"noisy region variance too low: {mean}");
    }

    [Fact]
    public void FromMoments_Computes_Second_Minus_First_Squared()
    {
        // E[l]=0.5, E[l²]=0.3 → var = 0.3 − 0.25 = 0.05.
        var m1 = new float[] { 0.5f, 0.2f };
        var m2 = new float[] { 0.30f, 0.04f };   // second exactly m1² → var 0 for pixel 1
        var v = SvgfVariance.FromMoments(m1, m2, 2, 1);
        Assert.Equal(0.05f, v[0], 5);
        Assert.Equal(0.0f, v[1], 5);
    }

    [Fact]
    public void Estimator_Is_Deterministic()
    {
        int w = 20, h = 20;
        var img = NoisyGray(w, h, 100, 30, 9);
        Assert.Equal(SvgfVariance.EstimateSpatial(img, w, h, 2), SvgfVariance.EstimateSpatial(img, w, h, 2));
    }

    // ── variance-guided À-Trous ──────────────────────────────────────────────

    [Fact]
    public void Null_Variance_Is_Identical_To_Plain_Denoise()
    {
        int w = 32, h = 32;
        var img = NoisyGray(w, h, 128, 30, 11);
        var p = new AtrousParams { Iterations = 3, ColorSigma = 0.05 };
        var plain = AtrousDenoiser.Denoise(img, w, h, p);
        var nullVar = AtrousDenoiser.Denoise(img, w, h, p, null, null, null, 4.0);
        Assert.Equal(plain, nullVar);
    }

    [Fact]
    public void Scale_Zero_Is_Identical_To_Plain_Denoise()
    {
        int w = 32, h = 32, n = w * h;
        var img = NoisyGray(w, h, 128, 30, 13);
        var p = new AtrousParams { Iterations = 3, ColorSigma = 0.05 };
        var variance = SvgfVariance.EstimateSpatial(img, w, h, 1);
        var plain = AtrousDenoiser.Denoise(img, w, h, p);
        var scaled0 = AtrousDenoiser.Denoise(img, w, h, p, null, null, variance, 0.0);
        Assert.Equal(plain, scaled0);
    }

    [Fact]
    public void High_Variance_Smooths_More_Than_Plain()
    {
        int w = 48, h = 48, n = w * h;
        var img = NoisyGray(w, h, 128, 30, 17);
        // A tight colour sigma → the plain filter preserves most of the noise.
        var p = new AtrousParams { Iterations = 4, ColorSigma = 0.01 };
        var plain = AtrousDenoiser.Denoise(img, w, h, p);

        // A high, uniform variance estimate + a strong scale loosens the colour
        // edge-stop everywhere → the same passes now average the noise down.
        var variance = new float[n];
        for (int i = 0; i < n; i++) variance[i] = 0.25f;
        var guided = AtrousDenoiser.Denoise(img, w, h, p, null, null, variance, 8.0);

        double plainSd = StdDevR(plain), guidedSd = StdDevR(guided);
        Assert.True(guidedSd < plainSd * 0.7,
            $"variance guiding did not smooth more (plain {plainSd:F1}, guided {guidedSd:F1})");
    }
}
