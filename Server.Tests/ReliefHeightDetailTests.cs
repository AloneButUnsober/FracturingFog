// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #518 — height-field detail shaping. Unsharp is a local high-pass that raises
// filament variation relative to the base without lifting the base; Gamma expands
// the top-end contrast, preserving the peak. Both are identity at their defaults.

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefHeightDetailTests
{
    // A base slab (constant) with a thin raised ridge down the middle column.
    private static float[] SlabWithRidge(int w, int h, float slab, float ridge)
    {
        var f = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                f[y * w + x] = (x == w / 2) ? slab + ridge : slab;
        return f;
    }

    [Fact]
    public void Unsharp_Gain1_Is_Exact_NoOp()
    {
        var a = SlabWithRidge(32, 32, 2f, 1f);
        var b = (float[])a.Clone();
        ReliefHeightDetail.Unsharp(b, 32, 32, gain: 1.0, radius: 3);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Unsharp_Raises_Ridge_Above_Base_Without_Lifting_Slab()
    {
        int w = 48, h = 48;
        var f = SlabWithRidge(w, h, slab: 2f, ridge: 1f);
        float ridgeBefore = f[h / 2 * w + w / 2];
        float slabBefore  = f[h / 2 * w + 3];          // a flat-slab cell far from the ridge

        ReliefHeightDetail.Unsharp(f, w, h, gain: 3.0, radius: 2);

        float ridgeAfter = f[h / 2 * w + w / 2];
        float slabAfter  = f[h / 2 * w + 3];

        // The ridge peak grows; the flat slab far from it is (essentially) unchanged.
        Assert.True(ridgeAfter > ridgeBefore + 0.5f,
            $"ridge should grow: {ridgeBefore} -> {ridgeAfter}");
        Assert.True(Math.Abs(slabAfter - slabBefore) < 0.05f,
            $"flat slab should stay put: {slabBefore} -> {slabAfter}");
        // Ridge-vs-slab separation increased (the whole point).
        Assert.True(ridgeAfter - slabAfter > ridgeBefore - slabBefore);
    }

    [Fact]
    public void Unsharp_Clamps_Base_NonNegative()
    {
        // A dip below a flat base: high gain would push it negative — must clamp to 0.
        int w = 32, h = 32;
        var f = new float[w * h];
        for (int i = 0; i < f.Length; i++) f[i] = 1f;
        f[h / 2 * w + w / 2] = 0f;                       // a pit
        ReliefHeightDetail.Unsharp(f, w, h, gain: 5.0, radius: 2);
        foreach (var v in f) Assert.True(v >= 0f);
    }

    [Fact]
    public void Gamma_1_Is_Exact_NoOp()
    {
        var a = SlabWithRidge(24, 24, 3f, 2f);
        var b = (float[])a.Clone();
        ReliefHeightDetail.Gamma(b, 24, 24, 1.0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Gamma_GreaterThan1_Preserves_Peak_And_Lowers_Midtones()
    {
        int w = 16, h = 16;
        var f = new float[w * h];
        // Values 0..1 across the row (peak 1 at the end).
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                f[y * w + x] = x / (float)(w - 1);
        float peakBefore = f[w - 1];
        float midBefore  = f[w / 2];

        ReliefHeightDetail.Gamma(f, w, h, 2.0);

        Assert.Equal(peakBefore, f[w - 1], 5);           // peak preserved (1^2 = 1)
        Assert.True(f[w / 2] < midBefore,                // midtone pushed down (contrast up)
            $"gamma>1 should lower midtones: {midBefore} -> {f[w / 2]}");
    }

    [Fact]
    public void AutoRadius_Scales_With_Short_Axis()
    {
        Assert.True(ReliefHeightDetail.AutoRadius(2000, 1000) > ReliefHeightDetail.AutoRadius(400, 300));
        Assert.True(ReliefHeightDetail.AutoRadius(10, 10) >= 1);   // never zero
    }
}
