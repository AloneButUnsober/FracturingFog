// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1252 — CoordNotation converted limbs with the C# (decimal)double cast, which
// rounds to 15 significant digits. The single-value CX/CY box was wrong from the
// 16th digit (a visible offset past ~1e13 zoom), and a typed 20-digit coordinate
// parsed ~1e-16 relative off. The oracle here is exact BigInteger arithmetic on
// the doubles' bits, independent of decimal and of CoordNotation.

using System;
using System.Globalization;
using System.Numerics;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1252CoordNotationPrecisionTests
{
    private const int K = 1100;   // every limb here is an integer multiple of 2^-K

    // d · 2^K exactly.
    private static BigInteger Scaled(double d)
    {
        if (d == 0.0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exp == 0) exp = 1; else man |= 1L << 52;
        var v = new BigInteger(man) << (exp - 1075 + K);
        return bits < 0 ? -v : v;
    }

    private static BigInteger Sum(params double[] limbs)
    {
        var s = BigInteger.Zero;
        foreach (double d in limbs) s += Scaled(d);
        return s;
    }

    // tol · 2^K, tol = n · 10^-28.
    private static BigInteger Tol(int n) => BigInteger.Pow(2, K) * n / BigInteger.Pow(10, 28);

    private static double Approx(BigInteger scaled) => Math.ScaleB((double)(scaled >> 900), 900 - K);

    [Theory]
    [InlineData(0.12345678901234567)]
    [InlineData(-0.5677880890624760266759978326)]
    [InlineData(0.1)]
    [InlineData(1e-5)]
    [InlineData(-1.9999999999999998)]
    [InlineData(-0.7436438870371587)]
    [InlineData(1234.5678901234567)]
    public void A_Lone_Double_Reads_Back_Exactly(double d)
    {
        string s = CoordNotation.FormatSingle(d);
        Assert.DoesNotContain("E", s, StringComparison.OrdinalIgnoreCase);
        Assert.True(CoordNotation.TryParse(s, out double hi, out double lo, out double l2, out double l3));
        Assert.Equal(d, hi);
        Assert.Equal(0.0, lo);
        Assert.Equal(0.0, l2);
        Assert.Equal(0.0, l3);
        Assert.Equal(s, CoordNotation.FormatPipe(d));
    }

    [Fact]
    public void ToDecimalExact_Is_Exact_To_Decimals_Floor()
    {
        var rng = new Random(1252);
        for (int i = 0; i < 2000; i++)
        {
            double d = (rng.NextDouble() * 2 - 1) * Math.Pow(10, rng.Next(-6, 6));
            decimal got = CoordNotation.ToDecimalExact(d);
            // decimal keeps 28 places or 29 significant digits, whichever runs
            // out first: |got - d| <= max(|d|·1e-27, 1e-28). The old cast was
            // ~1e-16 relative off.
            var gotScaled = BigInteger.Parse(Math.Abs(got).ToString(CultureInfo.InvariantCulture).Replace(".", ""))
                            * (got < 0 ? -1 : 1) * BigInteger.Pow(2, K) / BigInteger.Pow(10, got.Scale);
            var err = BigInteger.Abs(gotScaled - Scaled(d));
            var bound = BigInteger.Max(BigInteger.Abs(Scaled(d)) / BigInteger.Pow(10, 27), Tol(1));
            Assert.True(err <= bound, $"{d:R}: ToDecimalExact {got} off by {Approx(err):E3}");
        }
    }

    // Random deep DD/QD/OD-style centres: TryParse(FormatSingle(limbs)) must give
    // back limbs whose exact sum is the original's to decimal's floor (~1e-28),
    // where the old 15-digit cast was ~1e-16 off.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Single_Value_Round_Trips_Deep_Centres_To_Decimals_Floor(int limbCount)
    {
        var rng = new Random(1252 + limbCount);
        for (int i = 0; i < 1000; i++)
        {
            var limbs = new double[8];
            limbs[0] = (rng.NextDouble() * 4 - 2);
            if (Math.Abs(limbs[0]) < 0.1) limbs[0] += 0.5;
            for (int j = 1; j < limbCount; j++)
                limbs[j] = limbs[j - 1] * (rng.NextDouble() - 0.5) * Math.Pow(2, -52);

            string s = CoordNotation.FormatSingle(limbs[0], limbs[1], limbs[2], limbs[3],
                limbs[4], limbs[5], limbs[6], limbs[7]);
            Assert.True(CoordNotation.TryParse(s, out double hi, out double lo, out double l2, out double l3,
                out double l4, out double l5, out double l6, out double l7));

            var err = BigInteger.Abs(Sum(hi, lo, l2, l3, l4, l5, l6, l7) - Sum(limbs));
            Assert.True(err <= Tol(4),
                $"limbs {string.Join("|", limbs)}: '{s}' read back {Approx(err):E3} off");
            // The text itself is the centre to the same floor.
            var shown = BigInteger.Parse(s.Replace("-", "").Replace(".", ""));
            int places = s.Contains('.') ? s.Length - s.IndexOf('.') - 1 : 0;
            var shownScaled = (s.StartsWith('-') ? -shown : shown) * BigInteger.Pow(2, K) / BigInteger.Pow(10, places);
            Assert.True(BigInteger.Abs(shownScaled - Sum(limbs)) <= Tol(4),
                $"limbs {string.Join("|", limbs)}: shown '{s}' is {Approx(BigInteger.Abs(shownScaled - Sum(limbs))):E3} off");
        }
    }

    [Fact]
    public void A_Typed_Long_Coordinate_Parses_To_Decimals_Floor()
    {
        const string typed = "-0.5677880890624760266759978326";
        Assert.True(CoordNotation.TryParse(typed, out double hi, out double lo, out double l2, out double l3));
        var want = -BigInteger.Parse("5677880890624760266759978326") * BigInteger.Pow(2, K) / BigInteger.Pow(10, 28);
        var err = BigInteger.Abs(Sum(hi, lo, l2, l3) - want);
        Assert.True(err <= Tol(2), $"parsed limbs {hi:R}|{lo:R}|{l2:R}|{l3:R} are {Approx(err):E3} off");
        Assert.Equal(-0.5677880890624760266759978326, hi);   // hi is the nearest double
        Assert.Equal(typed, CoordNotation.FormatSingle(hi, lo, l2, l3));
    }

    [Fact]
    public void Out_Of_Range_Still_Falls_Back()
    {
        Assert.Equal((1e30).ToString("G17", CultureInfo.InvariantCulture), CoordNotation.FormatSingle(1e30));
        Assert.Equal((1e30).ToString("G17", CultureInfo.InvariantCulture), CoordNotation.FormatSingle(1e30, 1.0));
        Assert.Throws<OverflowException>(() => CoordNotation.ToDecimalExact(double.NaN));
        Assert.Equal(0m, CoordNotation.ToDecimalExact(0.0));
        Assert.Equal(-1.5m, CoordNotation.ToDecimalExact(-1.5));
        Assert.Equal(3m * 4611686018427387904m, CoordNotation.ToDecimalExact(3.0 * Math.Pow(2, 62)));
    }
}
