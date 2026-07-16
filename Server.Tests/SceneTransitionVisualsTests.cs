// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap Phase S8: the bespoke transition visuals' pure cores —
/// the light-sweep wipe weight (SceneTransitions.LightSweepWeight) and the
/// ParamMorph fractal-param interpolation (SceneParamMorph). The pixel /
/// render wiring lives in the Engine's SceneVideoRenderer (integration surface).
/// </summary>
public sealed class SceneTransitionVisualsTests
{
    // ── Light-sweep wipe ──────────────────────────────────────────────────────

    [Fact]
    public void LightSweep_is_all_outgoing_at_blend_zero_and_all_incoming_at_one()
    {
        for (double u = 0; u <= 1.0; u += 0.1)
        {
            Assert.Equal(0.0, SceneTransitions.LightSweepWeight(u, 0.0), precision: 9);
            Assert.Equal(1.0, SceneTransitions.LightSweepWeight(u, 1.0), precision: 9);
        }
    }

    [Fact]
    public void LightSweep_leads_on_the_left_edge()
    {
        // Mid-progress: the left edge (small u) is further into the incoming
        // shot than the right edge — a left→right wipe.
        double left  = SceneTransitions.LightSweepWeight(0.0, 0.5);
        double right = SceneTransitions.LightSweepWeight(1.0, 0.5);
        Assert.True(left > right);
    }

    [Fact]
    public void LightSweep_weight_rises_monotonically_with_blend()
    {
        double prev = -1;
        for (double blend = 0; blend <= 1.0; blend += 0.1)
        {
            double w = SceneTransitions.LightSweepWeight(0.5, blend);
            Assert.True(w >= prev - 1e-9);
            Assert.InRange(w, 0.0, 1.0);
            prev = w;
        }
    }

    // ── ParamMorph interpolation ──────────────────────────────────────────────

    [Fact]
    public void ParamMorph_lerps_double_knobs_between_endpoints()
    {
        var from = new FractalParameters { BulbPower = 2.0, MandelboxScale = 1.0 };
        var to   = new FractalParameters { BulbPower = 10.0, MandelboxScale = 3.0 };

        var mid = SceneParamMorph.Lerp(from, to, 0.5);
        Assert.Equal(6.0, mid.BulbPower, precision: 9);       // (2+10)/2
        Assert.Equal(2.0, mid.MandelboxScale, precision: 9);  // (1+3)/2

        var atStart = SceneParamMorph.Lerp(from, to, 0.0);
        Assert.Equal(2.0, atStart.BulbPower, precision: 9);

        var atEnd = SceneParamMorph.Lerp(from, to, 1.0);
        Assert.Equal(10.0, atEnd.BulbPower, precision: 9);
    }

    [Fact]
    public void ParamMorph_leaves_non_double_state_as_the_incoming_shot()
    {
        var from = new FractalParameters { PlasmaSeed = 111, FlamePresetName = "A", BulbPower = 2.0 };
        var to   = new FractalParameters { PlasmaSeed = 999, FlamePresetName = "B", BulbPower = 8.0 };

        var mid = SceneParamMorph.Lerp(from, to, 0.5);
        // Non-double state comes from the incoming (to) shot unchanged.
        Assert.Equal(999, mid.PlasmaSeed);
        Assert.Equal("B", mid.FlamePresetName);
        // Double knobs still blend.
        Assert.Equal(5.0, mid.BulbPower, precision: 9);
    }

    [Fact]
    public void ParamMorph_does_not_mutate_the_endpoints()
    {
        var from = new FractalParameters { BulbPower = 2.0 };
        var to   = new FractalParameters { BulbPower = 8.0 };

        _ = SceneParamMorph.Lerp(from, to, 0.5);

        Assert.Equal(2.0, from.BulbPower, precision: 9);
        Assert.Equal(8.0, to.BulbPower, precision: 9);
    }
}
