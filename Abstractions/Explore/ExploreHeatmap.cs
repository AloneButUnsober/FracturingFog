// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/ExploreHeatmap.cs
//
// Interest-score heatmap (#1240, follow-up to S6 #1190): the view split into a
// grid of cells, each scored by InterestScorer on its own probe render, drawn
// translucently over the frame so the user sees where Auto-explore would
// descend — and how the InterestWeights move that. Diagnostic only: it is
// composited into the live frame, never into an export.

using System;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Which number a heatmap cell shows.</summary>
public enum HeatmapTerm
{
    /// <summary>The interest score itself.</summary>
    Total,
    Boundary,
    Entropy,
    Dimension,
    /// <summary>How much the penalties keep (1 = no penalty).</summary>
    Penalty,
}

/// <summary>A scored grid over a view. <see cref="Values"/> is row-major
/// (row 0 at the top of the frame), each 0..1, NaN for a cell that could not
/// be probed. <see cref="CenterX"/>/<see cref="CenterY"/>/<see cref="Zoom"/>
/// identify the view it was computed for: drawn only while the view matches.</summary>
public sealed record ExploreHeatmap(
    int Cols, int Rows, float[] Values, double CenterX, double CenterY, double Zoom, string Label)
{
    /// <summary>Overlay opacity 0..1.</summary>
    public double Opacity { get; init; } = 0.5;

    /// <summary>Draw the value in each cell when the cell is this wide (px).</summary>
    public const int MinLabelCellPx = 56;

    /// <summary>True when the heatmap belongs to the view (centre / zoom).</summary>
    public bool Matches(double cx, double cy, double zoom)
        => cx == CenterX && cy == CenterY && zoom == Zoom;

    /// <summary>The number a cell shows for a score.</summary>
    public static float Value(in InterestScore s, HeatmapTerm term, InterestWeights weights) => (float)(term switch
    {
        HeatmapTerm.Boundary => s.Boundary,
        HeatmapTerm.Entropy => s.Entropy,
        HeatmapTerm.Dimension => s.Dimension,
        HeatmapTerm.Penalty => PenaltyKeep(s, weights),
        _ => s.Total,
    });

    // Total = weighted base × keep, so keep = Total / base (1 when base is 0).
    private static double PenaltyKeep(in InterestScore s, InterestWeights w)
    {
        double sum = SMath.Max(1e-12, w.BoundaryWeight + w.EntropyWeight + w.DimensionWeight);
        double b = (w.BoundaryWeight * s.Boundary + w.EntropyWeight * s.Entropy + w.DimensionWeight * s.Dimension) / sum;
        return b > 1e-9 ? SMath.Clamp(s.Total / b, 0, 1) : 1;
    }

    // Colour-blind-safe ramp (no red/green axis): deep blue → teal-grey →
    // yellow (cividis-like), distinguishable for protan / deutan vision.
    private static readonly (double T, byte R, byte G, byte B)[] Stops =
    {
        (0.00,   0,  32,  77),
        (0.25,  60,  77, 110),
        (0.50, 124, 123, 120),
        (0.75, 190, 175,  98),
        (1.00, 255, 234,  70),
    };

    /// <summary>Ramp colour for t in 0..1 (clamped).</summary>
    public static (byte R, byte G, byte B) Ramp(double t)
    {
        if (double.IsNaN(t)) t = 0;
        t = SMath.Clamp(t, 0, 1);
        for (int i = 1; i < Stops.Length; i++)
        {
            if (t > Stops[i].T) continue;
            var (t0, r0, g0, b0) = Stops[i - 1];
            var (t1, r1, g1, b1) = Stops[i];
            double u = (t - t0) / (t1 - t0);
            return ((byte)SMath.Round(r0 + (r1 - r0) * u), (byte)SMath.Round(g0 + (g1 - g0) * u), (byte)SMath.Round(b0 + (b1 - b0) * u));
        }
        return (Stops[^1].R, Stops[^1].G, Stops[^1].B);
    }
}
