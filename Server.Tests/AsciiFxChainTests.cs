// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>Unit coverage for the ASCII-native FX chain (#229): identity when
/// disabled, CRT scanline dimming, hue rotation, and glyph-density breathe.</summary>
public sealed class AsciiFxChainTests
{
    private const string Ramp = " .:-=+*#%@";

    private static AsciiCell[] Grid(int cols, int rows, Func<int, int, AsciiCell> f)
    {
        var cells = new AsciiCell[cols * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                cells[y * cols + x] = f(x, y);
        return cells;
    }

    [Fact]
    public void Disabled_IsIdentity()
    {
        var cells = Grid(3, 2, (x, y) => new AsciiCell('#', 100, 150, 200));
        var copy = (AsciiCell[])cells.Clone();
        AsciiFxChain.Apply(cells, 3, 2, Ramp, new AsciiFxSettings()); // nothing enabled
        for (int i = 0; i < cells.Length; i++)
        {
            Assert.Equal(copy[i].Glyph, cells[i].Glyph);
            Assert.Equal(copy[i].R, cells[i].R);
            Assert.Equal(copy[i].G, cells[i].G);
            Assert.Equal(copy[i].B, cells[i].B);
        }
    }

    [Fact]
    public void Crt_DimsOddRowsOnly()
    {
        var cells = Grid(2, 2, (x, y) => new AsciiCell('#', 200, 200, 200));
        AsciiFxChain.Apply(cells, 2, 2, Ramp,
            new AsciiFxSettings { Crt = true, CrtScanlineDim = 0.5 });
        // Row 0 (even) untouched, row 1 (odd) halved.
        Assert.Equal(200, cells[0].R);            // (0,0)
        Assert.Equal(200, cells[1].R);            // (1,0)
        Assert.Equal(100, cells[0 + 2].R);        // (0,1) dimmed
        Assert.Equal(100, cells[1 + 2].R);        // (1,1) dimmed
    }

    [Fact]
    public void HueCycle_RotatesColour_ButKeepsGlyph()
    {
        // Pure red, rotate 120° → pure green (HSV hue 0 → 120).
        var cells = Grid(1, 1, (x, y) => new AsciiCell('@', 255, 0, 0));
        AsciiFxChain.Apply(cells, 1, 1, Ramp, new AsciiFxSettings
        {
            HueCycle = true,
            HueCycleDegPerSec = 120.0,
            TimeSeconds = 1.0, // 120°
        });
        Assert.Equal('@', cells[0].Glyph);
        Assert.True(cells[0].G > 240, $"expected green-dominant, got ({cells[0].R},{cells[0].G},{cells[0].B})");
        Assert.True(cells[0].R < 15);
        Assert.True(cells[0].B < 15);
    }

    [Fact]
    public void HueCycle_LeavesBlackUnchanged()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell(' ', 0, 0, 0));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { HueCycle = true, TimeSeconds = 0.7 });
        Assert.Equal(0, cells[0].R);
        Assert.Equal(0, cells[0].G);
        Assert.Equal(0, cells[0].B);
    }

    [Fact]
    public void Breathe_ShiftsGlyphAlongRamp_AtGammaExtreme()
    {
        // A mid-ramp glyph. Sample the breathe at its peak (sin = +1) with a big
        // amplitude so gamma is well above 1 → t^gamma pulls a mid index DOWN
        // toward the blank end (lower ramp index).
        char mid = Ramp[5]; // '+'
        var cells = Grid(1, 1, (x, y) => new AsciiCell(mid, 120, 120, 120));
        // sin peaks at t = 0.25 / Hz. Hz = 1 → t = 0.25s.
        AsciiFxChain.Apply(cells, 1, 1, Ramp, new AsciiFxSettings
        {
            Breathe = true, BreatheGammaMid = 1.0, BreatheGammaAmp = 1.5, BreatheHz = 1.0,
            TimeSeconds = 0.25,
        });
        int oldIdx = Ramp.IndexOf(mid);
        int newIdx = Ramp.IndexOf(cells[0].Glyph);
        Assert.True(newIdx < oldIdx, $"gamma>1 should lower the ramp index ({oldIdx} -> {newIdx})");
    }

    [Fact]
    public void Breathe_LeavesSpacesBlank()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell(' ', 0, 0, 0));
        AsciiFxChain.Apply(cells, 1, 1, Ramp, new AsciiFxSettings
        { Breathe = true, BreatheGammaAmp = 1.5, TimeSeconds = 0.25 });
        Assert.Equal(' ', cells[0].Glyph);
    }

    [Fact]
    public void CharsetSwap_MapsDensityToNewSet_KeepsColour()
    {
        // Ramp[9] = '@' is the densest → maps to the densest swap glyph.
        var cells = Grid(1, 1, (x, y) => new AsciiCell('@', 30, 60, 90));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { CharsetSwap = true, SwapRamp = "abcd" });
        Assert.Equal('d', cells[0].Glyph); // densest → last swap glyph
        Assert.Equal(30, cells[0].R);
        Assert.Equal(60, cells[0].G);
        Assert.Equal(90, cells[0].B);
    }

    [Fact]
    public void CharsetSwap_MapsMidGlyphProportionally()
    {
        // Ramp index 0 (' ' is blank, skip) → use index 3 '-' (t = 3/9 = .33).
        // Swap "abcdefg" (len 7): round(.333*6) = 2 → 'c'.
        var cells = Grid(1, 1, (x, y) => new AsciiCell(Ramp[3], 10, 10, 10));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { CharsetSwap = true, SwapRamp = "abcdefg" });
        Assert.Equal('c', cells[0].Glyph);
    }

    [Fact]
    public void CharsetSwap_LeavesSpacesBlank()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell(' ', 0, 0, 0));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { CharsetSwap = true, SwapRamp = "abcd" });
        Assert.Equal(' ', cells[0].Glyph);
    }

    [Fact]
    public void Monochrome_TintsToHueKeepingBrightness()
    {
        // White (luma 1) → full tint colour; keeps glyph.
        var cells = Grid(1, 1, (x, y) => new AsciiCell('#', 255, 255, 255));
        AsciiFxChain.Apply(cells, 1, 1, Ramp, new AsciiFxSettings
        { Monochrome = true, MonochromeR = 40, MonochromeG = 255, MonochromeB = 90 });
        Assert.Equal('#', cells[0].Glyph);
        Assert.Equal(40, cells[0].R);
        Assert.Equal(255, cells[0].G);
        Assert.Equal(90, cells[0].B);
    }

    [Fact]
    public void Monochrome_ScalesTintByLuma()
    {
        // Mid-grey (luma ≈ 0.5) → roughly half the tint.
        var cells = Grid(1, 1, (x, y) => new AsciiCell('#', 128, 128, 128));
        AsciiFxChain.Apply(cells, 1, 1, Ramp, new AsciiFxSettings
        { Monochrome = true, MonochromeR = 0, MonochromeG = 200, MonochromeB = 0 });
        Assert.Equal(0, cells[0].R);
        Assert.InRange((int)cells[0].G, 90, 110); // ~0.5 * 200
        Assert.Equal(0, cells[0].B);
    }

    [Fact]
    public void Saturate_ZeroGivesGreyscale()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell('#', 200, 50, 50));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { Saturate = true, SaturateMid = 0.0 });
        // All channels collapse to the luma → equal.
        Assert.Equal(cells[0].R, cells[0].G);
        Assert.Equal(cells[0].G, cells[0].B);
    }

    [Fact]
    public void Saturate_BoostPushesChannelsApart()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell('#', 160, 120, 120));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { Saturate = true, SaturateMid = 2.0 });
        // Dominant channel gets more dominant; spread widens vs original 40.
        Assert.True(cells[0].R - cells[0].G > 40, $"spread {cells[0].R - cells[0].G}");
    }

    [Fact]
    public void Invert_NegatesChannels()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell('#', 10, 128, 250));
        AsciiFxChain.Apply(cells, 1, 1, Ramp, new AsciiFxSettings { Invert = true });
        Assert.Equal(245, cells[0].R);
        Assert.Equal(127, cells[0].G);
        Assert.Equal(5, cells[0].B);
    }

    [Fact]
    public void Solarize_InvertsOnlyBrightChannels()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell('#', 40, 200, 130));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { Solarize = true, SolarizeThreshold = 0.5 }); // thresh 127
        Assert.Equal(40, cells[0].R);         // below → untouched
        Assert.Equal(55, cells[0].G);         // 200 > 127 → 255-200
        Assert.Equal(125, cells[0].B);        // 130 > 127 → 255-130
    }

    [Fact]
    public void Quantize_PosterizesToLevels()
    {
        // 2 levels → each channel snaps to 0 or 255.
        var cells = Grid(1, 1, (x, y) => new AsciiCell('#', 100, 200, 10));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { Quantize = true, QuantizeLevels = 2 });
        Assert.Equal(0, cells[0].R);    // 100 < 127.5 → 0
        Assert.Equal(255, cells[0].G);  // 200 → 255
        Assert.Equal(0, cells[0].B);    // 10 → 0
    }

    [Fact]
    public void Quantize_Terminal16SnapsToPaletteColour()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell('#', 240, 10, 10));
        AsciiFxChain.Apply(cells, 1, 1, Ramp,
            new AsciiFxSettings { Quantize = true, QuantizeTerminal16 = true });
        // Nearest ANSI entry to bright red is (255,0,0).
        Assert.Equal(255, cells[0].R);
        Assert.Equal(0, cells[0].G);
        Assert.Equal(0, cells[0].B);
    }

    [Fact]
    public void RampScroll_ShiftsGlyphAlongRampOverTime()
    {
        // Ramp[3] = '-'. Speed 1 step/s, at t=2s → shift by 2 → Ramp[5] = '+'.
        var cells = Grid(1, 1, (x, y) => new AsciiCell(Ramp[3], 10, 10, 10));
        AsciiFxChain.Apply(cells, 1, 1, Ramp, new AsciiFxSettings
        { RampScroll = true, RampScrollSpeed = 1.0, TimeSeconds = 2.0 });
        Assert.Equal(Ramp[5], cells[0].Glyph);
    }

    [Fact]
    public void RampScroll_LeavesSpacesBlank()
    {
        var cells = Grid(1, 1, (x, y) => new AsciiCell(' ', 0, 0, 0));
        AsciiFxChain.Apply(cells, 1, 1, Ramp, new AsciiFxSettings
        { RampScroll = true, RampScrollSpeed = 3.0, TimeSeconds = 2.0 });
        Assert.Equal(' ', cells[0].Glyph);
    }

    [Fact]
    public void Duotone_MapsLumaToGradientEndpoints()
    {
        var lo = new AsciiFxSettings
        {
            Duotone = true,
            DuotoneLoR = 0, DuotoneLoG = 0, DuotoneLoB = 100,
            DuotoneHiR = 200, DuotoneHiG = 100, DuotoneHiB = 0,
        };
        // Black (luma 0) → shadow endpoint.
        var dark = Grid(1, 1, (x, y) => new AsciiCell('#', 0, 0, 0));
        AsciiFxChain.Apply(dark, 1, 1, Ramp, lo);
        Assert.Equal(0, dark[0].R); Assert.Equal(0, dark[0].G); Assert.Equal(100, dark[0].B);
        // White (luma 1) → highlight endpoint.
        var light = Grid(1, 1, (x, y) => new AsciiCell('#', 255, 255, 255));
        AsciiFxChain.Apply(light, 1, 1, Ramp, lo);
        Assert.Equal(200, light[0].R); Assert.Equal(100, light[0].G); Assert.Equal(0, light[0].B);
    }

    [Fact]
    public void MatrixRain_ProducesGreenDropsAndGhostsBackground()
    {
        const int cols = 20, rows = 30;
        var cells = Grid(cols, rows, (x, y) => new AsciiCell('#', 180, 180, 180));
        var fx = new AsciiFxSettings { MatrixRain = true, TimeSeconds = 1.0 };
        var state = new AsciiFxState();
        AsciiFxChain.Apply(cells, cols, rows, Ramp, fx, state);

        int green = 0, ghost = 0;
        foreach (var c in cells)
        {
            if (c.G > c.R && c.G > c.B && c.G > 40) green++;
            // Background ghosts: original 180 dimmed to ~21 and left grey.
            if (c.R == c.G && c.G == c.B && c.R < 40 && c.R > 0) ghost++;
        }
        Assert.True(green > 0, "expected some green rain cells");
        Assert.True(ghost > 0, "expected dimmed fractal ghost in the background");
    }

    [Fact]
    public void MatrixRain_NoStateIsNoOp()
    {
        var cells = Grid(4, 4, (x, y) => new AsciiCell('#', 180, 180, 180));
        var copy = (AsciiCell[])cells.Clone();
        AsciiFxChain.Apply(cells, 4, 4, Ramp,
            new AsciiFxSettings { MatrixRain = true, TimeSeconds = 1.0 }); // no state
        for (int i = 0; i < cells.Length; i++)
            Assert.Equal(copy[i].Glyph, cells[i].Glyph);
    }

    [Fact]
    public void MatrixRain_DeterministicForSameSeed()
    {
        const int cols = 16, rows = 24;
        AsciiCell[] Run()
        {
            var cells = Grid(cols, rows, (x, y) => new AsciiCell('#', 100, 100, 100));
            var s = new AsciiFxState(seed: 42);
            // Advance a few frames so drops move.
            for (int f = 0; f < 3; f++)
            {
                for (int i = 0; i < cells.Length; i++) cells[i] = new AsciiCell('#', 100, 100, 100);
                AsciiFxChain.Apply(cells, cols, rows, Ramp,
                    new AsciiFxSettings { MatrixRain = true, TimeSeconds = 0.1 * (f + 1) }, s);
            }
            return cells;
        }
        var a = Run();
        var b = Run();
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Glyph, b[i].Glyph);
            Assert.Equal(a[i].G, b[i].G);
        }
    }

    [Fact]
    public void NeedsState_TrueOnlyForStatefulEffects()
    {
        Assert.False(new AsciiFxSettings { HueCycle = true }.NeedsState);
        Assert.True(new AsciiFxSettings { MatrixRain = true }.NeedsState);
    }
}
