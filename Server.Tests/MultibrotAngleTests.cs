// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;

using FracturingFog.Abstractions.Explore;

using Xunit;

namespace FracturingFog.Server.Tests;

// #1242 — base-d external angles and parameter rays for z^d + c. Checked
// against independent facts: the binary ExternalAngle / ExternalRay for d = 2,
// closed-form nuclei and Misiurewicz points of z³ + c, and the (d − 1)-fold
// rotation and conjugation symmetries of the Multibrot set.
public sealed class MultibrotAngleTests
{
    private readonly ITestOutputHelper _out;
    public MultibrotAngleTests(ITestOutputHelper output) => _out = output;

    // ── BaseAngle ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(1, 7)]
    [InlineData(5, 12)]
    [InlineData(1, 6)]
    [InlineData(3, 4)]
    [InlineData(0, 1)]
    [InlineData(11, 56)]
    public void Base2_MatchesBinaryExternalAngle(int p, int q)
    {
        var b = BaseAngle.FromFraction(p, q, 2);
        var e = ExternalAngle.FromFraction(p, q);
        Assert.Equal(e.ToString(), b.ToString());
        Assert.Equal(e.Fraction, b.Fraction);
    }

    [Theory]
    [InlineData(3, 1, 8, ".(01)")]
    [InlineData(3, 1, 6, ".0(1)")]
    [InlineData(3, 1, 2, ".(1)")]
    [InlineData(3, 1, 3, ".1(0)")]
    [InlineData(3, 2, 3, ".2(0)")]
    [InlineData(4, 1, 15, ".(01)")]
    [InlineData(4, 2, 5, ".(12)")]
    [InlineData(10, 1, 7, ".(142857)")]
    [InlineData(16, 1, 3, ".(5)")]
    public void FromFraction_KnownExpansions(int b, int p, int q, string expected)
        => Assert.Equal(expected, BaseAngle.FromFraction(p, q, b).ToString());

    [Fact]
    public void Canonical_FoldsRecurringTopDigit()
    {
        Assert.Equal(".(0)", BaseAngle.FromDigits(3, "", "2").ToString());          // .(2) = 1 ≡ 0
        Assert.Equal(".2(0)", BaseAngle.FromDigits(3, "1", "2").ToString());        // .1(2) = .2
        Assert.Equal(".(01)", BaseAngle.FromDigits(3, "0", "10").ToString());       // .0(10) = .(01)
        Assert.Equal(".(01)", BaseAngle.FromDigits(3, "", "0101").ToString());
    }

    [Fact]
    public void Fraction_RoundTripsExactly()
    {
        var rng = new Random(1242);
        for (int t = 0; t < 400; t++)
        {
            int b = rng.Next(2, 13);
            long q = rng.Next(1, 2000), p = rng.Next(0, (int)q);
            var a = BaseAngle.FromFraction(p, q, b);
            var g = BigInteger.GreatestCommonDivisor(p, q);
            var (n, d) = a.Fraction;
            if (p == 0) { Assert.True(n.IsZero); continue; }
            Assert.Equal(p / g, n);
            Assert.Equal(q / g, d);
            // The text form parses back to the same angle.
            Assert.True(BaseAngle.TryParse(a.ToString(), b, out var back, out _));
            Assert.Equal(a, back);
        }
    }

    [Fact]
    public void TryParse_RejectsBadDigits()
    {
        Assert.False(BaseAngle.TryParse(".(03)", 3, out _, out var err));
        Assert.Contains("base-3", err);
        Assert.True(BaseAngle.TryParse("0.1(02)", 3, out var a, out _));
        Assert.Equal(".1(02)", a!.ToString());
    }

    // ── Rays: d = 2 agrees with the binary ExternalRay ───────────────────

