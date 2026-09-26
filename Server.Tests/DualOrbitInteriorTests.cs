// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #978 (#939-A) — the dual-orbit field honours the theme's interior colour, the
// global interior alpha (#96/#97) and the #615 out-of-bounds surround, like every
// other 2D escape-time family. Orbit states at the probed points are checked
// independently (brute-force iteration), not read back from the calculator.
public sealed class DualOrbitInteriorTests
{
    private sealed class ProbeMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColorValue = 0xFF112233u;
        public uint? OobValue;
        public uint InSetColor => InSetColorValue;
        public uint? OutOfBoundsColor => OobValue;
        // Encodes the smooth value so an exterior pixel is recognisably Map(smooth).
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu) | 0x00800000u));
    }

    private const int MaxIter = 300;

    private static (uint color, float smooth) Pin(double sx, double sy, ProbeMap map,
        DualOrbitField field = DualOrbitField.EscapeSeparation, double seedX = 0.3, double seedY = 0.2,
        int interiorAlpha = 255, double bailout = 128, bool cEqualsS = false)
    {
        var p = new FractalParameters
        {
            DualOrbitField = field, DualOrbitCSeedX = seedX, DualOrbitCSeedY = seedY,
            InteriorAlpha = interiorAlpha, DualOrbitBailout = bailout, DualOrbitCEqualsS = cEqualsS,
        };
        var c = new DualOrbitEscapeCalculator(1, 1)
        { CenterX = sx, CenterY = sy, Zoom = 1e12, MaxIterations = MaxIter, FractalParameters = p, ColorMap = map };
        c.Calculate();
        return (c.ColorBuffer[0], c.SmoothBuffer[0]);
    }

    // Independent escape index: first n with |u_n| > r (−1 if bounded).
    private static int EscapeIndex(double ur, double ui, double sr, double si, double r = 2.0, int n = 3000)
    {
        for (int i = 0; i < n; i++)
        {
            if (ur * ur + ui * ui > r * r) return i;
            double t = ur * ur - ui * ui + sr; ui = 2 * ur * ui + si; ur = t;
        }
        return -1;
    }

    private static uint MapOf(ProbeMap m, float smooth) => unchecked((uint)m.Map(smooth, 0f, MaxIter));

    [Fact]
    public void BothBounded_PaintsTheThemeInteriorColour()
    {
        Assert.Equal(-1, EscapeIndex(0, 0, -0.1, 0.1));       // s in M
        Assert.Equal(-1, EscapeIndex(0.3, 0.2, -0.1, 0.1));   // c-seed bounded too
        var map = new ProbeMap();
        Assert.Equal(map.InSetColorValue, Pin(-0.1, 0.1, map).color);
    }

    [Fact]
    public void OnlyZBounded_IsInteriorForTwoOrbitFields_ButLiveForEscapeTimeC()
    {
        // s in M, but this c-seed escapes: M \ M_c.
        const double sx = -0.1, sy = 0.1, cx = 1.5, cy = 0.0;
        Assert.Equal(-1, EscapeIndex(0, 0, sx, sy));
        Assert.True(EscapeIndex(cx, cy, sx, sy) > 0);
        var map = new ProbeMap();
        Assert.Equal(map.InSetColorValue, Pin(sx, sy, map, DualOrbitField.GreenRatio, cx, cy).color);
        Assert.Equal(map.InSetColorValue, Pin(sx, sy, map, DualOrbitField.EscapeTimeZ, cx, cy).color);
        var (c, smooth) = Pin(sx, sy, map, DualOrbitField.EscapeTimeC, cx, cy);
        Assert.Equal(MapOf(map, smooth), c);
    }

    [Fact]
    public void InteriorAlpha_ScalesTheInteriorOnly()
    {
        var map = new ProbeMap { InSetColorValue = 0xC0112233u };
        var inside = Pin(-0.1, 0.1, map, interiorAlpha: 128).color;
        Assert.Equal((0xC0u * 128u) / 255u, inside >> 24);
        Assert.Equal(0x112233u, inside & 0x00FFFFFFu);

        // An escaping pixel is untouched by the knob.
        var (outside, smooth) = Pin(0.45, 0.45, map, interiorAlpha: 128);
        Assert.Equal(MapOf(map, smooth), outside);
    }

    [Fact]
    public void Surround_PaintsWhereBothOrbitsEscapeByStepOne_Only()
    {
        var map = new ProbeMap { OobValue = 0x80ABCDEFu };
        // s = 3: z_1 = 3 and c_1 = c² + 3 both beyond R = 2 → surround.
        Assert.InRange(EscapeIndex(0, 0, 3.0, 0.0), 0, 1);
        Assert.InRange(EscapeIndex(0.3, 0.2, 3.0, 0.0), 0, 1);
        Assert.Equal(0x80ABCDEFu, Pin(3.0, 0.0, map, bailout: 2).color);

        // s = 1.2: outside M but the z-orbit needs two steps → normal field colour.
        Assert.True(EscapeIndex(0, 0, 1.2, 0.0) >= 2);
        var (c, smooth) = Pin(1.2, 0.0, map, bailout: 2);
        Assert.Equal(MapOf(map, smooth), c);
    }

    [Fact]
    public void NoSurroundColour_LeavesTheFieldColour()
    {
        var map = new ProbeMap();   // OutOfBoundsColor null (the default)
        var (c, smooth) = Pin(3.0, 0.0, map, bailout: 2);
        Assert.Equal(MapOf(map, smooth), c);
    }

    [Fact]
    public void ZeroValuedLiveField_IsNotMistakenForInterior()
    {
        // c = s control: the c-orbit is the z-orbit one step ahead, so separation is
        // a legitimate 0 wherever both escape. s = 0.5 is outside M.
        Assert.True(EscapeIndex(0, 0, 0.5, 0.0) > 0);
        var map = new ProbeMap();
        var (c, smooth) = Pin(0.5, 0.0, map, cEqualsS: true);
        Assert.Equal(0f, smooth);
        Assert.Equal(MapOf(map, 0f), c);
        Assert.NotEqual(map.InSetColorValue, c);
    }

    [Fact]
    public void FullFrame_ExteriorIsMapOfSmooth_InteriorIsInSetColour()
    {
        var map = new ProbeMap();
        var p = new FractalParameters { DualOrbitField = DualOrbitField.GreenRatio, DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2 };
        var calc = new DualOrbitEscapeCalculator(120, 120)
        { CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = MaxIter, FractalParameters = p, ColorMap = map };
        calc.Calculate();
        int interior = 0, exterior = 0;
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
        {
            // GreenRatio floors live values above 0, so 0 marks the no-value pixels here.
            if (calc.SmoothBuffer[i] == 0f) { Assert.Equal(map.InSetColorValue, calc.ColorBuffer[i]); interior++; }
            else { Assert.Equal(MapOf(map, calc.SmoothBuffer[i]), calc.ColorBuffer[i]); exterior++; }
        }
        Assert.True(interior > 500 && exterior > 5000, $"interior {interior}, exterior {exterior}");
    }
}
