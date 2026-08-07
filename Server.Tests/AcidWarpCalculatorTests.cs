// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #247 — AcidWarpCalculator is a clean-room procedural pattern field (Acid
// Warp, Noah Spurrier 1992). These lock in the properties the mode relies on:
//   • deterministic per (pattern, freq, centre, seed, size) — required so the
//     palette-cycling motion effect (#249) can recolor without re-deriving the
//     field, and so shuffle playback is reproducible
//   • the pattern selector wraps modulo PatternCount (any int is legal)
//   • distinct patterns produce distinct images
//   • the fill is non-trivial (not a flat single colour)
public class AcidWarpCalculatorTests
{
    private static AcidWarpCalculator Make(int pattern, int seed = 12345, double freq = 1.0)
        => new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters
            {
                AcidWarpPattern = pattern,
                AcidWarpFrequency = freq,
                AcidWarpSeed = seed,
            },
        };

    private static uint[] Render(int pattern, int seed = 12345, double freq = 1.0)
    {
        var c = Make(pattern, seed, freq);
        c.Calculate(default);
        return (uint[])c.ColorBuffer.Clone();
    }

    [Fact]
    public void Family_Is_IFractalCalculator_NoZoomPan()
    {
        var c = new AcidWarpCalculator(8, 8);
        Assert.IsAssignableFrom<IFractalCalculator>(c);
        Assert.False(c.SupportsZoomPan);
    }

    [Fact]
    public void Fill_Is_Deterministic()
    {
        for (int p = 0; p < AcidWarpCalculator.PatternCount; p++)
        {
            var a = Render(p);
            var b = Render(p);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Stochastic_Pattern_Is_Seed_Deterministic()
    {
        // Pattern 19 is the seeded value-noise field.
        var a = Render(19, seed: 777);
        var b = Render(19, seed: 777);
        var c = Render(19, seed: 778);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Pattern_Selector_Wraps_Modulo()
    {
        // pattern == PatternCount is the same as pattern 0; -1 == last.
        Assert.Equal(Render(0), Render(AcidWarpCalculator.PatternCount));
        Assert.Equal(Render(AcidWarpCalculator.PatternCount - 1), Render(-1));
    }

    [Fact]
    public void Distinct_Patterns_Differ()
    {
        var rings = Render(0);
        var spokes = Render(1);
        Assert.NotEqual(rings, spokes);
    }

    [Fact]
    public void Fill_Is_Not_Flat()
    {
        // A real field must contain more than one colour.
        var img = Render(0);
        uint first = img[0];
        bool anyDifferent = false;
        foreach (var px in img)
        {
            if (px != first) { anyDifferent = true; break; }
        }
        Assert.True(anyDifferent);
    }
}