    [Theory]
    [InlineData(1, 3)]
    [InlineData(1, 7)]
    [InlineData(2, 7)]
    [InlineData(1, 15)]
    public void Base2_PeriodicLanding_MatchesExternalRay(int p, int q)
    {
        var bin = ExternalRay.Land(ExternalAngle.FromFraction(p, q));
        var gen = MultibrotRay.Land(new MultibrotMap(2), BaseAngle.FromFraction(p, q, 2));
        Assert.Equal(RayLandingStatus.Component, bin.Status);
        Assert.Equal(RayLandingStatus.Component, gen.Status);
        Assert.Equal(bin.Nucleus.Period, gen.Nucleus.Period);
        double dx = gen.Nucleus.Point.X - bin.Nucleus.Nucleus.Re.X0, dy = gen.Nucleus.Point.Y - bin.Nucleus.Nucleus.Im.X0;
        Assert.True(Math.Sqrt(dx * dx + dy * dy) < 1e-12, $"{p}/{q}: {gen.Nucleus.Point} vs ({bin.Nucleus.Nucleus.Re.X0}, {bin.Nucleus.Nucleus.Im.X0})");
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(1, 4)]
    [InlineData(5, 12)]
    public void Base2_PreperiodicLanding_MatchesExternalRay(int p, int q)
    {
        var bin = ExternalRay.Land(ExternalAngle.FromFraction(p, q));
        var gen = MultibrotRay.Land(new MultibrotMap(2), BaseAngle.FromFraction(p, q, 2));
        Assert.Equal(RayLandingStatus.Misiurewicz, bin.Status);
        Assert.Equal(RayLandingStatus.Misiurewicz, gen.Status);
        Assert.Equal(bin.Misiurewicz.Preperiod, gen.Misiurewicz.Preperiod);
        Assert.Equal(bin.Misiurewicz.Period, gen.Misiurewicz.Period);
        double dx = gen.Misiurewicz.Point.X - bin.Misiurewicz.Point.Re.X0, dy = gen.Misiurewicz.Point.Y - bin.Misiurewicz.Point.Im.X0;
        Assert.True(Math.Sqrt(dx * dx + dy * dy) < 1e-12);
    }

    // ── Rays: z³ + c against closed forms ────────────────────────────────

    // Period-2 nuclei of z³ + c: c³ + c = 0 → c = ±i. The rays 1/8, 1/4, 3/8
    // land on the upper bulb (root, co-root, root): every component of M_3 has
    // d − 1 = 2 parabolic boundary points, so three period-2 rays per bulb.
    [Theory]
    [InlineData(1, 8, 1)]
    [InlineData(1, 4, 1)]
    [InlineData(3, 8, 1)]
    [InlineData(5, 8, -1)]
    [InlineData(3, 4, -1)]
    [InlineData(7, 8, -1)]
    public void Cubic_Period2Rays_LandOnClosedFormNuclei(int p, int q, int sign)
    {
        var r = MultibrotRay.Land(new MultibrotMap(3), BaseAngle.FromFraction(p, q, 3));
        Assert.Equal(RayLandingStatus.Component, r.Status);
        Assert.Equal(2, r.Nucleus.Period);
        Assert.True(Math.Abs(r.Nucleus.Point.X) < 1e-12 && Math.Abs(r.Nucleus.Point.Y - sign) < 1e-12, $"{p}/{q} → {r.Nucleus.Point}");
    }

    // Fixed angles 0 and 1/2 land on the two cusps of the main body,
    // c = ±2/(3√3) (z = ±1/√3 with multiplier 3z² = 1); its nucleus is 0.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void Cubic_FixedRays_LandOnTheCusps(int p, int q)
    {
        var r = MultibrotRay.Land(new MultibrotMap(3), BaseAngle.FromFraction(p, q, 3));
        Assert.Equal(RayLandingStatus.Component, r.Status);
        Assert.Equal(1, r.Nucleus.Period);
        Assert.True(r.Nucleus.Point.Length < 1e-12);
        double cusp = (p == 0 ? 1 : -1) * 2 / (3 * Math.Sqrt(3));
        double off = Math.Sqrt(Math.Pow(r.RayEnd.X - cusp, 2) + r.RayEnd.Y * r.RayEnd.Y);
        _out.WriteLine($"{p}/{q}: ray end {r.RayEnd}, {off:G3} from the cusp {cusp:R}");
        Assert.True(off < 0.05);
    }

