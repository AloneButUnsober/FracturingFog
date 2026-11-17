// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Numerics;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

// Interesting-location finder S2 (#1186): the jump plan — framing, quality
// promotion (never demotion), iteration hint, depth cap.
public sealed class MinibrotJumpTests
{
    private static NucleusResult Found(double sizeMag, int period, double re = -1.75)
        => new(NucleusStatus.Found, new DeepComplex(re, 0.0), period, new Complex(sizeMag, 0), 5, period);

    [Fact]
    public void ShallowMinibrot_KeepsTierAndHintsIterations()
    {
        var plan = MinibrotJump.Plan(Found(0.02, 3), QualityPreset.Standard, iterLocked: false, lockedIterations: 0);
        Assert.NotNull(plan);
        var p = plan!.Value;
        Assert.Equal(50.0, p.Zoom, 9);
        Assert.Same(QualityPreset.Standard, p.Quality);
        Assert.Equal(System.Math.Max(QualityPreset.Standard.ComputeIterations(50.0), 300), p.PreferredIterations);  // 100·3
        Assert.False(p.IterationsShort);
        // Framing: centre = nucleus + size·(−0.5).
        Assert.Equal(-1.75 - 0.01, p.Center.Re.X0, 12);
    }

    [Fact]
    public void DeepMinibrot_PromotesToTheFirstTierThatReachesIt()
    {
        var p = MinibrotJump.Plan(Found(1e-20, 200), QualityPreset.Standard, false, 0)!.Value;
        Assert.Equal(1e20, p.Zoom, 1e6);
        Assert.Same(QualityPreset.High, p.Quality);                 // Standard 1e13 < 1e20 ≤ High 1e22
    }

    [Fact]
    public void NeverDemotesAUserChosenTier()
    {
        var p = MinibrotJump.Plan(Found(0.02, 3), QualityPreset.Extreme, false, 0)!.Value;
        Assert.Same(QualityPreset.Extreme, p.Quality);
    }

    [Fact]
    public void BeyondTheDeepestTier_IsRefused()
        => Assert.Null(MinibrotJump.Plan(Found(1e-105, 3), QualityPreset.Standard, false, 0));

    [Fact]
    public void HighPeriod_RaisesTheHint_AndFlagsACap()
    {
        // Period 500 wants 50 000 > Standard's 2048 cap: the hint still asks for it.
        var mid = MinibrotJump.Plan(Found(1e-10, 500), QualityPreset.Standard, false, 0)!.Value;
        Assert.Equal(50_000, mid.PreferredIterations);
        Assert.False(mid.IterationsShort);

        // Period 50 000 wants 5 000 000. #1858 (#1426): past every tier's cap (Extreme's is
        // 131 072) the hint still asks for it, flagged as past the tier's cap.
        var p = MinibrotJump.Plan(Found(1e-10, 50_000), QualityPreset.Standard, false, 0)!.Value;
        Assert.Equal(5_000_000, p.WantedIterations);
        Assert.Equal(5_000_000, p.PreferredIterations);
        Assert.False(p.IterationsShort);
        Assert.True(p.BeyondTierCap);

        // Period 1 000 000 wants 100 000 000: capped at the explore ceiling (50 M), flagged short.
        var q = MinibrotJump.Plan(Found(1e-10, 1_000_000), QualityPreset.Standard, false, 0)!.Value;
        Assert.Equal(100_000_000, q.WantedIterations);
        Assert.Equal(IterationLimits.ExploreCeiling, q.PreferredIterations);
        Assert.Equal(MinibrotJump.MaxPreferredIterations, q.PreferredIterations);
        Assert.True(q.IterationsShort);
    }

    [Fact]
    public void Past_Extremes_Cap_The_Plan_Follows_The_Period_And_Says_So()
    {
        // #1858 — period 2 000 at Extreme depth wants 200 000, past its 131 072: before #1858
        // the plan stopped at 131 072 and the render came out short (#1426).
        var p = MinibrotJump.Plan(Found(1e-60, 2_000), QualityPreset.Extreme, false, 0)!.Value;
        Assert.Same(QualityPreset.Extreme, p.Quality);
        Assert.Equal(200_000, p.PreferredIterations);
        Assert.False(p.IterationsShort);
        Assert.True(p.BeyondTierCap);

        // A low period inside the tier's formula: the zoom's own count, not flagged.
        var low = MinibrotJump.Plan(Found(1e-60, 10), QualityPreset.Extreme, false, 0)!.Value;
        Assert.Equal(QualityPreset.Extreme.ComputeIterations(low.Zoom), low.PreferredIterations);
        Assert.False(low.BeyondTierCap);
        Assert.False(low.IterationsShort);
    }

    [Fact]
    public void LockedIterations_AreNotOverridden()
    {
        var p = MinibrotJump.Plan(Found(1e-6, 500), QualityPreset.Standard, iterLocked: true, lockedIterations: 1000)!.Value;
        Assert.Equal(0, p.PreferredIterations);
        Assert.True(p.IterationsShort);                              // 1000 < 100·500
    }
}
