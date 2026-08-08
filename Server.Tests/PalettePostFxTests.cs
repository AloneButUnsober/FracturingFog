// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #254 (sparkle) + #255 (seamless-under-rotation) — two opt-in palette post-fx
// baked into the gradient LUT. These lock in:
//   • both default OFF → the LUT is byte-identical to today
//   • sparkle brightens exactly the stride entries, leaves the rest untouched
//   • seamless closes the loop so the last LUT segment ramps back to the first
//     colour (palette cycling shows no seam)
//   • DTO round-trips through the data-driven map (Export == data in)
public class PalettePostFxTests
{
    private static ColorThemeData TwoStop(int sparkleStride = 0, float sparkleBoost = 0f,
                                          bool seamless = false)
        => new ColorThemeData
        {
            Name = "test",
            SparkleStride = sparkleStride,
            SparkleBoost = sparkleBoost,
            SeamlessCycle = seamless,
            Stops = new List<ColorStopData>
            {
                new() { Position = 0f, R = 255, G = 0, B = 0 },   // red
                new() { Position = 1f, R = 0,   G = 0, B = 255 }, // blue
            },
        };

    private static (int r, int g, int b) Sample(IColorMap map, float index)
    {
        uint argb = (uint)map.Map(index, 0f, 256);
        return ((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
    }

    [Fact]
    public void Defaults_Off_LutMatchesBaseline()
    {
        var baseMap = new DataDrivenGradient(TwoStop());
        var offMap = new DataDrivenGradient(TwoStop(sparkleStride: 0, sparkleBoost: 0f, seamless: false));
        for (int i = 0; i < 256; i++)
            Assert.Equal(Sample(baseMap, i), Sample(offMap, i));
    }

    [Fact]
    public void Sparkle_Brightens_Only_Stride_Entries()
    {
        var baseMap = new DataDrivenGradient(TwoStop());
        var sparkle = new DataDrivenGradient(TwoStop(sparkleStride: 16, sparkleBoost: 0.5f));

        // Entry 16 is a stride multiple → brighter than baseline.
        var b16 = Sample(baseMap, 16);
        var s16 = Sample(sparkle, 16);
        Assert.True(s16.r + s16.g + s16.b > b16.r + b16.g + b16.b);

        // Entry 5 is not a stride multiple → identical to baseline.
        Assert.Equal(Sample(baseMap, 5), Sample(sparkle, 5));
    }

    [Fact]
    public void Seamless_Closes_The_Loop()
    {
        var plain = new DataDrivenGradient(TwoStop(seamless: false));
        var seam = new DataDrivenGradient(TwoStop(seamless: true));

        // Just below t=1: plain stays near the last colour (blue); seamless ramps
        // back toward the first colour (red).
        var pEnd = Sample(plain, 255.9f);
        var sEnd = Sample(seam, 255.9f);

        Assert.True(pEnd.b > pEnd.r);   // plain → blue-ish at the seam
        Assert.True(sEnd.r > sEnd.b);   // seamless → red-ish (looped)
    }

    [Fact]
    public void PostFx_RoundTrips_Through_Map()
    {
        var map = new DataDrivenGradient(TwoStop(sparkleStride: 12, sparkleBoost: 0.4f, seamless: true));
        Assert.Equal(12, map.ExportSparkleStride);
        Assert.Equal(0.4f, map.ExportSparkleBoost, 3);
        Assert.True(map.ExportSeamlessCycle);
    }

    // #249 / IDEA-1 — live palette rotation (animate colour, not camera).
    [Fact]
    public void LivePaletteRotation_Zero_Is_Baseline_And_Wraps_At_One()
    {
        var map = new DataDrivenGradient(TwoStop());
        try
        {
            GradientColorMap.LivePaletteRotation = 0f;
            var baseline = new (int, int, int)[256];
            for (int i = 0; i < 256; i++) baseline[i] = Sample(map, i + 0.5f);

            // A full turn (1.0) wraps back to the baseline.
            GradientColorMap.LivePaletteRotation = 1f;
            for (int i = 0; i < 256; i++)
                Assert.Equal(baseline[i], Sample(map, i + 0.5f));

            // A quarter turn actually moves the colours.
            GradientColorMap.LivePaletteRotation = 0.25f;
            bool moved = false;
            for (int i = 0; i < 256; i++)
                if (Sample(map, i + 0.5f) != baseline[i]) { moved = true; break; }
            Assert.True(moved);
        }
        finally
        {
            GradientColorMap.LivePaletteRotation = 0f;
        }
    }

    [Fact]
    public void LivePaletteRotation_Half_Turn_Shifts_By_Half_The_Lut()
    {
        // Rotating by 0.5 maps index i to index i+128 (mod 256).
        var map = new DataDrivenGradient(TwoStop());
        try
        {
            GradientColorMap.LivePaletteRotation = 0f;
            var at192 = Sample(map, 192 + 0.5f);

            GradientColorMap.LivePaletteRotation = 0.5f;
            var at64 = Sample(map, 64 + 0.5f);   // 64 + 128 = 192

            Assert.Equal(at192, at64);
        }
        finally
        {
            GradientColorMap.LivePaletteRotation = 0f;
        }
    }
}
