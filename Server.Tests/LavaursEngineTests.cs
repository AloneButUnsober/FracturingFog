// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;
using FracturingFog.Abstractions.Animation;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>#918 SG1 — the Lavaurs <c>g_α</c> engine (production port of the S3/S4/S6 Fatou
/// numerics). Validates the coordinates, the attracting-side inverse, the Lavaurs map, Douady
/// re-injection, and the tabulated fast path against the direct engine.</summary>
public sealed class LavaursEngineTests
{
    static readonly LavaursEngine Eng = new LavaursEngine(iterations: 1500, deepOffset: 18.0);

    // germ f(w)=w+w² and backward map, for independent checks.
    static Complex F(Complex w) => w + w * w;

    [Fact]
    public void PhiAtt_SatisfiesAbelEquation()
    {
        // Φ_att(f(w)) − Φ_att(w) = 1 on attracting-basin points (the conjugacy equation).
        foreach (var w in new[] { new Complex(-0.3, 0.05), new Complex(-0.2, -0.1), new Complex(-0.45, 0.0), new Complex(-0.15, 0.12) })
        {
            var e = (Eng.PhiAtt(F(w)) - Eng.PhiAtt(w) - 1.0).Magnitude;
            Assert.True(e < 1e-8, $"Abel residual {e:E3} at w={w}");
        }
    }

    [Fact]
    public void PhiRepInv_RoundTripsThroughIndependentBackwardPhiRep()
    {
        // the inverse is built by forward f^k continuation; PhiRep here is computed by INDEPENDENT
        // backward iteration, so PhiRep(PhiRepInv(σ)) = σ is a genuine cross-construction check.
        foreach (var sigma in new[] { new Complex(2.8, 0.14), new Complex(5.0, -0.5), new Complex(1.5, 0.9), new Complex(8.0, 0.2) })
        {
            Assert.True(Eng.TryPhiRepInv(sigma, out Complex w), $"inverse failed at σ={sigma}");
            var back = Eng.PhiRep(w);
            Assert.True((back - sigma).Magnitude < 1e-6, $"round-trip {(back - sigma).Magnitude:E3} at σ={sigma}");
        }
    }

    [Fact]
    public void GAlpha_LavaursPeriodicity_And_DouadyReinjection()
    {
        // Lavaurs periodicity: g_{α+1} = f ∘ g_α (the functional-equation signature).
        foreach (var z in new[] { new Complex(0.2, 0.05), new Complex(0.3, -0.08) })
            foreach (double a in new[] { 0.2, 0.5, 0.75 })
            {
                Assert.True(Eng.TryGAlpha(z, a, out Complex ga));
                Assert.True(Eng.TryGAlpha(z, a + 1, out Complex ga1));
                var fga = ga * ga + LavaursEngine.C;              // f_c(g_α) in the z-plane
                Assert.True((ga1 - fga).Magnitude < 1e-6, $"periodicity {(ga1 - fga).Magnitude:E3} z={z} a={a}");
            }

        // Douady re-injection: under f_c alone an interior point crawls to z*=1/2; g_α re-injects it
        // away from the fixed point — the discontinuous enlargement that makes J(g_α) ⊋ J(f_c).
        var z0 = new Complex(0.2, 0.02);
        Complex zz = z0;
        for (int k = 0; k < 400; k++) zz = zz * zz + LavaursEngine.C;
        Assert.True((zz - LavaursEngine.FixedPoint).Magnitude < 0.05, "f_c orbit did not settle to z*");
        Assert.True(Eng.TryGAlpha(z0, 0.5, out Complex g));
        Assert.True((g - LavaursEngine.FixedPoint).Magnitude > 0.1, "g_α did not re-inject away from z*");
    }

    [Fact]
    public void TryGAlpha_OutsideBasin_ReturnsFalse_AndBasinPredicateAgrees()
    {
        // a clearly exterior point: g_α undefined (Φ_att diverges) → false.
        Assert.False(Eng.TryGAlpha(new Complex(2.0, 2.0), 0.5, out _));
        Assert.False(Eng.InAttractingBasin(new Complex(2.0, 2.0)));
        // an interior point: in the basin, g_α defined.
        Assert.True(Eng.InAttractingBasin(new Complex(0.2, 0.0)));
        Assert.True(Eng.TryGAlpha(new Complex(0.2, 0.0), 0.3, out _));
    }

    [Fact]
    public void CoordinateTable_ApproximatesDirectEngine()
    {
        // coarse table + a lighter engine (fast build) still tracks the direct g_α on interior
        // points; the production default grid (256²) is far finer. Validates the α-independent
        // precompute pipeline. Direct comparison uses the SAME light engine.
        var eng = new LavaursEngine(iterations: 500, deepOffset: 18.0);
        var table = new LavaursCoordinateTable(
            eng, attNx: 64, attNy: 64, invNx: 80, invNy: 80);
        int samples = 0; double maxErr = 0;
        for (double re = 0.05; re <= 0.45; re += 0.05)
            for (double im = -0.15; im <= 0.15; im += 0.05)
            {
                var z = new Complex(re, im);
                if (!eng.TryGAlpha(z, 0.5, out Complex direct)) continue;
                if (!table.TryGAlpha(z, 0.5, out Complex fast)) continue;
                maxErr = Math.Max(maxErr, (direct - fast).Magnitude);
                samples++;
            }
        Assert.True(samples >= 10, $"too few interior samples ({samples})");
        Assert.True(maxErr < 5e-2, $"table vs direct max err {maxErr:E3}");
    }
}
