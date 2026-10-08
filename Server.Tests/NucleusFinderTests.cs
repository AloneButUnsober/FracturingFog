// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;

using Xunit;

namespace FracturingFog.Server.Tests;

// Interesting-location finder S2 (#1186): Newton nucleus + atom size.
// Each result is checked against an invariant computed a different way:
//   • period-3 nuclei are the roots of f³_c(0)/c = c³ + 2c² + c + 1, so the
//     cubic (evaluated in OD) must vanish at them;
//   • the atom-size map c = nucleus + Size·c′ is checked with the S1 ball
//     detector, which shares no code with Newton.
public sealed class NucleusFinderTests
{
    private static DeepComplex C(double re, double im) => new(re, im);

    private static double Abs(OD x, OD y) => Math.Sqrt(x.X0 * x.X0 + y.X0 * y.X0);

    // |c³ + 2c² + c + 1| in OD complex arithmetic.
    private static double Period3Residual(DeepComplex c)
    {
        // Accumulate with OdExact: the cubic cancels to ~0 at a root, which
        // OD's pairwise (sloppy) add cannot resolve below ~1e-65.
        OD x = c.Re, y = c.Im;
        OD x2 = OdExact.Sub(x * x, y * y), y2 = (x * y) * 2.0;                 // c²
        OD x3 = OdExact.Sub(x2 * x, y2 * y), y3 = OdExact.Add(x2 * y, y2 * x);  // c³
        OD rx = OdExact.Add(OdExact.Add(OdExact.Add(x3, x2 * 2.0), x), new OD(1.0));
        OD ry = OdExact.Add(OdExact.Add(y3, y2 * 2.0), y);
        return Abs(rx, ry);
    }

    [Theory]
    [InlineData(-1.75, 0.0, 0.05)]           // real period-3 minibrot
    [InlineData(-0.12, 0.75, 0.05)]          // upper "rabbit" bulb
    [InlineData(-0.12, -0.75, 0.05)]         // lower rabbit bulb
    public void Period3_ConvergesToRootOfTheCubic(double re, double im, double radius)
    {
        var r = NucleusFinder.Find(C(re, im), 3, radius);
        Assert.Equal(NucleusStatus.Found, r.Status);
        Assert.True(Period3Residual(r.Nucleus) < 1e-100, $"cubic residual {Period3Residual(r.Nucleus):E2}");
    }

    [Theory]
    [InlineData(-1.02, 0.01, 2, -1.0, 0.0)]
    [InlineData(-1.30, 0.0, 4, -1.3107026413368328, 0.0)]
    [InlineData(0.01, -0.01, 1, 0.0, 0.0)]
    public void KnownNuclei(double re, double im, int period, double ere, double eim)
    {
        var r = NucleusFinder.Find(C(re, im), period, 0.05);
        Assert.Equal(NucleusStatus.Found, r.Status);
        Assert.Equal(ere, r.Nucleus.Re.X0, 14);
        Assert.Equal(eim, r.Nucleus.Im.X0, 14);
    }

    [Fact]
    public void DeepSeed_ResolvesNucleusToOdPrecision()
    {
        // Seed 1e-40 off the real period-3 nucleus in a 1e-35 view (OD path).
        var shallow = NucleusFinder.Find(C(-1.75, 0), 3, 0.05);
        var seed = shallow.Nucleus.Translate(1e-40, -1e-40);
        var r = NucleusFinder.Find(seed, 3, 1e-35);
        Assert.Equal(NucleusStatus.Found, r.Status);
        Assert.True(Period3Residual(r.Nucleus) < 1e-100);
        Assert.True(Abs(OdExact.Sub(r.Nucleus.Re, shallow.Nucleus.Re), OdExact.Sub(r.Nucleus.Im, shallow.Nucleus.Im)) < 1e-100);
    }

    [Fact]
    public void RootOfDivisorPeriod_IsRejected()
    {
        // −1 is the period-2 nucleus, and also a root of f⁴: Newton for period 4
        // seeded there stays put — the minimal-period check must refuse it.
        var r = NucleusFinder.Find(C(-1.0, 0.0), 4, 0.05);
        Assert.Equal(NucleusStatus.LowerPeriod, r.Status);
        Assert.Equal(2, r.ActualPeriod);
    }

    [Fact]
    public void NucleusOutsideView_IsRejected()
    {
        // Inside the main cardioid there is no period-3 nucleus within 0.01;
        // Newton must not hand back a distant one.
        var r = NucleusFinder.Find(C(-0.2, 0.1), 3, 0.01);
        Assert.False(r.Found);
        Assert.Contains(r.Status, new[] { NucleusStatus.LeftView, NucleusStatus.NoConvergence });
    }

    [Fact]
    public void OdExact_ResolvesCancellationThatSloppyAddLoses()
    {
        OD a = new OD(1.75) + 1e-80;                 // full-cascade OD+double: exact
        OD diff = OdExact.Sub(a, new OD(1.75));
        Assert.True(Math.Abs(diff.X0 - 1e-80) <= 1e-94, $"diff {diff.X0:E17}");
    }

    [Fact]
    public void BeyondOdPrecision_IsRefused()
        => Assert.Equal(NucleusStatus.PrecisionLimit, NucleusFinder.Find(C(-1, 0), 2, 1e-115).Status);

    [Fact]
    public void AtomSize_OfMainCardioid_IsOne()
        => Assert.Equal(Complex.One, NucleusFinder.AtomSize(C(0, 0), 1));

