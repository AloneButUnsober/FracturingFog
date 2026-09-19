// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
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

    // ── #875 preset library ──────────────────────────────────────────────────

    private static double Dist(KleinianGenerator a, KleinianGenerator b)
        => Math.Sqrt((a.Cx - b.Cx) * (a.Cx - b.Cx)
                   + (a.Cy - b.Cy) * (a.Cy - b.Cy)
                   + (a.Cz - b.Cz) * (a.Cz - b.Cz));

    [Fact]
    public void Octahedral6_SixSpheres_AdjacentTangent()
    {
        var g = KleinianGroup.Octahedral6(1.0, 16);
        var gens = g.Generators;
        Assert.Equal(6, gens.Count);
        Assert.True(g.UniformRadius);
        double r = 1.0 / Math.Sqrt(2.0);
        Assert.Equal(r, g.SphereRadius, 12);
        // (+x) and (+y) are adjacent → centre distance == 2r (externally tangent).
        Assert.Equal(2.0 * r, Dist(gens[0], gens[2]), 12);
        // (+x) and (-x) are opposite → distance 2 > 2r (disjoint).
        Assert.True(Dist(gens[0], gens[1]) > 2.0 * r + 1e-9);
    }

    [Fact]
    public void CubeCorner8_EightSpheres_EdgeTangent()
    {
        var g = KleinianGroup.CubeCorner8(1.0, 16);
        var gens = g.Generators;
        Assert.Equal(8, gens.Count);
        Assert.Equal(1.0, g.SphereRadius, 12);
        // (+,+,+) and (+,+,-) share an edge → distance 2 == 2r.
        Assert.Equal(2.0, Dist(gens[0], gens[1]), 12);
        // (+,+,+) and (-,-,-) are body-diagonal → 2√3 > 2r.
        Assert.True(Dist(gens[0], gens[7]) > 2.0 + 1e-9);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    public void Necklace_NeighboursTangent_AndPlanar(int n)
    {
        var g = KleinianGroup.Necklace(1.0, n, 16);
        var gens = g.Generators;
        Assert.Equal(n, gens.Count);
        double r = Math.Sin(Math.PI / n);
        Assert.Equal(r, g.SphereRadius, 12);
        for (int k = 0; k < n; k++)
        {
            Assert.Equal(0.0, gens[k].Cz, 12);                 // planar (z = 0)
            var next = gens[(k + 1) % n];
            Assert.Equal(2.0 * r, Dist(gens[k], next), 12);    // neighbour tangent
        }
    }

    [Fact]
    public void Necklace_ClampsCountToValidRange()
    {
        Assert.Equal(3, KleinianGroup.Necklace(1.0, 1, 16).Generators.Count);
        Assert.Equal(24, KleinianGroup.Necklace(1.0, 999, 16).Generators.Count);
    }

    [Theory]
    [InlineData(KleinianPreset.Tetrahedral, 4)]
    [InlineData(KleinianPreset.Octahedral6, 6)]
    [InlineData(KleinianPreset.CubeCorner8, 8)]
    public void FromPreset_DispatchesToCorrectFactory(KleinianPreset preset, int count)
        => Assert.Equal(count, KleinianGroup.FromPreset(preset, 1.0, 6, 16).Generators.Count);

    [Theory]
    [InlineData(KleinianPreset.Octahedral6)]
    [InlineData(KleinianPreset.CubeCorner8)]
    [InlineData(KleinianPreset.NecklaceN)]
    public void Calculator_EachPreset_RendersStructure(KleinianPreset preset)
    {
        var calc = new KleinianCalculator(96, 72)
        {
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { KleinianPreset = preset, KleinianNecklaceCount = 8 },
        };
        calc.Calculate(default);
        int distinct = calc.ColorBuffer.Distinct().Count();
        Assert.True(distinct > 4, $"{preset}: expected varied output, got {distinct}");
    }

    // ── #876 custom sphere list ───────────────────────────────────────────────

    [Fact]
    public void FromSpheres_BuildsGroupFromList()
    {
        var spheres = new List<KleinianSphereDef>
        {
            new(1, 0, 0, 1.0),
            new(-1, 0, 0, 1.5),
            new(0, 2, 0, 0.5),
        };
        var g = KleinianGroup.FromSpheres(spheres, 16);
        Assert.Equal(3, g.Generators.Count);
        Assert.False(g.UniformRadius);
        Assert.Equal(1.5, g.Generators[1].R);
    }

    [Fact]
    public void FromSpheres_EmptyOrNull_FallsBackToTetrahedral()
    {
        Assert.Equal(4, KleinianGroup.FromSpheres(new List<KleinianSphereDef>(), 16).Generators.Count);
        Assert.Equal(4, KleinianGroup.FromSpheres(null, 16).Generators.Count);
    }

    [Fact]
    public void ToSphereDefs_RoundTripsAPreset()
    {
        var g = KleinianGroup.Octahedral6(1.0, 16);
        var defs = g.ToSphereDefs();
        var g2 = KleinianGroup.FromSpheres(defs, 16);
        Assert.Equal(g.Generators.Count, g2.Generators.Count);
        for (int i = 0; i < g.Generators.Count; i++)
        {
            Assert.Equal(g.Generators[i].Cx, g2.Generators[i].Cx);
            Assert.Equal(g.Generators[i].R, g2.Generators[i].R);
        }
    }

    [Fact]
    public void Clone_DeepCopiesCustomSpheres()
    {
        var p = new FractalParameters();
        p.KleinianCustomSpheres.Add(new KleinianSphereDef(1, 2, 3, 4));
        var q = p.Clone();
        q.KleinianCustomSpheres[0].R = 99;
        Assert.Equal(4, p.KleinianCustomSpheres[0].R); // original unchanged
    }

    [Fact]
    public void Calculator_CustomPreset_RendersStructure()
    {
        var p = new FractalParameters { KleinianPreset = KleinianPreset.Custom };
        // A tetrahedral-like custom group authored by hand.
        double r = Math.Sqrt(2.0);
        p.KleinianCustomSpheres.AddRange(new[]
        {
            new KleinianSphereDef(1, 1, 1, r),
            new KleinianSphereDef(1, -1, -1, r),
            new KleinianSphereDef(-1, 1, -1, r),
            new KleinianSphereDef(-1, -1, 1, r),
        });
        var calc = new KleinianCalculator(96, 72) { ColorMap = new HsvPalette(), FractalParameters = p };
        calc.Calculate(default);
        Assert.True(calc.ColorBuffer.Distinct().Count() > 4);
    }

    [Fact]
    public void Region_RoundTrips_CustomPresetAndSpheres()
    {
        var src = new FractalParameters { KleinianPreset = KleinianPreset.Custom };
        src.KleinianCustomSpheres.AddRange(new[]
        {
            new KleinianSphereDef(1, 2, 3, 4),
            new KleinianSphereDef(-1, -2, -3, 0.5),
        });
        var snap = RegionFractalParams.Snapshot(FractalType.Kleinian, src);
        Assert.NotNull(snap);

        var dst = new FractalParameters();
        snap!.ApplyTo(dst);
        Assert.Equal(KleinianPreset.Custom, dst.KleinianPreset);
        Assert.Equal(2, dst.KleinianCustomSpheres.Count);
        Assert.Equal(4.0, dst.KleinianCustomSpheres[0].R);
        Assert.Equal(-3.0, dst.KleinianCustomSpheres[1].Cz);
    }

    // ── #877 rotation fold ────────────────────────────────────────────────────

    [Fact]
    public void KleinianRotation_ZeroAngle_IsNone_NonZero_IsNormalized()
    {
        Assert.False(new KleinianRotation(0.0, 0, 1, 0).Has);
        Assert.False(KleinianRotation.None.Has);
        var r = new KleinianRotation(Math.PI / 4, 0, 2, 0);   // axis not unit
        Assert.True(r.Has);
        Assert.Equal(1.0, Math.Sqrt(r.Ax * r.Ax + r.Ay * r.Ay + r.Az * r.Az), 12);
    }

    [Fact]
    public void WithRotation_Zero_ReturnsSame_NonZero_SetsFlag()
    {
        var g = KleinianGroup.Tetrahedral(1.0, 16);
        Assert.Same(g, g.WithRotation(KleinianRotation.None));
        var gr = g.WithRotation(new KleinianRotation(0.5, 0, 1, 0));
        Assert.True(gr.HasRotation);
        Assert.False(g.HasRotation);
    }

    [Fact]
    public void KleinianDE_NoneRotation_MatchesNoRotOverload()
    {
        var gens = KleinianGroup.Tetrahedral(1.0, 16).ToArray();
        var none = KleinianRotation.None;
        var rng = new Random(99);
        for (int i = 0; i < 2000; i++)
        {
            double x = rng.NextDouble() * 6 - 3, y = rng.NextDouble() * 6 - 3, z = rng.NextDouble() * 6 - 3;
            Assert.Equal(
                KleinianCalculator.KleinianDE(x, y, z, gens, 16),
                KleinianCalculator.KleinianDE(x, y, z, gens, 16, in none));
        }
    }

    [Fact]
    public void Calculator_Rotation_ChangesOutput()
    {
        KleinianCalculator Render(double angle)
        {
            var c = new KleinianCalculator(96, 72)
            {
                ColorMap = new HsvPalette(),
                FractalParameters = new FractalParameters { KleinianRotationAngle = angle },
            };
            c.Calculate(default);
            return c;
        }
        var a = Render(0.0);
        var b = Render(35.0);
        Assert.False(a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer)); // rotation is visible
        Assert.True(b.ColorBuffer.Distinct().Count() > 4);                 // still structured
    }

    [Fact]
    public void Region_RoundTrips_Rotation()
    {
        var src = new FractalParameters
        {
            KleinianRotationAngle = 42.0,
            KleinianRotationAxisX = 1.0, KleinianRotationAxisY = 0.0, KleinianRotationAxisZ = 0.5,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.Kleinian, src);
        Assert.NotNull(snap);
        var dst = new FractalParameters();
        snap!.ApplyTo(dst);
        Assert.Equal(42.0, dst.KleinianRotationAngle);
        Assert.Equal(1.0, dst.KleinianRotationAxisX);
        Assert.Equal(0.5, dst.KleinianRotationAxisZ);
    }

    // ── #878 colour drivers (word length / last generator) ────────────────────

    [Fact]
    public void KleinianWord_ReportsDepthAndGenerator()
    {
        var gens = KleinianGroup.Tetrahedral(1.0, 16).ToArray();
        var none = KleinianRotation.None;
        // A point inside a sphere (near, not exactly at, its centre) → descent runs.
        KleinianCalculator.KleinianWord(0.8, 0.8, 0.8, gens, 16, in none, out int depth, out int lastGen);
        Assert.True(depth > 0);
        Assert.InRange(lastGen, 0, 3);
        // A far point escapes immediately → no descent.
        KleinianCalculator.KleinianWord(10, 10, 10, gens, 16, in none, out int d2, out int g2);
        Assert.Equal(0, d2);
        Assert.Equal(-1, g2);
    }

    private static KleinianCalculator RenderKlein(KleinianColorSource src, int iter = 16)
    {
        var c = new KleinianCalculator(96, 72)
        {
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { KleinianColorSource = src, KleinianIterations = iter },
        };
        c.Calculate(default);
        return c;
    }

    [Theory]
    [InlineData(KleinianColorSource.WordLength)]
    [InlineData(KleinianColorSource.LastGenerator)]
    public void Calculator_ColorSource_RendersStructure_AndDiffersFromSmooth(KleinianColorSource src)
    {
        var wordish = RenderKlein(src);
        var smooth = RenderKlein(KleinianColorSource.Smooth);
        Assert.True(wordish.ColorBuffer.Distinct().Count() > 4);
        Assert.False(wordish.ColorBuffer.AsSpan().SequenceEqual(smooth.ColorBuffer));
    }

    [Fact]
    public void WordLengthColour_RespondsToInversionIterations()  // the #53 answer
    {
        var lo = RenderKlein(KleinianColorSource.WordLength, iter: 6);
        var hi = RenderKlein(KleinianColorSource.WordLength, iter: 40);
        Assert.False(lo.ColorBuffer.AsSpan().SequenceEqual(hi.ColorBuffer));
    }

    [Fact]
    public void Region_RoundTrips_ColorSource()
    {
        var src = new FractalParameters { KleinianColorSource = KleinianColorSource.LastGenerator };
        var snap = RegionFractalParams.Snapshot(FractalType.Kleinian, src);
        Assert.NotNull(snap);
        var dst = new FractalParameters();
        snap!.ApplyTo(dst);
        Assert.Equal(KleinianColorSource.LastGenerator, dst.KleinianColorSource);
    }

    // ── #881 DE relaxation factor ─────────────────────────────────────────────

    private static KleinianCalculator RenderDeFactor(double f)
    {
        var c = new KleinianCalculator(96, 72)
        {
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { KleinianDeFactor = f },
        };
        c.Calculate(default);
        return c;
    }

    [Fact]
    public void DeFactor_One_IsDefault_And_HalfChangesOutput()
    {
        var full = RenderDeFactor(1.0);
        var half = RenderDeFactor(0.5);
        Assert.True(full.ColorBuffer.Distinct().Count() > 4);
        Assert.False(full.ColorBuffer.AsSpan().SequenceEqual(half.ColorBuffer)); // under-relaxation is visible
    }

    [Fact]
    public void DeFactor_ClampedAboveOne_EqualsOne()
    {
        var clamped = RenderDeFactor(5.0);   // clamped to 1.0 in the calculator
        var one = RenderDeFactor(1.0);
        Assert.True(clamped.ColorBuffer.AsSpan().SequenceEqual(one.ColorBuffer));
    }

    [Fact]
    public void Region_RoundTrips_DeFactor()
    {
        var src = new FractalParameters { KleinianDeFactor = 0.4 };
        var snap = RegionFractalParams.Snapshot(FractalType.Kleinian, src);
        Assert.NotNull(snap);
        var dst = new FractalParameters();
        snap!.ApplyTo(dst);
        Assert.Equal(0.4, dst.KleinianDeFactor);
        // Default 1.0 is omitted from the snapshot.
        var snapDefault = RegionFractalParams.Snapshot(FractalType.Kleinian, new FractalParameters());
        Assert.Null(snapDefault!.KleinianDeFactor);
    }
}
