// Imaging/PaletteExtraction/SpatialKMeansExtractor.cs
//
// k-means in a 5-D feature space: (L, a, b, scaled_x, scaled_y). The xy
// terms pull together pixels that are both colour-similar AND spatially
// adjacent, so a red barn and a red sunset don't collapse into a single
// "red" cluster. SpatialWeight on PaletteExtractionOptions blends the
// strength of the spatial influence: 0 = ignore position (pure Lab
// k-means), 1 = position weighted equal to colour.
//
// Needs SourceWidth × SourceHeight on the options object to reconstruct
// xy from pixel index (the input buffer must be scanline-ordered and
// un-filtered — falls back to colour-only k-means if those preconditions
// are not met).

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class SpatialKMeansExtractor : IPaletteExtractor
    {
        public string Name => "Spatial K-Means";

        private const int MaxIterations = 18;
        private const float ConvergenceEpsilon = 0.5f;

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();
            int k = Math.Max(2, opts.ColorCount);

            int w = opts.SourceWidth;
            int h = opts.SourceHeight;
            bool spatial = w > 0 && h > 0 && pixelCount == w * h && opts.SpatialWeight > 0;

            // Feature: L, a, b, sx, sy. sx/sy scaled so SpatialWeight=1 puts
            // their magnitude on par with the Lab L range (~100).
            int dim = spatial ? 5 : 3;
            float spatialScale = spatial ? 100f * opts.SpatialWeight : 0f;
            float[] feat = new float[pixelCount * dim];
            for (int i = 0; i < pixelCount; i++)
            {
                ColorSpaces.RgbToLab(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2],
                    out feat[i * dim], out feat[i * dim + 1], out feat[i * dim + 2]);
                if (spatial)
                {
                    int px = i % w;
                    int py = i / w;
                    feat[i * dim + 3] = px / (float)w * spatialScale;
                    feat[i * dim + 4] = py / (float)h * spatialScale;
                }
            }

            var rng = new Random(opts.RandomSeed);
            float[] centroids = new float[k * dim];
            int[] seedIdx = KmeansPlusPlus(feat, pixelCount, k, dim, rng);
            for (int c = 0; c < k; c++)
                for (int d = 0; d < dim; d++)
                    centroids[c * dim + d] = feat[seedIdx[c] * dim + d];

            int[] assign = new int[pixelCount];
            int[] count = new int[k];
            double[][] sums = new double[k][];
            for (int c = 0; c < k; c++) sums[c] = new double[dim];

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    int best = 0;
                    float bestD = float.MaxValue;
                    for (int c = 0; c < k; c++)
                    {
                        float d2 = 0;
                        for (int d = 0; d < dim; d++)
                        {
                            float diff = feat[i * dim + d] - centroids[c * dim + d];
                            d2 += diff * diff;
                        }
                        if (d2 < bestD) { bestD = d2; best = c; }
                    }
                    assign[i] = best;
                }

                Array.Clear(count);
                for (int c = 0; c < k; c++) Array.Clear(sums[c]);
                for (int i = 0; i < pixelCount; i++)
                {
                    int c = assign[i];
                    count[c]++;
                    for (int d = 0; d < dim; d++) sums[c][d] += feat[i * dim + d];
                }

                float shift = 0f;
                for (int c = 0; c < k; c++)
                {
                    if (count[c] == 0) continue;
                    for (int d = 0; d < dim; d++)
                    {
                        float nv = (float)(sums[c][d] / count[c]);
                        float diff = nv - centroids[c * dim + d];
                        shift += diff * diff;
                        centroids[c * dim + d] = nv;
                    }
                }
                if (shift < ConvergenceEpsilon) break;
            }

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

        private static int[] KmeansPlusPlus(float[] feat, int n, int k, int dim, Random rng)
        {
            var seeds = new int[k];
            seeds[0] = rng.Next(n);
            double[] minDist = new double[n];
            for (int i = 0; i < n; i++) minDist[i] = double.MaxValue;
            for (int s = 1; s < k; s++)
            {
                int prev = seeds[s - 1];
                double total = 0;
                for (int i = 0; i < n; i++)
                {
                    double d = 0;
                    for (int d0 = 0; d0 < dim; d0++)
                    {
                        double diff = feat[i * dim + d0] - feat[prev * dim + d0];
                        d += diff * diff;
                    }
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
