// Imaging/PaletteExtraction/DbscanExtractor.cs
//
// Density-based spatial clustering in Lab. Runs on a 32³ Lab histogram of
// the pixel buffer (binning keeps the algorithm O(B² + N) where B is bin
// count, not O(N²) — DBSCAN on raw pixel arrays is too slow for typical
// 256×256 thumbnails). Each bin is treated as one weighted point.
//
// DbscanEpsilon — neighbour radius in Lab units (default ~8).
// DbscanMinPts  — minimum pixel weight a bin needs (summed within ε) to be
//                 a core point (default 20).
//
// Output is one swatch per cluster, capped at ColorCount and sorted by
// total pixel weight. Bins that never become core and aren't reachable
// from any core are noise — discarded.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class DbscanExtractor : IPaletteExtractor
    {
        public string Name => "DBSCAN";

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();

            float eps = opts.DbscanEpsilon <= 0 ? 8f : opts.DbscanEpsilon;
            int minPts = opts.DbscanMinPts <= 0 ? 20 : opts.DbscanMinPts;
            float eps2 = eps * eps;

            // Build Lab histogram bins (point per non-empty bin).
            const int bits = 5, shift = 8 - bits;
            var binIndex = new Dictionary<int, int>();
            var binWeight = new List<int>();
            var binSumR = new List<long>();
            var binSumG = new List<long>();
            var binSumB = new List<long>();
            var binLab = new List<(float L, float a, float b)>();

            for (int i = 0; i < pixelCount; i++)
            {
                byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                int key = (r >> shift) | ((g >> shift) << bits) | ((b >> shift) << (bits * 2));
                if (!binIndex.TryGetValue(key, out int idx))
                {
                    idx = binWeight.Count;
                    binIndex[key] = idx;
                    binWeight.Add(0);
                    binSumR.Add(0); binSumG.Add(0); binSumB.Add(0);
                    binLab.Add((0, 0, 0));
                }
                binWeight[idx] = binWeight[idx] + 1;
                binSumR[idx] += r;
                binSumG[idx] += g;
                binSumB[idx] += b;
            }

            int nBins = binWeight.Count;
            for (int i = 0; i < nBins; i++)
            {
                byte r = (byte)Math.Clamp(binSumR[i] / binWeight[i], 0, 255);
                byte g = (byte)Math.Clamp(binSumG[i] / binWeight[i], 0, 255);
                byte b = (byte)Math.Clamp(binSumB[i] / binWeight[i], 0, 255);
                ColorSpaces.RgbToLab(r, g, b, out float L, out float a, out float bb);
                binLab[i] = (L, a, bb);
            }

            // Precompute neighbours per bin.
            var neighbours = new List<int>[nBins];
            for (int i = 0; i < nBins; i++) neighbours[i] = new List<int>();
            for (int i = 0; i < nBins; i++)
            {
                var (Li, ai, bi) = binLab[i];
                for (int j = i + 1; j < nBins; j++)
                {
                    var (Lj, aj, bj) = binLab[j];
                    float dL = Li - Lj, dA = ai - aj, dB = bi - bj;
                    if (dL * dL + dA * dA + dB * dB <= eps2)
                    {
                        neighbours[i].Add(j);
                        neighbours[j].Add(i);
                    }
                }
            }

            // DBSCAN over bins.
            int[] cluster = new int[nBins];          // 0 = unvisited, -1 = noise, ≥1 = cluster id
            int curCluster = 0;
            for (int i = 0; i < nBins; i++)
            {
                if (cluster[i] != 0) continue;
                int weightSum = binWeight[i];
                foreach (int nb in neighbours[i]) weightSum += binWeight[nb];
                if (weightSum < minPts) { cluster[i] = -1; continue; }

                curCluster++;
                cluster[i] = curCluster;
                var queue = new Queue<int>(neighbours[i]);
                while (queue.Count > 0)
                {
                    int q = queue.Dequeue();
                    if (cluster[q] == -1) cluster[q] = curCluster;
                    if (cluster[q] != 0) continue;
                    cluster[q] = curCluster;

                    int qWeight = binWeight[q];
                    foreach (int nb in neighbours[q]) qWeight += binWeight[nb];
                    if (qWeight >= minPts)
                        foreach (int nb in neighbours[q]) if (cluster[nb] == 0 || cluster[nb] == -1) queue.Enqueue(nb);
                }
            }

            if (curCluster == 0) return Array.Empty<ExtractedColor>();

            long[] rSum = new long[curCluster + 1];
            long[] gSum = new long[curCluster + 1];
            long[] bSum = new long[curCluster + 1];
            int[] count = new int[curCluster + 1];
            for (int i = 0; i < nBins; i++)
            {
                int c = cluster[i];
                if (c <= 0) continue;
                rSum[c] += binSumR[i];
                gSum[c] += binSumG[i];
                bSum[c] += binSumB[i];
                count[c] += binWeight[i];
            }

            var ranked = new List<(int c, int w)>();
            for (int c = 1; c <= curCluster; c++) if (count[c] > 0) ranked.Add((c, count[c]));
            ranked.Sort((x, y) => y.w.CompareTo(x.w));

            int take = Math.Min(opts.ColorCount, ranked.Count);
            var result = new List<ExtractedColor>(take);
            for (int i = 0; i < take; i++)
            {
                int c = ranked[i].c;
                byte r = (byte)Math.Clamp(rSum[c] / count[c], 0, 255);
                byte g = (byte)Math.Clamp(gSum[c] / count[c], 0, 255);
                byte bb = (byte)Math.Clamp(bSum[c] / count[c], 0, 255);
                result.Add(new ExtractedColor(r, g, bb, count[c]));
            }
            return result;
        }
    }
}
