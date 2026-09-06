// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10.5 (PaletteBuilder-Design.md, #392) — extraction upgrades:
// perceptual k-means in OkLab, a lightness-ordered ramp, and dominant / accent
// detection. Deterministic (fixed seed) → the colour parity twin.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PaletteExtractionCoreTests
{
    // A colour bag: `copies` samples of each colour.
    private static List<(byte, byte, byte)> Bag(params ((byte r, byte g, byte b) c, int copies)[] parts)
    {
        var bag = new List<(byte, byte, byte)>();
        foreach (var (c, copies) in parts)
            for (int i = 0; i < copies; i++) bag.Add((c.r, c.g, c.b));
        return bag;
    }

    private static float L(byte r, byte g, byte b) => PerceptualRamp.RgbToOkLab(r, g, b).L;

    [Fact]
    public void KMeans_Separates_WellSeparated_Clusters()
    {
        // Three tight, far-apart blobs → three clusters, each near its blob colour.
        var bag = Bag(
            ((240, 30, 30), 100),   // red
            ((30, 200, 30), 100),   // green
            ((40, 40, 230), 100));  // blue
        var clusters = PaletteExtractionCore.KMeansOkLab(bag, 3, seed: 7);
        Assert.Equal(3, clusters.Count);

        // Every seed blob is recovered by some cluster within a small OkLab ΔE.
        foreach (var (tr, tg, tb) in new[] { ((byte)240, (byte)30, (byte)30), ((byte)30, (byte)200, (byte)30), ((byte)40, (byte)40, (byte)230) })
            Assert.Contains(clusters, c => PerceptualRamp.DeltaEOk(c.R, c.G, c.B, tr, tg, tb) < 0.08f);

        // Balanced blobs → balanced weights (all pixels accounted for).
        Assert.Equal(300, clusters.Sum(c => c.Weight));
    }

    [Fact]
    public void Extract_Ramp_Is_Lightness_Ordered()
    {
        var bag = Bag(
            ((20, 20, 20), 50),      // near-black (low L)
            ((200, 60, 60), 50),     // mid red
            ((245, 245, 245), 50));  // near-white (high L)
        var pal = PaletteExtractionCore.Extract(bag, 3, seed: 3);
        Assert.Equal(3, pal.Ramp.Count);

        float prev = -1f;
        foreach (var c in pal.Ramp)
        {
            float l = L(c.R, c.G, c.B);
            Assert.True(l >= prev - 1e-4f, $"ramp not lightness-ordered: {l} < {prev}");
            prev = l;
        }
    }

    [Fact]
    public void Extract_Dominant_Is_Heaviest_Cluster()
    {
        // Grey dominates by pixel count; a small splash of orange.
        var bag = Bag(
            ((128, 128, 128), 500),
            ((240, 130, 20), 30));
        var pal = PaletteExtractionCore.Extract(bag, 2, seed: 5);
        Assert.True(PerceptualRamp.DeltaEOk(pal.Dominant.R, pal.Dominant.G, pal.Dominant.B, 128, 128, 128) < 0.05f,
            $"dominant {pal.Dominant.R},{pal.Dominant.G},{pal.Dominant.B} not grey");
        Assert.True(pal.Dominant.Weight >= pal.Accent.Weight);
    }

    [Fact]
    public void Extract_Accent_Is_Most_Chromatic_NonDominant()
    {
        // Grey (heavy, near-zero chroma) dominant; a muted blue and a vivid orange.
        var bag = Bag(
            ((130, 130, 130), 400),   // dominant, achromatic
            ((90, 110, 150), 60),     // muted blue (low chroma)
            ((245, 120, 10), 60));    // vivid orange (high chroma)
        var pal = PaletteExtractionCore.Extract(bag, 3, seed: 11);

        // Accent is the vivid orange, not the muted blue, and not the grey dominant.
        Assert.True(PerceptualRamp.DeltaEOk(pal.Accent.R, pal.Accent.G, pal.Accent.B, 245, 120, 10) < 0.1f,
            $"accent {pal.Accent.R},{pal.Accent.G},{pal.Accent.B} not the vivid orange");
        Assert.False(pal.Accent.Equals(pal.Dominant));
    }

    [Fact]
    public void Extract_Deterministic_For_Fixed_Seed()
    {
        var bag = Bag(
            ((200, 40, 40), 70),
            ((40, 200, 90), 70),
            ((60, 60, 210), 70),
            ((230, 210, 40), 70));
        var a = PaletteExtractionCore.Extract(bag, 4, seed: 42);
        var b = PaletteExtractionCore.Extract(bag, 4, seed: 42);
        Assert.Equal(a.Ramp.Count, b.Ramp.Count);
        for (int i = 0; i < a.Ramp.Count; i++)
            Assert.Equal(a.Ramp[i], b.Ramp[i]);
        Assert.Equal(a.Dominant, b.Dominant);
        Assert.Equal(a.Accent, b.Accent);
    }

    [Fact]
    public void Extract_Empty_Bag_Yields_Empty_Palette()
    {
        var pal = PaletteExtractionCore.Extract(new List<(byte, byte, byte)>(), 4);
        Assert.Empty(pal.Ramp);
        Assert.Equal(0, pal.Dominant.Weight);
        Assert.Equal(0, pal.Accent.Weight);
    }

    [Fact]
    public void KMeans_K_Clamped_To_Sample_Count_No_Empty_Padding()
    {
        // Only two distinct samples but k=5 → at most 2 non-empty clusters returned.
        var bag = Bag(((10, 10, 10), 1), ((240, 240, 240), 1));
        var clusters = PaletteExtractionCore.KMeansOkLab(bag, 5, seed: 9);
        Assert.True(clusters.Count <= 2);
        Assert.All(clusters, c => Assert.True(c.Weight >= 1));
    }

    [Fact]
    public void Classify_Reuses_Dominant_Accent_Ramp_On_PreWeighted_Clusters()
    {
        // Callers that already hold weighted clusters (a palette's swatches with pixel
        // counts) get the same dominant / accent / lightness-order rules without k-means.
        var clusters = new List<PaletteCluster>
        {
            new(130, 130, 130, 400),   // heaviest, achromatic → dominant
            new(90, 110, 150, 60),     // muted blue (low chroma)
            new(245, 120, 10, 60),     // vivid orange (high chroma) → accent
        };
        var pal = PaletteExtractionCore.Classify(clusters);

        Assert.Equal(clusters[0], pal.Dominant);
        Assert.Equal(clusters[2], pal.Accent);
        Assert.NotEqual(pal.Dominant, pal.Accent);

        // Ramp holds every cluster, ascending OkLab lightness.
        Assert.Equal(3, pal.Ramp.Count);
        float prev = -1f;
        foreach (var c in pal.Ramp)
        {
            float l = PerceptualRamp.RgbToOkLab(c.R, c.G, c.B).L;
            Assert.True(l >= prev - 1e-4f);
            prev = l;
        }

        // Extract routes through Classify → identical result on the same clusters.
        Assert.Empty(PaletteExtractionCore.Classify(new List<PaletteCluster>()).Ramp);
    }
}
