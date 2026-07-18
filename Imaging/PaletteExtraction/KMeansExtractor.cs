// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/KMeansExtractor.cs
//
// Lloyd's k-means in RGB, Lab, or HSL feature space. Initial centroids
// chosen via k-means++ seeding so clustering doesn't collapse on degenerate
// images. Output centroids are remapped back to RGB regardless of the
// clustering space.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class KMeansExtractor : IPaletteExtractor
    {
        public string Name => "K-Means";

        private const int MaxIterations = 24;
        private const float ConvergenceEpsilon = 0.5f;

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            int k = Math.Max(2, opts.ColorCount);
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();

            // Build feature vectors in the requested space.
            float[] feat = new float[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
            {
                byte r = rgb[i * 3];
                byte g = rgb[i * 3 + 1];
                byte b = rgb[i * 3 + 2];
                ToSpace(r, g, b, opts.Space, opts.GammaCorrect,
                    out feat[i * 3], out feat[i * 3 + 1], out feat[i * 3 + 2]);
            }

            // k-means++ init
            var rng = new Random(opts.RandomSeed);
            float[] centroids = new float[k * 3];
            int[] seedIdx = KMeansPlusPlusSeed(feat, pixelCount, k, rng);
            for (int c = 0; c < k; c++)
            {
                int i = seedIdx[c];
                centroids[c * 3] = feat[i * 3];
                centroids[c * 3 + 1] = feat[i * 3 + 1];
                centroids[c * 3 + 2] = feat[i * 3 + 2];
            }

            int[] assign = new int[pixelCount];
            int[] count = new int[k];
            double[] sumA = new double[k], sumB = new double[k], sumC = new double[k];

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                // Assign
                for (int i = 0; i < pixelCount; i++)
                {
                    float fa = feat[i * 3], fb = feat[i * 3 + 1], fc = feat[i * 3 + 2];
                    float best = float.MaxValue;
                    int bestC = 0;
                    for (int c = 0; c < k; c++)
                    {
                        float da = fa - centroids[c * 3];
                        float db = fb - centroids[c * 3 + 1];
                        float dc = fc - centroids[c * 3 + 2];
                        float d = da * da + db * db + dc * dc;
                        if (d < best) { best = d; bestC = c; }
                    }
                    assign[i] = bestC;
                }

                // Update
                Array.Clear(count); Array.Clear(sumA); Array.Clear(sumB); Array.Clear(sumC);
                for (int i = 0; i < pixelCount; i++)
                {
                    int c = assign[i];
                    sumA[c] += feat[i * 3];
                    sumB[c] += feat[i * 3 + 1];
                    sumC[c] += feat[i * 3 + 2];
                    count[c]++;
                }

                float shift = 0f;
                for (int c = 0; c < k; c++)
                {
                    if (count[c] == 0) continue;
                    float na = (float)(sumA[c] / count[c]);
                    float nb = (float)(sumB[c] / count[c]);
                    float nc = (float)(sumC[c] / count[c]);
                    float da = na - centroids[c * 3];
                    float db = nb - centroids[c * 3 + 1];
                    float dc = nc - centroids[c * 3 + 2];
                    shift += da * da + db * db + dc * dc;
                    centroids[c * 3] = na;
                    centroids[c * 3 + 1] = nb;
                    centroids[c * 3 + 2] = nc;
                }

                if (shift < ConvergenceEpsilon) break;
            }

            // Convert centroids back to RGB by averaging original RGB pixels
            // per cluster (more reliable than inverting Lab/HSL for the
            // returned swatch).
            long[] rSum = new long[k], gSum = new long[k], bSum = new long[k];
            int[] cCount = new int[k];
            for (int i = 0; i < pixelCount; i++)
            {
                int c = assign[i];
                rSum[c] += rgb[i * 3];
                gSum[c] += rgb[i * 3 + 1];
                bSum[c] += rgb[i * 3 + 2];
                cCount[c]++;
            }

            var result = new List<ExtractedColor>(k);
            for (int c = 0; c < k; c++)
            {
                if (cCount[c] == 0) continue;
                byte r = (byte)Math.Clamp(rSum[c] / cCount[c], 0, 255);
                byte g = (byte)Math.Clamp(gSum[c] / cCount[c], 0, 255);
                byte b = (byte)Math.Clamp(bSum[c] / cCount[c], 0, 255);
                result.Add(new ExtractedColor(r, g, b, cCount[c]));
            }
            return result;
        }

        private static int[] KMeansPlusPlusSeed(float[] feat, int n, int k, Random rng)
        {
            var seeds = new int[k];
            seeds[0] = rng.Next(n);
            double[] minDist = new double[n];
            for (int i = 0; i < n; i++) minDist[i] = double.MaxValue;

            for (int s = 1; s < k; s++)
            {
                int prev = seeds[s - 1];
                float pa = feat[prev * 3], pb = feat[prev * 3 + 1], pc = feat[prev * 3 + 2];
                double total = 0;
                for (int i = 0; i < n; i++)
                {
                    float da = feat[i * 3] - pa;
                    float db = feat[i * 3 + 1] - pb;
                    float dc = feat[i * 3 + 2] - pc;
                    double d = da * da + db * db + dc * dc;
                    if (d < minDist[i]) minDist[i] = d;
                    total += minDist[i];
                }

                double r = rng.NextDouble() * total;
                double acc = 0;
                int pick = n - 1;
                for (int i = 0; i < n; i++)
                {
                    acc += minDist[i];
                    if (acc >= r) { pick = i; break; }
                }
                seeds[s] = pick;
            }
            return seeds;
        }

        private static void ToSpace(byte r, byte g, byte b, PaletteColorSpace space, bool gammaCorrect,
                                    out float a, out float bb, out float c)
        {
            switch (space)
            {
                case PaletteColorSpace.Lab:
                    ColorSpaces.RgbToLab(r, g, b, out a, out bb, out c);
                    break;
                case PaletteColorSpace.OkLab:
                    // OkLab natively has L≈[0,1] a/b≈[-0.5,0.5]. Scale up so
                    // ConvergenceEpsilon (0.5 in feature units) keeps similar
                    // semantics across spaces — a 0.5 shift in raw OkLab L is
                    // huge, while 50 units of scaled OkLab matches the rough
                    // magnitude of Lab L.
                    ColorSpaces.RgbToOkLab(r, g, b, out float oL, out float oA, out float oB);
                    a = oL * 100f;
                    bb = oA * 100f;
                    c = oB * 100f;
                    break;
                case PaletteColorSpace.Hsl:
                    ColorSpaces.RgbToHsl(r, g, b, out float h, out float s, out float l);
                    float rad = h * MathF.PI / 180f;
                    a = MathF.Cos(rad) * s * 100f;
                    bb = MathF.Sin(rad) * s * 100f;
                    c = l * 100f;
                    break;
                default:
                    if (gammaCorrect)
                    {
                        // Linearize so euclidean distance matches physical light
                        // intensity. Scale back to [0,255] so feature magnitudes
                        // stay compatible with the convergence epsilon.
                        a = ColorSpaces.SrgbToLinear(r / 255f) * 255f;
                        bb = ColorSpaces.SrgbToLinear(g / 255f) * 255f;
                        c = ColorSpaces.SrgbToLinear(b / 255f) * 255f;
                    }
                    else
                    {
                        a = r; bb = g; c = b;
                    }
                    break;
            }
        }
    }
}
