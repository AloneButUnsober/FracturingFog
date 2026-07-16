// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using System.Linq;

using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Animation Roadmap Phase 6: the pure animated-param ceiling policy. Covers
/// the hardware-derived default ceiling under controlled fake-hardware inputs
/// and the over-ceiling survivor selection (cost-first, then declaration
/// order).
/// </summary>
public sealed class AnimatedParamCeilingPolicyTests
{
    // ── DefaultCeiling ─────────────────────────────────────────────────────

    [Fact]
    public void DefaultCeiling_TwoDOnly_IsGenerous()
    {
        var hw = new HardwareProfile(LogicalCores: 4, DiscreteGpu: false);
        Assert.Equal(AnimatedParamCeilingPolicy.TwoDCeiling,
            AnimatedParamCeilingPolicy.DefaultCeiling(hw, includesRaymarched3D: false));
    }

    [Fact]
    public void DefaultCeiling_ThreeD_IntegratedGpu_IsTight()
    {
        var hw = new HardwareProfile(LogicalCores: 8, DiscreteGpu: false);
        Assert.Equal(AnimatedParamCeilingPolicy.ThreeDIntegratedGpuCeiling,
            AnimatedParamCeilingPolicy.DefaultCeiling(hw, includesRaymarched3D: true));
    }

    [Fact]
    public void DefaultCeiling_ThreeD_DiscreteGpu_IsBumped()
    {
        var hw = new HardwareProfile(LogicalCores: 16, DiscreteGpu: true);
        Assert.Equal(AnimatedParamCeilingPolicy.ThreeDDiscreteGpuCeiling,
            AnimatedParamCeilingPolicy.DefaultCeiling(hw, includesRaymarched3D: true));
    }

    [Fact]
    public void DefaultCeiling_DiscreteGpu_DoesNotRaise2DCeiling()
    {
        // A 2D-only leg ignores GPU class — the ceiling is bounded by the
        // param work, not the frame cost.
        var iGpu = new HardwareProfile(4, DiscreteGpu: false);
        var dGpu = new HardwareProfile(4, DiscreteGpu: true);
        Assert.Equal(
            AnimatedParamCeilingPolicy.DefaultCeiling(iGpu, false),
            AnimatedParamCeilingPolicy.DefaultCeiling(dGpu, false));
    }

    [Fact]
    public void Detect_ReportsAtLeastOneCoreAndConservativeGpu()
    {
        var hw = HardwareProfile.Detect();
        Assert.True(hw.LogicalCores >= 1);
        Assert.False(hw.DiscreteGpu); // conservative default until shell wires the real signal
    }

    // ── SelectActive ───────────────────────────────────────────────────────

    [Fact]
    public void SelectActive_WithinCeiling_KeepsAll()
    {
        var costs = new List<AnimatableParamCost>
        {
            AnimatableParamCost.Cheap, AnimatableParamCost.Moderate,
        };
        var keep = AnimatedParamCeilingPolicy.SelectActive(costs, ceiling: 4);
        Assert.Equal(new[] { true, true }, keep);
    }

    [Fact]
    public void SelectActive_ZeroCeiling_IsUnlimited()
    {
        var costs = new List<AnimatableParamCost>
        {
            AnimatableParamCost.Expensive, AnimatableParamCost.Expensive,
            AnimatableParamCost.Expensive,
        };
        var keep = AnimatedParamCeilingPolicy.SelectActive(costs, ceiling: 0);
        Assert.Equal(new[] { true, true, true }, keep);
    }

    [Fact]
    public void SelectActive_DropsMostExpensiveFirst()
    {
        // Ceiling 2 → drop 2 of 4. Expensive (idx 1, 3) go first regardless
        // of position; the two Cheap survive.
        var costs = new List<AnimatableParamCost>
        {
            AnimatableParamCost.Cheap,     // 0
            AnimatableParamCost.Expensive, // 1
            AnimatableParamCost.Cheap,     // 2
            AnimatableParamCost.Expensive, // 3
        };
        var keep = AnimatedParamCeilingPolicy.SelectActive(costs, ceiling: 2);
        Assert.Equal(new[] { true, false, true, false }, keep);
    }

    [Fact]
    public void SelectActive_TiesBreakByDeclarationOrder()
    {
        // All equal cost, ceiling 2 → keep the first two declared, drop the
        // later ones (later index dropped first).
        var costs = new List<AnimatableParamCost>
        {
            AnimatableParamCost.Moderate, AnimatableParamCost.Moderate,
            AnimatableParamCost.Moderate, AnimatableParamCost.Moderate,
        };
        var keep = AnimatedParamCeilingPolicy.SelectActive(costs, ceiling: 2);
        Assert.Equal(new[] { true, true, false, false }, keep);
    }

    [Fact]
    public void SelectActive_MixedCosts_KeepsCheapestFrontLoaded()
    {
        // Ceiling 3 of 5: drop the single Expensive, then the later Moderate.
        var costs = new List<AnimatableParamCost>
        {
            AnimatableParamCost.Cheap,     // 0 keep
            AnimatableParamCost.Moderate,  // 1 keep (earlier moderate)
            AnimatableParamCost.Moderate,  // 2 drop (later moderate)
            AnimatableParamCost.Expensive, // 3 drop (most expensive)
            AnimatableParamCost.Cheap,     // 4 keep
        };
        var keep = AnimatedParamCeilingPolicy.SelectActive(costs, ceiling: 3);
        Assert.Equal(new[] { true, true, false, false, true }, keep);
        Assert.Equal(3, System.Array.FindAll(keep, k => k).Length);
    }

    // ── ToAnimators cost resolution ────────────────────────────────────────

    [Fact]
    public void ToAnimators_ResolvesCostFromMap()
    {
        // BulbPower on Mandelbulb is Moderate (3D raymarched) in the map;
        // JuliaC on Julia is Cheap. The animator's Cost must reflect the map.
        var mandelbulb = new AnimationData
        {
            TargetFractalTypes = { FractalType.Mandelbulb },
            Tracks = { new AnimationTrack { ParamName = "BulbPower", Min = 2, Max = 8 } },
        };
        var bulbAnim = mandelbulb.ToAnimators(new FractalParameters()).Single();
        Assert.Equal(AnimatableParamCost.Moderate, bulbAnim.Cost);

        var julia = new AnimationData
        {
            TargetFractalTypes = { FractalType.Julia },
            Tracks = { new AnimationTrack { ParamName = "JuliaC", Min = 0.1, Max = 1.0 } },
        };
        var juliaAnim = julia.ToAnimators(new FractalParameters()).Single();
        Assert.Equal(AnimatableParamCost.Cheap, juliaAnim.Cost);
    }
}
