// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1861 (epic #1428, C9) — the Unbounded quality tier: zoom past 1e100. Checked:
//   • The tier is the deepest (last in All, Deepest, Get / FromName round-trip), has no zoom
//     limit, and its iteration formula continues Extreme's (the same count at 1e100, then
//     2048 + 1024 per decade past it, up to the explore ceiling).
//   • The wheel zooms past 1e100: from Extreme just under its cap, wheel steps promote the
//     view to Unbounded (with the slow-render warning) and carry it on past 1e100, and past
//     double's range (log zoom, C3) with no clamp.
//   • The Command builder reported a view past 1e100, or with an exact centre, as a fidelity
//     gap pointing at C10 (#1860); since C10 batch reproduces them, so neither is a gap.

using System;
using System.Collections.Generic;
using FracturingFog.Cli;
using FracturingFog.FFMath;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1861UnboundedTierTests
{
    [Fact]
    public void Unbounded_Is_The_Deepest_Tier_And_Continues_Extremes_Formula()
    {
        var u = QualityPreset.Unbounded;
        Assert.Same(u, QualityPreset.All[^1]);
        Assert.Same(u, QualityPreset.Deepest);
        Assert.Same(u, QualityPreset.Get(QualityTier.Unbounded));
        Assert.Same(u, QualityPreset.FromName("Unbounded"));
        Assert.True(QualityTier.Unbounded > QualityTier.Extreme);
        Assert.Equal(double.PositiveInfinity, u.ZoomMax);
        Assert.Equal(QualityPreset.Extreme.ComputeIterations(1e100), u.ComputeIterations(1e100));
        Assert.Equal(2048 + 1024 * 300, u.ComputeIterationsLog10(300));
        Assert.Equal(2048 + 1024 * 1000, u.ComputeIterationsLog10(1000));
        Assert.Equal(IterationLimits.ExploreCeiling, u.ComputeIterationsLog10(1e7));
    }

    [Fact]
    public void The_Wheel_Promotes_Past_1e100_And_Keeps_Going_Past_Double()
    {
        var vs = new FractalViewState { FractalType = FractalType.Mandelbrot, Quality = QualityPreset.Extreme };
        vs.SetCenter(new DeepComplex(new OD(-0.7436438870371587, 0, 0, 0, 0, 0, 0, 0), new OD(0.1318259042053119, 0, 0, 0, 0, 0, 0, 0)));
        vs.Zoom = 0.99e100;
        var input = new FractalInputController(vs);
        var messages = new List<string>();
        input.StatusRequested += (_, m) => messages.Add(m.Text);
        var wheel = new WheelInput(400, 300, 800, 600, +120, InputModifiers.None);

        input.OnWheel(wheel);
        input.OnWheel(wheel);
        Assert.Same(QualityPreset.Unbounded, vs.Quality);
        Assert.True(vs.Zoom > 1e100, $"{vs.Zoom:G4}");   // Extreme would have clamped at 1e100
        Assert.Contains(messages, m => m.Contains("Unbounded") && m.Contains("slow"));

        // On past double's range: the log carries the zoom (C3), the tier never clamps it.
        vs.SetZoomLog10(308);
        for (int i = 0; i < 40; i++) input.OnWheel(wheel);
        Assert.True(vs.ZoomBeyondDouble);
        Assert.True(vs.ZoomLog10 > 308.5, $"{vs.ZoomLog10}");
        Assert.Same(QualityPreset.Unbounded, vs.Quality);
    }

    [Fact]
    public void The_Command_Builder_Reported_Deep_Views_As_A_Gap_Until_C10()
    {
        // #1860 (C10) — batch reproduces them now: no gap for a deep zoom or an exact centre.
        var shallow = new BatchCommandSnapshot { Zoom = 1e40, QualityName = "Extreme" };
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(shallow), g => g.Contains("#1860"));

        var deep = new BatchCommandSnapshot { Zoom = 1e150, QualityName = "Unbounded" };
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(deep), g => g.Contains("#1860") || g.Contains("1e100"));

        var pastDouble = new BatchCommandSnapshot { Zoom = double.MaxValue, ZoomLog10 = 500, QualityName = "Unbounded" };
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(pastDouble), g => g.Contains("#1860") || g.Contains("1e500"));

        var exact = new BatchCommandSnapshot { Zoom = 1e60, CenterExactX = "0.1", CenterExactY = "0.2" };
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(exact), g => g.Contains("exact centre"));
    }
}
