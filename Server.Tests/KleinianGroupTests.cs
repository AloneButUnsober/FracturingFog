// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// Kleinian 3D generalization — slice S1 (#874, epic #850 / design #855).
// See Docs/Technical/Kleinian-Generalization-DesignPlan.md.
//
// The generalized distance estimator now loops over a KleinianGroup's generator
// list instead of a hard-coded 4-sphere array. These tests lock Tier 0 — the
// tetrahedral preset must be byte-identical to the pre-#874 algorithm — plus the
// descriptor's factory + flags, and that the calculator still renders structure.
public class KleinianGroupTests
{
    // The exact pre-#874 tetrahedral DE, inlined as the byte-identical baseline.
    private static double LegacyKleinianDe(double px, double py, double pz, double scaleK, int iter)
    {
        double r = Math.Sqrt(2.0) * scaleK;
        double[] cx = { +scaleK, +scaleK, -scaleK, -scaleK };
        double[] cy = { +scaleK, -scaleK, +scaleK, -scaleK };
        double[] cz = { +scaleK, -scaleK, -scaleK, +scaleK };
        double r2 = r * r, scale = 1.0;
        const int n = 4;
        for (int i = 0; i < iter; i++)
        {
            int bestK = -1; double bestDeep = 0.0;
            for (int k = 0; k < n; k++)
            {
                double dx = px - cx[k], dy = py - cy[k], dz = pz - cz[k];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - r;
                if (d < bestDeep) { bestDeep = d; bestK = k; }
            }
            if (bestK < 0) break;
            double ex = px - cx[bestK], ey = py - cy[bestK], ez = pz - cz[bestK];
            double e2 = ex * ex + ey * ey + ez * ez;
            if (e2 < 1e-30) break;
            double f = r2 / e2; scale *= f;
            px = cx[bestK] + ex * f; py = cy[bestK] + ey * f; pz = cz[bestK] + ez * f;
        }
        double nearest = double.PositiveInfinity;
        for (int k = 0; k < n; k++)
        {
            double dx = px - cx[k], dy = py - cy[k], dz = pz - cz[k];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - r;
            double a = Math.Abs(d);
            if (a < nearest) nearest = a;
        }
        if (scale < 1e-30) return 0.0;
        return nearest / scale;
    }

    // Legacy tetrahedral orbit-trap, inlined as the byte-identical baseline.
    private static double LegacyKleinianTrap(double px, double py, double pz, double scaleK, int iter)
    {
        double r = Math.Sqrt(2.0) * scaleK;
        double[] cx = { +scaleK, +scaleK, -scaleK, -scaleK };
        double[] cy = { +scaleK, -scaleK, +scaleK, -scaleK };
        double[] cz = { +scaleK, -scaleK, -scaleK, +scaleK };
        double r2 = r * r;
        const int n = 4;
        double minBoundary = double.MaxValue;
        for (int i = 0; i < iter; i++)
        {
            int bestK = -1; double bestDeep = 0.0;
            for (int k = 0; k < n; k++)
            {
                double dx = px - cx[k], dy = py - cy[k], dz = pz - cz[k];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz) - r;
                double a = Math.Abs(d);
                if (a < minBoundary) minBoundary = a;
                if (d < bestDeep) { bestDeep = d; bestK = k; }
            }
            if (bestK < 0) break;
            double ex = px - cx[bestK], ey = py - cy[bestK], ez = pz - cz[bestK];
            double e2 = ex * ex + ey * ey + ez * ez;
            if (e2 < 1e-30) break;
            double f = r2 / e2;
            px = cx[bestK] + ex * f; py = cy[bestK] + ey * f; pz = cz[bestK] + ez * f;
        }
        return Math.Clamp(minBoundary / Math.Max(r, 1e-9), 0.0, 1.0);
    }

    [Fact]
    public void Tetrahedral_FactoryMatchesLegacyCentresAndFlags()
    {
        var g = KleinianGroup.Tetrahedral(1.0, 16);
        var gens = g.Generators;
        Assert.Equal(4, gens.Count);
        double r = Math.Sqrt(2.0);
        var expect = new[]
        {
            (+1.0, +1.0, +1.0), (+1.0, -1.0, -1.0),
            (-1.0, +1.0, -1.0), (-1.0, -1.0, +1.0),
        };
        for (int k = 0; k < 4; k++)
        {
            Assert.Equal(KleinianGeneratorKind.Inversion, gens[k].Kind);
            Assert.Equal(expect[k].Item1, gens[k].Cx);
            Assert.Equal(expect[k].Item2, gens[k].Cy);
            Assert.Equal(expect[k].Item3, gens[k].Cz);
            Assert.Equal(r, gens[k].R);
        }
        Assert.True(g.AllInversions);
        Assert.True(g.UniformRadius);
        Assert.Equal(r, g.SphereRadius);
        Assert.Equal(16, g.MaxWordLength);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.7)]
    [InlineData(1.35)]
    public void GeneralizedDe_ByteIdentical_ToLegacyTetrahedral(double scaleK)
    {
        var gens = KleinianGroup.Tetrahedral(scaleK, 16).ToArray();
        var rng = new Random(20260919);
        for (int i = 0; i < 4000; i++)
        {
            double x = rng.NextDouble() * 6.0 - 3.0;
            double y = rng.NextDouble() * 6.0 - 3.0;
            double z = rng.NextDouble() * 6.0 - 3.0;
            double got = KleinianCalculator.KleinianDE(x, y, z, gens, 16);
            double exp = LegacyKleinianDe(x, y, z, scaleK, 16);
            Assert.Equal(exp, got); // exact — same ops, same order
        }
    }

    [Fact]
    public void GeneralizedTrap_ByteIdentical_ToLegacyTetrahedral()
    {
        var gens = KleinianGroup.Tetrahedral(1.0, 16).ToArray();
        var rng = new Random(7);
        for (int i = 0; i < 4000; i++)
        {
            double x = rng.NextDouble() * 6.0 - 3.0;
            double y = rng.NextDouble() * 6.0 - 3.0;
            double z = rng.NextDouble() * 6.0 - 3.0;
            double got = KleinianCalculator.KleinianTrap(x, y, z, gens, 16);
            double exp = LegacyKleinianTrap(x, y, z, 1.0, 16);
            Assert.Equal(exp, got);
        }
    }

    [Fact]
    public void GroupFlags_NonUniformRadius_DisablesUniformFlag()
    {
        var gens = new[]
        {
            KleinianGenerator.Inversion(1, 0, 0, 1.0),
            KleinianGenerator.Inversion(-1, 0, 0, 1.5),
        };
        var g = new KleinianGroup(gens, 16);
        Assert.True(g.AllInversions);
        Assert.False(g.UniformRadius);
    }

    // ── Calculator still renders structure (CPU path) ─────────────────────────

    private static KleinianCalculator RenderDefault()
    {
        var calc = new KleinianCalculator(96, 72)
        {
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters(),
        };
        calc.Calculate(default);
        return calc;
    }

    [Fact]
    public void Calculator_Default_ProducesStructure_NotFlat()
    {
        var calc = RenderDefault();
        int distinct = calc.ColorBuffer.Distinct().Count();
        Assert.True(distinct > 4, $"expected varied output, got {distinct} colours");
    }

    [Fact]
    public void Calculator_SameParams_AreDeterministic()
    {
        var a = RenderDefault();
        var b = RenderDefault();
        Assert.True(a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer));
    }
}