    [Fact]
    public void AtomSize_RealPeriod3_IsSmallPositiveReal()
    {
        // Same orientation as the main set (real, positive) and ~1/50 of its scale.
        var r = NucleusFinder.Find(C(-1.75, 0), 3, 0.05);
        Assert.True(Math.Abs(r.Size.Imaginary) < 1e-12);
        Assert.InRange(r.Size.Real, 0.012, 0.025);
    }

    [Theory]
    [InlineData(-1.75, 0.0, 0.05, 3)]
    [InlineData(-0.12, 0.75, 0.05, 3)]
    [InlineData(-0.1565, 1.0322, 0.01, 4)]
    public void AtomSizeMap_SendsMainPeriod2BulbToThePeriod2pBulb(double re, double im, double radius, int p)
    {
        // Under c = nucleus + Size·c′ the main set's period-2 nucleus (c′ = −1)
        // lands on this minibrot's period-2p bulb. The S1 ball detector — not
        // Newton — must see period 2p in a small disk there.
        var r = NucleusFinder.Find(C(re, im), p, radius);
        Assert.Equal(NucleusStatus.Found, r.Status);
        Complex at = -r.Size;
        var probe = r.Nucleus.Translate(at.Real, at.Imaginary);
        var d = PeriodDetector.Detect(probe, 0.1 * r.Size.Magnitude);
        Assert.Equal(2 * p, d.Period);
    }

    [Fact]
    public void Frame_OfMainCardioid_IsTheHomeView()
    {
        var r = NucleusFinder.Find(C(0.01, 0.01), 1, 0.05);
        var (centre, zoom) = NucleusFinder.Frame(r);
        Assert.Equal(-0.5, centre.Re.X0, 12);
        Assert.Equal(0.0, centre.Im.X0, 12);
        Assert.Equal(1.0, zoom, 12);
    }

    [Fact]
    public void Frame_ScalesZoomByInverseSize()
    {
        var r = NucleusFinder.Find(C(-1.75, 0), 3, 0.05);
        var (_, zoom) = NucleusFinder.Frame(r, framing: 2.0);
        Assert.Equal(2.0 / r.Size.Magnitude, zoom, 9);
    }

    // ── FindMinibrot: whole-view search (period unknown) ─────────────────

    private static double Offset(DeepComplex a, DeepComplex b)
        => Abs(OdExact.Sub(a.Re, b.Re), OdExact.Sub(a.Im, b.Im));

    // A found minibrot must (1) sit inside the view and (2) be confirmed at its
    // period by the S1 ball scan on a disk far smaller than the minibrot —
    // a different algorithm from the Newton that found it.
    private static void AssertVerifiedInView(NucleusResult r, DeepComplex centre, double radius)
    {
        Assert.Equal(NucleusStatus.Found, r.Status);
        Assert.True(Offset(r.Nucleus, centre) <= radius, "nucleus outside the view");
        var recheck = PeriodDetector.Detect(r.Nucleus, 0.01 * r.Size.Magnitude);
        Assert.Equal(r.Period, recheck.Period);
    }

    [Fact]
    public void FindMinibrot_HomeView_IsTheMainCardioid()
    {
        var centre = C(-0.5, 0);
        double radius = PeriodDetector.ViewDiskRadius(1.0, 1920, 1080);
        var r = NucleusFinder.FindMinibrot(centre, radius);
        Assert.Equal(1, r.Period);
        Assert.Equal(0.0, r.Nucleus.Re.X0, 14);
    }

    [Fact]
    public void FindMinibrot_SkipsTheBallsFalsePositive()
    {
        // Built-in region "Lakes and Rivers": the whole-view ball scan reports
        // period 116, but no period-116 nucleus is in view (it over-
        // approximates); the real lowest minibrot found from the centre is
        // higher. The result must be genuinely inside the view and verified.
        var centre = C(-0.7444389447199996, -0.10814196367335771);
        double radius = PeriodDetector.ViewDiskRadius(121739.57374223076, 1920, 1080);
        int ballLowerBound = PeriodDetector.Detect(centre, radius).Period;

        var r = NucleusFinder.FindMinibrot(centre, radius);
        AssertVerifiedInView(r, centre, radius);
        Assert.True(r.Period > ballLowerBound, $"period {r.Period} vs ball {ballLowerBound}");
    }

    [Fact]
    public void FindMinibrot_Zoom1e43_OnPerturbedPath()
    {
        // Built-in region "Sun with Feet" (four limbs per axis).
        var centre = DeepComplex.FromLimbs(
            -1.9918151296901943, -7.82198368112659e-17, -5.623766919601434e-33, -2.17062833971826e-49, 0, 0, 0, 0,
            -5.524041575397243e-06, -2.9567164202839686e-22, -2.2585741415682749e-38, -3.9464457093766516e-56, 0, 0, 0, 0);
        double radius = PeriodDetector.ViewDiskRadius(1.0414672394175723e+43, 1920, 1080);
        var r = NucleusFinder.FindMinibrot(centre, radius);
        AssertVerifiedInView(r, centre, radius);
    }

    [Fact]
    public void FindMinibrot_EscapingView_FindsNothing()
    {
        var r = NucleusFinder.FindMinibrot(C(1.0, 1.0), 0.1);
        Assert.False(r.Found);
    }

    [Fact]
    public void Cancellation_IsHonoured()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            NucleusFinder.Find(C(-1.75, 0), 3, 0.05, ct: cts.Token));
    }
}
