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
    public void DomainWarp_Zero_Is_Baseline_NonZero_Differs()
    {
        // #253 — warp strength 0 is an exact no-op; a positive strength deforms.
        var baseline = Render(0);

        var noWarp = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters { AcidWarpPattern = 0, AcidWarpWarpStrength = 0.0 },
        };
        noWarp.Calculate(default);
        Assert.Equal(baseline, noWarp.ColorBuffer);

        var warped = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters { AcidWarpPattern = 0, AcidWarpWarpStrength = 0.4 },
        };
        warped.Calculate(default);
        Assert.NotEqual(baseline, (uint[])warped.ColorBuffer.Clone());
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

    [Fact]
    public void TitleCard_Renders_Wordmark_Over_Rings()
    {
        // The title-card sentinel renders the "ACID FOG" wordmark (phase-shifted
        // over the ring field), so it must differ from a plain ring render but
        // share the ring background — i.e. differ in a bounded central band, not
        // everywhere.
        var rings = Render(0);

        var card = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters
            {
                AcidWarpTitleCard = true,
            },
        };
        card.Calculate(default);

        int diff = 0;
        for (int i = 0; i < rings.Length; i++)
            if (rings[i] != card.ColorBuffer[i]) diff++;

        Assert.True(diff > 0, "title card must stamp the wordmark");
        Assert.True(diff < rings.Length, "title card must keep the ring background");
    }

    [Fact]
    public void TitleCard_Is_Driven_By_Flag_Independent_Of_Pattern()
    {
        // Regression: the card was formerly a sentinel pattern value (0x7A17)
        // stashed in AcidWarpPattern, which the params-panel NumericUpDown
        // (Maximum == PatternCount-1) coerced away, so the wordmark never showed.
        // Now a dedicated flag drives it — the wordmark must render even with
        // AcidWarpPattern pinned at the in-range maximum.
        var plainMax = Render(AcidWarpCalculator.PatternCount - 1);

        var card = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters
            {
                AcidWarpTitleCard = true,
                AcidWarpPattern = AcidWarpCalculator.PatternCount - 1, // max in-range
            },
        };
        card.Calculate(default);

        // The flag forces the ring field (pattern 0) + wordmark, so it must
        // differ from a plain max-pattern render.
        Assert.NotEqual(plainMax, (uint[])card.ColorBuffer.Clone());

        // And it must match the ring-based card (pattern is ignored under the flag).
        var ringCard = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters { AcidWarpTitleCard = true, AcidWarpPattern = 0 },
        };
        ringCard.Calculate(default);
        Assert.Equal((uint[])ringCard.ColorBuffer.Clone(), card.ColorBuffer);
    }

    [Fact]
    public void Morph_Off_Is_Byte_Identical_To_Discrete_Pattern()
    {
        // With morph disabled the render must exactly match the discrete path,
        // regardless of the Flow value (Flow is inert while morph is off).
        var discrete = Render(9);

        var off = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters
            {
                AcidWarpPattern = 9, AcidWarpMorph = false, AcidWarpFlow = 3.7,
            },
        };
        off.Calculate(default);
        Assert.Equal(discrete, off.ColorBuffer);
    }

    [Fact]
    public void Morph_At_Integer_Flow_Equals_That_Pattern()
    {
        // Morph on, Flow == an integer N → mix 0 → identical to discrete pattern N.
        var discrete = Render(2);

        var morph = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters
            {
                AcidWarpMorph = true, AcidWarpFlow = 2.0,
            },
        };
        morph.Calculate(default);
        Assert.Equal(discrete, morph.ColorBuffer);
    }

    [Fact]
    public void Morph_Between_Patterns_Blends_Not_Hard_Cut()
    {
        // Fractional Flow must produce a field distinct from both neighbours —
        // a real morph, not a snap to either end.
        var p2 = Render(2);
        var p3 = Render(3);

        var mid = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters
            {
                AcidWarpMorph = true, AcidWarpFlow = 2.5,
            },
        };
        mid.Calculate(default);
        var blend = (uint[])mid.ColorBuffer.Clone();
        Assert.NotEqual(p2, blend);
        Assert.NotEqual(p3, blend);
    }

    [Fact]
    public void Morph_Flow_Wraps_Seamlessly_At_PatternCount()
    {
        // Flow == PatternCount wraps back to Flow == 0 (both are pattern 0, mix 0).
        var atZero = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters { AcidWarpMorph = true, AcidWarpFlow = 0.0 },
        };
        atZero.Calculate(default);

        var atCount = new AcidWarpCalculator(64, 48)
        {
            ColorMap = new GrayscalePalette(),
            FractalParameters = new FractalParameters
            {
                AcidWarpMorph = true, AcidWarpFlow = AcidWarpCalculator.PatternCount,
            },
        };
        atCount.Calculate(default);
        Assert.Equal((uint[])atZero.ColorBuffer.Clone(), atCount.ColorBuffer);
    }

    [Fact]
    public void Renders_Rich_Image_Through_Real_Gradient_At_Display_Size()
    {
        // End-to-end guard mimicking the app path: a multi-stop data-driven
        // theme, MaxIterations lifted off the (Mandelbrot) host, a realistic
        // surface size. A working ring field must yield many distinct colours —
        // a single solid colour (the resize/upload regression) would fail here.
        var theme = new DataDrivenGradient(new ColorThemeData
        {
            Name = "grad",
            Stops = new System.Collections.Generic.List<ColorStopData>
            {
                new() { Position = 0f,   R = 0,   G = 0,   B = 0 },
                new() { Position = 0.5f, R = 255, G = 0,   B = 128 },
                new() { Position = 1f,   R = 0,   G = 255, B = 255 },
            },
        });

        var c = new AcidWarpCalculator(160, 120)
        {
            ColorMap = theme,
            MaxIterations = 1000,   // as SyncAltStateFromMandel would set it
            FractalParameters = new FractalParameters { AcidWarpPattern = 0 },
        };
        c.Calculate(default);

        var distinct = new System.Collections.Generic.HashSet<uint>(c.ColorBuffer);
        Assert.True(distinct.Count > 50, $"expected a rich field, got {distinct.Count} colours");
    }
}
