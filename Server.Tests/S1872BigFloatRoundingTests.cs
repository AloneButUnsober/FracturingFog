// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1872 — BigFloat's rounding (Round, behind every operation) and ToDouble were rewritten
// to decide ties-to-even from the guard bit and trailing zeros instead of building the
// discarded field. The old algorithms are kept here verbatim as the reference; on random
// mantissas (forced exact ties, ties ± 1, carries to a power of two, exact cases) and
// exponents through double's subnormal and overflow range, the new results are the same
// fields / the same double bits.

using System;
using System.Numerics;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1872BigFloatRoundingTests
{
    // ── The old algorithms (verbatim), on (sign, mag, exp) ───────────────────

    private static (BigInteger Mantissa, long Exponent) OldRound(int sign, BigInteger mag, long exp, int prec)
    {
        if (mag.IsZero) return (BigInteger.Zero, 0);
        long bits = (long)mag.GetBitLength();
        if (bits > prec)
        {
            int shift = (int)(bits - prec);
            BigInteger q = mag >> shift;
            BigInteger rem = mag - (q << shift);
            BigInteger half = BigInteger.One << (shift - 1);
            int c = rem.CompareTo(half);
            bool up = c > 0 || (c == 0 && !q.IsEven);
            if (up) q += 1;
            mag = q; exp += shift;
        }
        int tz = mag.IsZero ? 0 : (int)BigInteger.TrailingZeroCount(mag);
        if (tz > 0) { mag >>= tz; exp += tz; }
        return (sign < 0 ? -mag : mag, exp);
    }

    private static double OldToDouble(BigInteger mantissa, long exponent)
    {
        if (mantissa.IsZero) return 0;
        var (rm, re) = OldRound(mantissa.Sign, BigInteger.Abs(mantissa), exponent, 53);
        double m = (double)rm;
        if (re > 2000) return m > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        if (re < -2200) return 0.0 * Math.Sign(m);
        return Math.ScaleB(m, (int)re);
    }

    // ── Building exact values ───────────────────────────────────────────────

    // mag · 2^exp exactly (sign ±), from 30-bit limbs (each exact as a double).
    private static BigFloat Make(int sign, BigInteger mag, long exp)
    {
        int bits = Math.Max(64, (int)mag.GetBitLength() + 8);
        var v = BigFloat.Zero(bits);
        int limb = 0;
        for (var m = mag; !m.IsZero; m >>= 30, limb++)
        {
            long part = (long)(m & ((1 << 30) - 1));
            if (part != 0) v = BigFloat.Add(v, BigFloat.FromDouble(part, bits).ScaleB(30L * limb), bits);
        }
        v = v.ScaleB(exp);
        return sign < 0 ? -v : v;
    }

    private static BigInteger RandomMag(Random rng, int bits)
    {
        var bytes = new byte[(bits + 7) / 8 + 1];
        rng.NextBytes(bytes);
        bytes[^1] = 0;
        var m = new BigInteger(bytes) & ((BigInteger.One << bits) - 1);
        return m | (BigInteger.One << (bits - 1));   // exactly `bits` bits
    }

    // A mantissa whose bits below `drop` are a chosen pattern: an exact tie, tie ± 1, all
    // zero (exact), all ones (a carry), or random.
    private static BigInteger Shaped(Random rng, int bits, int drop, int kind)
    {
        var m = RandomMag(rng, bits);
        if (drop <= 0 || drop >= bits) return m;
        var high = m >> drop << drop;
        BigInteger half = BigInteger.One << (drop - 1);
        return kind switch
        {
            0 => high | half,                                    // exact tie
            1 => high | (half + 1),                              // just above
            2 => drop > 1 ? high | (half - 1) : high,            // just below
            3 => high,                                           // exact
            4 => high | ((BigInteger.One << drop) - 1),          // all ones
            _ => m,
        };
    }

    [Fact]
    public void Rounding_Matches_The_Old_Algorithm_Field_For_Field()
    {
        var rng = new Random(1872);
        int ties = 0;
        for (int i = 0; i < 20000; i++)
        {
            int prec = 64 + rng.Next(400);
            int bits = 1 + rng.Next(prec + 300);
            int kind = rng.Next(6);
            var mag = Shaped(rng, bits, bits - prec, kind);
            if (kind == 0 && bits > prec) ties++;
            int sign = rng.Next(2) == 0 ? 1 : -1;
            long exp = rng.Next(-3000, 3000);
            var x = Make(sign, mag, exp);
            var got = x.WithPrecision(prec);
            var want = OldRound(x.Mantissa.Sign, BigInteger.Abs(x.Mantissa), x.Exponent, prec);
            Assert.True(got.Mantissa == want.Mantissa && got.Exponent == want.Exponent,
                $"WithPrecision({prec}) of {bits}-bit kind {kind}: {got.Mantissa}·2^{got.Exponent} vs {want.Mantissa}·2^{want.Exponent}");

            // A product rounds through the same path.
            var y = Make(rng.Next(2) == 0 ? 1 : -1, RandomMag(rng, 1 + rng.Next(300)), rng.Next(-500, 500));
            var p = BigFloat.Mul(x, y, prec);
            var pm = x.Mantissa * y.Mantissa;
            var pw = OldRound(pm.Sign, BigInteger.Abs(pm), x.Exponent + y.Exponent, prec);
            Assert.True(p.Mantissa == pw.Mantissa && p.Exponent == pw.Exponent, $"Mul at {prec}");
        }
        Assert.True(ties > 1000, $"precondition: exact ties exercised ({ties})");
    }

    [Fact]
    public void ToDouble_Matches_The_Old_Algorithm_Bit_For_Bit()
    {
        var rng = new Random(18721);
        int subnormal = 0, infinite = 0, ties = 0;
        for (int i = 0; i < 40000; i++)
        {
            int bits = 1 + rng.Next(400);
            int kind = rng.Next(6);
            var mag = Shaped(rng, bits, bits - 53, kind);
            if (kind == 0 && bits > 53) ties++;
            int sign = rng.Next(2) == 0 ? 1 : -1;
            // Exponents so the value lands anywhere from below the subnormals to past overflow.
            long exp = rng.Next(-1200, 1100) - bits;
            var x = Make(sign, mag, exp);
            double got = x.ToDouble(), want = OldToDouble(x.Mantissa, x.Exponent);
            if (want != 0 && Math.Abs(want) < double.Epsilon * 4503599627370496.0) subnormal++;
            if (double.IsInfinity(want)) infinite++;
            Assert.True(BitConverter.DoubleToInt64Bits(got) == BitConverter.DoubleToInt64Bits(want),
                $"{bits}-bit kind {kind} ·2^{exp}: {got:R} vs {want:R}");
        }
        Assert.True(ties > 1000 && subnormal > 100 && infinite > 100, $"precondition: ties {ties}, subnormal {subnormal}, inf {infinite}");
    }
}
