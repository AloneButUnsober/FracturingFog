// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 (3D-Rendering-Roadmap.md, #389 / #402) — the SVGF UNITE:
// ReliefDenoisePass.ApplySvgf composes temporal accumulation + variance-guided
// À-Trous over the render's motion / normal / depth AOVs + a persistent SvgfHistory.
// Contract: the first frame seeds the history (plain spatial denoise); a second
// frame accumulates toward it and comes out smoother; the temporal toggle off
// defers to the plain denoise; denoise off is a no-op; MakeCapture adds the motion
// AOV only when temporal is on; deterministic.

using System;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefSvgfIntegrationTests
{
    private const int W = 32, H = 32, N = W * H;

    private static uint[] NoisyGray(int seed)
    {
        var buf = new uint[N];
        uint s = (uint)seed | 1u;
        for (int i = 0; i < N; i++)
        {
            s = s * 1664525u + 1013904223u;
            int noise = (int)((s >> 8) % 61u) - 30;
            int v = Math.Clamp(128 + noise, 0, 255);
            buf[i] = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v;
        }
        return buf;
    }

    // A flat-geometry AOV: every pixel faces +Z at depth 1, zero motion (static camera).
    private static HeightfieldRaymarch2D.ReliefAovBuffers FlatAov()
    {
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(W, H, false, true);
        for (int i = 0; i < N; i++)
        {
            aov.NormalXyz[i * 3] = 0f; aov.NormalXyz[i * 3 + 1] = 0f; aov.NormalXyz[i * 3 + 2] = 1f;
            aov.Depth[i] = 1f;
            aov.Motion![i * 2] = 0f; aov.Motion[i * 2 + 1] = 0f;
        }
        return aov;
    }

    private static FractalParameters Params(bool temporal, int iters = 3)
        => new()
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DDenoiseIterations = iters,
            Relief2DDenoiseColorSigma = 0.05,
            Relief2DDenoiseTemporal = temporal,
            Relief2DDenoiseTemporalFeedback = 0.8,
            Relief2DDenoiseVarianceScale = 4.0,
        };

    private static double StdDevR(uint[] buf)
    {
        double m = 0;
        foreach (var c in buf) m += (c >> 16) & 0xFF;
        m /= buf.Length;
        double v = 0;
        foreach (var c in buf) { double d = ((c >> 16) & 0xFF) - m; v += d * d; }
        return Math.Sqrt(v / buf.Length);
    }

    [Fact]
    public void First_Frame_Seeds_The_History()
    {
        var history = new SvgfHistory();
        var beauty = NoisyGray(3);
        ReliefDenoisePass.ApplySvgf(beauty, FlatAov(), W, H, Params(temporal: true), history);
        Assert.True(history.Valid);
        Assert.NotNull(history.Color);
        Assert.Equal(W, history.W);
        Assert.Equal(H, history.H);
    }

    [Fact]
    public void Second_Frame_Accumulates_Smoother_Than_The_First()
    {
        var history = new SvgfHistory();
        var p = Params(temporal: true);
        var aov = FlatAov();

        var frameA = NoisyGray(5);
        ReliefDenoisePass.ApplySvgf(frameA, aov, W, H, p, history);   // seeds history

        var frameB = NoisyGray(5);                                    // same static noise
        ReliefDenoisePass.ApplySvgf(frameB, aov, W, H, p, history);   // accumulates toward it

        Assert.True(StdDevR(frameB) < StdDevR(frameA) * 0.9,
            $"temporal accumulation did not smooth across frames ({StdDevR(frameA):F1} → {StdDevR(frameB):F1})");
    }

    [Fact]
    public void Temporal_Off_Matches_The_Plain_Denoise()
    {
        var aov = FlatAov();
        var p = Params(temporal: false);

        var viaSvgf = NoisyGray(7);
        ReliefDenoisePass.ApplySvgf(viaSvgf, aov, W, H, p, new SvgfHistory());

        var viaPlain = NoisyGray(7);
        ReliefDenoisePass.Apply(viaPlain, aov, W, H, p);

        Assert.Equal(viaPlain, viaSvgf);
    }

    [Fact]
    public void Denoise_Off_Is_A_NoOp()
    {
        var aov = FlatAov();
        var p = Params(temporal: true, iters: 0);   // Enabled() false
        var beauty = NoisyGray(9);
        var before = (uint[])beauty.Clone();
        ReliefDenoisePass.ApplySvgf(beauty, aov, W, H, p, new SvgfHistory());
        Assert.Equal(before, beauty);
    }

    [Fact]
    public void MakeCapture_Adds_Motion_Only_When_Temporal()
    {
        var plain = ReliefDenoisePass.MakeCapture(Params(temporal: false), W, H);
        var svgf = ReliefDenoisePass.MakeCapture(Params(temporal: true), W, H);
        Assert.NotNull(plain);
        Assert.Null(plain!.Motion);            // plain denoise: normal + depth only
        Assert.NotNull(svgf);
        Assert.NotNull(svgf!.Motion);          // SVGF: motion captured for reprojection
    }

    [Fact]
    public void Is_Deterministic()
    {
        var p = Params(temporal: true);
        uint[] Run()
        {
            var h = new SvgfHistory();
            var a = NoisyGray(11); ReliefDenoisePass.ApplySvgf(a, FlatAov(), W, H, p, h);
            var b = NoisyGray(11); ReliefDenoisePass.ApplySvgf(b, FlatAov(), W, H, p, h);
            return b;
        }
        Assert.Equal(Run(), Run());
    }
}
