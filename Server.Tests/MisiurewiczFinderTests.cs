// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;

using Xunit;

namespace FracturingFog.Server.Tests;

// Interesting-location finder S3 (#1187): Misiurewicz snap.
// Fixtures are checked against values derived independently of the finder:
// exact orbits of c = i and c = −2 worked by hand, published coordinates of
// two well-known Misiurewicz points, and the defining equation evaluated in
// OD with OdExact (not the finder's own Newton code path).
public sealed class MisiurewiczFinderTests
{
    private static DeepComplex C(double re, double im) => new(re, im);

    private static double Dist(DeepComplex a, double re, double im)
        => Math.Sqrt(Math.Pow(OdExact.Sub(a.Re, new OD(re)).X0, 2) + Math.Pow(OdExact.Sub(a.Im, new OD(im)).X0, 2));

    // |z_{k+p} − z_k| at c, in OD.
    private static double Residual(DeepComplex c, int k, int p)
    {
        OD x = OD.Zero, y = OD.Zero;
        OD kx = OD.Zero, ky = OD.Zero;
        for (int n = 1; n <= k + p; n++)
        {
            (x, y) = OdExact.SquareAdd(x, y, c.Re, c.Im);
            if (n == k) { kx = x; ky = y; }
        }
        return Math.Sqrt(Math.Pow(OdExact.Sub(x, kx).X0, 2) + Math.Pow(OdExact.Sub(y, ky).X0, 2));
    }

    [Fact]
    public void NearI_SnapsToI_WithPreperiod2Period2()
    {
        // 0 → i → −1+i → −i → −1+i: z₄ = z₂, so (k, p) = (2, 2).
        var r = MisiurewiczFinder.FindNear(C(0.002, 1.001), 0.01);
        Assert.Equal(MisiurewiczStatus.Found, r.Status);
        Assert.Equal((2, 2), (r.Preperiod, r.Period));
        Assert.True(Dist(r.Point, 0, 1) < 1e-100);
        // λ = 2(−1+i)·2(−i) = 4(1+i).
        Assert.Equal(4.0, r.Multiplier.Real, 9);
        Assert.Equal(4.0, r.Multiplier.Imaginary, 9);
    }

    [Fact]
    public void NearMinus2_SnapsToTip_WithPreperiod2Period1()
    {
        // 0 → −2 → 2 → 2: (k, p) = (2, 1), λ = 2·2 = 4.
        var r = MisiurewiczFinder.FindNear(C(-1.9995, 0.0003), 0.005);
        Assert.Equal(MisiurewiczStatus.Found, r.Status);
        Assert.Equal((2, 1), (r.Preperiod, r.Period));
        Assert.True(Dist(r.Point, -2, 0) < 1e-100);
        Assert.Equal(4.0, r.Multiplier.Magnitude, 9);
    }

    [Theory]
    [InlineData(-0.10109636, 0.95628651)]    // classic upper-left spiral hub
    [InlineData(-0.77568377, 0.13646737)]    // seahorse-valley spiral
    public void PublishedPoints_AreRecovered(double re, double im)
    {
        var r = MisiurewiczFinder.FindNear(C(re + 2e-9, im - 2e-9), 1e-7);
        Assert.Equal(MisiurewiczStatus.Found, r.Status);
        Assert.True(Dist(r.Point, re, im) < 1e-8, $"off by {Dist(r.Point, re, im):E2} at M({r.Preperiod},{r.Period})");
        Assert.True(Residual(r.Point, r.Preperiod, r.Period) < 1e-90);
        Assert.True(r.Multiplier.Magnitude > 1.0, "the cycle must be repelling");
    }

    [Fact]
    public void Refine_RejectsANonMinimalPreperiod()
    {
        // i is M(2,2); asking for (3,2) must report the smaller preperiod.
        var r = MisiurewiczFinder.Refine(C(0, 1), 3, 2, C(0, 1), 0.01);
        Assert.Equal(MisiurewiczStatus.LowerPreperiod, r.Status);
        Assert.Equal(2, r.Preperiod);
    }

    [Fact]
    public void Refine_RejectsANonMinimalPeriod()
    {
        // −2 is M(2,1); asking for (2,2) must report the divisor period.
        var r = MisiurewiczFinder.Refine(C(-2, 0), 2, 2, C(-2, 0), 0.01);
        Assert.Equal(MisiurewiczStatus.LowerPeriod, r.Status);
        Assert.Equal(1, r.Period);
    }

    [Fact]
    public void DeepSnap_HoldsToOdPrecision()
    {
        // 1e-40 off c = i with a 1e-35 accept radius: the result must still be i.
        var click = C(0, 1).Translate(3e-41, -4e-41);
        var r = MisiurewiczFinder.FindNear(click, 1e-35);
        Assert.Equal(MisiurewiczStatus.Found, r.Status);
        Assert.True(Dist(r.Point, 0, 1) < 1e-100);
    }

    [Fact]
    public void ClickFarFromAnyHub_WithTinyRadius_FindsNothingInReach()
    {
        // Inside the main cardioid: no Misiurewicz point (they lie on the
        // boundary) within 1e-6.
        var r = MisiurewiczFinder.FindNear(C(-0.2, 0.1), 1e-6);
        Assert.False(r.Found);
    }

    [Fact]
    public void Cancellation_IsHonoured()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MisiurewiczFinder.FindNear(C(-0.7756, 0.1364), 1e-3, ct: cts.Token));
    }
}
