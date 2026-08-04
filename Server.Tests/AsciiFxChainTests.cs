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
}
