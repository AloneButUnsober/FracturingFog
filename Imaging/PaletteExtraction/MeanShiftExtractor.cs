// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/MeanShiftExtractor.cs
//
// Density-based mode discovery in Lab space. Seeds are a random subsample
// of pixels; each seed is iteratively shifted toward the weighted mean of
// neighbours within Bandwidth (Gaussian kernel) until it stops moving.
// Modes that converge near each other are merged. Top-K by aggregated
// weight is returned as the palette.
//
// Bandwidth lives on PaletteExtractionOptions and defaults to ~25 (Lab
// units — about a JND cluster radius). Smaller bandwidth = more, finer
// modes; larger = fewer, broader modes. Unlike k-means, the user doesn't
// need to pre-commit to a colour count — the algorithm finds whatever
// modes exist; ColorCount is used only as an upper cap on the output.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class MeanShiftExtractor : IPaletteExtractor
    {
        public string Name => "Mean Shift";

        private const int MaxIters = 24;
        private const float ConvergeEps = 0.25f;     // Lab units of shift
        private const float MergeMultiplier = 0.5f;  // merge if dist < Bandwidth * this

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();

            float bw = opts.Bandwidth <= 0 ? 25f : opts.Bandwidth;
            float bw2 = bw * bw;
            float twoSigma2 = 2f * bw2 / 4f; // sigma = bandwidth/2 → 2σ²

            float[] lab = new float[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
                ColorSpaces.RgbToLab(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2],
                    out lab[i * 3], out lab[i * 3 + 1], out lab[i * 3 + 2]);

            // Seed set: random subsample, cap at a sane upper bound so the
            // O(seeds × pixels) loops stay fast.
            int seedCount = Math.Min(pixelCount, Math.Max(48, opts.ColorCount * 6));
            var rng = new Random(opts.RandomSeed);
            int[] seedIdx = RandomDistinct(pixelCount, seedCount, rng);

            var modes = new List<(float L, float a, float b, double weight)>(seedCount);

            for (int s = 0; s < seedCount; s++)
            {
                float L = lab[seedIdx[s] * 3];
                float a = lab[seedIdx[s] * 3 + 1];
                float b = lab[seedIdx[s] * 3 + 2];

                for (int iter = 0; iter < MaxIters; iter++)
                {
                    double sumW = 0, sL = 0, sA = 0, sB = 0;
                    for (int i = 0; i < pixelCount; i++)
                    {
                        float dL = lab[i * 3] - L;
                        float dA = lab[i * 3 + 1] - a;
                        float dB = lab[i * 3 + 2] - b;
                        float d2 = dL * dL + dA * dA + dB * dB;
                        if (d2 > bw2 * 4) continue;            // outside influence radius
                        float wt = MathF.Exp(-d2 / twoSigma2);
                        sumW += wt;
                        sL += wt * lab[i * 3];
                        sA += wt * lab[i * 3 + 1];
                        sB += wt * lab[i * 3 + 2];
                    }
                    if (sumW <= 0) break;
                    float nL = (float)(sL / sumW);
                    float nA = (float)(sA / sumW);
                    float nB = (float)(sB / sumW);
                    float shift2 = (nL - L) * (nL - L) + (nA - a) * (nA - a) + (nB - b) * (nB - b);
                    L = nL; a = nA; b = nB;
                    if (shift2 < ConvergeEps * ConvergeEps) break;
                }

                modes.Add((L, a, b, 0));
            }

            // Merge nearby modes.
            float mergeR2 = (bw * MergeMultiplier) * (bw * MergeMultiplier);
            var merged = new List<(float L, float a, float b, double w)>();
            foreach (var m in modes)
            {
                int hit = -1;
                for (int i = 0; i < merged.Count; i++)
                {
                    float dL = merged[i].L - m.L;
                    float dA = merged[i].a - m.a;
                    float dB = merged[i].b - m.b;
                    if (dL * dL + dA * dA + dB * dB < mergeR2) { hit = i; break; }
                }
                if (hit < 0) merged.Add((m.L, m.a, m.b, 1));
                else merged[hit] = (merged[hit].L, merged[hit].a, merged[hit].b, merged[hit].w + 1);
            }

            // Assign every pixel to its nearest merged mode to recover an
            // accurate weight + RGB-mean per mode.
            int nModes = merged.Count;
            long[] rSum = new long[nModes], gSum = new long[nModes], bSum = new long[nModes];
            int[] count = new int[nModes];
            for (int i = 0; i < pixelCount; i++)
            {
                float pL = lab[i * 3], pA = lab[i * 3 + 1], pB = lab[i * 3 + 2];
                int best = 0;
                float bestD = float.MaxValue;
                for (int m = 0; m < nModes; m++)
                {
                    float dL = merged[m].L - pL;
                    float dA = merged[m].a - pA;
                    float dB = merged[m].b - pB;
                    float d2 = dL * dL + dA * dA + dB * dB;
                    if (d2 < bestD) { bestD = d2; best = m; }
                }
                rSum[best] += rgb[i * 3];
                gSum[best] += rgb[i * 3 + 1];
                bSum[best] += rgb[i * 3 + 2];
                count[best]++;
            }

            // Take top-K by count, cap at ColorCount.
            var ranked = new List<(int idx, int w)>();
            for (int m = 0; m < nModes; m++) if (count[m] > 0) ranked.Add((m, count[m]));
            ranked.Sort((x, y) => y.w.CompareTo(x.w));
            int take = Math.Min(opts.ColorCount, ranked.Count);

            var result = new List<ExtractedColor>(take);
            for (int i = 0; i < take; i++)
            {
                int m = ranked[i].idx;
                byte r = (byte)Math.Clamp(rSum[m] / count[m], 0, 255);
                byte g = (byte)Math.Clamp(gSum[m] / count[m], 0, 255);
                byte bb = (byte)Math.Clamp(bSum[m] / count[m], 0, 255);
                result.Add(new ExtractedColor(r, g, bb, count[m]));
            }
            return result;
        }

        private static int[] RandomDistinct(int n, int k, Random rng)
        {
            if (k >= n) { var all = new int[n]; for (int i = 0; i < n; i++) all[i] = i; return all; }
            var seen = new HashSet<int>(k);
            var arr = new int[k];
            int got = 0;
            while (got < k)
            {
                int v = rng.Next(n);
                if (seen.Add(v)) arr[got++] = v;
            }
            return arr;
        }
    }
}
