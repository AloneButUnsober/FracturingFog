// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.1 (PaletteBuilder-Design.md, #392) — the perceptual core.
// PerceptualRamp authors / interpolates / measures colour in OkLab / OKLCH and
// emits perceptually-even, luminance-structured ramps. The design's parity-twin
// discipline for colour: the conversions are deterministic → epsilon-stable
// round-trips, and the luminance monotonicity that serves BOTH 3D form-reading and
// colourblind vision is asserted directly.

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PerceptualRampTests
{
    private static readonly (byte r, byte g, byte b)[] Samples =
    {
        (0, 0, 0), (255, 255, 255), (255, 0, 0), (0, 255, 0), (0, 0, 255),
        (68, 1, 84), (253, 231, 37), (18, 120, 200), (200, 150, 90), (127, 127, 127),
    };

    [Fact]
    public void Srgb_OkLab_RoundTrip_Within_A_Byte_Or_Two()
    {
        foreach (var (r, g, b) in Samples)
        {
            var (L, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
            var (r2, g2, b2) = PerceptualRamp.OkLabToRgb(L, a, bb);
            Assert.True(Math.Abs(r - r2) <= 2, $"R {r}->{r2}");
            Assert.True(Math.Abs(g - g2) <= 2, $"G {g}->{g2}");
            Assert.True(Math.Abs(b - b2) <= 2, $"B {b}->{b2}");
        }
    }

    [Fact]
    public void OkLab_Oklch_RoundTrip_Is_Epsilon_Stable()
    {
        foreach (var (r, g, b) in Samples)
        {
            var (L, a, bb) = PerceptualRamp.RgbToOkLab(r, g, b);
            var (L2, C, H) = PerceptualRamp.OkLabToOklch(L, a, bb);
            var (L3, a3, b3) = PerceptualRamp.OklchToOkLab(L2, C, H);
            Assert.Equal(L, L3, 4);
            Assert.Equal(a, a3, 4);
            Assert.Equal(bb, b3, 4);
        }
    }

    [Fact]
    public void DeltaEOk_Zero_Symmetric_Positive()
    {
        Assert.Equal(0f, PerceptualRamp.DeltaEOk(120, 90, 30, 120, 90, 30), 5);
        float ab = PerceptualRamp.DeltaEOk(10, 20, 30, 200, 180, 60);
        float ba = PerceptualRamp.DeltaEOk(200, 180, 60, 10, 20, 30);
        Assert.Equal(ab, ba, 5);
        Assert.True(ab > 0f);
        // Black↔white is the largest lightness gap → a big ΔE.
        Assert.True(PerceptualRamp.DeltaEOk(0, 0, 0, 255, 255, 255) > 0.9f);
    }

    [Fact]
    public void SampleOkLab_Clamps_Endpoints_And_Interpolates()
    {
        var stops = new[]
        {
            new PerceptualRamp.Stop(0f, 0, 0, 0),
            new PerceptualRamp.Stop(1f, 255, 255, 255),
        };
        Assert.Equal((byte)0, PerceptualRamp.SampleOkLab(stops, -1f).r);      // t<=0 → first
        Assert.Equal((byte)255, PerceptualRamp.SampleOkLab(stops, 2f).r);     // t>=1 → last
        var mid = PerceptualRamp.SampleOkLab(stops, 0.5f);
        // OkLab L=0.5 neutral → sRGB ~99 (a perceptual grey, darker than the sRGB
        // midpoint 128; OkLab L is not CIE L*). Neutral stays neutral, strictly interior.
        Assert.True(mid.r > 60 && mid.r < 190, $"mid grey {mid.r}");
        Assert.Equal(mid.r, mid.g);
        Assert.Equal(mid.g, mid.b);
    }

    [Fact]
    public void Viridis_And_Cividis_Are_Luminance_Monotonic()
    {
        AssertLuminanceMonotonic(PerceptualRamp.Viridis, 0.01f);
        AssertLuminanceMonotonic(PerceptualRamp.Cividis, 0.03f);
    }

    [Fact]
    public void Viridis_Cividis_Endpoints_Match_Anchors()
    {
        Assert.Equal(((byte)68, (byte)1, (byte)84), PerceptualRamp.Viridis(0f));
        Assert.Equal(((byte)253, (byte)231, (byte)37), PerceptualRamp.Viridis(1f));
        Assert.Equal(((byte)0, (byte)32, (byte)76), PerceptualRamp.Cividis(0f));
        Assert.Equal(((byte)255, (byte)233, (byte)69), PerceptualRamp.Cividis(1f));
    }

    [Fact]
    public void UniformLuminanceRamp_Is_Strictly_Monotonic_And_Preserves_Endpoints()
    {
        var ramp = PerceptualRamp.UniformLuminanceRamp(20, 10, 60, 250, 230, 120, 16);
        Assert.Equal(16, ramp.Length);
        // Endpoints preserved (± a byte from the gamut clip).
        Assert.True(Near(ramp[0], 20, 10, 60));
        Assert.True(Near(ramp[15], 250, 230, 120));
        // Lightness strictly increases (CVD-safe / 3D-form-safe by construction).
        float prev = -1f;
        foreach (var argb in ramp)
        {
            var (L, _, _) = PerceptualRamp.RgbToOkLab((byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
            Assert.True(L > prev, $"L not increasing: {L} <= {prev}");
            prev = L;
        }
    }

    [Fact]
    public void Emit_Count_Endpoints_And_Opaque()
    {
        var e = PerceptualRamp.Emit(PerceptualRamp.Viridis, 8);
        Assert.Equal(8, e.Length);
        foreach (var c in e) Assert.Equal(0xFFu, (c >> 24) & 0xFF);   // opaque
        Assert.Equal(0xFF000000u | (68u << 16) | (1u << 8) | 84u, e[0]);
        Assert.Equal(0xFF000000u | (253u << 16) | (231u << 8) | 37u, e[7]);
    }

    private static void AssertLuminanceMonotonic(Func<float, (byte r, byte g, byte b)> ramp, float tol)
    {
        float prev = -1f;
        for (int i = 0; i <= 64; i++)
        {
            var (r, g, b) = ramp(i / 64f);
            var (L, _, _) = PerceptualRamp.RgbToOkLab(r, g, b);
            Assert.True(L >= prev - tol, $"L dipped at t={i / 64f}: {L} < {prev}");
            prev = L;
        }
    }

    private static bool Near(uint argb, byte r, byte g, byte b)
        => Math.Abs((int)((argb >> 16) & 0xFF) - r) <= 2
        && Math.Abs((int)((argb >> 8) & 0xFF) - g) <= 2
        && Math.Abs((int)(argb & 0xFF) - b) <= 2;
}
