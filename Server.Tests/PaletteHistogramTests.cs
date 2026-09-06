// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.3 (PaletteBuilder-Design.md, #392) — fractal-aware preview core.
// PaletteHistogram: where a palette's range lands on a view's density, a histogram-
// equalised stop redistribution, and the seamless-cycle check. Deterministic → the
// colour parity twin.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PaletteHistogramTests
{
    [Fact]
    public void Build_Counts_Clamps_And_Skips_NaN()
    {
        var t = new List<float> { 0f, 0.5f, 0.99f, -1f, 2f, float.NaN };
        var h = PaletteHistogram.Build(t, 10);
        Assert.Equal(5, PaletteHistogram.Total(h));   // NaN skipped, out-of-range clamped
        Assert.Equal(2, h[0]);                        // 0 and clamped -1
        Assert.Equal(1, h[5]);                        // 0.5
        Assert.Equal(2, h[9]);                        // 0.99 and clamped 2
    }

    [Fact]
    public void WastedFraction_Zero_When_Full_Range_Used_High_When_Concentrated()
    {
        // Uniform across the whole range → nothing wasted.
        var uniform = new List<float>();
        for (int i = 0; i < 1000; i++) uniform.Add(i / 1000f);
        double wUniform = PaletteHistogram.WastedFraction(PaletteHistogram.Build(uniform, 10));
        Assert.True(wUniform < 0.05, $"uniform waste {wUniform}");

        // All pixels in the low tenth → ~90% of the ramp unused.
        var low = new List<float>();
        for (int i = 0; i < 1000; i++) low.Add(0.05f);
        double wLow = PaletteHistogram.WastedFraction(PaletteHistogram.Build(low, 10));
        Assert.True(wLow >= 0.85, $"concentrated waste {wLow}");

        Assert.Equal(1.0, PaletteHistogram.WastedFraction(new int[0]));   // empty = fully wasted
    }

    [Fact]
    public void EqualizeStopPositions_Flat_Is_Even()
    {
        var flat = Enumerable.Repeat(100, 10).ToArray();
        var pos = PaletteHistogram.EqualizeStopPositions(flat, 5);
        Assert.Equal(5, pos.Length);
        Assert.Equal(0f, pos[0]);
        Assert.Equal(1f, pos[4]);
        for (int i = 0; i < 5; i++) Assert.Equal(i / 4f, pos[i], 2);   // even spacing
    }

    [Fact]
    public void EqualizeStopPositions_FrontLoaded_Clusters_Low_And_Is_Monotonic()
    {
        // Mass in the low bins → interior stops pack toward 0 (more colour where pixels are).
        var hist = new int[10];
        hist[0] = 900; hist[1] = 100;
        var pos = PaletteHistogram.EqualizeStopPositions(hist, 5);
        Assert.Equal(0f, pos[0]);
        Assert.Equal(1f, pos[4]);
        Assert.True(pos[2] < 0.5f, $"mid stop should cluster low, got {pos[2]}");
        for (int i = 1; i < pos.Length; i++)
            Assert.True(pos[i] > pos[i - 1], $"not monotonic at {i}: {pos[i]} <= {pos[i - 1]}");
    }

    [Fact]
    public void CycleSeam_Zero_When_Ends_Match_Large_When_Not()
    {
        Assert.Equal(0f, PaletteHistogram.CycleSeamDeltaE(40, 90, 160, 40, 90, 160), 5);
        Assert.True(PaletteHistogram.IsSeamlessCycle(40, 90, 160, 40, 90, 160));

        // Black↔white ends → a glaring seam.
        Assert.True(PaletteHistogram.CycleSeamDeltaE(0, 0, 0, 255, 255, 255) > 0.9f);
        Assert.False(PaletteHistogram.IsSeamlessCycle(0, 0, 0, 255, 255, 255));
    }
}
