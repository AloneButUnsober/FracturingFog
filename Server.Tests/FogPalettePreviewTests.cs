// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.8 (PaletteBuilder-Design.md, #392) — fog / volumetric palette
// preview. Composite a ramp over a backdrop across optical depth (the render's
// #180/#185 fog remap), warn on wash-out, and derive a fog-optimised sub-ramp.
// Deterministic → the colour parity twin.

using System.Collections.Generic;
using System.Linq;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FogPalettePreviewTests
{
    private static float L(byte r, byte g, byte b) => PerceptualRamp.RgbToOkLab(r, g, b).L;
    private static float L((byte r, byte g, byte b) c) => L(c.r, c.g, c.b);

    private static readonly List<(byte, byte, byte)> BlueCyanWhite =
        new() { ((byte)10, (byte)10, (byte)60), ((byte)40, (byte)140, (byte)200), ((byte)240, (byte)250, (byte)255) };

    [Fact]
    public void FogSweep_Thin_Is_Backdrop_Thick_Is_Fog_End()
    {
        var bg = ((byte)5, (byte)5, (byte)15);
        var sweep = FogPalettePreview.FogSweep(BlueCyanWhite, 16, bg.Item1, bg.Item2, bg.Item3);
        Assert.Equal(16, sweep.Length);

        // Thin end (τ=0, T=1) is the untouched backdrop.
        Assert.True(PerceptualRamp.DeltaEOk(sweep[0].r, sweep[0].g, sweep[0].b, bg.Item1, bg.Item2, bg.Item3) < 0.02f,
            $"thin end {sweep[0]} should be the backdrop");

        // Thick end is dominated by the fog ramp's far colour, far from the backdrop.
        var rampEnd = BlueCyanWhite[^1];
        float toEnd = PerceptualRamp.DeltaEOk(sweep[^1].r, sweep[^1].g, sweep[^1].b, rampEnd.Item1, rampEnd.Item2, rampEnd.Item3);
        float toBg = PerceptualRamp.DeltaEOk(sweep[^1].r, sweep[^1].g, sweep[^1].b, bg.Item1, bg.Item2, bg.Item3);
        Assert.True(toEnd < toBg, $"thick end {sweep[^1]} should read as fog, not backdrop");
    }

    [Fact]
    public void FogInscatter_Keys_On_Optical_Depth_Fraction()
    {
        var thin = FogPalettePreview.FogInscatter(BlueCyanWhite, 0f);
        var thick = FogPalettePreview.FogInscatter(BlueCyanWhite, 1f);
        Assert.True(PerceptualRamp.DeltaEOk(thin.r, thin.g, thin.b, 10, 10, 60) < 0.02f);
        Assert.True(PerceptualRamp.DeltaEOk(thick.r, thick.g, thick.b, 240, 250, 255) < 0.02f);
        // Thicker fog samples deeper (brighter) into this ramp.
        Assert.True(L(thick) > L(thin));
    }

    [Fact]
    public void WashesOut_When_Ramp_Matches_Backdrop_Not_When_Vivid()
    {
        // Fog the same colour as the backdrop → no visible haze gradient.
        var flat = new List<(byte, byte, byte)> { ((byte)8, (byte)8, (byte)16), ((byte)8, (byte)8, (byte)16) };
        Assert.True(FogPalettePreview.WashesOut(flat, 8, 8, 16, out float flatSpan) && flatSpan < 0.08f);

        // A bright, saturated ramp over a dark sky reads clearly.
        Assert.False(FogPalettePreview.WashesOut(BlueCyanWhite, 5, 5, 15, out _));
    }

    [Fact]
    public void FogOptimizedSubRamp_Culls_Dark_Tail_And_Rises()
    {
        var src = new List<(byte, byte, byte)>
        {
            ((byte)2, (byte)2, (byte)4),      // near-black → culled (adds no light)
            ((byte)40, (byte)140, (byte)200),
            ((byte)245, (byte)250, (byte)255),
        };
        var ramp = FogPalettePreview.FogOptimizedSubRamp(src, 6);
        Assert.Equal(6, ramp.Length);

        float prev = -1f;
        foreach (var packed in ramp)
        {
            byte r = (byte)((packed >> 16) & 0xFF), g = (byte)((packed >> 8) & 0xFF), b = (byte)(packed & 0xFF);
            float l = L(r, g, b);
            Assert.True(l >= 0.15f - 0.02f, $"fog sub-ramp kept a too-dark stop (L={l})");
            Assert.True(l >= prev - 0.01f, $"fog sub-ramp not luminance-ascending: {l} < {prev}");
            prev = l;
        }
    }

    [Fact]
    public void FogOptimizedSubRamp_All_Dark_Source_Is_Lifted()
    {
        var allDark = new List<(byte, byte, byte)> { ((byte)5, (byte)5, (byte)5), ((byte)10, (byte)10, (byte)10) };
        var ramp = FogPalettePreview.FogOptimizedSubRamp(allDark, 5);
        Assert.Equal(5, ramp.Length);
        // Lifted toward white — the last stop is (near-)white and far brighter than the source.
        byte lr = (byte)((ramp[^1] >> 16) & 0xFF), lg = (byte)((ramp[^1] >> 8) & 0xFF), lb = (byte)(ramp[^1] & 0xFF);
        Assert.True(L(lr, lg, lb) > 0.9f, $"lifted fog ramp should end bright, got L={L(lr, lg, lb)}");
    }

    [Fact]
    public void FogSweep_Is_Deterministic()
    {
        var a = FogPalettePreview.FogSweep(BlueCyanWhite, 12, 5, 5, 15, density: 1.5f);
        var b = FogPalettePreview.FogSweep(BlueCyanWhite, 12, 5, 5, 15, density: 1.5f);
        Assert.True(a.SequenceEqual(b));
    }
}
