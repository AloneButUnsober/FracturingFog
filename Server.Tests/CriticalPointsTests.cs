// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;

using FracturingFog.Abstractions.Explore;
using FracturingFog.Cli;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

// #1242 — critical points of a User Equation and the z₀ seed they set.
// Expected points are solved by hand (f′(z) = 0); the independent check is
// dynamical: with a critical z₀ the nuclei the general finder accepts are
// SUPERattracting (multiplier 0), because the cycle passes through z₀.
public sealed class CriticalPointsTests
{
    private static Complex[] Points(string src, Complex c, out CriticalPoint[] all)
    {
        var found = CriticalPoints.Find(SandboxExpression.Parse(src), c, out var why);
        Assert.True(found != null, why);
        all = found!.ToArray();
        return all.Select(p => p.Z).ToArray();
    }

    private static void Has(Complex[] zs, Complex expected)
        => Assert.True(zs.Any(z => (z - expected).Magnitude < 1e-10), $"{expected} not in [{string.Join(", ", zs)}]");

    [Theory]
    [InlineData("z^2 + c", 1)]
    [InlineData("z^5 + c", 4)]
    [InlineData("z*z*z*z*z*z*z*z + c", 7)]
    [InlineData("pow(z, 3) + c", 2)]
    public void Monomial_HasTheMultipleCriticalPointZero(string src, int multiplicity)
    {
        var zs = Points(src, new Complex(-0.3, 0.2), out var all);
        Assert.Single(zs);
        Assert.Equal(Complex.Zero, zs[0]);           // snapped exactly
        Assert.Equal(multiplicity, all[0].Multiplicity);
        Assert.False(all[0].DependsOnC);
        Assert.Equal("0", all[0].SeedText);
    }

    [Fact]
    public void Cubic_MinusThreeZ_HasPlusMinusOne()
    {
        // f′ = 3z² − 3.
        var zs = Points("z^3 - 3*z + c", new Complex(0.1, 0.4), out var all);
        Assert.Equal(2, zs.Length);
        Has(zs, 1);
        Has(zs, -1);
        Assert.All(all, p => Assert.False(p.DependsOnC));
        Assert.Contains(all, p => p.SeedText == "1");
        Assert.Contains(all, p => p.SeedText == "-1");
    }

    [Fact]
    public void Quartic_DoubleWell_HasZeroAndPlusMinusOne()
    {
        // f′ = 4z³ − 4z = 4z(z − 1)(z + 1).
        var zs = Points("z^4 - 2*z^2 + c", new Complex(-0.5, 0), out _);
        Assert.Equal(3, zs.Length);
        Has(zs, 0); Has(zs, 1); Has(zs, -1);
    }

    [Fact]
    public void ComplexCriticalPoints_FormatAsDsl()
    {
        // f′ = 3z² + 3 → z = ±i.
        var zs = Points("z^3 + 3*z + c", Complex.Zero, out var all);
        Has(zs, Complex.ImaginaryOne);
        Has(zs, -Complex.ImaginaryOne);
        Assert.Contains(all, p => p.SeedText == "1*i");
        Assert.Contains(all, p => p.SeedText == "-1*i");
        Assert.Equal("-0.5 + 0.25*i", CriticalPoints.Format(new Complex(-0.5, 0.25)));
        Assert.Equal("2 - 1.5*i", CriticalPoints.Format(new Complex(2, -1.5)));
        // The seed text parses back to the same value.
        var back = SandboxExpression.Parse("-0.5 + 0.25*i").EvalStep(Complex.Zero, Complex.Zero, 0, SandboxExpression.Parse("-0.5 + 0.25*i").NewEnv());
        Assert.Equal(new Complex(-0.5, 0.25), back);
    }

    [Fact]
    public void ParameterDependentPoints_AreFlagged()
    {
        // z³ + c·z: f′ = 3z² + c → z = ±√(−c/3); at c = −0.75, ±0.5.
        var zs = Points("z^3 + c*z", new Complex(-0.75, 0), out var all);
        Has(zs, 0.5);
        Has(zs, -0.5);
        Assert.All(all, p => Assert.True(p.DependsOnC));
    }

    [Fact]
    public void NoCriticalPoints_ForExp()
        => Assert.Empty(Points("exp(z) + c", Complex.Zero, out _));

