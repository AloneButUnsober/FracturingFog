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

        // Period 50 000 wants 5 000 000: capped at the deepest tier's limit, flagged.
        var p = MinibrotJump.Plan(Found(1e-10, 50_000), QualityPreset.Standard, false, 0)!.Value;
        Assert.Equal(5_000_000, p.WantedIterations);
        Assert.Equal(MinibrotJump.MaxPreferredIterations, p.PreferredIterations);
        Assert.True(p.IterationsShort);
    }

    [Fact]
    public void LockedIterations_AreNotOverridden()
    {
        var p = MinibrotJump.Plan(Found(1e-6, 500), QualityPreset.Standard, iterLocked: true, lockedIterations: 1000)!.Value;
        Assert.Equal(0, p.PreferredIterations);
        Assert.True(p.IterationsShort);                              // 1000 < 100·500
    }
}
