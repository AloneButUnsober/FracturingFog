// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 (3D-Rendering-Roadmap.md, parent #389) — the guided À-Trous
// denoiser. Contract: iterations 0 is the identity (default off), the filter is
// deterministic, it reduces noise on a flat region, it stops at color edges (and
// harder at guided normal/depth edges), so detail survives while noise averages.

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AtrousDenoiserTests
{
    // Deterministic value noise around a mean (no RNG dependency across runs).
    private static uint[] NoisyGray(int w, int h, int mean, int amp, int seed)
    {
        var buf = new uint[w * h];
        uint s = (uint)seed | 1u;
        for (int i = 0; i < buf.Length; i++)
        {
            s = s * 1664525u + 1013904223u;              // LCG
            int noise = (int)((s >> 8) % (uint)(2 * amp + 1)) - amp;
            int v = Math.Clamp(mean + noise, 0, 255);
            buf[i] = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v;
        }
        return buf;
    }

    private static double StdDevLum(uint[] buf)
    {
        double m = 0;
        foreach (var c in buf) m += (c >> 16) & 0xFF;
        m /= buf.Length;
        double v = 0;
        foreach (var c in buf) { double d = ((c >> 16) & 0xFF) - m; v += d * d; }
        return Math.Sqrt(v / buf.Length);
    }

    // Iterations 0 is the identity — the default render is unchanged.
    [Fact]
    public void ZeroIterations_Is_Identity()
    {
        var input = NoisyGray(16, 16, 128, 30, 3);
        var outp = AtrousDenoiser.Denoise(input, 16, 16, new AtrousParams { Iterations = 0 });
        Assert.Equal(input, outp);
    }

    // Deterministic — same input, same output.
    [Fact]
    public void Is_Deterministic()
    {
        var input = NoisyGray(32, 32, 100, 25, 7);
        var p = new AtrousParams { Iterations = 3, ColorSigma = 0.4 };
        var a = AtrousDenoiser.Denoise(input, 32, 32, p);
        var b = AtrousDenoiser.Denoise(input, 32, 32, p);
        Assert.Equal(a, b);
    }

    // A flat noisy region loses variance — noise averages toward the mean.
    [Fact]
    public void Flat_Noise_Is_Smoothed()
    {
        int w = 48, h = 48;
        var input = NoisyGray(w, h, 128, 30, 11);
        var outp = AtrousDenoiser.Denoise(input, w, h, new AtrousParams { Iterations = 4, ColorSigma = 1.0 });
        double before = StdDevLum(input), after = StdDevLum(outp);
        Assert.True(after < before * 0.6, $"denoise did not smooth (before {before:F1}, after {after:F1})");
    }

    // A hard color edge: a small color sigma preserves it (the columns straddling
    // the black/white seam stay near the extremes); a large sigma blurs it (they
    // migrate toward mid-gray).
    [Fact]
    public void Color_Edge_Preserved_When_Sharp_Blurred_When_Loose()
    {
        int w = 32, h = 16, mid = w / 2;
        var input = new uint[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            input[y * w + x] = x < mid ? 0xFF000000u : 0xFFFFFFFFu;

        var sharp = AtrousDenoiser.Denoise(input, w, h, new AtrousParams { Iterations = 3, ColorSigma = 0.005 });
        var loose = AtrousDenoiser.Denoise(input, w, h, new AtrousParams { Iterations = 3, ColorSigma = 100.0 });

        int leftCol = mid - 1, rightCol = mid;   // columns straddling the seam
        int sL = (int)((sharp[8 * w + leftCol] >> 16) & 0xFF);
        int sR = (int)((sharp[8 * w + rightCol] >> 16) & 0xFF);
        int lL = (int)((loose[8 * w + leftCol] >> 16) & 0xFF);
        int lR = (int)((loose[8 * w + rightCol] >> 16) & 0xFF);

        Assert.True(sL < 40 && sR > 215, $"sharp edge not preserved (L={sL}, R={sR})");
        Assert.True(Math.Abs(lL - 128) < 90 && Math.Abs(lR - 128) < 90, $"loose edge not blurred (L={lL}, R={lR})");
    }

    // A normal-guide discontinuity stops the filter even when the color sigma is
    // wide: with a loose color sigma the edge blurs, but adding a normal guide
    // that flips at the same seam keeps it sharp.
    [Fact]
    public void Normal_Guide_Preserves_Edge_Under_Loose_Color()
    {
        int w = 32, h = 16, mid = w / 2;
        var input = new uint[w * h];
        var normal = new float[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            input[i] = x < mid ? 0xFF303030u : 0xFFD0D0D0u;
            // Left faces +Z, right faces +X — a hard normal discontinuity at mid.
            if (x < mid) { normal[i * 3] = 0; normal[i * 3 + 1] = 0; normal[i * 3 + 2] = 1; }
            else { normal[i * 3] = 1; normal[i * 3 + 1] = 0; normal[i * 3 + 2] = 0; }
        }

        var noGuide = AtrousDenoiser.Denoise(input, w, h, new AtrousParams { Iterations = 3, ColorSigma = 100.0 });
        var guided = AtrousDenoiser.Denoise(input, w, h, new AtrousParams { Iterations = 3, ColorSigma = 100.0, NormalSigma = 0.02 }, normal);

        int row = 8 * w;
        int contrastNo = (int)((noGuide[row + mid] >> 16) & 0xFF) - (int)((noGuide[row + mid - 1] >> 16) & 0xFF);
        int contrastGuided = (int)((guided[row + mid] >> 16) & 0xFF) - (int)((guided[row + mid - 1] >> 16) & 0xFF);
        Assert.True(contrastGuided > contrastNo + 30,
            $"normal guide did not preserve the edge (no-guide {contrastNo}, guided {contrastGuided})");
    }
}
