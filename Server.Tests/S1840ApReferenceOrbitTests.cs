// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1840 (epic #1428, C4) — the arbitrary-precision (BigFloat) reference orbit. Checked:
//   • The first steps are the exact orbit: Z_n computed here in exact dyadic arithmetic
//     (BigInteger, no rounding) matches the stored limbs to 2^-400 relative.
//   • Where OD is still good (1e110) the AP orbit is the OD orbit: equal to 1e-20 while the
//     orbit's own δ-amplification stays under 90 decades, same length, same detail depth.
//   • Past OD it tracks centre digits OD can't hold: two exact centres 1e-250 apart (same
//     OD limbs) give orbits whose difference is δc · dZ_n/dc, the derivative computed here
//     in double (linear perturbation theory) — so the cache keys on the exact centre too.
//   • Up to the threshold nothing changes (the OD tier); the exact centre is dropped when
//     the limbs move; a cancelled build throws and leaves no cache.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1840ApReferenceOrbitTests
{
    private static string Digits(int seed, int n) { var r = new Random(seed); return new(Enumerable.Range(0, n).Select(_ => (char)('0' + r.Next(10))).ToArray()); }

    private static MandelbrotCalculator Calc(ExactCentre? exact, DeepComplex limbs, double zoom, int maxIter)
    {
        var c = new MandelbrotCalculator(64, 64)
        {
            CenterX = limbs.Re.X0, CenterXLo = limbs.Re.X1, CenterX2 = limbs.Re.X2, CenterX3 = limbs.Re.X3,
            CenterX4 = limbs.Re.X4, CenterX5 = limbs.Re.X5, CenterX6 = limbs.Re.X6, CenterX7 = limbs.Re.X7,
            CenterY = limbs.Im.X0, CenterYLo = limbs.Im.X1, CenterY2 = limbs.Im.X2, CenterY3 = limbs.Im.X3,
            CenterY4 = limbs.Im.X4, CenterY5 = limbs.Im.X5, CenterY6 = limbs.Im.X6, CenterY7 = limbs.Im.X7,
            Zoom = zoom, MaxIterations = maxIter,
        };
        c.SetDepth(exact);
        return c;
    }

    // ── exact dyadic arithmetic (value = M · 2^E), no rounding anywhere ───────
    private readonly record struct Dy(BigInteger M, long E)
    {
        public static Dy Of(BigFloat b) => new(b.Mantissa, b.Exponent);
        public static Dy Of(double d) => Of(BigFloat.FromDouble(d, 64));   // 53 bits: exact
        public static Dy operator +(Dy a, Dy b)
        {
            long e = Math.Min(a.E, b.E);
            return new((a.M << (int)(a.E - e)) + (b.M << (int)(b.E - e)), e);
        }
        public static Dy operator -(Dy a) => new(-a.M, a.E);
        public static Dy operator *(Dy a, Dy b) => new(a.M * b.M, a.E + b.E);
        public static Dy OfOd(OD v) => Of(v.X0) + Of(v.X1) + Of(v.X2) + Of(v.X3) + Of(v.X4) + Of(v.X5) + Of(v.X6) + Of(v.X7);
    }

    // |a − b| ≤ 2^-bits · |b| (exactly, in integers).
    private static bool Close(Dy a, Dy b, int bits)
    {
        var d = a + -b;
        long e = Math.Min(d.E, b.E);
        var dm = BigInteger.Abs(d.M << (int)(d.E - e));
        var bm = BigInteger.Abs(b.M << (int)(b.E - e));
        return (dm << bits) <= bm;
    }

    [Fact]
    public void The_First_Steps_Are_The_Exact_Orbit()
    {
        int prec = BigFloat.PrecisionForZoom(300);
        Assert.True(ExactCentre.TryParse("-0.10109636384562" + Digits(1, 300), "0.95628651080914" + Digits(2, 300), out var c, prec));
        var calc = Calc(c, c.ToDeep(), 1e300, 50);
        calc.BuildReferenceOrbit();
        Assert.Equal(3, calc.ReferenceOrbitTier);

        Dy cr = Dy.Of(c.Re), ci = Dy.Of(c.Im), zr = new(0, 0), zi = new(0, 0);
        var two = new Dy(2, 0);
        for (int n = 0; n <= 6; n++)
        {
            var (sr, si) = calc.ReferenceOrbitPoint(n);
            if (n > 0)
            {
                Assert.True(Close(Dy.OfOd(sr), zr, 400), $"Re Z_{n}");
                Assert.True(Close(Dy.OfOd(si), zi, 400), $"Im Z_{n}");
            }
            var nr = zr * zr + -(zi * zi) + cr;
            zi = two * zr * zi + ci;
            zr = nr;
        }
    }

    [Fact]
    public void Where_OD_Is_Good_The_AP_Orbit_Is_The_OD_Orbit()
    {
        Assert.True(ExactCentre.TryParse("-0.10109636384562" + Digits(3, 120), "0.95628651080914" + Digits(4, 120), out var e));
        var limbs = e.ToDeep();
        var od = Calc(null, limbs, 1e110, 3000);
        var ap = Calc(null, limbs, 1e110, 3000);
        ap.ApReferenceZoomLog10 = 100;
        od.BuildReferenceOrbit();
        ap.BuildReferenceOrbit();
        Assert.Equal(2, od.ReferenceOrbitTier);   // the default threshold keeps OD at 1e110
        Assert.Equal(3, ap.ReferenceOrbitTier);
        Assert.Equal(od.ReferenceOrbitLength, ap.ReferenceOrbitLength);
        Assert.Equal(od.MaxUsefulZoomLog10, ap.MaxUsefulZoomLog10, 9);

        double amp = 0;   // decades of δ-amplification so far (the orbit's own Σ log10|2Z|)
        int checkedSteps = 0;
        for (int n = 0; n < od.ReferenceOrbitLength && amp < 90; n++)
        {
            var (ar, ai) = ap.ReferenceOrbitPoint(n);
            var (oR, oI) = od.ReferenceOrbitPoint(n);
            Assert.InRange((double)(ar - oR), -1e-20, 1e-20);
            Assert.InRange((double)(ai - oI), -1e-20, 1e-20);
            double m2 = oR.X0 * oR.X0 + oI.X0 * oI.X0;
            if (m2 > 0) amp += Math.Max(0, 0.5 * Math.Log10(4 * m2));
            checkedSteps++;
        }
        Assert.True(checkedSteps > 20, $"{checkedSteps}");
    }

    [Fact]
    public void Past_OD_The_Orbit_Follows_Centre_Digits_OD_Cannot_Hold()
    {
        int prec = BigFloat.PrecisionForZoom(300);
        // A real centre in (-2, 0.25): the orbit stays bounded, and is chaotic, so dZ/dc grows.
        Assert.True(BigFloat.TryParse("-1.9" + Digits(5, 320), prec, out var re));
        var c = new ExactCentre(re, BigFloat.Zero(prec));
        var dc = BigFloat.Parse("1e-250", prec);
        var c2 = new ExactCentre(BigFloat.Add(re, dc, prec), BigFloat.Zero(prec));
        Assert.True(ExactCentre.SameLimbs(c.ToDeep(), c2.ToDeep()));   // OD can't tell them apart

        const int N = 1000;
        var calc = Calc(c, c.ToDeep(), 1e300, N);
        calc.BuildReferenceOrbit();
        Assert.Equal(3, calc.ReferenceOrbitTier);
        Assert.Equal(N, calc.ReferenceOrbitLength);   // bounded
        var z1 = Enumerable.Range(0, N).Select(n => calc.ReferenceOrbitPoint(n).Re).ToArray();

        calc.CenterExact = c;   // the same exact centre: the cache holds
        calc.BuildReferenceOrbit();
        Assert.Equal(z1[N - 1], calc.ReferenceOrbitPoint(N - 1).Re);

        calc.CenterExact = c2;  // same limbs, different exact centre: a new orbit
        calc.BuildReferenceOrbit();
        // ΔZ_n = δc · D_n while it is small, D_{n+1} = 2·Z_n·D_n + 1 (dZ/dc, in double).
        double d = 0, dcD = 1e-250;
        int hits = 0;
        double maxD = 0;
        for (int n = 0; n < N; n++)
        {
            maxD = Math.Max(maxD, Math.Abs(d));
            double pred = dcD * d;
            if (Math.Abs(pred) is > 1e-40 and < 1e-20)
            {
                double got = (double)(calc.ReferenceOrbitPoint(n).Re - z1[n]);
                Assert.InRange(got / pred, 1 - 1e-6, 1 + 1e-6);
                hits++;
            }
            if (Math.Abs(d) > 1e300) break;
            d = 2 * z1[n].X0 * d + 1;
        }
        Assert.True(hits >= 10, $"{hits} steps in the linear range; max |dZ/dc| {maxD:E3}; z[1..5] {string.Join(",", z1.Skip(1).Take(5).Select(z => z.X0.ToString("G6")))}; z[995] {z1[995].X0:G6}");
    }

    [Fact]
    public void Up_To_The_Threshold_The_OD_Orbit_Is_Kept()
    {
        Assert.True(ExactCentre.TryParse("-0.10109636384562" + Digits(6, 120), "0.95628651080914" + Digits(7, 120), out var e));
        var calc = Calc(e, e.ToDeep(), 1e100, 500);
        calc.BuildReferenceOrbit();
        Assert.Equal(2, calc.ReferenceOrbitTier);
    }

    [Fact]
    public void The_Exact_Centre_Is_Dropped_When_The_Limbs_Move()
    {
        Assert.True(ExactCentre.TryParse("-0.10109636384562" + Digits(8, 300), "0.95628651080914" + Digits(9, 300), out var e));
        var calc = Calc(e, e.ToDeep(), 1e300, 50);
        Assert.Equal(e, calc.CenterExact);
        double x = calc.CenterX;
        calc.CenterX = x + 1e-12;   // a jitter / pan of the limbs
        Assert.Null(calc.CenterExact);
        calc.CenterX = x;           // restored
        Assert.Equal(e, calc.CenterExact);
    }

    [Fact]
    public void A_Cancelled_AP_Build_Throws_And_Leaves_No_Cache()
    {
        Assert.True(ExactCentre.TryParse("-1.9" + Digits(10, 320), "0", out var e, BigFloat.PrecisionForZoom(300)));
        var calc = Calc(e, e.ToDeep(), 1e300, 5000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => calc.BuildReferenceOrbit(cts.Token));
        Assert.Equal(-1, calc.ReferenceOrbitTier);
        Assert.Equal(5000, calc.BuildReferenceOrbit());
        Assert.Equal(3, calc.ReferenceOrbitTier);
    }
}
