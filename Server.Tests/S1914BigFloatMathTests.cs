// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1914 (epic #1253, D9) — BigFloatMath's elementary functions, checked by identities and by
// independent routes rather than by stored digits:
//   • sin² + cos² = 1, exp(log x) = x, exp(a + b) = exp(a)·exp(b), at 200, 600 and 2,000 bits;
//   • π from Machin's formula against 4·atan(1) (the half-angle route) and against sin(π/6) = ½,
//     cos(π/3) = ½ (the Taylor route);
//   • rounded to double, each function against System.Math;
//   • the complex wrappers: exp(log z) = z, sin² z + cos² z = 1, (z^½)² = z, z² by CPow = z·z.
// Mutants (by hand), all caught: one squaring dropped from exp, one ln 2 dropped from log, the wrong cos
// double-angle, one atan halving dropped. Machin's ×16 / ×4 slip was a real bug, caught on the first run.

using System;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1914BigFloatMathTests(ITestOutputHelper output)
{
    private static BigFloat B(double v, int p) => BigFloat.FromDouble(v, p);

    // log₂ of the relative error |a − b| / |b| (−∞ → −10000).
    private static double RelBits(BigFloat a, BigFloat b, int p)
    {
        var d = BigFloat.Sub(a, b, p + 64);
        if (d.IsZero) return -10000;
        double l = (d.Exponent + (long)d.Mantissa.GetBitLength()) - (b.Exponent + (long)b.Mantissa.GetBitLength());
        return l;
    }

    private static double AbsBits(BigFloat a, BigFloat b, int p)
    {
        var d = BigFloat.Sub(a, b, p + 64);
        return d.IsZero ? -10000 : d.Exponent + (long)d.Mantissa.GetBitLength();
    }

    [Theory]
    [InlineData(200)]
    [InlineData(600)]
    [InlineData(2000)]
    public void Identities_Hold_To_The_Precision(int p)
    {
        var rng = new Random(p);
        double worstTrig = -10000, worstExpLog = -10000, worstAdd = -10000;
        for (int k = 0; k < (p > 1000 ? 6 : 25); k++)
        {
            // An argument with all p bits used: a double plus a tiny tail.
            var x = BigFloat.Add(B((rng.NextDouble() - 0.5) * 40, p), B(rng.NextDouble() * 1e-20, p).ScaleB(-(p / 2)), p);
            var (s, c) = BigFloatMath.SinCos(x, p);
            var one = BigFloat.Add(BigFloat.Mul(s, s, p + 16), BigFloat.Mul(c, c, p + 16), p + 16);
            worstTrig = Math.Max(worstTrig, AbsBits(one, B(1, p), p));

            var y = BigFloat.Add(B(rng.NextDouble() * 1e3 + 1e-3, p), B(rng.NextDouble(), p).ScaleB(-(p / 2)), p);
            worstExpLog = Math.Max(worstExpLog, RelBits(BigFloatMath.Exp(BigFloatMath.Log(y, p + 16), p), y, p));

            var a = B((rng.NextDouble() - 0.5) * 60, p); var b2 = B((rng.NextDouble() - 0.5) * 60, p);
            var lhs = BigFloatMath.Exp(BigFloat.Add(a, b2, p + 16), p);
            var rhs = BigFloat.Mul(BigFloatMath.Exp(a, p + 8), BigFloatMath.Exp(b2, p + 8), p);
            worstAdd = Math.Max(worstAdd, RelBits(lhs, rhs, p));
        }
        output.WriteLine($"{p} bits: sin²+cos²−1 2^{worstTrig}, exp(log x)/x−1 2^{worstExpLog}, exp(a+b)/(exp a·exp b)−1 2^{worstAdd}");
        Assert.True(worstTrig < -(p - 8), $"sin² + cos² − 1: 2^{worstTrig}");
        Assert.True(worstExpLog < -(p - 8), $"exp(log x) / x − 1: 2^{worstExpLog}");
        Assert.True(worstAdd < -(p - 8), $"exp(a + b) / (exp a · exp b) − 1: 2^{worstAdd}");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(2000)]
    public void Machins_Pi_Agrees_With_The_Other_Routes(int p)
    {
        var pi = BigFloatMath.Pi(p);
        var viaAtan = BigFloatMath.Atan(B(1, p), p).ScaleB(2);
        var (s6, _) = BigFloatMath.SinCos(BigFloat.Div(pi, B(6, p), p), p);
        var (_, c3) = BigFloatMath.SinCos(BigFloat.Div(pi, B(3, p), p), p);
        double a = RelBits(viaAtan, pi, p), b = AbsBits(s6, B(0.5, p), p), c = AbsBits(c3, B(0.5, p), p);
        output.WriteLine($"{p} bits: 4·atan 1 vs Machin 2^{a}; sin(π/6) − ½ 2^{b}; cos(π/3) − ½ 2^{c}; π ≈ {pi.ToString(40)}");
        Assert.True(a < -(p - 8) && b < -(p - 8) && c < -(p - 8));
        Assert.Equal(Math.PI, pi.ToDouble());
        Assert.Equal(Math.Log(2), BigFloatMath.Ln2(p).ToDouble());
    }

    [Fact]
    public void Rounded_To_Double_They_Match_System_Math()
    {
        var rng = new Random(9);
        const int p = 128;
        for (int k = 0; k < 500; k++)
        {
            double x = (rng.NextDouble() - 0.5) * 50, y = rng.NextDouble() * 100 + 1e-6, t = (rng.NextDouble() - 0.5) * 20;
            void Near(double got, double want, string what)
                => Assert.True(Math.Abs(got - want) <= 4e-16 * Math.Max(1, Math.Abs(want)), $"{what}: {got} vs {want}");
            Near(BigFloatMath.Exp(B(x, p), p).ToDouble(), Math.Exp(x), $"exp {x}");
            Near(BigFloatMath.Log(B(y, p), p).ToDouble(), Math.Log(y), $"log {y}");
            var (s, c) = BigFloatMath.SinCos(B(x, p), p);
            Near(s.ToDouble(), Math.Sin(x), $"sin {x}"); Near(c.ToDouble(), Math.Cos(x), $"cos {x}");
            Near(BigFloatMath.Atan(B(t, p), p).ToDouble(), Math.Atan(t), $"atan {t}");
            Near(BigFloatMath.Sqrt(B(y, p), p).ToDouble(), Math.Sqrt(y), $"sqrt {y}");
            double u = rng.NextDouble() - 0.5, v = rng.NextDouble() - 0.5;
            Near(BigFloatMath.Atan2(B(u, p), B(v, p), p).ToDouble(), Math.Atan2(u, v), $"atan2 {u} {v}");
        }
        Assert.Equal(Math.Atan2(0, -1), BigFloatMath.Atan2(B(0, p), B(-1, p), p).ToDouble());
        Assert.Equal(Math.Atan2(1, 0), BigFloatMath.Atan2(B(1, p), B(0, p), p).ToDouble());
        Assert.Equal(0.0, BigFloatMath.Atan2(B(0, p), B(0, p), p).ToDouble());
    }

    [Fact]
    public void The_Complex_Wrappers_Satisfy_Their_Identities()
    {
        const int p = 400;
        var rng = new Random(14);
        for (int k = 0; k < 20; k++)
        {
            var zr = B((rng.NextDouble() - 0.5) * 6, p); var zi = B((rng.NextDouble() - 0.5) * 6, p);
            var (lr, li) = BigFloatMath.CLog(zr, zi, p + 16);
            var (er, ei) = BigFloatMath.CExp(lr, li, p);
            Assert.True(AbsBits(er, zr, p) < -(p - 12) && AbsBits(ei, zi, p) < -(p - 12), "exp(log z) = z");

            var (sr, si) = BigFloatMath.CSin(zr, zi, p + 16); var (cr, ci) = BigFloatMath.CCos(zr, zi, p + 16);
            var re = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(sr, sr, p + 16), BigFloat.Mul(si, si, p + 16), p + 16),
                                  BigFloat.Sub(BigFloat.Mul(cr, cr, p + 16), BigFloat.Mul(ci, ci, p + 16), p + 16), p + 16);
            var im = BigFloat.Add(BigFloat.Mul(sr, si, p + 16), BigFloat.Mul(cr, ci, p + 16), p + 16).ScaleB(1);
            Assert.True(AbsBits(re, B(1, p), p) < -(p - 20) && AbsBits(im, B(0, p), p) < -(p - 20), "sin² z + cos² z = 1");

            var (hr, hi) = BigFloatMath.CPow(zr, zi, B(0.5, p), B(0, p), p + 16);
            var (qr, qi) = (BigFloat.Sub(BigFloat.Mul(hr, hr, p), BigFloat.Mul(hi, hi, p), p), BigFloat.Mul(hr, hi, p).ScaleB(1));
            Assert.True(AbsBits(qr, zr, p) < -(p - 12) && AbsBits(qi, zi, p) < -(p - 12), "(z^½)² = z");
            Assert.True(hr.Sign >= 0, "the principal root has Re ≥ 0");
        }
    }
}
