// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/InterestScorer.cs
//
// Interesting-location finder S6 (#1190): a theory-free "how interesting is
// this view" score, computed from a small probe render of any 2D family. No
// dynamics knowledge is used — only the rendered field — so it works for
// Burning Ship, Newton basins, Lyapunov, IFS and User Equation alike.
//
// Terms (each 0..1):
//   boundary  — share of pixels on an inside/outside edge or a steep band jump
//               (filament density), as a preference band: too few is a blob,
//               too many is pixel noise;
//   entropy   — Shannon entropy of the band histogram (many bands vs flat);
//   dimension — box-count dimension of the boundary mask, scored by closeness
//               to a preferred value (1 = smooth curve, 2 = noise).
// Penalties (multiplicative): inside fraction, one dominant escaped band
// (trivially escaped / empty), late-escaping pixels (maxIter saturation).
// Every weight and threshold is a tunable on InterestWeights.

using System;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>What a probe field's values mean.</summary>
public enum ProbeFieldKind
{
    /// <summary>Smooth (continuous) iteration count; 0 = did not escape.</summary>
    SmoothIterations,
    /// <summary>Rendered luminance 0..1 (families with no iteration field).</summary>
    Luminance,
}

/// <summary>A small probe render: one scalar per pixel, row-major.
/// <see cref="Inside"/> marks pixels that never escaped; null when the family
/// has no such notion (luminance fields).</summary>
public sealed class ProbeField
{
    public ProbeField(int width, int height, float[] value, ProbeFieldKind kind,
                      bool[]? inside = null, double maxIterations = 0)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (value.Length < width * height) throw new ArgumentException("Field too short.", nameof(value));
        if (inside != null && inside.Length < width * height) throw new ArgumentException("Mask too short.", nameof(inside));
        Width = width; Height = height; Value = value; Kind = kind; Inside = inside;
        MaxIterations = maxIterations;
    }

    public int Width { get; }
    public int Height { get; }
    public float[] Value { get; }
    public ProbeFieldKind Kind { get; }
    public bool[]? Inside { get; }
    /// <summary>Iteration cap of the probe (saturation check); 0 = n/a.</summary>
    public double MaxIterations { get; }

    /// <summary>A smooth-iteration field whose in-set pixels read 0 (the
    /// <c>SmoothBuffer</c> convention of every escape-time calculator).</summary>
    public static ProbeField FromSmooth(int width, int height, float[] smooth, double maxIterations)
    {
        int n = width * height;
        var inside = new bool[n];
        for (int i = 0; i < n; i++) inside[i] = smooth[i] == 0f || float.IsNaN(smooth[i]);
        return new ProbeField(width, height, smooth, ProbeFieldKind.SmoothIterations, inside, maxIterations);
    }

    /// <summary>A luminance field from 0xAARRGGBB pixels (Rec. 709 weights).</summary>
    public static ProbeField FromArgb(int width, int height, uint[] argb)
    {
        int n = width * height;
        var lum = new float[n];
        for (int i = 0; i < n; i++)
        {
            uint p = argb[i];
            lum[i] = (0.2126f * ((p >> 16) & 0xFF) + 0.7152f * ((p >> 8) & 0xFF) + 0.0722f * (p & 0xFF)) / 255f;
        }
        return new ProbeField(width, height, lum, ProbeFieldKind.Luminance);
    }
}

/// <summary>Tunable weights and thresholds of <see cref="InterestScorer"/>.</summary>
public sealed record InterestWeights
{
    public double BoundaryWeight { get; init; } = 0.4;
    public double EntropyWeight { get; init; } = 0.3;
    public double DimensionWeight { get; init; } = 0.3;

    /// <summary>Band width in iterations (smooth fields).</summary>
    public double IterationBand { get; init; } = 1.0;
    /// <summary>Band width in luminance (luminance fields): 1/32.</summary>
    public double LuminanceBand { get; init; } = 1.0 / 32;
    /// <summary>A neighbour jump of at least this many bands marks a boundary.</summary>
    public double EdgeBands { get; init; } = 1.0;

    /// <summary>Boundary fraction where the boundary term reaches 1.</summary>
    public double BoundaryTarget { get; init; } = 0.3;
    /// <summary>Boundary fraction above which the view counts as noise; the
    /// term falls linearly to 0 at an all-boundary view.</summary>
    public double BoundaryNoise { get; init; } = 0.6;

    /// <summary>Band count whose uniform histogram scores entropy 1.</summary>
    public int EntropyBands { get; init; } = 64;

    /// <summary>Preferred box-count dimension and its Gaussian width.</summary>
    public double DimensionCentre { get; init; } = 1.7;
    public double DimensionWidth { get; init; } = 0.35;
    /// <summary>Boundary pixels needed for full trust in the dimension fit.</summary>
    public int DimensionMinPixels { get; init; } = 24;

    /// <summary>Inside fraction above which the penalty starts (full at 1).</summary>
    public double InsideMax { get; init; } = 0.6;
    /// <summary>Share of all pixels in one escaped band above which the
    /// penalty starts (full at 1): a trivially escaped or empty view.</summary>
    public double FlatMax { get; init; } = 0.6;
    /// <summary>An escaped pixel at or above this fraction of maxIter counts
    /// as near saturation.</summary>
    public double SaturationLevel { get; init; } = 0.9;
    /// <summary>Near-saturation share above which the penalty starts; full at
    /// 4× this share.</summary>
    public double SaturationMax { get; init; } = 0.02;

    public static InterestWeights Default { get; } = new();
}

