// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1614 — a reference orbit centred on a period-p nucleus returns to |Z_p| below
// the pixel size and repeats, so the CPU builders stop at Z_{p+1} instead of
// running to the iteration cap, and the PT4 / PT8 rows wrap their reference index
// at p (δ += Z_p, the exact rebase at Z_p ≈ 0). Checked on the bench-kit
// period-953 minibrot at 1.3e53 (https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit,
// as used in rith's FF 0.7.1 benchmark report, a fractalforums.org direct message;
// see the bibliography):
//   • the period is found and the orbit is p + 1 long; with detection off it runs
//     to the cap;
//   • sampled pixels match exact iteration (fixed-point BigInteger, no perturbation)
//     at least as well as with the full-length orbit;
//   • the two frames agree on iteration counts and, where those agree, on the smooth
//     value to a tenth of a band (the escape z is rebuilt at m = iteration mod p);
//   • a view whose reference is not periodic renders byte-identically either way.
// Mutants: wrapping one step early is caught (8/40 exact, 1,782 / 2,304 pixels
// moved). Near-equivalent by construction, so not caught: dropping Z_p from the wrap
// (|Z_p| is below one pixel, far below δ), rebuilding the escape z at the iteration
// count instead of m (|z| ≥ 512 there, |Z| ≤ 2), and letting BLA skip past p (the
// table already stops at its own length, p + 1).

using System;
using System.Linq;
using System.Numerics;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes MandelbrotCalculator statics
public sealed class S1614PeriodicReferenceTests(ITestOutputHelper output)
{
    private const int W = 64, H = 36, Bits = 700;

    private sealed record Scene(double[] Cx, double[] Cy, double Zoom, int MaxIter);

    private static readonly Scene P953 = new(
        [
            -0.028041054305504548, 1.3292199503012176e-18, -5.275751227722853e-35, -4.203746728154813e-51,
            -4.269596053996866e-68, 1.9389949392385534e-84, 6.25819023760954e-101, -2.6646795788290983e-117,
        ],
        [
            -0.6948927538996524, 5.189489838305484e-17, 5.567887477024055e-34, 1.3771597857226995e-50,
            -5.482812918750436e-67, 4.692321383846679e-84, -2.313052339214723e-100, 2.1139231852108862e-116,
        ],
        1.342765e53, 28590);

    // The period-448 minibrot at 5.2e33: its reference is not detected as periodic.
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

    private static MandelbrotCalculator Render(Scene s, bool periodic)
    {
        bool saved = MandelbrotCalculator.PeriodicReferenceOrbit;
        MandelbrotCalculator.PeriodicReferenceOrbit = periodic;
        try
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
            };
            c.Calculate();
            return c;
        }
        finally { MandelbrotCalculator.PeriodicReferenceOrbit = saved; }
    }

    private static BigInteger Fixed(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        int shift = exp - 1075 + Bits;
        BigInteger v = shift >= 0 ? new BigInteger(mant) << shift : new BigInteger(mant) >> -shift;
        return d < 0 ? -v : v;
    }

    private static int Exact(Scene s, double dcR, double dcI)
    {
        BigInteger cr = s.Cx.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l)) + Fixed(dcR);
        BigInteger ci = s.Cy.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l)) + Fixed(dcI);
        BigInteger er2 = new BigInteger(512 * 512) << Bits, zr = 0, zi = 0;
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

    [Fact]
    public void A_Nucleus_Reference_Stops_At_The_Period_And_Matches_Exact_Iteration()
    {
        var on = Render(P953, periodic: true);
        var off = Render(P953, periodic: false);
        Assert.Equal(953, on.ReferencePeriod);
        Assert.Equal(954, on.ReferenceOrbitLength);
        Assert.Equal(0, off.ReferencePeriod);
        Assert.True(off.ReferenceOrbitLength >= P953.MaxIter - 1, $"full orbit {off.ReferenceOrbitLength}");

        double scale = (3.5 / Math.Max(W, H)) / P953.Zoom;
        var rng = new Random(1614);
        int n = 40, onOk = 0, offOk = 0;
        for (int k = 0; k < n; k++)
        {
            int x = rng.Next(W), y = rng.Next(H), i = y * W + x;
            int exact = Exact(P953, (x - W * 0.5) * scale, (y - H * 0.5) * scale);
            if (on.IterationBuffer[i] == exact) onOk++;
            if (off.IterationBuffer[i] == exact) offOk++;
        }
        output.WriteLine($"exact matches: periodic {onOk}/{n}, full orbit {offOk}/{n}");
        Assert.True(offOk >= n - 4, $"full-orbit reference only {offOk}/{n} exact");
        Assert.True(onOk >= offOk - 1, $"periodic {onOk}/{n} vs full orbit {offOk}/{n}");
    }

    [Fact]
    public void Periodic_And_Full_Orbit_Frames_Agree_On_Iterations_And_Smooth_Values()
    {
        var on = Render(P953, periodic: true);
        var off = Render(P953, periodic: false);
        int iterDiff = 0, agree = 0, smoothDiff = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (on.IterationBuffer[i] != off.IterationBuffer[i]) { iterDiff++; continue; }
            if (on.IterationBuffer[i] >= P953.MaxIter) continue;
            agree++;
            // SmoothBuffer is a float: near 10,000 one ulp is ~0.001. A wrong escape
            // index would put Z off by O(1) and the smooth value off by whole bands; the
            // BLA steps (which can't cross the period) only move it by rounding.
            if (Math.Abs(on.SmoothBuffer[i] - off.SmoothBuffer[i]) > 0.1) smoothDiff++;
        }
        output.WriteLine($"iteration diff {iterDiff}/{W * H}; smooth diff {smoothDiff}/{agree} escaped pixels that agree");
        Assert.True(agree > W * H / 4, $"precondition: {agree} escaped pixels agree");
        Assert.True(iterDiff <= W * H * 3 / 100, $"{iterDiff}/{W * H} iteration counts differ");
        Assert.True(smoothDiff <= agree / 100, $"{smoothDiff}/{agree} smooth values differ");
    }

    [Fact]
    public void A_Non_Periodic_Reference_Renders_Byte_Identically_With_Detection_On_Or_Off()
    {
        var on = Render(P448, periodic: true);
        var off = Render(P448, periodic: false);
        Assert.Equal(0, on.ReferencePeriod);   // precondition: nothing to truncate
        Assert.Equal(off.IterationBuffer, on.IterationBuffer);
        Assert.Equal(off.SmoothBuffer, on.SmoothBuffer);
        Assert.Equal(off.ColorBuffer, on.ColorBuffer);
    }
}
