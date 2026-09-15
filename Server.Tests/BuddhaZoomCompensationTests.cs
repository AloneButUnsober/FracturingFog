// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #837 (#65 point 4): auto zoom-detail compensation for the Buddhabrot family.
// A zoomed viewport catches far fewer of the fixed-domain sample orbits, so
// coverage collapses (dark/grainy). When enabled and zoomed past the threshold,
// the sampler scales the effective sample budget with zoom and auto-enables
// Metropolis importance sampling, so many more pixels get populated. Below the
// threshold it is a no-op — zoomed-out renders stay byte-identical whether the
// flag is on or off. Uses Buddhabrot (single-channel ColorMap), so a 0-hit
// pixel is exactly ColorMap.InSetColor and coverage = pixels != InSetColor.

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BuddhaZoomCompensationTests
{
    private const int W = 96, H = 72;

    private static FractalParameters Params(bool compensation) => new()
    {
        BuddhaSamples = 40_000,
        BuddhaIterLow = 50,
        BuddhaIterMid = 500,
        BuddhaIterHigh = 5_000,
        BuddhaSeed = 777,
        BuddhaQualityMode = BuddhaQualityMode.Standard,
        BuddhaMetropolis = false,
        BuddhaProgressive = false,
        BuddhaZoomCompensation = compensation,
    };

    private static uint[] Render(double zoom, bool compensation, out uint inSet)
    {
        IColorMap map = new HsvPalette();
        inSet = map.InSetColor;
        var c = new BuddhabrotCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = zoom, MaxIterations = 5_000,
            Quality = QualityPreset.Standard, ColorMap = map,
            FractalParameters = Params(compensation),
        };
        c.Calculate(default);
        return (uint[])c.ColorBuffer.Clone();
    }

    private static int Coverage(uint[] buf, uint inSet)
    {
        int n = 0;
        foreach (var p in buf) if (p != inSet) n++;
        return n;
    }

    [Fact]
    public void Below_Threshold_Compensation_Is_A_NoOp()
    {
        // Zoom 1.0 < threshold (1.2): the flag must not change the render.
        var off = Render(1.0, compensation: false, out _);
        var on  = Render(1.0, compensation: true,  out _);
        Assert.Equal(off, on);
    }

    [Fact]
    public void Zoomed_Compensation_Increases_Coverage()
    {
        var off = Render(3.0, compensation: false, out uint inSet);
        var on  = Render(3.0, compensation: true,  out _);

        int covOff = Coverage(off, inSet);
        int covOn  = Coverage(on,  inSet);

        Assert.True(covOff > 0, "sanity: uncompensated zoom still hits some pixels");
        Assert.True(covOn > covOff,
            $"compensation should populate more of the zoomed viewport (on={covOn}, off={covOff})");
    }
}
