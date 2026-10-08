// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;

using Xunit;

namespace FracturingFog.Server.Tests;

// Interesting-location finder S1 (#1185): ball-arithmetic period detection.
// Fixtures are known nuclei of z² + c, checked against the invariant "a disk
// that contains a period-p nucleus (and no lower-period one) reports p", not
// against the detector's own output.
public sealed class PeriodDetectorTests
{
    private static DeepComplex C(double re, double im) => new(re, im);

    [Theory]
    [InlineData(0.0, 0.0, 0.1, 1)]                                   // main cardioid nucleus
    [InlineData(-1.0, 0.0, 0.05, 2)]                                 // period-2 bulb
    [InlineData(-1.7548776662466927, 0.0, 0.01, 3)]                  // real-axis period-3 minibrot
    [InlineData(-0.1225611668766536, 0.7448617666197442, 0.01, 3)]   // "rabbit" period-3 bulb
    [InlineData(-1.3107026413368328, 0.0, 0.005, 4)]                 // period-4 bulb off the period-2 bulb
    public void DiskAroundKnownNucleus_ReportsItsPeriod(double re, double im, double radius, int period)
    {
        var d = PeriodDetector.Detect(C(re, im), radius);
        Assert.Equal(PeriodStop.Found, d.Stop);
        Assert.Equal(period, d.Period);
    }

    [Fact]
    public void LowerPeriodNucleusOutsideDisk_IsNotReported()
    {
        // The disk around −1 excludes 0 (period 1); the answer must be 2, not 1.
        var d = PeriodDetector.Detect(C(-1.0, 0.0), 0.05);
        Assert.NotEqual(1, d.Period);
    }

    [Fact]
    public void DiskOutsideTheSet_Escapes()
    {
        var d = PeriodDetector.Detect(C(1.0, 1.0), 0.1);
        Assert.False(d.Found);
        Assert.Equal(PeriodStop.Escaped, d.Stop);
    }

    [Fact]
    public void InteriorDiskWithoutNucleus_FindsNothing()
    {
        // Inside the main cardioid but away from its nucleus: the orbit settles
        // on an attracting fixed point ≠ 0, so no ball ever contains 0.
        var d = PeriodDetector.Detect(C(-0.2, 0.1), 0.02, maxPeriod: 5000);
        Assert.False(d.Found);
    }

    // Independent nucleus: the period-3 real nucleus is the real root of
    // f³_c(0)/c = c³ + 2c² + c + 1. Newton on that cubic in OD — no use of the
    // detector — gives it to ~120 digits.
    private static OD Period3RealNucleus()
    {
        OD c = new(-1.7548776662466927);
        for (int i = 0; i < 6; i++)
        {
            OD c2 = c * c;
            OD p = c2 * c + c2 * 2.0 + c + 1.0;
            OD dp = c2 * 3.0 + c * 4.0 + 1.0;
            c = c - p / dp;
        }
        return c;
    }

    [Fact]
    public void DeepDisk_ContainingNucleus_ReportsPeriod_OnOdPath()
    {
        var nucleus = new DeepComplex(Period3RealNucleus(), OD.Zero);
        var d = PeriodDetector.Detect(nucleus, 1e-60, maxPeriod: 3000);
        Assert.Equal(PeriodStop.Found, d.Stop);
        Assert.Equal(3, d.Period);
    }

    [Fact]
    public void DeepDisk_JustMissingNucleus_DoesNotReportIt()
    {
        // Same nucleus shifted by 1e-50 with a 1e-60 disk: the nucleus is
        // outside, so period 3 must NOT be reported. A double-precision orbit
        // (error ~1e-16) could not tell these apart; this pins the OD path.
        var shifted = new DeepComplex(Period3RealNucleus() + 1e-50, OD.Zero);
        var d = PeriodDetector.Detect(shifted, 1e-60, maxPeriod: 3000);
        Assert.NotEqual(3, d.Period);
    }

    [Fact]
    public void ShallowAndDeepPaths_Agree()
    {
        // Straddle DoublePathMinRadius on the same nucleus.
        var nucleus = new DeepComplex(Period3RealNucleus(), OD.Zero);
        var shallow = PeriodDetector.Detect(nucleus, PeriodDetector.DoublePathMinRadius * 10);
        var deep = PeriodDetector.Detect(nucleus, PeriodDetector.DoublePathMinRadius / 10);
        Assert.Equal(3, shallow.Period);
        Assert.Equal(3, deep.Period);
    }

    [Fact]
    public void ViewDiskRadius_IsHalfDiagonal()
    {
        // zoom 1, 1000×500: 3.5/1000 world units per px; half diagonal of 1000×500 px.
        double r = PeriodDetector.ViewDiskRadius(1.0, 1000, 500);
        double expected = 0.5 * (3.5 / 1000) * Math.Sqrt(1000.0 * 1000 + 500.0 * 500);
        Assert.Equal(expected, r, 12);
    }

    [Fact]
    public void Cancellation_IsHonoured()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PeriodDetector.Detect(C(-0.2, 0.1), 1e-20, maxPeriod: 10_000, ct: cts.Token));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidRadius_Throws(double radius)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PeriodDetector.Detect(C(0, 0), radius));
}
