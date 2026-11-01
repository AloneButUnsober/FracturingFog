// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1613 — in the QD band (1e25..1e50) the PT4 / PT8 rows used to flag a pixel as
// glitched whenever Z + δ == Z in double (δ below Z's ulp), which at those depths is
// nearly every pixel, and send it to the rebased loop from iteration 0 without BLA.
// The QD-band rows now use the rebase test (|z| < |δ|) and hand a tripped pixel to
// ResumePixelPTRebased, which continues from its state with BLA looked up by the
// reference index, at a BLA tolerance of at most QdBandBlaTolerance. Checked against
// exact iteration (fixed-point BigInteger, no perturbation at all) on sampled pixels
// of two bench-kit scenes (https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit,
// as used in rith's FF 0.7.1 benchmark report on fractalforums.org; see the
// bibliography): a period-448 minibrot at 5.2e33 and a spiral path at 1e33.
// Mutants: QdBandBlaTolerance at 1e-6 (25x the frame disagreement) and a lookup one
// reference step off are caught; a lookup by iteration count instead of m is not —
// near these minibrots the reference is close to periodic and a pixel rebases about
// a whole period on, so both indices find nearly the same entries (also on the #1274
// 3E47 view, where exact iteration is chaotic and agrees with no double method).
//   • at the default BlaTolerance, the resumed path agrees with exact iteration as
//     well as plain rebasing (acceleration off) does on sampled pixels, and with plain
//     rebasing on all but 1 % of the frame;
//   • the precondition that most pixels really went through the rebased loop.

using System;
using System.Linq;
using System.Numerics;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes MandelbrotCalculator statics
public sealed class S1613RebaseResumeTests(ITestOutputHelper output)
{
    private const int W = 64, H = 36, Bits = 900;

    private sealed record Scene(double[] Cx, double[] Cy, double Zoom, int MaxIter);

    private static readonly Scene P448 = new(
        [
            -0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50,
            -1.0894092006534645e-66, -5.481175082318642e-83, -1.5340609125118276e-99, 1.4285946495395532e-116,
        ],
        [
            -0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50,
            -4.820603700098655e-67, -3.1189999875481587e-83, 1.2339668272603997e-99, -9.480108552256602e-117,
        ],
        5.176068e33, 13440);

    private static readonly Scene Path = new(
        [
            -0.028041054305504548, 1.3292199503012176e-18, -5.27591145479967e-35, 2.798737894555097e-51,
            -1.4491927624390092e-67, -6.233008271562717e-84, 4.7734689309385144e-101, 2.9022031733306904e-117,
        ],
        [
            -0.6948927538996524, 5.189489838305484e-17, 5.567870094609155e-34, 1.7818449158374648e-50,
            1.8079413574553354e-68, 2.5201987988615068e-85, -7.246511953022119e-102, -6.412330137037785e-118,
        ],
        1e33, 2859);

