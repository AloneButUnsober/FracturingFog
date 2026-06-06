// Imaging/PaletteExtraction/MiniBatchKMeansExtractor.cs
//
// Stochastic k-means (Sculley 2010). Each iteration samples a small random
// mini-batch from the pixel buffer, assigns each sample to its nearest
// centroid, and applies a per-centroid learning-rate update (1/visitCount)
// instead of the full Lloyd re-mean. Converges in far fewer pixel touches
// than vanilla k-means at the cost of slightly noisier centroids.
//
// Practical for big images where the downsample-to-256 already used by
// BitmapSampler is still too slow (e.g. user disables downsampling, or
// runs Compare-All at 1024 max-dim).

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class MiniBatchKMeansExtractor : IPaletteExtractor
    {
        public string Name => "Mini-Batch K-Means";

        private const int Iterations = 60;
        private const int BatchSize = 1024;

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();
            int k = Math.Max(2, opts.ColorCount);

            float[] feat = new float[pixelCount * 3];
            for (int i = 0; i < pixelCount; i++)
            {
                byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                ToSpaceRgb(r, g, b, opts.Space, opts.GammaCorrect,
                    out feat[i * 3], out feat[i * 3 + 1], out feat[i * 3 + 2]);
            }

            var rng = new Random(opts.RandomSeed);
            float[] centroids = new float[k * 3];
            int[] seedIdx = SeedPlusPlus(feat, pixelCount, k, rng);
            for (int c = 0; c < k; c++)
            {
                int i = seedIdx[c];
                centroids[c * 3] = feat[i * 3];
                centroids[c * 3 + 1] = feat[i * 3 + 1];
                centroids[c * 3 + 2] = feat[i * 3 + 2];
            }

            int[] visits = new int[k];
            int batch = Math.Min(BatchSize, pixelCount);
            int[] batchIdx = new int[batch];

            for (int iter = 0; iter < Iterations; iter++)
            {
                for (int i = 0; i < batch; i++) batchIdx[i] = rng.Next(pixelCount);

                // Assign and update with per-centroid learning rate η = 1 / visits.
                for (int s = 0; s < batch; s++)
                {
                    int i = batchIdx[s];
                    float fa = feat[i * 3], fb = feat[i * 3 + 1], fc = feat[i * 3 + 2];
                    int best = 0;
                    float bestD = float.MaxValue;
                    for (int c = 0; c < k; c++)
                    {
                        float da = fa - centroids[c * 3];
                        float db = fb - centroids[c * 3 + 1];
                        float dc = fc - centroids[c * 3 + 2];
                        float d = da * da + db * db + dc * dc;
                        if (d < bestD) { bestD = d; best = c; }
                    }
                    visits[best]++;
                    float eta = 1f / visits[best];
                    centroids[best * 3]     = (1 - eta) * centroids[best * 3]     + eta * fa;
                    centroids[best * 3 + 1] = (1 - eta) * centroids[best * 3 + 1] + eta * fb;
                    centroids[best * 3 + 2] = (1 - eta) * centroids[best * 3 + 2] + eta * fc;
                }
            }

            // Final assignment + RGB averaging.
            long[] rSum = new long[k], gSum = new long[k], bSum = new long[k];
            int[] cCount = new int[k];
            for (int i = 0; i < pixelCount; i++)
            {
                float fa = feat[i * 3], fb = feat[i * 3 + 1], fc = feat[i * 3 + 2];
                int best = 0;
                float bestD = float.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    float da = fa - centroids[c * 3];
                    float db = fb - centroids[c * 3 + 1];
                    float dc = fc - centroids[c * 3 + 2];
                    float d = da * da + db * db + dc * dc;
                    if (d < bestD) { bestD = d; best = c; }
                }
                rSum[best] += rgb[i * 3];
                gSum[best] += rgb[i * 3 + 1];
                bSum[best] += rgb[i * 3 + 2];
                cCount[best]++;
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

        private static int[] SeedPlusPlus(float[] feat, int n, int k, Random rng)
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

        private static void ToSpaceRgb(byte r, byte g, byte b, PaletteColorSpace space, bool gamma,
                                       out float a, out float bb, out float c)
        {
            switch (space)
            {
                case PaletteColorSpace.Lab:
                    ColorSpaces.RgbToLab(r, g, b, out a, out bb, out c); break;
                case PaletteColorSpace.OkLab:
                    ColorSpaces.RgbToOkLab(r, g, b, out float oL, out float oA, out float oB);
                    a = oL * 100f; bb = oA * 100f; c = oB * 100f; break;
                case PaletteColorSpace.Hsl:
                    ColorSpaces.RgbToHsl(r, g, b, out float h, out float s, out float l);
                    float rad = h * MathF.PI / 180f;
                    a = MathF.Cos(rad) * s * 100f;
                    bb = MathF.Sin(rad) * s * 100f;
                    c = l * 100f; break;
                default:
                    if (gamma)
                    {
                        a = ColorSpaces.SrgbToLinear(r / 255f) * 255f;
                        bb = ColorSpaces.SrgbToLinear(g / 255f) * 255f;
                        c = ColorSpaces.SrgbToLinear(b / 255f) * 255f;
                    }
                    else { a = r; bb = g; c = b; }
                    break;
            }
        }
    }
}
