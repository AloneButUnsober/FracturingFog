// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #979 (#939-B) — per-orbit layer colour mode. Each orbit is its own layer (own
// theme, interior colour × interior alpha, #615 surround, opacity) and the c layer
// is blended with the z layer. Checks: blend maths against the W3C formulas
// written out independently; a single visible layer reproduces the Field-mode
// single-orbit fields byte-for-byte (a separate code path); exact colours in the
// three reachable region states; theme resolution.
public sealed class DualOrbitLayerTests
{
    private sealed class ConstMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint Exterior, Interior;
        public uint? Oob;
        public uint InSetColor => Interior;
        public uint? OutOfBoundsColor => Oob;
        public int Map(float smooth, float distance, int iterations) => unchecked((int)Exterior);
    }

    // Smooth-encoding map so gradient pixels differ across the frame.
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static readonly ConstMap Blue = new() { Exterior = 0xFF3366CCu, Interior = 0xFF000080u };
    private static readonly ConstMap Yellow = new() { Exterior = 0xFFFFCC00u, Interior = 0xFF806600u };

    private static byte Ch(uint argb, int shift) => (byte)((argb >> shift) & 0xFF);
    private static uint Argb(double a, double r, double g, double b)
    {
        static uint B(double v) => (uint)Math.Clamp((int)Math.Round(v * 255.0), 0, 255);
        return (B(a) << 24) | (B(r) << 16) | (B(g) << 8) | B(b);
    }

    // ── Blend maths ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DualOrbitLayerBlend.COverZ)]
    [InlineData(DualOrbitLayerBlend.Screen)]
    [InlineData(DualOrbitLayerBlend.Multiply)]
    [InlineData(DualOrbitLayerBlend.Mix)]
    public void InvisibleCLayer_LeavesZ(DualOrbitLayerBlend mode)
    {
        uint z = 0xC8336699u;
        Assert.Equal(z, DualOrbitEscapeCalculator.BlendLayers(z, 1.0, 0xFFFFCC00u, 0.0, mode));
    }

    [Fact]
    public void OpaqueTopLayer_Wins()
    {
        uint z = 0xFF3366CCu, c = 0xFFFFCC00u;
        Assert.Equal(c, DualOrbitEscapeCalculator.BlendLayers(z, 1, c, 1, DualOrbitLayerBlend.COverZ));
        Assert.Equal(z, DualOrbitEscapeCalculator.BlendLayers(z, 1, c, 1, DualOrbitLayerBlend.ZOverC));
    }

    [Fact]
    public void Screen_And_Multiply_MatchTheirFormulas_WhenOpaque()
    {
        uint z = 0xFF336699u, c = 0xFF808080u;
        double[] zc = { 0x33 / 255.0, 0x66 / 255.0, 0x99 / 255.0 }, cc = { 0x80 / 255.0, 0x80 / 255.0, 0x80 / 255.0 };
        uint screen = Argb(1, 1 - (1 - zc[0]) * (1 - cc[0]), 1 - (1 - zc[1]) * (1 - cc[1]), 1 - (1 - zc[2]) * (1 - cc[2]));
        uint mult = Argb(1, zc[0] * cc[0], zc[1] * cc[1], zc[2] * cc[2]);
        Assert.Equal(screen, DualOrbitEscapeCalculator.BlendLayers(z, 1, c, 1, DualOrbitLayerBlend.Screen));
        Assert.Equal(mult, DualOrbitEscapeCalculator.BlendLayers(z, 1, c, 1, DualOrbitLayerBlend.Multiply));
    }

    [Fact]
    public void HalfOpaqueCOverOpaqueZ_IsTheLerp()
    {
        uint z = 0xFF204060u, c = 0xFFE0C0A0u;
        uint expected = Argb(1,
            0.5 * 0xE0 / 255.0 + 0.5 * 0x20 / 255.0,
            0.5 * 0xC0 / 255.0 + 0.5 * 0x40 / 255.0,
            0.5 * 0xA0 / 255.0 + 0.5 * 0x60 / 255.0);
        Assert.Equal(expected, DualOrbitEscapeCalculator.BlendLayers(z, 1, c, 0.5, DualOrbitLayerBlend.COverZ));
    }

    [Fact]
    public void OverOfTranslucentLayers_FollowsPorterDuffAlpha()
    {
        // ao = ac + az (1 − ac) = 0.5 + 0.4·0.5 = 0.7
        uint z = 0x66000000u | 0x00FF0000u;   // A = 0.4
        uint c = 0xFF0000FFu;                  // opacity 0.5 → 0.5
        uint r = DualOrbitEscapeCalculator.BlendLayers(z, 1, c, 0.5, DualOrbitLayerBlend.COverZ);
        Assert.Equal((uint)Math.Round(0.7 * 255), r >> 24);
    }

    [Fact]
    public void Mix_CrossFadesByOpacity_AndBothZeroIsTransparent()
    {
        uint z = 0xFF000000u, c = 0xFFFFFFFFu;
        uint half = DualOrbitEscapeCalculator.BlendLayers(z, 1, c, 1, DualOrbitLayerBlend.Mix);
        Assert.InRange(Ch(half, 16), 127, 128);
        Assert.Equal(0u, DualOrbitEscapeCalculator.BlendLayers(z, 0, c, 0, DualOrbitLayerBlend.Mix));
        Assert.Equal(0u, DualOrbitEscapeCalculator.BlendLayers(z, 0, c, 0, DualOrbitLayerBlend.COverZ));
    }

    // ── Calculator ───────────────────────────────────────────────────────────

    private static DualOrbitEscapeCalculator Frame(FractalParameters p, IColorMap main, int w = 120)
        => new(w, w) { CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = 300, FractalParameters = p, ColorMap = main };

    private static FractalParameters Params(DualOrbitColorMode mode, DualOrbitField field = DualOrbitField.EscapeSeparation,
        double opZ = 1, double opC = 1, DualOrbitLayerBlend blend = DualOrbitLayerBlend.COverZ, double bailout = 128)
        => new()
        {
            DualOrbitColorMode = mode, DualOrbitField = field, DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2,
            DualOrbitOpacityZ = opZ, DualOrbitOpacityC = opC, DualOrbitLayerBlend = blend,
            DualOrbitBailout = bailout, InteriorAlpha = 200,
            DualOrbitThemeZ = "", DualOrbitThemeC = "",   // main theme for both (the tests inject / compare against it)
        };

    [Theory]
    [InlineData(128.0)]
    [InlineData(2.0)]     // small bailout: exercises the #615 surround on both paths
    public void ZLayerAlone_ReproducesFieldModeEscapeTimeZ(double bailout)
    {
        var main = new RampMap();
        var field = Frame(Params(DualOrbitColorMode.Field, DualOrbitField.EscapeTimeZ, bailout: bailout), main);
        var layer = Frame(Params(DualOrbitColorMode.PerOrbitLayers, opC: 0, bailout: bailout), main);
        field.Calculate(); layer.Calculate();
        Assert.Equal(field.ColorBuffer, layer.ColorBuffer);
        Assert.Equal(field.SmoothBuffer, layer.SmoothBuffer);   // relief height = z layer
    }

    [Fact]
    public void CLayerAlone_ReproducesFieldModeEscapeTimeC()
    {
        var main = new RampMap();
        var field = Frame(Params(DualOrbitColorMode.Field, DualOrbitField.EscapeTimeC), main);
        var layer = Frame(Params(DualOrbitColorMode.PerOrbitLayers, opZ: 0), main);
        field.Calculate(); layer.Calculate();
        Assert.Equal(field.ColorBuffer, layer.ColorBuffer);
    }

    // Pixel pinned at s (1×1, tiny pitch) with the two constant themes injected.
    private static uint Pin(double sx, double sy, double cx, double cy, DualOrbitLayerBlend blend)
    {
        var p = new FractalParameters
        {
            DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers, DualOrbitCSeedX = cx, DualOrbitCSeedY = cy,
            DualOrbitLayerBlend = blend, DualOrbitOpacityZ = 1, DualOrbitOpacityC = 1,
        };
        var calc = new DualOrbitEscapeCalculator(1, 1)
        {
            CenterX = sx, CenterY = sy, Zoom = 1e12, MaxIterations = 300, FractalParameters = p,
            LayerThemeZ = Blue, LayerThemeC = Yellow,
        };
        calc.Calculate();
        return calc.ColorBuffer[0];
    }

    private static bool Bounded(double ur, double ui, double sr, double si)
    {
        for (int i = 0; i < 3000; i++)
        {
            if (ur * ur + ui * ui > 4) return false;
            double t = ur * ur - ui * ui + sr; ui = 2 * ur * ui + si; ur = t;
        }
        return true;
    }

    [Theory]
    // exterior: both escape
    [InlineData(0.45, 0.45, 0.3, 0.2, false, false)]
    // M \ M_c: z bounded, c escapes
    [InlineData(-0.1, 0.1, 1.5, 0.0, true, false)]
    // M_c: both bounded
    [InlineData(-0.1, 0.1, 0.3, 0.2, true, true)]
    public void RegionStates_TakeEachLayersOwnColours(double sx, double sy, double cx, double cy, bool zB, bool cB)
    {
        Assert.Equal(zB, Bounded(0, 0, sx, sy));
        Assert.Equal(cB, Bounded(cx, cy, sx, sy));
        uint expectZ = zB ? Blue.Interior : Blue.Exterior;
        uint expectC = cB ? Yellow.Interior : Yellow.Exterior;
        Assert.Equal(expectC, Pin(sx, sy, cx, cy, DualOrbitLayerBlend.COverZ));
        Assert.Equal(expectZ, Pin(sx, sy, cx, cy, DualOrbitLayerBlend.ZOverC));
    }

    [Fact]
    public void LayerSurround_UsesEachThemesOwnDisc()
    {
        // Bailout 2, s = 3: both orbits escape by step 1 → each layer's surround.
        var z = new ConstMap { Exterior = 0xFF3366CCu, Interior = 0xFF000080u, Oob = 0xFF010203u };
        var c = new ConstMap { Exterior = 0xFFFFCC00u, Interior = 0xFF806600u, Oob = 0xFF0A0B0Cu };
        var p = new FractalParameters
        {
            DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers, DualOrbitBailout = 2,
            DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitLayerBlend = DualOrbitLayerBlend.ZOverC,
        };
        var calc = new DualOrbitEscapeCalculator(1, 1) { CenterX = 3, CenterY = 0, Zoom = 1e12, FractalParameters = p, LayerThemeZ = z, LayerThemeC = c };
        calc.Calculate();
        Assert.Equal(0xFF010203u, calc.ColorBuffer[0]);
        p.DualOrbitLayerBlend = DualOrbitLayerBlend.COverZ; p.DualOrbitOpacityC = 1;
        calc.Calculate();
        Assert.Equal(0xFF0A0B0Cu, calc.ColorBuffer[0]);
    }

    [Fact]
    public void ThemeNames_Resolve_Unknown_FallsBackAndIsReported()
    {
        var main = new RampMap();
        var p = Params(DualOrbitColorMode.PerOrbitLayers, opZ: 1, opC: 0);
        p.DualOrbitThemeZ = "No Such Theme 939";
        var unknown = Frame(p, main); unknown.Calculate();
        Assert.Contains("No Such Theme 939", unknown.UnresolvedLayerThemes);

        var viaMain = Frame(Params(DualOrbitColorMode.PerOrbitLayers, opZ: 1, opC: 0), main); viaMain.Calculate();
        Assert.Empty(viaMain.UnresolvedLayerThemes);
        Assert.Equal(viaMain.ColorBuffer, unknown.ColorBuffer);   // fell back to the main theme

        // A real library theme resolves and changes the picture.
        string real = ColorPalette.GetPaletteNames().First(n => !string.Equals(n, HsvPalette.Name, StringComparison.OrdinalIgnoreCase));
        var pr = Params(DualOrbitColorMode.PerOrbitLayers, opZ: 1, opC: 0); pr.DualOrbitThemeZ = real;
        var named = Frame(pr, main); named.Calculate();
        Assert.Empty(named.UnresolvedLayerThemes);
        Assert.NotEqual(viaMain.ColorBuffer, named.ColorBuffer);
    }

    [Fact]
    public void InjectedTheme_WinsOverName()
    {
        var p = Params(DualOrbitColorMode.PerOrbitLayers, opZ: 1, opC: 0);
        p.DualOrbitThemeZ = "No Such Theme 939";
        var calc = Frame(p, new RampMap());
        calc.LayerThemeZ = Blue;
        calc.Calculate();
        Assert.Empty(calc.UnresolvedLayerThemes);
        Assert.All(calc.ColorBuffer, c => Assert.True(c == Blue.Exterior || c == InteriorAlphaStamp.ScaleArgbAlpha(Blue.Interior, 200)));
    }

    [Fact]
    public void Quaternion_LayerMode_ShowsBothLayers()
    {
        var p = Params(DualOrbitColorMode.PerOrbitLayers, opZ: 1, opC: 1, blend: DualOrbitLayerBlend.Mix);
        p.DualOrbitMap = DualOrbitMap.Quaternion;
        var calc = Frame(p, new RampMap());
        calc.LayerThemeZ = Blue; calc.LayerThemeC = Yellow;
        calc.Calculate();
        Assert.True(calc.ColorBuffer.Distinct().Count() >= 3, "expected z-only, c-only and mixed colours");
    }

    [Fact]
    public void LayerParams_Clone()
    {
        var p = new FractalParameters
        {
            DualOrbitColorMode = DualOrbitColorMode.PerOrbitLayers, DualOrbitThemeZ = "A", DualOrbitThemeC = "B",
            DualOrbitLayerBlend = DualOrbitLayerBlend.Screen, DualOrbitOpacityZ = 0.3, DualOrbitOpacityC = 0.7,
        };
        var c = p.Clone();
        Assert.Equal(DualOrbitColorMode.PerOrbitLayers, c.DualOrbitColorMode);
        Assert.Equal("A", c.DualOrbitThemeZ);
        Assert.Equal("B", c.DualOrbitThemeC);
        Assert.Equal(DualOrbitLayerBlend.Screen, c.DualOrbitLayerBlend);
        Assert.Equal(0.3, c.DualOrbitOpacityZ);
        Assert.Equal(0.7, c.DualOrbitOpacityC);
    }
}
