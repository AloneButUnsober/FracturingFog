// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1868 (epic #1428, C7b) — DeepMisiurewiczFinder: Snap to spiral past octuple-double.
// The target is the Misiurewicz point M(4,1) near -0.1011 + 0.9563i (z₄ = −z₃: preperiod 4,
// period 1), refined here independently by Newton in BigFloat on z₃ + z₄ = 0 (S1851) to
// far more digits than any view below needs. Checked:
//   • A click a few pixels off it, at 1e150 (past OD) and 1e320 (past double), with the
//     snap's 10-pixel reach: M(4,1) is found, at the independent point to 1e-20 of the
//     reach (far past OD's grain), with λ = 2·z₄ computed independently.
//   • Where OD holds (1e60) it agrees with MisiurewiczFinder: the same (k, p), the point
//     within 1e-20 of the reach, the same λ.
//   • Minimality: refining at the point as M(5,1) reports LowerPreperiod 4, as M(4,2)
//     LowerPeriod 1. A click 1000 reaches away finds nothing.

using System;
using System.Numerics;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1868DeepMisiurewiczFinderTests
{
    private const double Log2Of10 = 3.3219280948873622;

    // M(4,1) by Newton on g(c) = z₃(c) + z₄(c) (z₄ − z₃ also vanishes at c = 0).
    private static (BigFloat Re, BigFloat Im) M41(int prec)
    {
        var cr = BigFloat.Parse("-0.10109636384562", prec);
        var ci = BigFloat.Parse("0.95628651080914", prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int step = 0; step < 40; step++)
        {
            BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec), dr = BigFloat.Zero(prec), di = BigFloat.Zero(prec);
            BigFloat z3r = zr, z3i = zi, d3r = dr, d3i = di;
            for (int n = 1; n <= 4; n++)
            {
                var ndr = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, dr, prec), BigFloat.Mul(zi, di, prec), prec).ScaleB(1), one, prec);
                var ndi = BigFloat.Add(BigFloat.Mul(zr, di, prec), BigFloat.Mul(zi, dr, prec), prec).ScaleB(1);
                var nzr = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, zr, prec), BigFloat.Mul(zi, zi, prec), prec), cr, prec);
                var nzi = BigFloat.Add(BigFloat.Mul(zr, zi, prec).ScaleB(1), ci, prec);
                (zr, zi, dr, di) = (nzr, nzi, ndr, ndi);
                if (n == 3) { z3r = zr; z3i = zi; d3r = dr; d3i = di; }
            }
            var gr = BigFloat.Add(zr, z3r, prec); var gi = BigFloat.Add(zi, z3i, prec);
            var hr = BigFloat.Add(dr, d3r, prec); var hi = BigFloat.Add(di, d3i, prec);
            var den = BigFloat.Add(BigFloat.Mul(hr, hr, prec), BigFloat.Mul(hi, hi, prec), prec);
            cr = BigFloat.Sub(cr, BigFloat.Div(BigFloat.Add(BigFloat.Mul(gr, hr, prec), BigFloat.Mul(gi, hi, prec), prec), den, prec), prec);
            ci = BigFloat.Sub(ci, BigFloat.Div(BigFloat.Sub(BigFloat.Mul(gi, hr, prec), BigFloat.Mul(gr, hi, prec), prec), den, prec), prec);
        }
        return (cr, ci);
    }

    // λ = 2·z₄ at the point (the cycle is the fixed point z₄).
    private static Complex Lambda(BigFloat cr, BigFloat ci, int prec)
    {
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        for (int n = 1; n <= 4; n++)
            (zr, zi) = (BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, zr, prec), BigFloat.Mul(zi, zi, prec), prec), cr, prec),
                        BigFloat.Add(BigFloat.Mul(zr, zi, prec).ScaleB(1), ci, prec));
        return 2.0 * new Complex(zr.ToDouble(), zi.ToDouble());
    }

    private const int Prec = 1600;   // ~480 digits: past every view below + its headroom

    // The snap's reach (10 px) at log10 zoom zl on a 1024-pixel-wide view.
    private static double ReachLog10(double zl) => Math.Log10(10 * 3.5 / 1024) - zl;

    private static ExactCentre Off(BigFloat re, BigFloat im, double reachLog10, double px, double py)
        => new ExactCentre(re, im).TranslatePixels(px, py, Math.Pow(10, reachLog10 - 1), reachLog10 - 1, Prec);

    private static double ErrLog2(ExactCentre a, BigFloat re, BigFloat im)
        => new CFx(BigFloat.Sub(a.Re.WithPrecision(Prec), re, Prec).ToFloatExp(), BigFloat.Sub(a.Im.WithPrecision(Prec), im, Prec).ToFloatExp()).Log2Abs();

    [Theory]
    [InlineData(150)]   // past OD
    [InlineData(320)]   // past double
    public void A_Click_Near_M41_Snaps_To_It(double zl)
    {
        var (mr, mi) = M41(Prec);
        double reach = ReachLog10(zl);
        var click = Off(mr, mi, reach, 3, -2);   // 3.6 px off, within the 10-px reach
        var m = DeepMisiurewiczFinder.FindNear(click, reach);
        Assert.True(m.Found, $"{m.Status} after {m.Attempts} attempts");
        Assert.Equal(4, m.Preperiod);
        Assert.Equal(1, m.Period);
        double err = ErrLog2(m.Point, mr, mi);
        Assert.True(err < reach * Log2Of10 - 66, $"point off by 2^{err:F1}, reach 2^{reach * Log2Of10:F1}");
        var lambda = Lambda(mr, mi, Prec);
        Assert.True(Complex.Abs(m.Multiplier - lambda) < 1e-12 * Complex.Abs(lambda), $"λ {m.Multiplier} vs {lambda}");
    }

    [Fact]
    public void It_Agrees_With_The_OD_Finder_Where_OD_Holds()
    {
        var (mr, mi) = M41(Prec);
        double reach = ReachLog10(60);
        var click = Off(mr, mi, reach, 3, -2);
        var deep = DeepMisiurewiczFinder.FindNear(click, reach);
        var od = MisiurewiczFinder.FindNear(click.ToDeep(), Math.Pow(10, reach));
        Assert.True(deep.Found && od.Found, $"{deep.Status} / {od.Status}");
        Assert.Equal((od.Preperiod, od.Period), (deep.Preperiod, deep.Period));
        double err = ErrLog2(deep.Point, BigFloat.FromOD(od.Point.Re, Prec), BigFloat.FromOD(od.Point.Im, Prec));
        Assert.True(err < reach * Log2Of10 - 66, $"deep vs OD 2^{err:F1}");
        Assert.True(Complex.Abs(deep.Multiplier - od.Multiplier) < 1e-12 * Complex.Abs(od.Multiplier));
    }

    [Fact]
    public void It_Refuses_Non_Minimal_Pairs_And_Far_Clicks()
    {
        var (mr, mi) = M41(Prec);
        double reach = ReachLog10(150);
        var at = new ExactCentre(mr, mi);

        var r51 = DeepMisiurewiczFinder.Refine(at, 5, 1, at, reach);
        Assert.Equal(MisiurewiczStatus.LowerPreperiod, r51.Status);
        Assert.Equal(4, r51.Preperiod);
        var r42 = DeepMisiurewiczFinder.Refine(at, 4, 2, at, reach);
        Assert.Equal(MisiurewiczStatus.LowerPeriod, r42.Status);
        Assert.Equal(1, r42.Period);

        // Converging on M(4,1) from an anchor 5 reaches away: within Newton's 8-reach
        // limit, outside the accept radius.
        var anchor5 = Off(mr, mi, reach, 50, 0);
        Assert.Equal(MisiurewiczStatus.NotFound, DeepMisiurewiczFinder.Refine(at, 4, 1, anchor5, reach).Status);
        Assert.True(DeepMisiurewiczFinder.Refine(at, 4, 1, Off(mr, mi, reach, 5, 0), reach).Found);

        var far = Off(mr, mi, reach, 10000, 0);   // 1000 reaches away
        Assert.False(DeepMisiurewiczFinder.FindNear(far, reach).Found);
    }

    // The period-n antenna minibrot next to −2 and its atom size (S1864 / S1866).
    private static (BigFloat C, BigFloat Size) Antenna(int n, int prec)
    {
        var c = BigFloat.FromDouble(-2, prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int step = 0; step < 40; step++)
        {
            var z = BigFloat.Zero(prec); var dz = BigFloat.Zero(prec);
            for (int k = 0; k < n; k++)
            {
                dz = BigFloat.Add(BigFloat.Mul(z, dz, prec).ScaleB(1), one, prec);
                z = BigFloat.Add(BigFloat.Mul(z, z, prec), c, prec);
            }
            c = BigFloat.Sub(c, BigFloat.Div(z, dz, prec), prec);
        }
        var zz = BigFloat.Zero(prec); var l = one; var b = one;
        for (int q = 1; q < n; q++)
        {
            zz = BigFloat.Add(BigFloat.Mul(zz, zz, prec), c, prec);
            l = BigFloat.Mul(zz, l, prec).ScaleB(1);
            b = BigFloat.Add(b, BigFloat.Div(one, l, prec), prec);
        }
        return (c, BigFloat.Div(one, BigFloat.Mul(b, BigFloat.Mul(l, l, prec), prec), prec));
    }

    [Fact]
    public void It_Snaps_To_The_Tip_Of_A_Deep_Minibrot()
    {
        // The period-125 antenna minibrot (~1e-149) framed like the home view: its tip, the
        // image of −2 under c = nucleus + Size·c′, is a Misiurewicz point with a long
        // preperiod. Click 1% of a size inside the tip, with the 10-px reach at that zoom.
        const int n = 125;
        var (c, size) = Antenna(n, Prec);
        double zl = -size.ToFloatExp().Log2Abs() / Log2Of10;
        double reach = ReachLog10(zl);
        var clickRe = BigFloat.Add(c, BigFloat.Mul(size, BigFloat.FromDouble(-1.995, Prec), Prec), Prec);
        var m = DeepMisiurewiczFinder.FindNear(new ExactCentre(clickRe, BigFloat.Zero(Prec)), reach);
        Assert.True(m.Found, $"{m.Status} after {m.Attempts} attempts");
        Assert.True(m.Preperiod + m.Period > n, $"M({m.Preperiod},{m.Period})");

        // Independently, in BigFloat at Prec: z_{k+p} = z_k there (the Newton step is far
        // under the reach), and no smaller preperiod or divisor period holds.
        int k = m.Preperiod, p = m.Period, len = k + p;
        var zr = new BigFloat[len + 1]; var zi = new BigFloat[len + 1];
        var dr = new BigFloat[len + 1]; var di = new BigFloat[len + 1];
        zr[0] = zi[0] = dr[0] = di[0] = BigFloat.Zero(Prec);
        var cr = m.Point.Re.WithPrecision(Prec); var ci = m.Point.Im.WithPrecision(Prec);
        var one = BigFloat.FromDouble(1, Prec);
        for (int j = 0; j < len; j++)
        {
            dr[j + 1] = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr[j], dr[j], Prec), BigFloat.Mul(zi[j], di[j], Prec), Prec).ScaleB(1), one, Prec);
            di[j + 1] = BigFloat.Add(BigFloat.Mul(zr[j], di[j], Prec), BigFloat.Mul(zi[j], dr[j], Prec), Prec).ScaleB(1);
            zr[j + 1] = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr[j], zr[j], Prec), BigFloat.Mul(zi[j], zi[j], Prec), Prec), cr, Prec);
            zi[j + 1] = BigFloat.Add(BigFloat.Mul(zr[j], zi[j], Prec).ScaleB(1), ci, Prec);
        }
        double Step(int a, int b2)
            => new CFx(BigFloat.Sub(zr[a + b2], zr[a], Prec).ToFloatExp(), BigFloat.Sub(zi[a + b2], zi[a], Prec).ToFloatExp()).Log2Abs()
             - new CFx(BigFloat.Sub(dr[a + b2], dr[a], Prec).ToFloatExp(), BigFloat.Sub(di[a + b2], di[a], Prec).ToFloatExp()).Log2Abs();
        double reachLog2 = reach * Log2Of10;
        Assert.True(Step(k, p) < reachLog2 - 66, $"(k, p) step 2^{Step(k, p):F1}");
        for (int i = 1; i < k; i++) Assert.True(Step(i, p) > reachLog2 - 30, $"preperiod {i} also holds");
        for (int q = 1; q < p; q++) if (p % q == 0) Assert.True(Step(k, q) > reachLog2 - 30, $"period {q} also holds");
        // Within reach of the click.
        double off = new CFx(BigFloat.Sub(cr, clickRe, Prec).ToFloatExp(), ci.ToFloatExp()).Log2Abs();
        Assert.True(off <= reachLog2, "outside the reach");
    }
}
