// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1872 — the deep ray landing's speed-ups (DeepRayTuning) land where the plain path does:
//   • the trace's Newton substeps in double (where every value is a normal double) instead
//     of CFx;
//   • TryMisiurewicz skipping, in the wider reach windows, a divisor whose Newton ran out of
//     steps in the first (it creeps next to a cluster of roots and would do the same).
// On S1870's targets — M(4,1)'s angle tuned into the period-n antenna minibrot, and the
// antenna angle itself — the default and plain landings agree: same kind, same (k, p) /
// period, the point equal to within the landing's own precision floor (measured: equal),
// and the default path solves its substeps in double. (BigFloat's rounding rewrite is
// checked field for field in S1872BigFloatRoundingTests.)
// Mutants (scripted): caught — Round's tie test off by one, Round without ties-to-even,
// ToDouble rounding half up, ToDouble without the sticky bits, dc without its + 1 (the
// double path then always falls back: caught by the substep count), the reciprocal's sign.
// Survives — skipping every divisor after the first window, not only exhausted ones: on
// these views the landing divisor lands in the first window, so the two agree.

using System;
using System.Text;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1872DeepRayLandingSpeedTests(ITestOutputHelper output)
{
    private static ExternalAngle Tuned(int n)
    {
        string w0 = "0" + new string('1', n - 1), w1 = "1" + new string('0', n - 1);
        string Tune(string s) { var sb = new StringBuilder(); foreach (char ch in s) sb.Append(ch == '0' ? w0 : w1); return sb.ToString(); }
        return ExternalAngle.FromBits(Tune("001"), Tune("010"));   // M(4,1)'s .001(010), tuned
    }

    private static double DistLog2(ExactCentre a, ExactCentre b)
    {
        int prec = Math.Max(a.Precision, b.Precision);
        return new CFx(BigFloat.Sub(a.Re.WithPrecision(prec), b.Re.WithPrecision(prec), prec).ToFloatExp(),
                       BigFloat.Sub(a.Im.WithPrecision(prec), b.Im.WithPrecision(prec), prec).ToFloatExp()).Log2Abs();
    }

    [Fact]
    public void A_Tuned_Preperiodic_Landing_Is_The_Same_With_And_Without_The_Speed_Ups()
    {
        const int n = 40;
        var angle = Tuned(n);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var plain = DeepExternalRay.Land(angle, DeepRayTuning.Plain);
        double tPlain = sw.Elapsed.TotalSeconds; sw.Restart();
        var fast = DeepExternalRay.Land(angle, DeepRayTuning.Default);
        double tFast = sw.Elapsed.TotalSeconds;
        Assert.Equal(RayLandingStatus.Misiurewicz, plain.Status);
        Assert.Equal(RayLandingStatus.Misiurewicz, fast.Status);
        Assert.Equal(plain.Misiurewicz.Preperiod, fast.Misiurewicz.Preperiod);
        Assert.Equal(plain.Misiurewicz.Period, fast.Misiurewicz.Period);
        Assert.Equal(3 * n + 1, fast.Misiurewicz.Preperiod);
        int prec = Math.Min(plain.Misiurewicz.Point.Precision, fast.Misiurewicz.Point.Precision);
        double d = DistLog2(plain.Misiurewicz.Point, fast.Misiurewicz.Point);
        output.WriteLine($"n={n}: M({fast.Misiurewicz.Preperiod},{fast.Misiurewicz.Period}); plain {tPlain:F2} s, default {tFast:F2} s; points 2^{d:F1} apart (precision {prec})");
        Assert.True(d <= 1 - prec + 40, $"points 2^{d:F1} apart, precision {prec}");
        // The double substeps actually ran (a broken double path would fall back to CFx and
        // land the same, only slower): all of them at ~1e-46, none on the plain path.
        output.WriteLine($"substeps: default {fast.DoubleSubsteps} of {fast.Substeps} in double; plain {plain.DoubleSubsteps} of {plain.Substeps}");
        Assert.True(fast.Substeps > 100 && fast.DoubleSubsteps >= fast.Substeps * 99 / 100, $"{fast.DoubleSubsteps} of {fast.Substeps} in double");
        Assert.Equal(0, plain.DoubleSubsteps);
    }

    [Fact]
    public void A_Periodic_Landing_Is_The_Same_With_And_Without_The_Speed_Ups()
    {
        const int n = 60;
        var angle = ExternalAngle.FromBits("", "0" + new string('1', n - 1));
        var plain = DeepExternalRay.Land(angle, DeepRayTuning.Plain);
        var fast = DeepExternalRay.Land(angle, DeepRayTuning.Default);
        Assert.Equal(RayLandingStatus.Component, plain.Status);
        Assert.Equal(RayLandingStatus.Component, fast.Status);
        Assert.Equal(n, fast.Nucleus.Period);
        int prec = Math.Min(plain.Nucleus.Nucleus.Precision, fast.Nucleus.Nucleus.Precision);
        double d = DistLog2(plain.Nucleus.Nucleus, fast.Nucleus.Nucleus);
        output.WriteLine($"antenna n={n}: nuclei 2^{d:F1} apart (precision {prec})");
        Assert.True(d <= 1 - prec + 40, $"nuclei 2^{d:F1} apart, precision {prec}");
    }
}
