// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.4 (PaletteBuilder-Design.md, #392) — harmony + generation in
// perceptual space. ColorHarmony (OKLCH hue rotation), CosinePalette (IQ a+b·cos),
// BezierRamp (chroma.js Bézier-through-OkLab + lightness correction). Deterministic.

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ColorHarmonyTests
{
    private static (float L, float C, float H) Oklch(byte r, byte g, byte b)
    {
        var (L, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
        return PerceptualRamp.OkLabToOklch(L, a, bb);
    }

    private static float L(byte r, byte g, byte b) => PerceptualRamp.RgbToOkLab(r, g, b).L;

    [Fact]
    public void RotateHue_0_And_360_Are_Identity()
    {
        foreach (var d in new[] { 0f, 360f })
        {
            var (r, g, b) = ColorHarmony.RotateHue(170, 120, 80, d);
            Assert.True(Math.Abs(r - 170) <= 2 && Math.Abs(g - 120) <= 2 && Math.Abs(b - 80) <= 2,
                $"rotate {d}: {r},{g},{b}");
        }
    }

    [Fact]
    public void Harmony_Counts_And_Base_First()
    {
        var (br, bg, bb) = ((byte)170, (byte)120, (byte)80);
        Assert.Equal(2, ColorHarmony.Harmony(br, bg, bb, ColorHarmony.Scheme.Complementary).Length);
        Assert.Equal(3, ColorHarmony.Harmony(br, bg, bb, ColorHarmony.Scheme.Triadic).Length);
        Assert.Equal(3, ColorHarmony.Harmony(br, bg, bb, ColorHarmony.Scheme.Analogous).Length);
        Assert.Equal(3, ColorHarmony.Harmony(br, bg, bb, ColorHarmony.Scheme.SplitComplementary).Length);
        var tetr = ColorHarmony.Harmony(br, bg, bb, ColorHarmony.Scheme.Tetradic);
        Assert.Equal(4, tetr.Length);
        Assert.Equal((br, bg, bb), tetr[0]);   // base first, exact
    }

    [Fact]
    public void Complementary_Rotates_Hue_By_About_180_Preserving_Lightness()
    {
        var (br, bg, bb) = ((byte)170, (byte)120, (byte)80);
        var comp = ColorHarmony.Harmony(br, bg, bb, ColorHarmony.Scheme.Complementary)[1];
        var baseH = Oklch(br, bg, bb).H;
        var compH = Oklch(comp.r, comp.g, comp.b).H;
        float dh = Math.Abs(((compH - baseH) % 360f + 360f) % 360f - 180f);
        Assert.True(dh < 30f, $"complement hue Δ off 180 by {dh}");
        // Lightness preserved (± the gamut round-trip).
        Assert.True(Math.Abs(L(br, bg, bb) - L(comp.r, comp.g, comp.b)) < 0.08f);
    }

    [Fact]
    public void Cosine_Rainbow_Deterministic_And_Periodic()
    {
        var (a, b, c, d) = CosinePalette.Rainbow;
        var at0 = CosinePalette.Sample(a, b, c, d, 0f);
        Assert.Equal((byte)255, at0.r);                       // .5+.5cos(0)=1
        Assert.Equal(at0, CosinePalette.Sample(a, b, c, d, 0f));   // deterministic
        Assert.Equal(at0, CosinePalette.Sample(a, b, c, d, 1f));   // c=1 → period 1

        var e = CosinePalette.Emit(a, b, c, d, 8);
        Assert.Equal(8, e.Length);
        foreach (var v in e) Assert.Equal(0xFFu, (v >> 24) & 0xFF);
    }

    [Fact]
    public void Bezier_TwoControls_Preserves_Endpoints()
    {
        var controls = new[] { ((byte)20, (byte)10, (byte)60), ((byte)250, (byte)230, (byte)120) };
        var s0 = BezierRamp.Sample(controls, 0f);
        var s1 = BezierRamp.Sample(controls, 1f);
        Assert.True(Math.Abs(s0.r - 20) <= 2 && Math.Abs(s0.b - 60) <= 2);
        Assert.True(Math.Abs(s1.r - 250) <= 2 && Math.Abs(s1.b - 120) <= 2);
        // Interior is a genuine blend (not either endpoint).
        var mid = BezierRamp.Sample(controls, 0.5f);
        Assert.True(mid.r > 20 && mid.r < 250);
    }

    [Fact]
    public void Bezier_LightnessCorrection_Monotonises_A_Dip()
    {
        // Dark middle control makes the RAW OkLab lightness dip below the start.
        var controls = new[]
        {
            ((byte)80, (byte)80, (byte)80),    // L ~0.55
            ((byte)10, (byte)10, (byte)10),    // L ~0.10 (dip)
            ((byte)240, (byte)240, (byte)240), // L ~0.97
        };
        // Raw: lightness is NOT monotonic (mid dips below the start).
        float rawStart = L(BezierRamp.Sample(controls, 0f, false).r, BezierRamp.Sample(controls, 0f, false).g, BezierRamp.Sample(controls, 0f, false).b);
        var rawMidC = BezierRamp.Sample(controls, 0.5f, false);
        Assert.True(L(rawMidC.r, rawMidC.g, rawMidC.b) < rawStart, "raw curve should dip");

        // Corrected: lightness rises monotonically end-to-end.
        float prev = -1f;
        for (int i = 0; i <= 8; i++)
        {
            var c = BezierRamp.Sample(controls, i / 8f, true);
            float l = L(c.r, c.g, c.b);
            Assert.True(l >= prev - 0.01f, $"corrected L not monotonic at {i / 8f}: {l} < {prev}");
            prev = l;
        }
        // Endpoints still preserved under correction.
        var e0 = BezierRamp.Sample(controls, 0f, true);
        var e1 = BezierRamp.Sample(controls, 1f, true);
        Assert.True(Math.Abs(e0.r - 80) <= 3 && Math.Abs(e1.r - 240) <= 3);
    }
}
