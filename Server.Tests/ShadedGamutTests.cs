// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.7 (PaletteBuilder-Design.md, #392) — preview under 3D lighting.
// Shade each swatch full-shadow → lit → specular IN LINEAR LIGHT and preview through
// the render's view transform (S2). Author in linear, preview through the tonemap.
// Deterministic → the colour parity twin.

using System.Collections.Generic;
using System.Linq;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ShadedGamutTests
{
    private static float L(byte r, byte g, byte b) => PerceptualRamp.RgbToOkLab(r, g, b).L;
    private static float L((byte r, byte g, byte b) c) => L(c.r, c.g, c.b);

    [Fact]
    public void Sweep_Length_And_Rises_From_Shadow_To_Specular()
    {
        var sweep = ShadedGamut.Sweep(180, 90, 60, 12);
        Assert.Equal(12, sweep.Length);

        // Luminance climbs monotonically shadow → lit → specular (small tolerance for
        // the sRGB encode rounding).
        float prev = -1f;
        foreach (var c in sweep)
        {
            float l = L(c);
            Assert.True(l >= prev - 0.02f, $"shaded gamut not rising: {l} < {prev}");
            prev = l;
        }
        // Shadow end is clearly darker than the specular end.
        Assert.True(L(sweep[0]) < L(sweep[^1]) - 0.3f);
    }

    [Fact]
    public void Dark_Swatch_Crushes_In_Shadow_Bright_Does_Not()
    {
        var dark = ShadedGamut.Analyze(25, 20, 30, 8);
        Assert.True(dark.CrushesInShadow, $"dark shadow end {dark.Sweep[0]} should crush");

        var bright = ShadedGamut.Analyze(200, 200, 200, 8);
        Assert.False(bright.CrushesInShadow);
    }

    [Fact]
    public void Bright_Swatch_Blows_In_Specular_Under_None_MidDark_Does_Not()
    {
        var bright = ShadedGamut.Analyze(235, 235, 235, 8, ViewTransform.None);
        Assert.True(bright.BlowsInSpecular, $"bright specular end {bright.Sweep[^1]} should blow to white");

        var midDark = ShadedGamut.Analyze(80, 80, 80, 8, ViewTransform.None);
        Assert.False(midDark.BlowsInSpecular);
    }

    [Fact]
    public void Tonemap_Rolls_Off_The_Specular_Highlight_That_None_Clips()
    {
        // A bright swatch's specular end hard-clips to white under None but is rolled
        // off (kept below white) by a real view transform — the S2 coupling.
        var none = ShadedGamut.Sweep(235, 235, 235, 8, ViewTransform.None);
        var agx = ShadedGamut.Sweep(235, 235, 235, 8, ViewTransform.AgX);

        var noneHi = none[^1];
        var agxHi = agx[^1];
        Assert.Equal(((byte)255, (byte)255, (byte)255), noneHi);   // None clips
        int noneSum = noneHi.r + noneHi.g + noneHi.b;
        int agxSum = agxHi.r + agxHi.g + agxHi.b;
        Assert.True(agxSum < noneSum, $"AgX ({agxSum}) should roll off below None ({noneSum})");
    }

    [Fact]
    public void Exposure_Brightens_The_Lit_Swatch()
    {
        // Index 7 of an 11-step sweep is t = 0.7 = the fully-lit point (diffuse = 1,
        // no specular yet). A dark albedo keeps it clear of the clip ceiling.
        var ev0 = ShadedGamut.Sweep(100, 100, 100, 11, ViewTransform.None, exposureEv: 0f);
        var ev1 = ShadedGamut.Sweep(100, 100, 100, 11, ViewTransform.None, exposureEv: 1f);
        Assert.True(L(ev1[7]) > L(ev0[7]), "one stop of exposure should brighten the lit swatch");
    }

    [Fact]
    public void AnalyzeRamp_One_Swatch_Per_Stop_In_Order()
    {
        var stops = new List<(byte, byte, byte)> { (20, 20, 30), (120, 80, 60), (240, 240, 240) };
        var rows = ShadedGamut.AnalyzeRamp(stops, 6);
        Assert.Equal(3, rows.Count);
        for (int i = 0; i < stops.Count; i++)
        {
            Assert.Equal(stops[i], (rows[i].Albedo.r, rows[i].Albedo.g, rows[i].Albedo.b));
            Assert.Equal(6, rows[i].Sweep.Length);
        }
    }

    [Fact]
    public void Sweep_Is_Deterministic()
    {
        var a = ShadedGamut.Sweep(170, 110, 200, 10, ViewTransform.Filmic, 0.5f);
        var b = ShadedGamut.Sweep(170, 110, 200, 10, ViewTransform.Filmic, 0.5f);
        Assert.True(a.SequenceEqual(b));
    }
}
