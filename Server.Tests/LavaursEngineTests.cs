// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;
using FracturingFog.Abstractions.Animation;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>#918 SG1 / #934 — the Lavaurs <c>g_α</c> engine. The two <b>independent</b> checks
/// lead: brute-force Lavaurs limit (<c>f_c^k → g_α</c>) and real symmetry
/// (<c>G_α(z̄) = conj G_α(z)</c>). Abel, the inverse round-trip and <c>g_{α+1} = f∘g_α</c> are
/// self-consistency checks — they passed on the wrong (<c>−iπ</c>-shifted) sheet before #934, so
/// they are kept only as regressions, never as proof of correctness.</summary>
public sealed class LavaursEngineTests
{
    static readonly LavaursEngine Eng = new LavaursEngine(iterations: 1500, deepOffset: 18.0);

    // germ f(w)=w+w², for independent checks.
    static Complex F(Complex w) => w + w * w;

    [Fact]
    public void GAlpha_MatchesBruteForceLavaursLimit()
    {
        // Lavaurs' theorem: for c = 1/4 + ε², f_c^k → g_α on the parabolic basin with
        // α = k − π/ε − 1 (engine convention g_α = f∘Φ_rep⁻¹∘T_α∘Φ_att). Iterate f_c directly —
        // no Fatou coordinates — and compare wherever the limit is moderate. Error is O(ε).
        const double eps = 0.004;
        var c = new Complex(0.25 + eps * eps, 0);
        int k = (int)Math.Round(Math.PI / eps);
        double alpha = k - Math.PI / eps - 1.0;
        int samples = 0; double worst = 0;
        for (double re = -0.40; re <= 0.46; re += 0.08)
            for (double im = -0.40; im <= 0.40; im += 0.08)
            {
                var z = new Complex(re, im);
                if (!Eng.InAttractingBasin(z)) continue;
                Complex d = z;
                for (int i = 0; i < k && d.Magnitude < 1e3; i++) d = d * d + c;
                if (!(d.Magnitude < 3.0)) continue;            // escaped / huge: not comparable
                Assert.True(Eng.TryGAlpha(z, alpha, out Complex g), $"g_α undefined at z={z} (limit {d})");
                double rel = (g - d).Magnitude / Math.Max(1.0, d.Magnitude);
                worst = Math.Max(worst, rel);
                samples++;
            }
        Assert.True(samples >= 20, $"too few comparable points ({samples})");
        Assert.True(worst < 8 * eps, $"g_α vs f_c^k worst relative error {worst:E3}");
    }

    [Fact]
    public void GAlpha_IsRealSymmetric()
    {
        // f_c has real coefficients, so for real α every defined value satisfies
        // G_α(z̄) = conj G_α(z). The pre-#934 [0,2π) branch failed this on 0/654 pairs.
        int pairs = 0;
        for (double re = -0.45; re <= 0.47; re += 0.06)
            for (double im = 0.02; im <= 0.44; im += 0.06)
            {
                var z = new Complex(re, im);
                if (!Eng.InAttractingBasin(z)) continue;
                bool a = Eng.TryGAlpha(z, 0.5, out Complex g1);
                bool b = Eng.TryGAlpha(Complex.Conjugate(z), 0.5, out Complex g2);
                Assert.Equal(a, b);
                if (!a) continue;
                double e = (g2 - Complex.Conjugate(g1)).Magnitude / Math.Max(1.0, g1.Magnitude);
                Assert.True(e < 1e-9, $"asymmetry {e:E3} at z={z}: {g1} vs {g2}");
                pairs++;
            }
        Assert.True(pairs >= 40, $"too few defined pairs ({pairs})");
    }

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
    public void PhiRepInv_RoundTripsThroughBackwardPhiRep()
    {
        // Regression (self-consistency): Φ_rep(Φ_rep⁻¹(σ)) = σ, with Φ_rep by backward iteration.
        // Only σ whose inverse the principal backward branch can retrace are meaningful here.
        foreach (var sigma in new[] { new Complex(2.8, 0.14), new Complex(1.5, 0.9), new Complex(-3.0, 1.2), new Complex(0.5, -2.0), new Complex(-1.0, -0.5) })
        {
            Assert.True(Eng.TryPhiRepInv(sigma, out Complex w), $"inverse failed at σ={sigma}");
            var back = Eng.PhiRep(w);
            Assert.True((back - sigma).Magnitude < 1e-6, $"round-trip {(back - sigma).Magnitude:E3} at σ={sigma}");
        }
    }

    [Fact]
    public void GAlpha_LavaursPeriodicity_And_DouadyReinjection()
    {
        // Regression (structural): g_{α+1} = f ∘ g_α.
        foreach (var z in new[] { new Complex(-0.4, 0.08), new Complex(-0.36, -0.12) })
            foreach (double a in new[] { 0.2, 0.5, 0.75 })
            {
                Assert.True(Eng.TryGAlpha(z, a, out Complex ga));
                Assert.True(Eng.TryGAlpha(z, a + 1, out Complex ga1));
                var fga = ga * ga + LavaursEngine.C;              // f_c(g_α) in the z-plane
                Assert.True((ga1 - fga).Magnitude < 1e-9, $"periodicity {(ga1 - fga).Magnitude:E3} z={z} a={a}");
            }

        // Douady re-injection: under f_c alone the point crawls to z* = 1/2; g_α re-injects it away.
        var z0 = new Complex(-0.36, -0.12);
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
        Assert.True(Eng.InAttractingBasin(new Complex(-0.36, -0.12)));
        Assert.True(Eng.TryGAlpha(new Complex(-0.36, -0.12), 0.3, out _));
    }

    [Fact]
    public void CoordinateTable_ApproximatesDirectEngine()
    {
        // coarse table + a lighter engine (fast build) tracks the direct g_α wherever the value is
        // moderate (|g| ≤ 3; beyond that the point is escaping and bilinear interpolation of a
        // fast-growing Φ_rep⁻¹ is not meaningful). The production grid (256²) is far finer.
        var eng = new LavaursEngine(iterations: 500, deepOffset: 18.0);
        var table = new LavaursCoordinateTable(
            eng, attNx: 64, attNy: 64, invNx: 80, invNy: 80);
        int samples = 0; double maxErr = 0;
        for (double re = -0.45; re <= 0.45; re += 0.05)
            for (double im = -0.40; im <= 0.40; im += 0.05)
            {
                var z = new Complex(re, im);
                if (!eng.TryGAlpha(z, 0.5, out Complex direct) || direct.Magnitude > 3.0) continue;
                if (!table.TryGAlpha(z, 0.5, out Complex fast)) continue;
                maxErr = Math.Max(maxErr, (direct - fast).Magnitude);
                samples++;
            }
        Assert.True(samples >= 20, $"too few moderate samples ({samples})");
        Assert.True(maxErr < 0.15, $"table vs direct max err {maxErr:E3}");
    }
}
