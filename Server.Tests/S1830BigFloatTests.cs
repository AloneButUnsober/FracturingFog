// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1830 (epic #1428, C1) — BigFloat, the arbitrary-precision float. Every check is
// against an independent reference, not against BigFloat itself:
//   • Rounding: each operation's result equals the exact rational result (BigInteger
//     numerator / denominator) rounded to P bits by a separate rational
//     round-to-nearest-even here (find k with 2^(P-1) ≤ |x|/2^k < 2^P, then divide).
//   • Doubles round-trip exactly (normal and subnormal), and ToDouble is that rational
//     rounding at 53 bits.
//   • OD: the limb sum is exact, and the greedy 8-limb split of a 400-bit value
//     reconstructs it exactly.
//   • Decimal: Parse equals the rational rounding of the decimal's exact value; ToString
//     at the round-trip digit count parses back to the same value; for doubles it
//     agrees with .NET's shortest round-trip "R" text.
//   • A Mandelbrot orbit at P = 256 bits stays within ~2^-250 of a fixed-point
//     BigInteger orbit at 1,024 bits, at an attracting parameter (errors don't grow).

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1830BigFloatTests(ITestOutputHelper output)
{
    // ── The independent reference: exact rationals, rounded here ─────────────────

    private readonly record struct Q(BigInteger Num, BigInteger Den)   // Den > 0
    {
        public static Q Of(BigFloat x) => x.Exponent >= 0
            ? new(x.Mantissa << (int)x.Exponent, 1)
            : new(x.Mantissa, BigInteger.One << (int)-x.Exponent);
        public static Q operator +(Q a, Q b) => new(a.Num * b.Den + b.Num * a.Den, a.Den * b.Den);
        public static Q operator -(Q a, Q b) => new(a.Num * b.Den - b.Num * a.Den, a.Den * b.Den);
        public static Q operator *(Q a, Q b) => new(a.Num * b.Num, a.Den * b.Den);
        public static Q operator /(Q a, Q b) => b.Num.Sign < 0 ? new(-a.Num * b.Den, a.Den * -b.Num) : new(a.Num * b.Den, a.Den * b.Num);
    }

    // |x| rounded to prec bits, ties to even, as (signed mantissa, exponent) with trailing
    // zeros stripped — BigFloat's canonical fields.
    private static (BigInteger M, long E) RoundRef(Q x, int prec)
    {
        if (x.Num.IsZero) return (0, 0);
        int sign = x.Num.Sign;
        BigInteger n = BigInteger.Abs(x.Num), d = x.Den;
        // k = floor(log2 |x|) - (prec - 1), by bit lengths then a correction.
        long k = (long)n.GetBitLength() - (long)d.GetBitLength() - (prec - 1);
        for (int it = 0; it < 4; it++)
        {
            // scaled = |x| / 2^k
            BigInteger sn = n, sd = d;
            if (k >= 0) sd <<= (int)k; else sn <<= (int)-k;
            BigInteger q = BigInteger.DivRem(sn, sd, out BigInteger r);
            long bits = (long)q.GetBitLength();
            if (bits < prec) { k--; continue; }
            if (bits > prec) { k++; continue; }
            int c = (r * 2).CompareTo(sd);
            if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
            long e = k;
            while (!q.IsZero && q.IsEven) { q >>= 1; e++; }
            return (sign < 0 ? -q : q, e);
        }
        throw new InvalidOperationException("reference rounding did not settle");
    }

    private static void AssertIs((BigInteger M, long E) want, BigFloat got, string what)
    {
        Assert.True(want.M == got.Mantissa && (want.M.IsZero || want.E == got.Exponent),
            $"{what}: want {want.M}·2^{want.E}, got {got.Mantissa}·2^{got.Exponent}");
    }

    private static BigFloat RandomBig(Random r, int bits, int prec, int expSpread)
    {
        var bytes = new byte[(bits + 7) / 8 + 1];
        r.NextBytes(bytes);
        bytes[^1] = 0;   // non-negative
        var m = new BigInteger(bytes);
        if (m.IsZero) m = 1;
        if (r.Next(2) == 0) m = -m;
        // Build via exact text-free route: m · 2^e as m (exact at a wide precision) scaled.
        var x = BigFloat.FromDouble(0, prec);
        // Compose from the integer's 52-bit chunks exactly, at a precision wide enough to hold m.
        int wide = (int)BigInteger.Abs(m).GetBitLength() + 8;
        BigFloat acc = BigFloat.Zero(Math.Max(wide, BigFloat.MinPrecision));
        var am = BigInteger.Abs(m);
        int shift = 0;
        while (!am.IsZero)
        {
            long chunk = (long)(am & ((BigInteger.One << 52) - 1));
            if (chunk != 0) acc = BigFloat.Add(acc, BigFloat.FromDouble(chunk, wide).ScaleB(shift), wide);
            am >>= 52; shift += 52;
        }
        if (m.Sign < 0) acc = -acc;
        return acc.ScaleB(r.Next(-expSpread, expSpread)).WithPrecision(prec);
    }

    public static TheoryData<int> Precisions => new() { 64, 113, 256, 424, 1000 };

    [Theory]
    [MemberData(nameof(Precisions))]
    public void Add_Sub_Mul_Div_Are_The_Exact_Result_Correctly_Rounded(int prec)
    {
        var r = new Random(1830 + prec);
        for (int t = 0; t < 400; t++)
        {
            // Operand widths and exponent gaps that hit both the aligned and the far-apart add.
            var a = RandomBig(r, r.Next(1, prec + 1), prec, t % 4 == 0 ? 3 * prec : 40);
            var b = RandomBig(r, r.Next(1, prec + 1), prec, t % 4 == 0 ? 3 * prec : 40);
            int p = r.Next(2) == 0 ? prec : BigFloat.MinPrecision + r.Next(prec);
            Q qa = Q.Of(a), qb = Q.Of(b);
            AssertIs(RoundRef(qa + qb, p), BigFloat.Add(a, b, p), $"add #{t} p={p}");
            AssertIs(RoundRef(qa - qb, p), BigFloat.Sub(a, b, p), $"sub #{t} p={p}");
            AssertIs(RoundRef(qa * qb, p), BigFloat.Mul(a, b, p), $"mul #{t} p={p}");
            if (!b.IsZero) AssertIs(RoundRef(qa / qb, p), BigFloat.Div(a, b, p), $"div #{t} p={p}");
            // Compare agrees with the exact difference's sign.
            Assert.Equal((qa - qb).Num.Sign, Math.Sign(a.CompareTo(b)));
        }
    }

    // Ties: a + b exactly halfway between two P-bit values goes to the even one; one ulp's
    // worth of a far smaller operand breaks the tie the right way.
    [Fact]
    public void Ties_Go_To_Even_And_A_Far_Smaller_Operand_Breaks_Them()
    {
        const int p = 64;
        var one = BigFloat.FromDouble(1, p);
        var halfUlp = BigFloat.FromDouble(1, p).ScaleB(-p);   // 2^-64: half of 1's ulp at 64 bits
        Assert.Equal(one, one + halfUlp);                         // 1 is even → stays
        var oneUp = one + BigFloat.FromDouble(1, p).ScaleB(-(p - 1));   // 1 + ulp (odd last bit)
        AssertIs(RoundRef(Q.Of(oneUp) + Q.Of(halfUlp), p), oneUp + halfUlp, "tie to even upward");
        var tiny = BigFloat.FromDouble(1, p).ScaleB(-500);
        AssertIs(RoundRef(Q.Of(one) + Q.Of(halfUlp) + Q.Of(tiny), p), BigFloat.Add(BigFloat.Add(one, halfUlp, 200), tiny, p), "tie broken up");
        AssertIs(RoundRef(Q.Of(one) - Q.Of(tiny), p), BigFloat.Sub(one, tiny, p), "far subtract");
        AssertIs(RoundRef(Q.Of(one) + Q.Of(tiny), p), BigFloat.Add(one, tiny, p), "far add");
    }

    [Fact]
    public void Doubles_Round_Trip_And_ToDouble_Is_Correctly_Rounded()
    {
        var r = new Random(18301);
        double[] special = { 1, -1, 0.1, Math.PI, double.Epsilon, -double.Epsilon, 2.2250738585072014E-308, 1.7976931348623157E308, 5e-324 * 3 };
        foreach (double d in special.Concat(Enumerable.Range(0, 2000).Select(_ => BitConverter.Int64BitsToDouble(r.NextInt64()))).Where(double.IsFinite))
        {
            var x = BigFloat.FromDouble(d, 64);
            Assert.Equal(d, x.ToDouble());
        }
        for (int t = 0; t < 500; t++)
        {
            var x = RandomBig(r, 200, 200, 600);
            var (m, e) = RoundRef(Q.Of(x), 53);
            double want = Math.ScaleB((double)m, (int)e);
            Assert.Equal(want, x.ToDouble());
        }
    }

    [Fact]
    public void Od_Limbs_Sum_Exactly_And_A_400_Bit_Value_Splits_Back_Exactly()
    {
        var r = new Random(18302);
        for (int t = 0; t < 300; t++)
        {
            var x = RandomBig(r, 400, 400, 200);
            OD od = x.ToOD();
            Assert.Equal(x, BigFloat.FromOD(od, 400));
            // The limbs are a non-overlapping expansion: each below half an ulp of the one before.
            double[] l = { od.X0, od.X1, od.X2, od.X3, od.X4, od.X5, od.X6, od.X7 };
            for (int i = 1; i < 8 && l[i] != 0; i++) Assert.True(Math.Abs(l[i]) <= Math.ScaleB(Math.Abs(l[i - 1]), -52));
            // FromOD is the exact limb sum (rational reference).
            Q sum = l.Where(v => v != 0).Select(v => Q.Of(BigFloat.FromDouble(v, 64))).Aggregate(new Q(0, 1), (a, b) => a + b);
            AssertIs(RoundRef(sum, 2000), BigFloat.FromOD(od, 2000), "limb sum");
        }
    }

    [Theory]
    [MemberData(nameof(Precisions))]
    public void Decimal_Text_Round_Trips_And_Parse_Is_Correctly_Rounded(int prec)
    {
        var r = new Random(18303 + prec);
        for (int t = 0; t < 200; t++)
        {
            var x = RandomBig(r, prec, prec, 4 * prec);
            string s = x.ToString();
            Assert.Equal(x, BigFloat.Parse(s, prec));
        }
        foreach (string s in new[] { "0.1", "-0.028041054305504548", "1e-300", "6.02214076e23", "-1.9918151296901943e0", "123456789012345678901234567890.000000001" })
        {
            // Exact value of the decimal as a rational: digits / 10^frac · 10^exp.
            var parts = s.TrimStart('-').Split('e');
            string mant = parts[0]; int exp = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
            int dot = mant.IndexOf('.');
            string digits = mant.Replace(".", "");
            int frac = dot < 0 ? 0 : mant.Length - dot - 1;
            BigInteger n = BigInteger.Parse(digits, CultureInfo.InvariantCulture) * (s.StartsWith('-') ? -1 : 1);
            int k = exp - frac;
            Q q = k >= 0 ? new Q(n * BigInteger.Pow(10, k), 1) : new Q(n, BigInteger.Pow(10, -k));
            AssertIs(RoundRef(q, prec), BigFloat.Parse(s, prec), $"parse {s}");
        }
    }

    [Fact]
    public void Formatting_A_Double_Agrees_With_Dotnets_Round_Trip_Text()
    {
        var r = new Random(18304);
        for (int t = 0; t < 1000; t++)
        {
            double d = (r.NextDouble() - 0.5) * Math.Pow(10, r.Next(-300, 300));
            if (d == 0) continue;
            string ours = BigFloat.FromDouble(d, 64).ToString(17);
            Assert.Equal(d, double.Parse(ours, CultureInfo.InvariantCulture));
            // Same digits and decimal exponent as .NET's 17-digit scientific text.
            string net = d.ToString("E16", CultureInfo.InvariantCulture);
            var (nm, ne) = (net.Split('E')[0], int.Parse(net.Split('E')[1], CultureInfo.InvariantCulture));
            var (om, oe) = (ours.Split('e')[0], int.Parse(ours.Split('e')[1], CultureInfo.InvariantCulture));
            Assert.Equal(nm, om);
            Assert.Equal(ne, oe);
        }
    }

    // ── A Mandelbrot orbit against fixed-point BigInteger ────────────────────────

    [Fact]
    public void A_256_Bit_Orbit_Tracks_A_1024_Bit_Fixed_Point_Orbit()
    {
        const int p = 256, fixBits = 1024;
        // An attracting parameter (the period-3 bulb, c ≈ -0.1225 + 0.7448i): rounding
        // errors are damped, so the gap stays near one P-bit rounding.
        var cr = BigFloat.Parse("-0.1225611668766536", p);
        var ci = BigFloat.Parse("0.7448617666197442", p);
        BigInteger Fix(BigFloat v) => v.Exponent + fixBits >= 0 ? v.Mantissa << (int)(v.Exponent + fixBits) : v.Mantissa >> (int)-(v.Exponent + fixBits);
        BigInteger fcr = Fix(cr), fci = Fix(ci), fzr = 0, fzi = 0;
        BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
        double worst = 0;
        for (int n = 0; n < 2000; n++)
        {
            var nzi = BigFloat.Mul(zr, zi, p).ScaleB(1) + ci;
            zr = BigFloat.Sub(zr.Square(p), zi.Square(p), p) + cr;
            zi = nzi;
            var nfzi = ((fzr * fzi) >> (fixBits - 1)) + fci;
            fzr = ((fzr * fzr - fzi * fzi) >> fixBits) + fcr;
            fzi = nfzi;
            // Gap in units of 2^-fixBits, as a power of two.
            BigInteger dr = BigInteger.Abs(Fix(zr) - fzr), di = BigInteger.Abs(Fix(zi) - fzi);
            double gap = Math.Max(dr.IsZero ? -1e9 : BigInteger.Log(dr, 2), di.IsZero ? -1e9 : BigInteger.Log(di, 2)) - fixBits;
            worst = Math.Max(worst == 0 ? -1e9 : worst, gap);
        }
        output.WriteLine($"worst |BigFloat - fixed| over 2,000 iterations: 2^{worst:F1}");
        Assert.True(worst < -(p - 8), $"orbit drifted to 2^{worst:F1}");
    }

    [Fact]
    public void Precision_For_Zoom_Covers_The_Pixel()
    {
        Assert.Equal(BigFloat.MinPrecision, BigFloat.PrecisionForZoom(0, 0));
        // 1e300: ~997 bits for the pixel, plus the guard.
        Assert.Equal(997 + 64, BigFloat.PrecisionForZoom(300));
        Assert.True(BigFloat.PrecisionForZoom(1000) >= 3322 + 64);
    }
}
