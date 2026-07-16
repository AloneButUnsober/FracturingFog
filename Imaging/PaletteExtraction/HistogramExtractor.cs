// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/HistogramExtractor.cs
//
// Dumb-but-fast palette: quantize each channel to 3 bits (8 levels =
// 512 RGB buckets), count pixels per bucket, then return the top-N
// buckets by population. Each bucket's color is the average of the
// pixels assigned to it. Often the right tool for posterized or limited-
// palette source art.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class HistogramExtractor : IPaletteExtractor
    {
        public string Name => "Histogram";

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();
            int k = Math.Max(2, opts.ColorCount);

            int[] count = new int[512];
            long[] rSum = new long[512];
            long[] gSum = new long[512];
            long[] bSum = new long[512];

            for (int i = 0; i < pixelCount; i++)
            {
                byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                int bucket = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
                count[bucket]++;
                rSum[bucket] += r;
                gSum[bucket] += g;
                bSum[bucket] += b;
            }

            var ordered = Enumerable.Range(0, 512)
                .Where(i => count[i] > 0)
                .OrderByDescending(i => count[i])
                .Take(k)
                .ToList();

            var result = new List<ExtractedColor>(ordered.Count);
            foreach (int i in ordered)
            {
                int n = count[i];
                result.Add(new ExtractedColor(
                    (byte)(rSum[i] / n), (byte)(gSum[i] / n), (byte)(bSum[i] / n), n));
            }
            return result;
        }
    }
}
