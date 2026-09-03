// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Calculators;
using FracturingFog.Abstractions.Animation;

namespace FracturingFog.Server.Tests;

// #632 (Renderer C2) — precision-sweep convergence animation. Locks in the
// pieces that let the existing video/scene pipeline drive a tier-ladder sweep:
//   * the enum animation binding (Float→QD ladder) resolves + clamps,
//   * the PrecisionField type advertises its tier knobs to the editor/bus,
//   * the built-in "Precision convergence sweep" ships and targets the type,
//   * the actual convergence invariant — climbing the low tier toward a QD
//     reference monotonically dims the divergence field to black.
public class PrecisionFieldSweepTests
{
    // ── enum animation binding (the engine hook C2 needs) ──────────────────

    [Theory]
    [InlineData(0, PrecisionTier.Float)]
    [InlineData(1, PrecisionTier.Double)]
    [InlineData(2, PrecisionTier.DoubleDouble)]
    [InlineData(3, PrecisionTier.QuadDouble)]
    public void EnumTrack_Hold_LandsOnLadderMember(int index, PrecisionTier expected)
    {
        var fp = new FractalParameters { PrecisionLowTier = PrecisionTier.Double };
        var data = OneTrack("PrecisionLowTier", AnimationMode.Hold, index, index);

        var animators = data.ToAnimators(fp).ToList();
        Assert.Single(animators);
        animators[0].Tick(0.1);

        Assert.Equal(expected, fp.PrecisionLowTier);
    }

    [Theory]
    [InlineData(-5, PrecisionTier.Float)]        // below the ladder → floor
    [InlineData(99, PrecisionTier.QuadDouble)]   // above the ladder → ceiling
    public void EnumTrack_OutOfRange_ClampsToLadder(int index, PrecisionTier expected)
    {
        var fp = new FractalParameters();
        var data = OneTrack("PrecisionLowTier", AnimationMode.Hold, index, index);
        var anim = data.ToAnimators(fp).Single();
        anim.Tick(0.1);
        Assert.Equal(expected, fp.PrecisionLowTier);
    }

    [Fact]
    public void LinearRamp_SweepsAcrossMultipleTiers()
    {
        // A Linear (sawtooth) ramp 0→3 must visit more than one distinct tier
        // as phase advances — i.e. the enum actually moves, not stuck at Float.
        var fp = new FractalParameters();
        var data = OneTrack("PrecisionLowTier", AnimationMode.Linear, 0, 3, freqHz: 0.25);
        var anim = data.ToAnimators(fp).Single();

        var seen = new HashSet<PrecisionTier>();
        for (int i = 0; i < 40; i++)
        {
            anim.Tick(0.1);
            seen.Add(fp.PrecisionLowTier);
        }
        Assert.True(seen.Count >= 2, $"ramp only ever reached {seen.Count} tier(s)");
        Assert.Contains(PrecisionTier.QuadDouble, seen);
    }

    // ── registry + built-in preset ─────────────────────────────────────────

    [Fact]
    public void Map_Lists_Both_Tier_Params_As_Enum()
    {
        var d = FractalAnimatableParamsMap.For(FractalType.PrecisionField);
        Assert.Contains(d, x => x.ParamName == "PrecisionLowTier"
            && x.Kind == AnimatableParamKind.Enum
            && x.Cost == AnimatableParamCost.Expensive);
        Assert.Contains(d, x => x.ParamName == "PrecisionHighTier"
            && x.Kind == AnimatableParamKind.Enum);
    }

    [Fact]
    public void BuiltIn_Sweep_Ships_And_Targets_PrecisionField()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();   // merges the in-source built-in seeds
        var sweep = lib.GetByName("Precision convergence sweep");

        Assert.NotNull(sweep);
        Assert.Contains(FractalType.PrecisionField, sweep!.TargetFractalTypes);

        var low = sweep.Tracks.Single(t => t.ParamName == "PrecisionLowTier");
        Assert.Equal(AnimationMode.Linear, low.Mode);
        Assert.Equal(0, low.Min);
        Assert.Equal(3, low.Max);

        var high = sweep.Tracks.Single(t => t.ParamName == "PrecisionHighTier");
        Assert.Equal(AnimationMode.Hold, high.Mode);
        Assert.Equal(3, high.Min);   // pinned at QuadDouble

        // And it must materialise cleanly onto a PrecisionField target.
        var fp = new FractalParameters();
        Assert.Equal(2, sweep.ToAnimators(fp).Count());
    }

    // ── the convergence invariant (the actual "fractal image") ─────────────

    [Fact]
    public void Climbing_Low_Tier_Monotonically_Dims_The_Field()
    {
        // Deep enough that Float is inadequate; QuadDouble is the reference.
        // As the low tier climbs Float→Double→DD→QD, its outcome agrees with
        // the reference over more of the frame, so mean divergence must not
        // increase, and must reach zero when low == reference.
        double m0 = MeanDivergence(PrecisionTier.Float);
        double m1 = MeanDivergence(PrecisionTier.Double);
        double m2 = MeanDivergence(PrecisionTier.DoubleDouble);
        double m3 = MeanDivergence(PrecisionTier.QuadDouble);

        Assert.True(m0 >= m1, $"Float ({m0}) should be ≥ Double ({m1})");
        Assert.True(m1 >= m2, $"Double ({m1}) should be ≥ DD ({m2})");
        Assert.True(m2 >= m3, $"DD ({m2}) should be ≥ QD ({m3})");
        Assert.Equal(0.0, m3);          // reference vs itself = converged
        Assert.True(m0 > 0.0, "Float vs QD showed no divergence at deep zoom");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static double MeanDivergence(PrecisionTier low)
    {
        var c = new PrecisionFieldCalculator(48, 48)
        {
            CenterX = -0.743643887,
            CenterY = 0.131825904,
            Zoom = 50000.0,
            MaxIterations = 300,
            FractalParameters = new FractalParameters
            {
                PrecisionLowTier = low,
                PrecisionHighTier = PrecisionTier.QuadDouble,
            },
        };
        c.Calculate(default);
        double s = 0; foreach (float v in c.SmoothBuffer) s += v;
        return s / c.SmoothBuffer.Length;
    }

    private static AnimationData OneTrack(
        string param, AnimationMode mode, double min, double max, double freqHz = 0.1)
        => new()
        {
            Name = "probe",
            TargetFractalTypes = new List<FractalType> { FractalType.PrecisionField },
            Tracks = new List<AnimationTrack>
            {
                new() { ParamName = param, Mode = mode, Min = min, Max = max, FrequencyHz = freqHz, Enabled = true },
            },
        };
}