    // M(2, 1) of z³ + c: z₂ = z₃ gives c² = ω − 1 or ω̄ − 1 (ω = e^{2πi/3}).
    // The 1/6 ray lands on 3^{1/4}·e^{i·75°}; the symmetries give the rest.
    [Theory]
    [InlineData(1, 6, 75)]
    [InlineData(2, 3, 255)]
    [InlineData(5, 6, -75)]
    [InlineData(1, 3, 105)]
    public void Cubic_Misiurewicz_M21_ClosedForm(int p, int q, double degrees)
    {
        var r = MultibrotRay.Land(new MultibrotMap(3), BaseAngle.FromFraction(p, q, 3));
        Assert.Equal(RayLandingStatus.Misiurewicz, r.Status);
        Assert.Equal(2, r.Misiurewicz.Preperiod);
        Assert.Equal(1, r.Misiurewicz.Period);
        var expected = Complex.FromPolarCoordinates(Math.Pow(3, 0.25), degrees * Math.PI / 180);
        var c = new Complex(r.Misiurewicz.Point.X, r.Misiurewicz.Point.Y);
        Assert.True((c - expected).Magnitude < 1e-12, $"{p}/{q} → {c}, expected {expected}");
        // Independent: c² = ω − 1 for one of the two primitive cube roots ω.
        var w = c * c + 1;
        Assert.True(Math.Abs(w.Magnitude - 1) < 1e-12 && Math.Abs(Math.Abs(w.Phase) - 2 * Math.PI / 3) < 1e-12);
    }

    // ── Symmetry: M_d is invariant under c ↦ e^{2πi/(d−1)}·c, which maps the
    // ray θ to θ + 1/(d − 1), and under conjugation (θ ↦ −θ). ─────────────

    [Theory]
    [InlineData(4, 1, 15)]
    [InlineData(4, 1, 12)]
    [InlineData(5, 1, 24)]
    [InlineData(5, 7, 40)]
    public void RotationAndConjugationSymmetry(int d, int p, int q)
    {
        var map = new MultibrotMap(d);
        var a = BaseAngle.FromFraction(p, q, d);
        var rot = BaseAngle.FromFraction(p * (d - 1) + q, q * (d - 1), d);   // θ + 1/(d−1)
        var con = BaseAngle.FromFraction(q - p, q, d);                     // −θ
        var l0 = Point(MultibrotRay.Land(map, a));
        var l1 = Point(MultibrotRay.Land(map, rot));
        var l2 = Point(MultibrotRay.Land(map, con));
        var w = Complex.FromPolarCoordinates(1, 2 * Math.PI / (d - 1));
        _out.WriteLine($"d={d} {a}: {l0}; rotated {l1}; conjugate {l2}");
        Assert.True((l1 - w * l0).Magnitude < 1e-11, $"rotation: {l1} vs {w * l0}");
        Assert.True((l2 - Complex.Conjugate(l0)).Magnitude < 1e-11, $"conjugation: {l2} vs {Complex.Conjugate(l0)}");
    }

    private static Complex Point(GeneralRayLanding r)
    {
        Assert.NotEqual(RayLandingStatus.Failed, r.Status);
        var v = r.Status == RayLandingStatus.Component ? r.Nucleus.Point : r.Misiurewicz.Point;
        return new Complex(v.X, v.Y);
    }

    [Fact]
    public void Quartic_Period2Nuclei_AreRootsOfC3PlusOne()
    {
        // c⁴ + c = 0 → c³ = −1 for the period-2 nuclei of z⁴ + c.
        var map = new MultibrotMap(4);
        // Twelve period-2 angles k/15 (k not a multiple of 5): three components,
        // each with a root (two rays) and two co-roots (one ray each).
        var hits = new System.Collections.Generic.Dictionary<int, int>();
        foreach (int p in new[] { 1, 2, 3, 4, 6, 7, 8, 9, 11, 12, 13, 14 })
        {
            int q = 15;
            var r = MultibrotRay.Land(map, BaseAngle.FromFraction(p, q, 4));
            Assert.Equal(RayLandingStatus.Component, r.Status);
            var c = new Complex(r.Nucleus.Point.X, r.Nucleus.Point.Y);
            Assert.Equal(2, r.Nucleus.Period);
            Assert.True((c * c * c + 1).Magnitude < 1e-11, $"{p}/{q} → {c}");
            int key = (int)Math.Round(c.Phase * 3 / Math.PI);
            hits[key] = (hits.TryGetValue(key, out int h) ? h : 0) + 1;
        }
        Assert.Equal(3, hits.Count);
        Assert.All(hits.Values, n => Assert.Equal(4, n));
    }

    [Fact]
    public void Land_RejectsAWrongBase()
    {
        var r = MultibrotRay.Land(new MultibrotMap(3), BaseAngle.FromFraction(1, 7, 2));
        Assert.Equal(RayLandingStatus.Failed, r.Status);
    }
}
