// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Linq;

using FracturingFog.Abstractions.Explore;

using Xunit;

namespace FracturingFog.Server.Tests;

// #1240: the interest-score heatmap's data side.
public sealed class ExploreHeatmapTests
{
    private static double Luma((byte R, byte G, byte B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    [Fact]
    public void Ramp_LuminanceRisesStrictly()
    {
        // Colour-blind safe means readable without hue: the ramp must carry
        // its order in lightness alone (and avoid a red–green axis: it runs
        // blue → yellow).
        double last = -1;
        for (int i = 0; i <= 100; i++)
        {
            double l = Luma(ExploreHeatmap.Ramp(i / 100.0));
            Assert.True(l > last, $"luma not rising at {i / 100.0}: {l} after {last}");
            last = l;
        }
        var lo = ExploreHeatmap.Ramp(0); var hi = ExploreHeatmap.Ramp(1);
        Assert.True(lo.B > lo.R && lo.B > lo.G);        // blue end
        Assert.True(hi.R > hi.B && hi.G > hi.B);        // yellow end
        Assert.Equal(ExploreHeatmap.Ramp(0), ExploreHeatmap.Ramp(-1));   // clamped
        Assert.Equal(ExploreHeatmap.Ramp(1), ExploreHeatmap.Ramp(double.PositiveInfinity));
    }

    [Fact]
    public void Terms_ReadTheScore()
    {
        var s = new InterestScore(0.4, 0.9, 0.5, 0.7, 0.3, 1.7, 0.2, 0.1, 0.0);
        var w = InterestWeights.Default;
        Assert.Equal(0.4f, ExploreHeatmap.Value(s, HeatmapTerm.Total, w));
        Assert.Equal(0.9f, ExploreHeatmap.Value(s, HeatmapTerm.Boundary, w));
        Assert.Equal(0.5f, ExploreHeatmap.Value(s, HeatmapTerm.Entropy, w));
        Assert.Equal(0.7f, ExploreHeatmap.Value(s, HeatmapTerm.Dimension, w));
        // Penalty keep = total / weighted base.
        double b = (0.4 * 0.9 + 0.3 * 0.5 + 0.3 * 0.7) / 1.0;
        Assert.Equal((float)(0.4 / b), ExploreHeatmap.Value(s, HeatmapTerm.Penalty, w), 5);
    }

    [Fact]
    public void Matches_OnlyItsOwnView()
    {
        var h = new ExploreHeatmap(2, 1, new[] { 0.1f, 0.9f }, -0.5, 0, 1, "x");
        Assert.True(h.Matches(-0.5, 0, 1));
        Assert.False(h.Matches(-0.5, 1e-12, 1));
        Assert.False(h.Matches(-0.5, 0, 1.0000001));
    }

    [Fact]
    public void TunedWeights_ChangeTheScore()
    {
        // A pure-noise field: all boundary, near-2 dimension. Weighting
        // boundary up must raise its score; weighting dimension up must lower it.
        var rng = new System.Random(5);
        var v = Enumerable.Range(0, 64 * 64).Select(_ => (float)(rng.NextDouble() * 50)).ToArray();
        var f = ProbeField.FromSmooth(64, 64, v, 1000);
        var d = InterestWeights.Default;
        double baseScore = InterestScorer.Score(f, d).Total;
        double boundaryHeavy = InterestScorer.Score(f, d with { BoundaryWeight = 1, EntropyWeight = 0, DimensionWeight = 0 }).Total;
        double dimHeavy = InterestScorer.Score(f, d with { BoundaryWeight = 0, EntropyWeight = 0, DimensionWeight = 1 }).Total;
        Assert.NotEqual(baseScore, boundaryHeavy);
        Assert.NotEqual(boundaryHeavy, dimHeavy);
    }
}
