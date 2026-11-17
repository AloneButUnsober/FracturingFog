// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1854 (epic #1428, C5f) — periodic-reference detection (#1614) past 1e154. Its tolerance,
// (pixel size)², underflows double there, so a nucleus-centred view never detected its
// period and the BigFloat orbit ran to maxIter. Checked, at the period-3 nucleus (found here
// by Newton in BigFloat on z_3(c) = 0):
//   • at 1e150 (double tolerance), 1e200 and 1e400 (FloatExp tolerance) the reference stops
//     at period 3 (length 4: Z_0 .. Z_3, then Z_4) instead of maxIter;
//   • the frame is the interior it is (every pixel at the cap), and the cached orbit is reused
//     for the next frame (PeriodicOrbitStillValid holds past 1e154 too);
//   • BigFloat.ToFloatExp is the value to 2^-52.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1854PeriodicReferenceDeepTests
{
    // The period-3 nucleus near −0.1226 + 0.7449i ("rabbit"): Newton on z_3(c) = 0.
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

    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(400)]
    public void A_Nucleus_Centred_View_Stops_The_Reference_At_Its_Period(int zl)
    {
        const int MaxIt = 5000;
        int prec = BigFloat.PrecisionForZoom(zl) + 64;
        var c = Nucleus3(prec);
        var d = c.ToDeep();
        var calc = new MandelbrotCalculator(16, 16)
        {
            MaxIterations = MaxIt, ColorMap = new HsvPalette(),
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.ApReferenceZoomLog10 = 100;
        calc.SetDepth(c);
        calc.Calculate(CancellationToken.None);
        Assert.Equal(3, calc.ReferencePeriod);
        Assert.Equal(4, calc.ReferenceOrbitLength);
        Assert.All(calc.IterationBuffer, n => Assert.Equal(MaxIt, n));   // the rabbit's interior

        // The next frame at the same view keeps the periodic orbit: no rebuild.
        int gen = calc.ReferenceOrbitGeneration;
        calc.Calculate(CancellationToken.None);
        Assert.Equal(gen, calc.ReferenceOrbitGeneration);
        Assert.Equal(4, calc.ReferenceOrbitLength);
        Assert.Equal(3, calc.ReferencePeriod);
    }

    // The tolerance is (pixel size)², not the pixel: 1000 pixels off the nucleus |Z_3| is
    // ~1000 pixels, and the attracting cycle's point near 0 stays there (|Z_{3k}| → |z*| ≈
    // |Z_3|), so no step is a period (a wrap would move pixels). A tolerance of one pixel
    // (|Z|² < px) would take period 3.
    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    public void The_Tolerance_Is_The_Squared_Pixel(int zl)
    {
        int prec = BigFloat.PrecisionForZoom(zl) + 64;
        var nuc = Nucleus3(prec);
        var px = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(16, prec), prec);
        var c = new ExactCentre(BigFloat.Add(nuc.Re, BigFloat.Mul(px, BigFloat.FromDouble(1000, prec), prec), prec), nuc.Im);
        var d = c.ToDeep();
        var calc = new MandelbrotCalculator(16, 16)
        {
            MaxIterations = 5000, ColorMap = new HsvPalette(),
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.ApReferenceZoomLog10 = 100;
        calc.SetDepth(c);
        calc.BuildReferenceOrbit();
        Assert.Equal(0, calc.ReferencePeriod);
    }

    [Fact]
    public void BigFloat_To_FloatExp_Is_The_Value()
    {
        var r = new Random(1854);
        for (int i = 0; i < 500; i++)
        {
            int p = 64 + r.Next(2000);
            var v = BigFloat.FromDouble(r.NextDouble() * 2 - 1, p).ScaleB(r.Next(-200000, 200000));
            v = BigFloat.Add(v, BigFloat.FromDouble(r.NextDouble(), p).ScaleB(-r.Next(60, 900)), p);   // bits past 53
            var f = v.ToFloatExp();
            var back = BigFloat.FromDouble(f.M, p).ScaleB(f.E);
            var err = BigFloat.Sub(back, v, p).Abs();
            Assert.True(v.IsZero || err.ScaleB(52) <= v.Abs(), $"{v} → {f}");
            Assert.True(f.IsZero || (Math.Abs(f.M) >= 1 && Math.Abs(f.M) < 2));
        }
        Assert.True(BigFloat.Zero(64).ToFloatExp().IsZero);
    }
}
