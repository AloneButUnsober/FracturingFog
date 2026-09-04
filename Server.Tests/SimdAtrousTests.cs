// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 (3D-Rendering-Roadmap.md, #389 / #402) — the opt-in SIMD À-Trous
// path. It vectorizes the per-pass gather over 8 pixels with a fast poly-exp, so it
// is NOT byte-identical to the scalar oracle (float32 + reordered accumulation): the
// contract is that it stays CLOSE (within a small tolerance) and is deterministic,
// while UseSimd off leaves the scalar path exactly as before (the existing
// AtrousDenoiserTests are the byte-identity guard).

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SimdAtrousTests
{
    private static uint[] NoisyRgb(int w, int h, int seed)
    {
        var buf = new uint[w * h];
        uint s = (uint)seed | 1u;
        byte Ch() { s = s * 1664525u + 1013904223u; return (byte)((s >> 9) & 0xFF); }
        for (int i = 0; i < buf.Length; i++)
            buf[i] = 0xFF000000u | ((uint)Ch() << 16) | ((uint)Ch() << 8) | Ch();
        return buf;
    }

    private static (float[] normal, float[] depth, float[] variance) Guides(int w, int h)
    {
        int n = w * h;
        var normal = new float[n * 3];
        var depth = new float[n];
        var variance = new float[n];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            bool right = x >= w / 2;
            normal[i * 3] = right ? 1f : 0f;
            normal[i * 3 + 1] = 0f;
            normal[i * 3 + 2] = right ? 0f : 1f;
            depth[i] = right ? 2.0f : 1.0f;
            variance[i] = (x % 7 == 0) ? 0.2f : 0.01f;
        }
        return (normal, depth, variance);
    }

    private static (int max, double mean) Diff(uint[] a, uint[] b)
    {
        int max = 0; long acc = 0; long cnt = 0;
        for (int i = 0; i < a.Length; i++)
        {
            for (int sh = 0; sh <= 16; sh += 8)
            {
                int d = Math.Abs((int)((a[i] >> sh) & 0xFF) - (int)((b[i] >> sh) & 0xFF));
                if (d > max) max = d;
                acc += d; cnt++;
            }
        }
        return (max, (double)acc / cnt);
    }

    [Fact]
    public void Simd_Is_Close_To_Scalar_ColorOnly()
    {
        int w = 96, h = 64;
        var img = NoisyRgb(w, h, 5);
        var p = new AtrousParams { Iterations = 4, ColorSigma = 0.2 };
        var scalar = AtrousDenoiser.Denoise(img, w, h, p);
        p.UseSimd = true;
        var simd = AtrousDenoiser.Denoise(img, w, h, p);

        var (max, mean) = Diff(scalar, simd);
        Assert.True(max <= 4, $"SIMD color-only diverged from scalar too far (max {max})");
        Assert.True(mean < 0.4, $"SIMD color-only mean diff too high ({mean:F3})");
    }

    [Fact]
    public void Simd_Is_Close_To_Scalar_FullyGuided()
    {
        int w = 96, h = 64;
        var img = NoisyRgb(w, h, 9);
        var (normal, depth, variance) = Guides(w, h);
        var p = new AtrousParams { Iterations = 3, ColorSigma = 0.15, NormalSigma = 0.4, DepthSigma = 0.3 };

        var scalar = AtrousDenoiser.Denoise(img, w, h, p, normal, depth, variance, 4.0);
        p.UseSimd = true;
        var simd = AtrousDenoiser.Denoise(img, w, h, p, normal, depth, variance, 4.0);

        var (max, mean) = Diff(scalar, simd);
        Assert.True(max <= 5, $"SIMD guided diverged from scalar too far (max {max})");
        Assert.True(mean < 0.5, $"SIMD guided mean diff too high ({mean:F3})");
    }

    [Fact]
    public void Simd_Is_Deterministic()
    {
        int w = 80, h = 48;
        var img = NoisyRgb(w, h, 13);
        var (normal, depth, _) = Guides(w, h);
        var p = new AtrousParams { Iterations = 3, ColorSigma = 0.1, UseSimd = true };
        var a = AtrousDenoiser.Denoise(img, w, h, p, normal, depth);
        var b = AtrousDenoiser.Denoise(img, w, h, p, normal, depth);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Simd_Off_Matches_Plain_Scalar_Exactly()
    {
        int w = 40, h = 40;
        var img = NoisyRgb(w, h, 17);
        var plain = AtrousDenoiser.Denoise(img, w, h, new AtrousParams { Iterations = 3, ColorSigma = 0.2 });
        var off = AtrousDenoiser.Denoise(img, w, h, new AtrousParams { Iterations = 3, ColorSigma = 0.2, UseSimd = false });
        Assert.Equal(plain, off);   // UseSimd default/false is the untouched scalar path
    }
}
