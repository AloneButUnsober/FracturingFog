// Imaging/PaletteExtraction/MaterialPaletteExtractor.cs
//
// Android's Material palette: picks colour slots that best fit named
// targets (Vibrant, LightVibrant, DarkVibrant, Muted, LightMuted, DarkMuted,
// Dominant) rather than clustering for evenness. Each target has ideal
// (saturation, lightness) anchors and weights; every histogrammed bin is
// scored against every target and the winning bin per target becomes that
// slot. Same bin can't win two slots — score-weighted dedup applied.
//
// Output is up to 7 colours (or whatever subset is reachable in the image),
// trimmed to ColorCount. Honours Sort downstream — the named slots aren't
// preserved past this stage (Phase 4 named-stops will fix that).

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class MaterialPaletteExtractor : IPaletteExtractor
    {
        public string Name => "Material Palette";

        // (TargetSat, TargetLight, SatWeight, LightWeight, PopWeight)
        private static readonly (float ts, float tl, float ws, float wl, float wp)[] Targets =
        {
            (1.00f, 0.50f, 3f, 6f, 1f),   // Vibrant
            (1.00f, 0.74f, 3f, 6f, 1f),   // LightVibrant
            (1.00f, 0.26f, 3f, 6f, 1f),   // DarkVibrant
            (0.30f, 0.50f, 3f, 6f, 1f),   // Muted
            (0.30f, 0.74f, 3f, 6f, 1f),   // LightMuted
            (0.30f, 0.26f, 3f, 6f, 1f),   // DarkMuted
        };

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();

            // Histogram on a coarse 16³ grid → fast scoring.
            const int bits = 4, side = 1 << bits, shift = 8 - bits;
            int totalBins = side * side * side;
            int[] weight = new int[totalBins];
            long[] sumR = new long[totalBins];
            long[] sumG = new long[totalBins];
            long[] sumB = new long[totalBins];
            int maxWeight = 1;
            for (int i = 0; i < pixelCount; i++)
            {
                byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                int key = (r >> shift) | ((g >> shift) << bits) | ((b >> shift) << (bits * 2));
                weight[key]++;
                if (weight[key] > maxWeight) maxWeight = weight[key];
                sumR[key] += r; sumG[key] += g; sumB[key] += b;
            }

            int dominant = 0;
            for (int i = 1; i < totalBins; i++) if (weight[i] > weight[dominant]) dominant = i;

            var picked = new List<int>(Targets.Length + 1);
            picked.Add(dominant);

            foreach (var t in Targets)
            {
                int bestBin = -1;
                float bestScore = -1f;
                for (int i = 0; i < totalBins; i++)
                {
                    if (weight[i] == 0) continue;
                    if (picked.Contains(i)) continue;
                    byte r = (byte)(sumR[i] / weight[i]);
                    byte g = (byte)(sumG[i] / weight[i]);
                    byte b = (byte)(sumB[i] / weight[i]);
                    ColorSpaces.RgbToHsl(r, g, b, out _, out float s, out float l);
                    float ds = Math.Abs(s - t.ts);
                    float dl = Math.Abs(l - t.tl);
                    float pop = weight[i] / (float)maxWeight;
                    // Higher is better. Invert distances; add popularity boost.
                    float score = (1 - ds) * t.ws + (1 - dl) * t.wl + pop * t.wp;
                    if (score > bestScore) { bestScore = score; bestBin = i; }
                }
                if (bestBin >= 0) picked.Add(bestBin);
            }

            var result = new List<ExtractedColor>(picked.Count);
            foreach (int bin in picked)
            {
                if (weight[bin] == 0) continue;
                byte r = (byte)Math.Clamp(sumR[bin] / weight[bin], 0, 255);
                byte g = (byte)Math.Clamp(sumG[bin] / weight[bin], 0, 255);
                byte b = (byte)Math.Clamp(sumB[bin] / weight[bin], 0, 255);
                result.Add(new ExtractedColor(r, g, b, weight[bin]));
            }

            // Honour ColorCount as an upper bound — trim least-popular if over.
            if (result.Count > opts.ColorCount)
            {
                result.Sort((x, y) => y.Weight.CompareTo(x.Weight));
                result = result.GetRange(0, opts.ColorCount);
            }
            return result;
        }
    }
}