    private static MandelbrotCalculator Render(Scene s, bool noAccel)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            CenterX = s.Cx[0], CenterXLo = s.Cx[1], CenterX2 = s.Cx[2], CenterX3 = s.Cx[3],
            CenterX4 = s.Cx[4], CenterX5 = s.Cx[5], CenterX6 = s.Cx[6], CenterX7 = s.Cx[7],
            CenterY = s.Cy[0], CenterYLo = s.Cy[1], CenterY2 = s.Cy[2], CenterY3 = s.Cy[3],
            CenterY4 = s.Cy[4], CenterY5 = s.Cy[5], CenterY6 = s.Cy[6], CenterY7 = s.Cy[7],
            Zoom = s.Zoom, MaxIterations = s.MaxIter,
            Quality = QualityPreset.Extreme,
            ColorMap = new HsvPalette(),
            DisableAcceleration = noAccel,
        };
        c.Calculate();
        return c;
    }

    // A double as a fixed-point BigInteger with Bits fraction bits, exactly.
    private static BigInteger Fixed(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        int shift = exp - 1075 + Bits;   // value = mant · 2^(exp − 1075)
        BigInteger v = shift >= 0 ? new BigInteger(mant) << shift : new BigInteger(mant) >> -shift;
        return d < 0 ? -v : v;
    }

    // Escape count of c = centre + (dcR, dcI) by plain iteration, as the calculator
    // counts it (escape test |z|² ≥ 512² before the step, z_0 = 0).
    private static int Exact(Scene s, double dcR, double dcI)
    {
        BigInteger cr = s.Cx.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l)) + Fixed(dcR);
        BigInteger ci = s.Cy.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l)) + Fixed(dcI);
        BigInteger er2 = new BigInteger(512 * 512) << Bits;
        BigInteger zr = 0, zi = 0;
        int n = 0;
        for (; n < s.MaxIter; n++)
        {
            BigInteger zr2 = (zr * zr) >> Bits, zi2 = (zi * zi) >> Bits;
            if (zr2 + zi2 >= er2) break;
            zi = ((zr * zi) >> (Bits - 1)) + ci;
            zr = zr2 - zi2 + cr;
        }
        return n;
    }

    [Theory]
    [InlineData("p448 minibrot 5.2e33")]
    [InlineData("spiral path 1e33")]
    public void ResumedRebasing_MatchesExactIteration_AsWellAsPlainRebasing(string name)
    {
        var s = name.StartsWith("p448") ? P448 : Path;
        bool prevResume = MandelbrotCalculator.ResumeRebased, prevAllow = MandelbrotCalculator.AllowPtRebasing;
        MandelbrotCalculator.ResumeRebased = true;
        MandelbrotCalculator.AllowPtRebasing = true;
        try
        {
            var bla = Render(s, noAccel: false);
            var plain = Render(s, noAccel: true);

            // Precondition: the QD-band case — most pixels tripped the rebase test
            // and went through the rebased loop.
            Assert.True(bla.PtRebasedPixels > W * H / 2, $"only {bla.PtRebasedPixels} of {W * H} pixels rebased");

            double scale = (3.5 / Math.Max(W, H)) / s.Zoom;
            var rng = new Random(1613);
            int n = 40, blaOk = 0, plainOk = 0;
            for (int k = 0; k < n; k++)
            {
                int x = rng.Next(W), y = rng.Next(H);
                int exact = Exact(s, (x - W * 0.5) * scale, (y - H * 0.5) * scale);
                int i = y * W + x;
                if (bla.IterationBuffer[i] == exact) blaOk++;
                if (plain.IterationBuffer[i] == exact) plainOk++;
                output.WriteLine($"({x},{y}) exact {exact} bla {bla.IterationBuffer[i]} plain {plain.IterationBuffer[i]}");
            }
            int frameDiff = 0;
            for (int i = 0; i < W * H; i++) if (bla.IterationBuffer[i] != plain.IterationBuffer[i]) frameDiff++;
            output.WriteLine($"{name}: exact matches resumed+BLA {blaOk}/{n}, plain rebasing {plainOk}/{n}; " +
                             $"{frameDiff}/{W * H} pixels differ from plain rebasing");
            Assert.True(plainOk >= n - 2, $"plain rebasing reference only {plainOk}/{n} exact");
            Assert.True(blaOk >= plainOk - 1, $"resumed+BLA {blaOk}/{n} vs plain {plainOk}/{n}");
            // The whole frame against the sampled-exact reference: at QdBandBlaTolerance
            // a few tenths of a percent differ; at the default 1e-6, a few percent.
            Assert.True(frameDiff <= W * H / 100, $"{frameDiff}/{W * H} pixels differ from plain rebasing");
        }
        finally
        {
            MandelbrotCalculator.ResumeRebased = prevResume;
            MandelbrotCalculator.AllowPtRebasing = prevAllow;
        }
    }
}