/// <summary>A view's interest score and its parts (all 0..1, except the raw
/// <see cref="BoxDimension"/>).</summary>
public readonly record struct InterestScore(
    double Total,
    double Boundary, double Entropy, double Dimension,
    double BoundaryFraction, double BoxDimension,
    double InsideFraction, double FlatFraction, double SaturatedFraction)
{
    public override string ToString()
        => $"{Total:F3} (edge {Boundary:F2}, entropy {Entropy:F2}, dim {BoxDimension:F2}->{Dimension:F2}; "
         + $"inside {InsideFraction:P0}, flat {FlatFraction:P0}, sat {SaturatedFraction:P0})";
}

public static class InterestScorer
{
    public static InterestScore Score(ProbeField f, InterestWeights? weights = null)
    {
        var w = weights ?? InterestWeights.Default;
        int W = f.Width, H = f.Height, n = W * H;
        var v = f.Value;
        var inside = f.Inside;
        double band = f.Kind == ProbeFieldKind.Luminance ? w.LuminanceBand : w.IterationBand;
        double edge = w.EdgeBands * band;

        // ── Boundary mask: inside/outside change or a steep band jump to the
        //    right / lower neighbour (both pixels marked).
        var mask = new bool[n];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (x + 1 < W) Mark(i, i + 1);
                if (y + 1 < H) Mark(i, i + W);
            }
        }
        void Mark(int a, int b)
        {
            bool ia = inside != null && inside[a], ib = inside != null && inside[b];
            if (ia != ib || (!ia && SMath.Abs(v[a] - v[b]) >= edge)) { mask[a] = true; mask[b] = true; }
        }
        int edgeCount = 0;
        for (int i = 0; i < n; i++) if (mask[i]) edgeCount++;
        double bf = (double)edgeCount / n;
        double boundary = bf <= w.BoundaryTarget ? bf / w.BoundaryTarget
                        : bf <= w.BoundaryNoise ? 1.0
                        : SMath.Max(0, (1 - bf) / (1 - w.BoundaryNoise));

        // ── Band histogram over escaped pixels (entropy, flatness, saturation).
        var counts = new System.Collections.Generic.Dictionary<long, int>();
        int escaped = 0, saturated = 0;
        double satAt = f.MaxIterations > 0 ? w.SaturationLevel * f.MaxIterations : double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            if (inside != null && inside[i]) continue;
            float val = v[i];
            if (!float.IsFinite(val)) continue;
            escaped++;
            if (val >= satAt) saturated++;
            long b = (long)SMath.Floor(val / band);
            counts[b] = counts.TryGetValue(b, out int c) ? c + 1 : 1;
        }
        double entropy = 0;
        int dominant = 0;
        if (escaped > 0)
        {
            double h = 0;
            foreach (int c in counts.Values)
            {
                double p = (double)c / escaped;
                h -= p * SMath.Log(p);
                if (c > dominant) dominant = c;
            }
            // A luminance field has only 1/band distinct bands to fill.
            int bands = f.Kind == ProbeFieldKind.Luminance
                ? SMath.Min(w.EntropyBands, (int)SMath.Ceiling(1 / band))
                : w.EntropyBands;
            entropy = SMath.Min(1, h / SMath.Log(SMath.Max(2, bands)));
        }

        // ── Box-count dimension of the boundary mask.
        double dim = edgeCount > 0 ? BoxDimension(mask, W, H) : 0;
        double dimPref = edgeCount == 0 ? 0
            : SMath.Exp(-SMath.Pow((dim - w.DimensionCentre) / w.DimensionWidth, 2))
              * SMath.Min(1, (double)edgeCount / SMath.Max(1, w.DimensionMinPixels));

        double insideFrac = inside == null ? 0 : (double)(n - escaped) / n;
        double flatFrac = (double)dominant / n;
        double satFrac = (double)saturated / n;

        double baseScore = (w.BoundaryWeight * boundary + w.EntropyWeight * entropy + w.DimensionWeight * dimPref)
                           / SMath.Max(1e-12, w.BoundaryWeight + w.EntropyWeight + w.DimensionWeight);
        double keep = (1 - Ramp(insideFrac, w.InsideMax, 1))
                    * (1 - Ramp(flatFrac, w.FlatMax, 1))
                    * (1 - Ramp(satFrac, w.SaturationMax, 4 * w.SaturationMax));
        double total = SMath.Clamp(baseScore * keep, 0, 1);

        return new InterestScore(total, boundary, entropy, dimPref, bf, dim, insideFrac, flatFrac, satFrac);
    }

    // 0 at or below lo, 1 at or above hi, linear between.
    private static double Ramp(double x, double lo, double hi)
        => x <= lo ? 0 : x >= hi ? 1 : (x - lo) / (hi - lo);

    /// <summary>Box-count dimension: least-squares slope of ln N(s) against
    /// ln(1/s) over box sizes s = 1, 2, 4, … up to a quarter of the short side.</summary>
    public static double BoxDimension(bool[] mask, int width, int height)
    {
        int maxBox = SMath.Max(1, SMath.Min(width, height) / 4);
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int m = 0;
        for (int s = 1; s <= maxBox; s *= 2)
        {
            int count = 0;
            for (int by = 0; by < height; by += s)
                for (int bx = 0; bx < width; bx += s)
                {
                    bool hit = false;
                    for (int y = by; y < SMath.Min(by + s, height) && !hit; y++)
                        for (int x = bx; x < SMath.Min(bx + s, width); x++)
                            if (mask[y * width + x]) { hit = true; break; }
                    if (hit) count++;
                }
            if (count == 0) continue;
            double lx = SMath.Log(1.0 / s), ly = SMath.Log(count);
            sx += lx; sy += ly; sxx += lx * lx; sxy += lx * ly; m++;
        }
        if (m < 2) return 0;
        double den = m * sxx - sx * sx;
        return den == 0 ? 0 : SMath.Clamp((m * sxy - sx * sy) / den, 0, 2);
    }
}
