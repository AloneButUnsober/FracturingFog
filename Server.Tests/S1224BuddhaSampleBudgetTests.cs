// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1224 — the Buddhabrot sample budget.
//   • BuddhaSamples is the budget for a 640×480 frame; with
//     BuddhaScaleSamplesWithWindow a larger frame samples proportionally more, so
//     the hit density does not fall as the window grows (never scaled down;
//     Metropolis is not window-scaled).
//   • Zoom compensation raises a uniform budget with zoom (window × zoom ≤ 64)
//     and turns Metropolis on only past zoom 100, where the float uniform sampler
//     stops; Dual Buddhabrot (no shared sampler) keeps the old 1.2 threshold
//     and ×8.
// The rules are checked on the calculators' own LastEffectiveSamples /
// LastCalculateUsedMetropolis, and the window rule by its purpose: equal hit
// density on a real render at two sizes.

using System;
using System.Linq;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1224BuddhaSampleBudgetTests
{
    private const int S = 10_000;

    private static FractalParameters P(bool window = true, bool zoomComp = true, bool mh = false) => new()
    {
        BuddhaSamples = S, BuddhaIterLow = 20, BuddhaIterMid = 200, BuddhaIterHigh = 2000,
        BuddhaScaleSamplesWithWindow = window, BuddhaZoomCompensation = zoomComp, BuddhaMetropolis = mh,
    };

    private static (int Samples, bool Mh) Budget(BuddhaFamilyCalculator c, double zoom, FractalParameters p)
    {
        c.Zoom = zoom; c.FractalParameters = p; c.MaxIterations = 2000;
        c.Calculate();
        return (c.LastEffectiveSamples, c.LastCalculateUsedMetropolis);
    }

    [Theory]
    [InlineData(640, 480, true, S)]          // the reference frame
    [InlineData(160, 120, true, S)]          // never scaled down
    [InlineData(1280, 960, true, 4 * S)]     // 4× the pixels
    [InlineData(1280, 960, false, S)]        // toggle off
    public void Window_Scales_The_Uniform_Budget(int w, int h, bool window, int want)
        => Assert.Equal((want, false), Budget(new BuddhabrotCalculator(w, h), 1.0, P(window)));

    [Fact]
    public void Metropolis_Is_Not_Window_Scaled()
        => Assert.Equal((S, true), Budget(new BuddhabrotCalculator(1280, 960), 1.0, P(mh: true)));

    [Theory]
    [InlineData(1.0, 1.0, false)]     // below the threshold: no-op
    [InlineData(6.0, 6.0, false)]     // uniform, ×zoom (was Metropolis before #1224)
    [InlineData(80.0, 64.0, false)]   // uniform, capped at ×64
    [InlineData(150.0, 8.0, true)]    // past zoom 100: Metropolis, ×8
    public void Zoom_Compensation_Stays_Uniform_Up_To_Zoom_100(double zoom, double factor, bool mh)
    {
        var c = new BuddhabrotCalculator(64, 48) { CenterX = -0.75, CenterY = 0.1 };
        Assert.Equal(((int)(S * factor), mh), Budget(c, zoom, P()));
    }

    [Fact]
    public void Window_And_Zoom_Share_The_64x_Cap()
    {
        // 1280×960 = 4× the pixels, so zoom adds at most ×16.
        var c = new BuddhabrotCalculator(1280, 960) { CenterX = -0.75, CenterY = 0.1 };
        Assert.Equal((64 * S, false), Budget(c, 80.0, P()));
    }

    [Fact]
    public void Dual_Buddhabrot_Keeps_Auto_Metropolis_Past_Zoom_1_2()
    {
        var c = new DualBuddhabrotCalculator(64, 48);
        Assert.Equal((3 * S, true), Budget(c, 3.0, P()));
    }

    // The window rule's purpose: the same view, four times the pixels, the same
    // hits per pixel (without it, a quarter).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Larger_Window_Keeps_The_Hit_Density(bool window)
    {
        double Density(int w, int h)
        {
            var c = new BuddhabrotCalculator(w, h)
            {
                CenterX = -0.5, MaxIterations = 2000,
                FractalParameters = new FractalParameters
                {
                    BuddhaSamples = 300_000, BuddhaIterLow = 20, BuddhaIterMid = 200, BuddhaIterHigh = 2000,
                    BuddhaScaleSamplesWithWindow = window,
                },
            };
            c.Calculate();
            long hits = c.HitsR.Sum(v => (long)v) + c.HitsG.Sum(v => (long)v) + c.HitsB.Sum(v => (long)v);
            return hits / (double)(w * h);
        }
        double ratio = Density(1280, 960) / Density(640, 480);
        TestContext.Current.TestOutputHelper?.WriteLine($"window={window}: density ratio {ratio:F3}");
        if (window) Assert.InRange(ratio, 0.8, 1.25);
        else Assert.InRange(ratio, 0.2, 0.31);
    }
}
