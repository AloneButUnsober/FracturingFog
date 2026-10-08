// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1222 — a Buddhabrot frame's CPU cost grew with the window: per-thread full-frame
// histograms, a serial merge and a serial composite. They are now one atomic
// histogram (shared sampler), a block-parallel merge (classic / Metropolis) and a
// block-parallel composite. These check what the rework must keep, at a frame large
// enough for several pixel blocks:
//   • the composite and the relief height field equal the formula, pixel for pixel,
//     from the hit arrays (both colour modes);
//   • progressive classic rendering adds each batch once (the per-thread locals are
//     cleared between batches): its hit total matches a single pass of as many
//     samples, not the 2.5× or more a cumulative merge would give.
// The shared sampler's atomic split is pinned by S838's single-thread equality.

using System;
using System.Linq;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1222BuddhaWindowScalingTests
{
    private const int W = 400, H = 300;   // 120 000 px: several composite blocks

    // Each type forces its composite (#836): Nebulabrot the bands, Buddhabrot the colour map.
    private static BuddhaFamilyCalculator Calc(BuddhaColorMode mode)
    {
        BuddhaFamilyCalculator c = mode == BuddhaColorMode.NebulabrotBands
            ? new NebulabrotCalculator(W, H) : new BuddhabrotCalculator(W, H);
        c.CenterX = -0.5;
        c.MaxIterations = 2000;
        c.FractalParameters = new FractalParameters
        {
            BuddhaSamples = 200_000, BuddhaIterLow = 20, BuddhaIterMid = 200, BuddhaIterHigh = 2000,
            BuddhaColorMode = mode, BuddhaZoomCompensation = false,
        };
        return c;
    }

    private static double Inv(uint max) => max > 1 ? 1.0 / Math.Log(max + 1) : 1.0;

    [Fact]
    public void Band_Composite_And_Height_Field_Equal_The_Formula()
    {
        var c = Calc(BuddhaColorMode.NebulabrotBands);
        c.Calculate();
        uint mR = c.HitsR.Max(), mG = c.HitsG.Max(), mB = c.HitsB.Max();
        uint mT = Enumerable.Range(0, W * H).Max(i => c.HitsR[i] + c.HitsG[i] + c.HitsB[i]);
        Assert.True(mT > 0);
        for (int i = 0; i < W * H; i++)
        {
            byte r = (byte)Math.Clamp(Math.Log(c.HitsR[i] + 1) * Inv(mR) * 255, 0, 255);
            byte g = (byte)Math.Clamp(Math.Log(c.HitsG[i] + 1) * Inv(mG) * 255, 0, 255);
            byte b = (byte)Math.Clamp(Math.Log(c.HitsB[i] + 1) * Inv(mB) * 255, 0, 255);
            Assert.Equal(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b, c.ColorBuffer[i]);
            uint sum = c.HitsR[i] + c.HitsG[i] + c.HitsB[i];
            float h = sum == 0 ? 0f : (float)(Math.Log(sum + 1.0) * (1.0 / Math.Log(mT + 1.0)));
            Assert.Equal(h, c.SmoothBuffer[i]);
        }
    }

    [Fact]
    public void ColorMap_Composite_Equals_The_Formula()
    {
        var c = Calc(BuddhaColorMode.ColorMap);
        c.Calculate();
        uint mT = Enumerable.Range(0, W * H).Max(i => c.HitsR[i] + c.HitsG[i] + c.HitsB[i]);
        double inv = 1.0 / Math.Log(mT + 1.0);
        int iters = c.MaxIterations;
        uint bg = c.ColorMap.InSetColor;
        for (int i = 0; i < W * H; i++)
        {
            uint sum = c.HitsR[i] + c.HitsG[i] + c.HitsB[i];
            uint want = bg;
            if (sum > 0)
            {
                double norm = Math.Log(sum + 1.0) * inv, a = norm * norm;
                uint f = unchecked((uint)c.ColorMap.Map((float)((1.0 - norm) * iters), 0f, iters));
                byte Mix(int sh) => (byte)(((f >> sh) & 0xFF) * a + ((bg >> sh) & 0xFF) * (1.0 - a));
                want = 0xFF000000u | ((uint)Mix(16) << 16) | ((uint)Mix(8) << 8) | Mix(0);
            }
            Assert.Equal(want, c.ColorBuffer[i]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Progressive_Classic_Render_Adds_Each_Batch_Once(bool metropolis)
    {
        long Total(bool progressive)
        {
            var c = Calc(BuddhaColorMode.NebulabrotBands);
            c.FractalParameters.BuddhaMetropolis = metropolis;
            c.FractalParameters.BuddhaSamples = metropolis ? 20_000 : 200_000;
            c.FractalParameters.BuddhaProgressive = progressive;
            c.ProgressiveBatchesOverride = 4;
            bool saved = BuddhaFamilyCalculator.UseSharedUniformSampler;
            BuddhaFamilyCalculator.UseSharedUniformSampler = false;   // the classic per-thread path
            try { c.Calculate(); }
            finally { BuddhaFamilyCalculator.UseSharedUniformSampler = saved; }
            return c.HitsR.Sum(v => (long)v) + c.HitsG.Sum(v => (long)v) + c.HitsB.Sum(v => (long)v);
        }
        long single = Total(false), prog = Total(true);
        double ratio = prog / (double)single;
        TestContext.Current.TestOutputHelper?.WriteLine($"metropolis={metropolis}: progressive/single hit total {ratio:F3}");
        Assert.InRange(ratio, 0.5, 1.8);   // a cumulative merge gives ~2.5× (uniform), ~3× (Metropolis)
    }
}
