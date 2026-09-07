// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.6 (PaletteBuilder-Design.md, #392) — the colour advisor. Composes
// the S10.1–S10.4 cores into guiding advisories (CVD-collapse, shadow-crush,
// histogram-waste, cycle-seam). Deterministic → the colour parity twin.

using System.Collections.Generic;
using System.Linq;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ColorAdvisorTests
{
    private static bool Has(List<ColorAdvice> a, ColorAdviceKind k) => a.Any(x => x.Kind == k);

    [Fact]
    public void ShadowCrushes_True_For_Dark_LowThird_False_For_Bright()
    {
        var dark = new List<(byte, byte, byte)> { (12, 10, 20), (20, 16, 12), (28, 24, 30), (200, 200, 200) };
        Assert.True(ColorAdvisor.ShadowCrushes(dark, 0.25f, out float ds) && ds < 0.04f);

        var bright = new List<(byte, byte, byte)> { (210, 90, 90), (90, 210, 90), (90, 90, 210), (240, 240, 240) };
        Assert.False(ColorAdvisor.ShadowCrushes(bright, 0.25f, out _));

        // Fewer than two low-third stops → never a crush.
        var two = new List<(byte, byte, byte)> { (10, 10, 10), (250, 250, 250) };
        Assert.False(ColorAdvisor.ShadowCrushes(two, 0.25f, out _));
    }

    [Fact]
    public void Review_Flags_Cvd_Collapse()
    {
        // Red/green collapse under deutan; use a threshold above the ~0.22 collapse.
        var rg = new List<(byte, byte, byte)> { (255, 0, 0), (0, 255, 0) };
        var advice = ColorAdvisor.Review(rg, cvdThreshold: 0.3f);
        Assert.True(Has(advice, ColorAdviceKind.CvdCollapse));

        // Stop numbers are 1-based in the message (the two stops read as 1 & 2, not 0 & 1).
        var msg = advice.First(a => a.Kind == ColorAdviceKind.CvdCollapse).Message;
        Assert.Contains("stops 1&2", msg);
        Assert.DoesNotContain("stops 0", msg);
    }

    [Fact]
    public void Review_Flags_ShadowCrush_On_Dark_Ramp()
    {
        var dark = new List<(byte, byte, byte)> { (12, 10, 20), (20, 16, 12), (28, 24, 30), (200, 200, 200) };
        var advice = ColorAdvisor.Review(dark);
        Assert.True(Has(advice, ColorAdviceKind.ShadowCrush));
    }

    [Fact]
    public void Review_Flags_HistogramWaste_When_Concentrated_Not_Uniform()
    {
        var stops = new List<(byte, byte, byte)> { (40, 40, 120), (120, 120, 200), (240, 240, 250) };

        var concentrated = new List<float>();
        for (int i = 0; i < 1000; i++) concentrated.Add(0.05f);
        var wasteHist = PaletteHistogram.Build(concentrated, 10);
        Assert.True(Has(ColorAdvisor.Review(stops, viewHistogram: wasteHist), ColorAdviceKind.HistogramWaste));

        var uniform = new List<float>();
        for (int i = 0; i < 1000; i++) uniform.Add(i / 1000f);
        var evenHist = PaletteHistogram.Build(uniform, 10);
        Assert.False(Has(ColorAdvisor.Review(stops, viewHistogram: evenHist), ColorAdviceKind.HistogramWaste));
    }

    [Fact]
    public void Review_Flags_CycleSeam_Only_When_Cycling_And_Mismatched()
    {
        var mismatched = new List<(byte, byte, byte)> { (20, 20, 120), (120, 120, 60), (240, 240, 40) };
        Assert.True(Has(ColorAdvisor.Review(mismatched, cycling: true), ColorAdviceKind.CycleSeam));
        // Not cycling → seam irrelevant.
        Assert.False(Has(ColorAdvisor.Review(mismatched, cycling: false), ColorAdviceKind.CycleSeam));
        // Cycling but ends match → seamless.
        var matched = new List<(byte, byte, byte)> { (30, 90, 160), (200, 120, 40), (30, 90, 160) };
        Assert.False(Has(ColorAdvisor.Review(matched, cycling: true), ColorAdviceKind.CycleSeam));
    }

    [Fact]
    public void Review_Clean_Palette_Has_No_Advice()
    {
        // Bright, luminance-monotonic greys: CVD-distinct (lightness), no dark tail, not
        // cycling, full-range histogram → nothing to flag.
        var clean = new List<(byte, byte, byte)> { (120, 120, 120), (160, 160, 160), (200, 200, 200), (245, 245, 245) };
        var uniform = new List<float>();
        for (int i = 0; i < 1000; i++) uniform.Add(i / 1000f);
        var hist = PaletteHistogram.Build(uniform, 10);
        var advice = ColorAdvisor.Review(clean, viewHistogram: hist, cycling: false);
        Assert.Empty(advice);
    }
}
