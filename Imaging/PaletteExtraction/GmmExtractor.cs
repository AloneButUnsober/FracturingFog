// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/GmmExtractor.cs
//
// Gaussian Mixture in Lab with isotropic (single scalar variance per
// component) covariances. Soft assignment via the E-step lets a pixel
// contribute to multiple clusters proportional to its membership
// probability — produces smoother centroids than hard k-means on images
// with overlapping colour regions.
//
// Init: k-means++ seeds + 3 Lloyd iterations to get a good starting point.
// Then 8 EM iterations. Output centroid colour = soft-weighted RGB mean
// across all pixels (not just the hardest assigned ones).

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class GmmExtractor : IPaletteExtractor
    {
        public string Name => "GMM (EM)";

        private const int LloydInit = 3;
        private const int EmIters = 8;

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();
            int k = Math.Max(2, opts.ColorCount);

            float[] lab = new float[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
                ColorSpaces.RgbToLab(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2],
                    out lab[i * 3], out lab[i * 3 + 1], out lab[i * 3 + 2]);

            // Init centroids via kmeans++ seeding.
            var rng = new Random(opts.RandomSeed);
            int[] seedIdx = KmeansPlusPlus(lab, pixelCount, k, rng);
            float[] mu = new float[k * 3];
            for (int c = 0; c < k; c++)
            {
                int i = seedIdx[c];
                mu[c * 3] = lab[i * 3];
                mu[c * 3 + 1] = lab[i * 3 + 1];
                mu[c * 3 + 2] = lab[i * 3 + 2];
            }

            // Lloyd warm-up.
            int[] assign = new int[pixelCount];
            for (int iter = 0; iter < LloydInit; iter++)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    float fa = lab[i * 3], fb = lab[i * 3 + 1], fc = lab[i * 3 + 2];
                    int best = 0; float bestD = float.MaxValue;
                    for (int c = 0; c < k; c++)
                    {
                        float da = fa - mu[c * 3], db = fb - mu[c * 3 + 1], dc = fc - mu[c * 3 + 2];
                        float d = da * da + db * db + dc * dc;
                        if (d < bestD) { bestD = d; best = c; }
                    }
                    assign[i] = best;
                }
                long[] cnt = new long[k];
                double[] sa = new double[k], sb = new double[k], sc = new double[k];
                for (int i = 0; i < pixelCount; i++)
                {
                    int c = assign[i];
                    cnt[c]++; sa[c] += lab[i * 3]; sb[c] += lab[i * 3 + 1]; sc[c] += lab[i * 3 + 2];
                }
                for (int c = 0; c < k; c++)
                {
                    if (cnt[c] == 0) continue;
                    mu[c * 3]     = (float)(sa[c] / cnt[c]);
                    mu[c * 3 + 1] = (float)(sb[c] / cnt[c]);
                    mu[c * 3 + 2] = (float)(sc[c] / cnt[c]);
                }
            }

            // EM with isotropic σ² per component and uniform priors.
            double[] sigma2 = new double[k];
            double[] pi = new double[k];
            for (int c = 0; c < k; c++) { sigma2[c] = 400; pi[c] = 1.0 / k; }

            double[] resp = new double[k];
            double[] sumResp = new double[k];
            double[] sumLR = new double[k], sumLG = new double[k], sumLB = new double[k];
            double[] sumSq = new double[k];

            for (int iter = 0; iter < EmIters; iter++)
            {
                Array.Clear(sumResp); Array.Clear(sumLR); Array.Clear(sumLG); Array.Clear(sumLB); Array.Clear(sumSq);
                for (int i = 0; i < pixelCount; i++)
                {
                    float fa = lab[i * 3], fb = lab[i * 3 + 1], fc = lab[i * 3 + 2];
                    double total = 0;
                    for (int c = 0; c < k; c++)
                    {
                        double da = fa - mu[c * 3], db = fb - mu[c * 3 + 1], dc = fc - mu[c * 3 + 2];
                        double d2 = da * da + db * db + dc * dc;
                        double pdf = Math.Exp(-d2 / (2 * sigma2[c])) / Math.Pow(2 * Math.PI * sigma2[c], 1.5);
                        resp[c] = pi[c] * pdf;
                        total += resp[c];
                    }
                    if (total <= 0) continue;
                    for (int c = 0; c < k; c++)
                    {
                        double r = resp[c] / total;
                        sumResp[c] += r;
                        sumLR[c] += r * fa;
                        sumLG[c] += r * fb;
                        sumLB[c] += r * fc;
                        double da = fa - mu[c * 3], db = fb - mu[c * 3 + 1], dc = fc - mu[c * 3 + 2];
                        sumSq[c] += r * (da * da + db * db + dc * dc);
                    }
                }
                for (int c = 0; c < k; c++)
                {
                    if (sumResp[c] < 1e-9) continue;
                    mu[c * 3]     = (float)(sumLR[c] / sumResp[c]);
                    mu[c * 3 + 1] = (float)(sumLG[c] / sumResp[c]);
                    mu[c * 3 + 2] = (float)(sumLB[c] / sumResp[c]);
                    sigma2[c] = Math.Max(4, sumSq[c] / (3 * sumResp[c]));
                    pi[c] = sumResp[c] / pixelCount;
                }
            }

            // Output: soft-weighted RGB means.
            double[] rR = new double[k], rG = new double[k], rB = new double[k];
            double[] rW = new double[k];
            for (int i = 0; i < pixelCount; i++)
            {
                float fa = lab[i * 3], fb = lab[i * 3 + 1], fc = lab[i * 3 + 2];
                double total = 0;
                for (int c = 0; c < k; c++)
                {
                    double da = fa - mu[c * 3], db = fb - mu[c * 3 + 1], dc = fc - mu[c * 3 + 2];
                    double d2 = da * da + db * db + dc * dc;
                    double pdf = Math.Exp(-d2 / (2 * sigma2[c])) / Math.Pow(2 * Math.PI * sigma2[c], 1.5);
                    resp[c] = pi[c] * pdf;
                    total += resp[c];
                }
                if (total <= 0) continue;
                for (int c = 0; c < k; c++)
                {
                    double r = resp[c] / total;
                    rR[c] += r * rgb[i * 3];
                    rG[c] += r * rgb[i * 3 + 1];
                    rB[c] += r * rgb[i * 3 + 2];
                    rW[c] += r;
                }
            }

            var result = new List<ExtractedColor>(k);
            for (int c = 0; c < k; c++)
            {
                if (rW[c] < 1e-6) continue;
                byte r = (byte)Math.Clamp((int)Math.Round(rR[c] / rW[c]), 0, 255);
                byte g = (byte)Math.Clamp((int)Math.Round(rG[c] / rW[c]), 0, 255);
                byte b = (byte)Math.Clamp((int)Math.Round(rB[c] / rW[c]), 0, 255);
                result.Add(new ExtractedColor(r, g, b, (int)Math.Max(1, rW[c])));
            }
            return result;
        }

        private static int[] KmeansPlusPlus(float[] feat, int n, int k, Random rng)
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
                double acc = 0; int pick = n - 1;
                for (int i = 0; i < n; i++)
                {
                    acc += minDist[i];
                    if (acc >= r) { pick = i; break; }
                }
                seeds[s] = pick;
            }
            return seeds;
        }
    }
}
