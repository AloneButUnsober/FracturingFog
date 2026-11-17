// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1856 / #1828 (epic #1428, C8a) — the reference-orbit arrays grow as the orbit is built
// instead of being sized to the iteration cap. Checked, in every CPU tier (DD 1e15, QD 1e30,
// OD 1e60, AP 1e150):
//   • A periodic orbit (the period-3 nucleus, Newton in BigFloat) under a 10,000,000 cap
//     stops at length 4 and keeps the initial 65,536 entries (it allocated 10 M + 1 per array).
//   • A bounded orbit of 300,000 steps at c = i, whose Z_n are exact in double (0, i, then
//     −1+i / −i alternating), is exact at every growth boundary (65,536, 131,072, 262,144).
//   • A short orbit after the long one trims the arrays back.
//   • A cancelled first build under a 10 M cap has allocated only the initial size.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1856OrbitGrowthTests
{
    private static ExactCentre Nucleus3(int prec)
    {
        var cr = BigFloat.Parse("-0.1225611668766536", prec);
        var ci = BigFloat.Parse("0.7448617666197446", prec);
        for (int step = 0; step < 30; step++)
        {
            BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec), dr = BigFloat.Zero(prec), di = BigFloat.Zero(prec);
            for (int n = 1; n <= 3; n++)
            {
                var ndr = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, dr, prec), BigFloat.Mul(zi, di, prec), prec).ScaleB(1), BigFloat.FromDouble(1, prec), prec);
                var ndi = BigFloat.Add(BigFloat.Mul(zr, di, prec), BigFloat.Mul(zi, dr, prec), prec).ScaleB(1);
                var nzr = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, zr, prec), BigFloat.Mul(zi, zi, prec), prec), cr, prec);
                var nzi = BigFloat.Add(BigFloat.Mul(zr, zi, prec).ScaleB(1), ci, prec);
                zr = nzr; zi = nzi; dr = ndr; di = ndi;
            }
            var den = BigFloat.Add(BigFloat.Mul(dr, dr, prec), BigFloat.Mul(di, di, prec), prec);
            var qr = BigFloat.Div(BigFloat.Add(BigFloat.Mul(zr, dr, prec), BigFloat.Mul(zi, di, prec), prec), den, prec);
            var qi = BigFloat.Div(BigFloat.Sub(BigFloat.Mul(zi, dr, prec), BigFloat.Mul(zr, di, prec), prec), den, prec);
            cr = BigFloat.Sub(cr, qr, prec); ci = BigFloat.Sub(ci, qi, prec);
        }
        return new ExactCentre(cr, ci);
    }

    private static MandelbrotCalculator Calc(ExactCentre c, int zl, int maxIter)
    {
        var d = c.ToDeep();
        var calc = new MandelbrotCalculator(16, 16)
        {
            MaxIterations = maxIter, ColorMap = new HsvPalette(), Zoom = Math.Pow(10, zl),
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.ApReferenceZoomLog10 = 100;
        calc.SetDepth(c);
        return calc;
    }

    private static int TierFor(int zl) => zl switch { <= 25 => 0, <= 50 => 1, <= 100 => 2, _ => 3 };

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(150)]
    public void A_Periodic_Orbit_Under_A_Huge_Cap_Keeps_The_Initial_Size(int zl)
    {
        var calc = Calc(Nucleus3(BigFloat.PrecisionForZoom(zl) + 64), zl, 10_000_000);
        Assert.Equal(4, calc.BuildReferenceOrbit());
        Assert.Equal(TierFor(zl), calc.ReferenceOrbitTier);
        Assert.Equal(1 << 16, calc.ReferenceOrbitCapacity);   // was 10,000,001
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(150)]
    public void A_Long_Orbit_Grows_Exactly_And_A_Short_One_After_It_Trims(int zl)
    {
        const int N = 300_000;
        int prec = BigFloat.PrecisionForZoom(zl) + 64;
        var calc = Calc(new ExactCentre(BigFloat.Zero(prec), BigFloat.FromDouble(1, prec)), zl, N);   // c = i
        Assert.Equal(N, calc.BuildReferenceOrbit());   // bounded: runs to the cap
        Assert.Equal(TierFor(zl), calc.ReferenceOrbitTier);
        Assert.Equal(N + 1, calc.ReferenceOrbitCapacity);
        foreach (int n in new[] { 0, 1, 2, 3, 65_535, 65_536, 65_537, 131_071, 131_072, 131_073, 262_143, 262_144, 262_145, N - 1, N })
        {
            var (re, im) = calc.ReferenceOrbitPoint(n);
            (double wr, double wi) = n == 0 ? (0.0, 0.0) : n == 1 ? (0.0, 1.0) : n % 2 == 0 ? (-1.0, 1.0) : (0.0, -1.0);
            Assert.Equal(wr, (double)re); Assert.Equal(wi, (double)im);
            Assert.Equal(0.0, re.X1); Assert.Equal(0.0, im.X1);   // the lower limbs too
        }

        // A short (periodic) orbit at the same calculator: the arrays go back down.
        var nuc = Nucleus3(prec);
        var d = nuc.ToDeep();
        calc.CenterX = d.Re.X0; calc.CenterXLo = d.Re.X1; calc.CenterX2 = d.Re.X2; calc.CenterX3 = d.Re.X3;
        calc.CenterX4 = d.Re.X4; calc.CenterX5 = d.Re.X5; calc.CenterX6 = d.Re.X6; calc.CenterX7 = d.Re.X7;
        calc.CenterY = d.Im.X0; calc.CenterYLo = d.Im.X1; calc.CenterY2 = d.Im.X2; calc.CenterY3 = d.Im.X3;
        calc.CenterY4 = d.Im.X4; calc.CenterY5 = d.Im.X5; calc.CenterY6 = d.Im.X6; calc.CenterY7 = d.Im.X7;
        calc.SetDepth(nuc);
        Assert.Equal(4, calc.BuildReferenceOrbit());
        Assert.Equal(1 << 16, calc.ReferenceOrbitCapacity);
        // The trim kept the new orbit: Z_1 = c, Z_2 = c² + c (here in double).
        double cr = d.Re.X0, ci = d.Im.X0;
        Assert.Equal(cr, calc.ReferenceOrbitPoint(1).Re.X0);
        Assert.Equal(ci, calc.ReferenceOrbitPoint(1).Im.X0);
        Assert.Equal(cr * cr - ci * ci + cr, calc.ReferenceOrbitPoint(2).Re.X0, 12);
    }

    [Fact]
    public void A_Cancelled_First_Build_Allocates_Only_The_Initial_Size()
    {
        int prec = BigFloat.PrecisionForZoom(60) + 64;
        var calc = Calc(new ExactCentre(BigFloat.Zero(prec), BigFloat.FromDouble(1, prec)), 60, 10_000_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => calc.BuildReferenceOrbit(cts.Token));
        Assert.Equal(1 << 16, calc.ReferenceOrbitCapacity);
    }
}
