// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1873 (epic #1428, C7e) — DeepLocationAngles: the external angles of a minibrot or
// Misiurewicz point found past octuple-double. Expected answers from theory, not from FF:
//   • The period-n antenna minibrot next to −2 is the one the rays .(0 1ⁿ⁻¹) and .(1 0ⁿ⁻¹)
//     land on (its angle pair). At n = 125 (~1e-149) ComponentAngles returns exactly that
//     pair; at n = 30 it agrees with LocationAngles (OD).
//   • Its root is the image of the main cardioid's cusp 1/4 under c = nucleus + Size·c′: the
//     root offset is ~Size/4 (to 5% of the size).
//   • M(4,1)'s three angles .001(010), .001(100), .010(001), tuned into the period-n minibrot
//     (0 → 0 1ⁿ⁻¹, 1 → 1 0ⁿ⁻¹), are the angles of M(4,1)'s copy there: MisiurewiczAngles
//     returns exactly those three (n = 10; the same at n = 30 and 60 was measured, 48 s and
//     145 s, not in the suite).

using System;
using System.Linq;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1873DeepLocationAnglesTests
{
    private static ExternalAngle Periodic(string per) => ExternalAngle.FromBits("", per);

    private static (string W0, string W1) Pair(int n) => ("0" + new string('1', n - 1), "1" + new string('0', n - 1));

    private static string Tune(string s, int n)
    {
        var (w0, w1) = Pair(n);
        var sb = new System.Text.StringBuilder();
        foreach (char ch in s) sb.Append(ch == '0' ? w0 : w1);
        return sb.ToString();
    }

    [Fact]
    public void A_Deep_Minibrot_Gets_Its_Angle_Pair_And_Root()
    {
        const int n = 125;   // ~1e-149
        var (w0, w1) = Pair(n);
        var land = DeepExternalRay.Land(Periodic(w0));
        Assert.Equal(RayLandingStatus.Component, land.Status);
        var nucleus = land.Nucleus;

        var angles = DeepLocationAngles.ComponentAngles(nucleus);
        Assert.Equal(new[] { Periodic(w0), Periodic(w1) }, angles);

        // The root: the cusp 1/4 of the main cardioid, carried by the atom-size map.
        Assert.True(DeepLocationAngles.Root(nucleus.Nucleus, n, out var off));
        var quarter = nucleus.Size.Scale(0.25);
        double err = (off - quarter).Log2Abs() - nucleus.Size.Log2Abs();
        Assert.True(err < Math.Log2(0.05), $"root off Size/4 by 2^{err:F1} sizes");
    }

    [Fact]
    public void It_Agrees_With_The_OD_Angles_Where_OD_Holds()
    {
        const int n = 30;
        var (w0, _) = Pair(n);
        var deep = DeepLocationAngles.ComponentAngles(DeepExternalRay.Land(Periodic(w0)).Nucleus);
        var od = LocationAngles.ComponentAngles(ExternalRay.Land(Periodic(w0)).Nucleus);
        Assert.Equal(2, od.Count);
        Assert.Equal(od, deep);
    }

    [Fact]
    public void A_Tuned_Misiurewicz_Point_Gets_Its_Tuned_Angles()
    {
        const int n = 10;
        var land = DeepExternalRay.Land(ExternalAngle.FromBits(Tune("001", n), Tune("010", n)));
        Assert.Equal(RayLandingStatus.Misiurewicz, land.Status);

        var angles = DeepLocationAngles.MisiurewiczAngles(land.Misiurewicz, land.ContextRadiusLog10);
        var expected = new[] { ("001", "010"), ("001", "100"), ("010", "001") }
            .Select(a => ExternalAngle.FromBits(Tune(a.Item1, n), Tune(a.Item2, n))).ToHashSet();
        Assert.Equal(3, angles.Count);
        Assert.True(expected.SetEquals(angles), string.Join(", ", angles));
        // Ascending, as LocationAngles returns them.
        for (int i = 1; i < angles.Count; i++)
        {
            var (an, ad) = angles[i - 1].Fraction; var (bn, bd) = angles[i].Fraction;
            Assert.True(an * bd < bn * ad);
        }
    }
}
