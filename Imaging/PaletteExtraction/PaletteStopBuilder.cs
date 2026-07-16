// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/PaletteStopBuilder.cs
//
// Convert a flat list of ExtractedColor into List<ColorStopData> ready
// for the theme editor. Handles:
//   • Sort modes: hue / luminance / cluster-size / nearest-neighbor chain
//   • ΔE76 dedup (merge near-identical colors, weighted average)
//   • Stop positions: uniform OR weighted by cluster size

using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog.Models;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class PaletteStopBuilder
    {
        public StopSortMode Sort { get; set; } = StopSortMode.NearestNeighborChain;
        public float DedupDeltaE { get; set; } = 2f;
        public bool WeightedPositions { get; set; } = false;

        /// <summary>
        /// Distance formula used by the dedup pass. CIE76 (default) is the
        /// historical behaviour; CIEDE2000 is more perceptually accurate at
        /// the cost of trig calls per comparison.
        /// </summary>
        public DeltaEMetric DedupMetric { get; set; } = DeltaEMetric.DeltaE76;

        public List<ColorStopData> Build(IReadOnlyList<ExtractedColor> palette)
        {
            if (palette.Count == 0)
                return new List<ColorStopData>();

            var deduped = DedupByDeltaE(palette, DedupDeltaE, DedupMetric);
            var sorted = Sort switch
            {
                StopSortMode.Hue                  => SortByHue(deduped),
                StopSortMode.Luminance            => SortByLuminance(deduped),
                StopSortMode.ClusterSize          => deduped.OrderByDescending(c => c.Weight).ToList(),
                StopSortMode.NearestNeighborChain => SortByNNChain(deduped),
                _ => deduped.ToList(),
            };

            int n = sorted.Count;
            var stops = new List<ColorStopData>(n);

            if (!WeightedPositions || n < 2)
            {
                for (int i = 0; i < n; i++)
                {
                    float pos = n == 1 ? 0f : (float)i / (n - 1);
                    stops.Add(new ColorStopData
                    {
                        Position = pos,
                        R = sorted[i].R, G = sorted[i].G, B = sorted[i].B,
                    });
                }
                return stops;
            }

            // Weighted layout: each color occupies a fraction of [0,1]
            // proportional to its cluster weight. The stop is placed at the
            // midpoint of its slice.
            long totalWeight = sorted.Sum(c => (long)c.Weight);
            if (totalWeight <= 0)
                totalWeight = sorted.Count;
            double cursor = 0;
            for (int i = 0; i < n; i++)
            {
                double w = sorted[i].Weight <= 0 ? 1.0 : sorted[i].Weight;
                double slice = w / totalWeight;
                float pos = (float)Math.Clamp(cursor + slice * 0.5, 0.0, 1.0);
                stops.Add(new ColorStopData
                {
                    Position = pos,
                    R = sorted[i].R, G = sorted[i].G, B = sorted[i].B,
                });
                cursor += slice;
            }

            // Anchor the ends so the gradient covers the full [0,1] range.
            if (stops.Count >= 2)
            {
                stops[0].Position = 0f;
                stops[^1].Position = 1f;
            }
            return stops;
        }

        private static List<ExtractedColor> DedupByDeltaE(IReadOnlyList<ExtractedColor> input, float threshold, DeltaEMetric metric)
        {
            if (threshold <= 0f) return input.ToList();

            var labCache = new (float L, float a, float b)[input.Count];
            for (int i = 0; i < input.Count; i++)
                ColorSpaces.RgbToLab(input[i].R, input[i].G, input[i].B,
                    out labCache[i].L, out labCache[i].a, out labCache[i].b);

            // Iterate by descending weight so heavier colors absorb lighter ones.
            var order = Enumerable.Range(0, input.Count)
                .OrderByDescending(i => input[i].Weight)
                .ToList();

            var kept = new List<int>();
            var mergedR = new long[input.Count];
            var mergedG = new long[input.Count];
            var mergedB = new long[input.Count];
            var mergedW = new int[input.Count];

            foreach (int i in order)
            {
                int bestKept = -1;
                float bestDe = float.MaxValue;
                foreach (int j in kept)
                {
                    float de = metric == DeltaEMetric.DeltaE2000
                        ? ColorSpaces.DeltaE2000(
                            labCache[i].L, labCache[i].a, labCache[i].b,
                            labCache[j].L, labCache[j].a, labCache[j].b)
                        : ColorSpaces.DeltaE76(
                            labCache[i].L, labCache[i].a, labCache[i].b,
                            labCache[j].L, labCache[j].a, labCache[j].b);
                    if (de < bestDe) { bestDe = de; bestKept = j; }
                }

                if (bestKept >= 0 && bestDe <= threshold)
                {
                    mergedR[bestKept] += input[i].R * (long)input[i].Weight;
                    mergedG[bestKept] += input[i].G * (long)input[i].Weight;
                    mergedB[bestKept] += input[i].B * (long)input[i].Weight;
                    mergedW[bestKept] += input[i].Weight;
                }
                else
                {
                    kept.Add(i);
                    mergedR[i] = input[i].R * (long)input[i].Weight;
                    mergedG[i] = input[i].G * (long)input[i].Weight;
                    mergedB[i] = input[i].B * (long)input[i].Weight;
                    mergedW[i] = input[i].Weight;
                }
            }

            var result = new List<ExtractedColor>(kept.Count);
            foreach (int i in kept)
            {
                int w = Math.Max(1, mergedW[i]);
                result.Add(new ExtractedColor(
                    (byte)Math.Clamp(mergedR[i] / w, 0, 255),
                    (byte)Math.Clamp(mergedG[i] / w, 0, 255),
                    (byte)Math.Clamp(mergedB[i] / w, 0, 255),
                    mergedW[i]));
            }
            return result;
        }

        private static List<ExtractedColor> SortByHue(IReadOnlyList<ExtractedColor> colors)
        {
            return colors.OrderBy(c =>
            {
                ColorSpaces.RgbToHsl(c.R, c.G, c.B, out float h, out float s, out _);
                // Push very desaturated colors to the start so the rainbow
                // pickup doesn't wrap them into a random hue.
                return s < 0.05f ? -1f : h;
            }).ToList();
        }

        private static List<ExtractedColor> SortByLuminance(IReadOnlyList<ExtractedColor> colors)
            => colors.OrderBy(c => ColorSpaces.Luminance(c.R, c.G, c.B)).ToList();

        private static List<ExtractedColor> SortByNNChain(IReadOnlyList<ExtractedColor> colors)
        {
            int n = colors.Count;
            if (n <= 2) return colors.ToList();

            var lab = new (float L, float a, float b)[n];
            for (int i = 0; i < n; i++)
                ColorSpaces.RgbToLab(colors[i].R, colors[i].G, colors[i].B,
                    out lab[i].L, out lab[i].a, out lab[i].b);

            // Start at the darkest color (lowest L).
            int start = 0;
            for (int i = 1; i < n; i++)
                if (lab[i].L < lab[start].L) start = i;

            var visited = new bool[n];
            var order = new List<int> { start };
            visited[start] = true;

            for (int step = 1; step < n; step++)
            {
                int prev = order[^1];
                int best = -1;
                float bestD = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (visited[i]) continue;
                    float d = ColorSpaces.DeltaE76(
                        lab[prev].L, lab[prev].a, lab[prev].b,
                        lab[i].L, lab[i].a, lab[i].b);
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best < 0) break;
                order.Add(best);
                visited[best] = true;
            }

            var result = new List<ExtractedColor>(n);
            foreach (int i in order) result.Add(colors[i]);
            return result;
        }
    }
}
