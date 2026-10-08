// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

// Interesting-location finder S5 (#1189): Julia-morph steps.
public sealed class JuliaMorphTests
{
    private static NucleusResult Found(double sizeMag, int period, double re = -1.75, double im = 0)
        => new(NucleusStatus.Found, new DeepComplex(re, im), period, new Complex(sizeMag, 0), 5, period);

    [Fact]
    public void Step_LandsAtTheLogMidpoint_CentredOnTheNucleus()
    {
        var s = JuliaMorph.Plan(Found(1e-8, 40), currentZoom: 1e2, QualityPreset.Standard, false, 0);
        Assert.True(s.Ok);
        Assert.Equal(1e8, s.MinibrotZoom, 1);
        Assert.Equal(1e5, s.Plan.Zoom, 1e-6);                 // √(1e2 · 1e8)
        Assert.Equal(-1.75, s.Plan.Center.Re.X0, 15);         // nucleus, not the framed centre
        Assert.Equal(4000, s.Plan.PreferredIterations);        // 100 × period
    }

    [Theory]
    [InlineData(1.0, 1e8)]       // α = 1 lands on the minibrot's own framing
    [InlineData(0.25, 3162.2776601683795)]   // 10^3.5
    public void Depth_InterpolatesInLogZoom(double depth, double expected)
    {
        var s = JuliaMorph.Plan(Found(1e-8, 40), 1e2, QualityPreset.Standard, false, 0, depth);
        Assert.Equal(Math.Log10(expected), Math.Log10(s.Plan.Zoom), 9);
    }

    [Fact]
    public void TargetNotDeeper_IsRefused()
        => Assert.Equal(JuliaMorphRefusal.NotDeeper,
                        JuliaMorph.Plan(Found(1e-2, 3), 1e4, QualityPreset.Standard, false, 0).Refusal);

    [Fact]
    public void PastTheDeepestTier_IsRefused()
        => Assert.Equal(JuliaMorphRefusal.TooDeep,
                        JuliaMorph.Plan(Found(1e-230, 3), 1e20, QualityPreset.Standard, false, 0).Refusal);

    [Fact]
    public void ThreeStepMorphChain_StaysNested_AndVerified()
    {
        // Start in a dense minibrot field (built-in region "Lakes and
        // Rivers"), then morph three times, each time aiming at a point
        // 30 % / 20 % of the view off-centre. The invariants (not the shapes)
        // are what must hold: every target minibrot lies inside the view it
        // was picked from, zoom strictly grows, and the S1 ball scan confirms
        // each period independently of Newton.
        var centre = new DeepComplex(-0.7444389447199996, -0.10814196367335771);
        double zoom = 121739.57374223076;
        const int W = 1920, H = 1080;

        for (int step = 0; step < 3; step++)
        {
            double r = PeriodDetector.ViewDiskRadius(zoom, W, H);
            var target = centre.Translate(0.3 * r, 0.2 * r);
            var q = NucleusFinder.FindMinibrot(target, 0.25 * r);
            Assert.True(q.Found, $"step {step}: no minibrot near the target");

            double off = Math.Sqrt(Math.Pow(OdExact.Sub(q.Nucleus.Re, centre.Re).X0, 2)
                                 + Math.Pow(OdExact.Sub(q.Nucleus.Im, centre.Im).X0, 2));
            Assert.True(off <= r, $"step {step}: target minibrot outside the view");
            Assert.Equal(q.Period, PeriodDetector.Detect(q.Nucleus, 0.01 * q.Size.Magnitude).Period);

            var s = JuliaMorph.Plan(q, zoom, QualityPreset.Extreme, false, 0);
            Assert.True(s.Ok, $"step {step}: {s.Refusal}");
            Assert.True(s.Plan.Zoom > zoom);
            (centre, zoom) = (s.Plan.Center, s.Plan.Zoom);
        }
    }
}
