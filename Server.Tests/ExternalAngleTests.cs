// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;

using Xunit;

namespace FracturingFog.Server.Tests;

// Interesting-location finder S4 (#1188): external angles, internal addresses,
// ray landing. Expectations are textbook facts (Douady–Hubbard landing pairs,
// Lau–Schleicher addresses) and hand-worked binary expansions — not output
// of the code under test.
public sealed class ExternalAngleTests
{
    private static ExternalAngle A(string s)
    {
        Assert.True(ExternalAngle.TryParse(s, out var a, out var err), err);
        return a!;
    }

    // ── Parsing / exact arithmetic ───────────────────────────────────────

    [Theory]
    [InlineData("1/3", "", "01")]
    [InlineData("1/7", "", "001")]
    [InlineData("3/7", "", "011")]
    [InlineData("1/6", "0", "01")]
    [InlineData("1/2", "1", "0")]
    [InlineData("5/12", "01", "10")]
    [InlineData("1/15", "", "0001")]
    public void Fraction_ToBinary(string frac, string pre, string per)
    {
        var a = A(frac);
        Assert.Equal(pre, a.Pre);
        Assert.Equal(per, a.Per);
    }

    [Theory]
    [InlineData(".(011)", 3, 7)]
    [InlineData("0.0(01)", 1, 6)]
    [InlineData(".01(10)", 5, 12)]
    [InlineData(".1", 1, 2)]
    [InlineData(".(011011)", 3, 7)]      // non-minimal period folds to (011)
    [InlineData(".0(1)", 1, 2)]          // .0111… = .1
    [InlineData(".1(01)", 2, 3)]         // .10101… = .(10) = 2/3
    public void Binary_ToExactFraction(string text, int n, int d)
        => Assert.Equal((new BigInteger(n), new BigInteger(d)), A(text).Fraction);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(".(012)")]
    [InlineData(".(01")]
    [InlineData("1/0")]
    public void Invalid_IsRejectedWithAMessage(string text)
    {
        Assert.False(ExternalAngle.TryParse(text, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void Bits_AreTheDoublingItinerary()
    {
        var a = A(".01(10)");
        Assert.Equal(new[] { 0, 1, 1, 0, 1, 0 }, Enumerable.Range(1, 6).Select(a.Bit));
        Assert.Equal(5.0 / 12.0, a.DoubledValue(0), 15);
        Assert.Equal(5.0 / 6.0, a.DoubledValue(1), 15);   // 2·5/12
    }

    // ── Internal addresses (Lau–Schleicher) ───────────────────────────────

    [Theory]
    [InlineData("1/3", new[] { 1, 2 })]        // basilica
    [InlineData("1/7", new[] { 1, 3 })]        // rabbit
    [InlineData("2/7", new[] { 1, 3 })]        // rabbit, other ray
    [InlineData("3/7", new[] { 1, 2, 3 })]     // airplane
    [InlineData("1/15", new[] { 1, 4 })]       // period-4 bulb on the cardioid (1/4 limb)
    public void InternalAddress_OfKnownComponents(string angle, int[] address)
        => Assert.Equal(address, A(angle).InternalAddress());

    // ── Ray landing ───────────────────────────────────────────────────────

    private static double Dist(DeepComplex a, double re, double im)
        => Math.Sqrt(Math.Pow(OdExact.Sub(a.Re, new OD(re)).X0, 2) + Math.Pow(OdExact.Sub(a.Im, new OD(im)).X0, 2));

    [Theory]
    [InlineData("1/3", 2, -1.0, 0.0)]                                    // basilica
    [InlineData("2/3", 2, -1.0, 0.0)]
    [InlineData("1/7", 3, -0.1225611668766536, 0.7448617666197442)]      // rabbit
    [InlineData("2/7", 3, -0.1225611668766536, 0.7448617666197442)]
    [InlineData("3/7", 3, -1.7548776662466927, 0.0)]                     // airplane
    [InlineData("4/7", 3, -1.7548776662466927, 0.0)]
    [InlineData("1/15", 4, 0.2822713907669139, 0.5300606175785253)]      // period-4 bulb, 1/4 limb
    public void PeriodicRay_LandsOnItsComponent(string angle, int period, double re, double im)
    {
        var r = ExternalRay.Land(A(angle));
        Assert.Equal(RayLandingStatus.Component, r.Status);
        Assert.Equal(period, r.Nucleus.Period);
        Assert.True(Dist(r.Nucleus.Nucleus, re, im) < 1e-12, $"{r.Detail} off {Dist(r.Nucleus.Nucleus, re, im):E2}");
    }

    [Theory]
    [InlineData("1/6", 2, 2, 0.0, 1.0)]      // c = i,  M(2,2)
    [InlineData("1/2", 2, 1, -2.0, 0.0)]     // c = −2, M(2,1)
    [InlineData("5/6", 2, 2, 0.0, -1.0)]     // c = −i
    public void PreperiodicRay_LandsOnItsMisiurewiczPoint(string angle, int k, int p, double re, double im)
    {
        var r = ExternalRay.Land(A(angle));
        Assert.Equal(RayLandingStatus.Misiurewicz, r.Status);
        Assert.Equal((k, p), (r.Misiurewicz.Preperiod, r.Misiurewicz.Period));
        Assert.True(Dist(r.Misiurewicz.Point, re, im) < 1e-100);
    }

    [Theory]
    [InlineData("6/15", "9/15", -1.3107026413368328)]   // period-4 bulb on the period-2 bulb
    [InlineData("7/15", "8/15", -1.9407998065294848)]   // real period-4 primitive
    public void RealAxisRayPair_LandsOnTheSameNucleus(string a, string b, double re)
    {
        // Conjugate angles (a + b = 1) land on the real axis; the pair lands on
        // one component, so both rays must reach the same nucleus.
        var ra = ExternalRay.Land(A(a));
        var rb = ExternalRay.Land(A(b));
        Assert.Equal(RayLandingStatus.Component, ra.Status);
        Assert.Equal(RayLandingStatus.Component, rb.Status);
        Assert.True(Dist(ra.Nucleus.Nucleus, re, 0) < 1e-9);
        Assert.True(Dist(rb.Nucleus.Nucleus, re, 0) < 1e-9);
        // Independent confirmation: the S1 ball scan sees period 4 there.
        Assert.Equal(4, PeriodDetector.Detect(ra.Nucleus.Nucleus, 0.01 * ra.Nucleus.Size.Magnitude).Period);
    }
}