    [Fact]
    public void NonHolomorphic_IsRefused()
    {
        Assert.Null(CriticalPoints.Find(SandboxExpression.Parse("conj(z)^2 + c"), Complex.Zero, out var why));
        Assert.Contains("holomorphic", why);
    }

    [Fact]
    public void DualDerivative_OfPowerAtZero_IsFinite()
    {
        // d/dz z^3 at 0 was 0·∞ = NaN through the log form.
        var e = SandboxExpression.Parse("z^3 + c");
        var (f, df) = e.EvalStepDz(Complex.Zero, new Complex(0.25, 0), 0, e.NewDualEnv());
        Assert.Equal(new Complex(0.25, 0), f);
        Assert.Equal(Complex.Zero, df);
        var lin = SandboxExpression.Parse("z^1 + c");
        Assert.Equal(Complex.One, lin.EvalStepDz(Complex.Zero, Complex.Zero, 0, lin.NewDualEnv()).dfdz);
    }

    // Independent check: a critical seed makes the found nuclei superattracting.
    [Theory]
    [InlineData("1", 3.0)]     // z₁ = 1 − 3 + c = 1 → c = 3
    [InlineData("-1", -3.0)]   // z₁ = −1 + 3 + c = −1 → c = −3
    public void CriticalSeed_GivesSuperattractingNuclei(string seed, double nucleus)
    {
        var step = SandboxExpression.Parse("z^3 - 3*z + c");
        var map = new UserEquationMap(step, SandboxExpression.Parse(seed), 0, "z^3 - 3z + c");
        var n = GeneralFinder.FindNucleus(map, new V2(nucleus + 0.01, 0.005), 1, 0.1);
        Assert.True(n.HasValue);
        Assert.True(Math.Abs(n!.Value.Point.X - nucleus) < 1e-10 && Math.Abs(n.Value.Point.Y) < 1e-10);
        Assert.True(n.Value.CycleRadius < 1e-6, $"multiplier {n.Value.CycleRadius}");

        // A non-critical seed: the period-1 component exists but its centre is
        // only attracting, never superattracting.
        var off = new UserEquationMap(step, SandboxExpression.Parse("0.5"), 0, "z^3 - 3z + c");
        var m = GeneralFinder.FindMinibrot(off, new V2(0, 0), 4);
        if (m.Found) Assert.True(m.CycleRadius > 1e-3, $"non-critical seed gave multiplier {m.CycleRadius}");
    }

    // ── Batch parity: the seed rides in the region snapshot (--param). ─────

    [Fact]
    public void Seed_RoundTripsThroughParam()
    {
        var p = new FractalParameters { UserEquationSeed = "-0.5 + 0.25*i" };
        var snap = RegionFractalParams.Snapshot(FractalType.UserEquation, p);
        Assert.NotNull(snap);
        var kv = snap!.ToKeyValues();
        Assert.Contains(kv, k => k.Key == "UserEquationSeed" && k.Value == "-0.5 + 0.25*i");
        var back = RegionFractalParams.FromKeyValues(kv, FractalType.UserEquation, out var err);
        Assert.True(back != null, err);
        var q = new FractalParameters();
        back!.ApplyTo(q);
        Assert.Equal("-0.5 + 0.25*i", q.UserEquationSeed);

        // Blank seed (z₀ = 0) is not emitted, so a saved equation's seed stands.
        var blank = RegionFractalParams.Snapshot(FractalType.UserEquation, new FractalParameters());
        Assert.DoesNotContain(blank?.ToKeyValues() ?? Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>(),
                              k => k.Key == "UserEquationSeed");
    }

    [Fact]
    public void CommandBuilder_CarriesTheSeedWithTheEquation()
    {
        // #1249 — with the source inline and the seed as --param, no gap remains.
        var p = new FractalParameters { UserEquationSource = "z^3 - 3*z + c", UserEquationSeed = "1" };
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.UserEquation, EquationSource = p.UserEquationSource, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.UserEquation, p)!.ToKeyValues(),
        };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Contains("--equation-source", cmd);
        Assert.Contains("UserEquationSeed=1", cmd);
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(snap), g => g.Contains("equation"));
    }
}
