// Imaging/PaletteExtraction/WuExtractor.cs
//
// Variance-minimising 3-D histogram cut. Loose interpretation of Wu's
// classic algorithm: build a 32×32×32 RGB histogram so distinct colours
// land in distinct cells, then iteratively split the box with the largest
// weighted variance along the channel that gives the biggest sum-of-
// squares reduction. Output is the weighted mean of each surviving box.
//
// Sharper than Median Cut on photographic content (variance-aware split
// selection avoids cleaving high-density regions in half) while staying
// cheaper than full Wu (no precomputed moment tables — recomputed on demand).

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class WuExtractor : IPaletteExtractor
    {
        public string Name => "Wu (variance cut)";

        private const int Bits = 5;          // 32 bins / channel
        private const int Side = 1 << Bits;  // 32

        private sealed class Box
        {
            public int R0, R1, G0, G1, B0, B1;   // inclusive lo, exclusive hi (in bin space)
            public long Weight;
            public double SumR, SumG, SumB;
            public double SumSq;
            public double VarianceReduction;     // cached best-split gain
            public int SplitChan;
            public int SplitAt;
        }

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();
            int k = Math.Max(2, opts.ColorCount);

            // Build 3-D histogram of weighted sums per bin.
            var w = new long[Side, Side, Side];
            var sr = new long[Side, Side, Side];
            var sg = new long[Side, Side, Side];
            var sb = new long[Side, Side, Side];
            var sq = new double[Side, Side, Side];

            int shift = 8 - Bits;
            for (int i = 0; i < pixelCount; i++)
            {
                byte r = rgb[i * 3];
                byte g = rgb[i * 3 + 1];
                byte b = rgb[i * 3 + 2];
                int ri = r >> shift, gi = g >> shift, bi = b >> shift;
                w[ri, gi, bi]++;
                sr[ri, gi, bi] += r;
                sg[ri, gi, bi] += g;
                sb[ri, gi, bi] += b;
                sq[ri, gi, bi] += r * r + g * g + b * b;
            }

            var root = new Box { R0 = 0, R1 = Side, G0 = 0, G1 = Side, B0 = 0, B1 = Side };
            Aggregate(root, w, sr, sg, sb, sq);
            FindBestSplit(root, w, sr, sg, sb, sq);

            var boxes = new List<Box> { root };
            while (boxes.Count < k)
            {
                int pick = -1;
                double bestGain = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Weight < 2) continue;
                    if (boxes[i].VarianceReduction > bestGain)
                    {
                        bestGain = boxes[i].VarianceReduction;
                        pick = i;
                    }
                }
                if (pick < 0) break;

                var src = boxes[pick];
                var (left, right) = SplitBox(src);
                Aggregate(left, w, sr, sg, sb, sq);
                Aggregate(right, w, sr, sg, sb, sq);
                FindBestSplit(left, w, sr, sg, sb, sq);
                FindBestSplit(right, w, sr, sg, sb, sq);

                boxes[pick] = left;
                boxes.Add(right);
            }

            var result = new List<ExtractedColor>(boxes.Count);
            foreach (var b in boxes)
            {
                if (b.Weight == 0) continue;
                byte r = (byte)Math.Clamp((int)(b.SumR / b.Weight), 0, 255);
                byte g = (byte)Math.Clamp((int)(b.SumG / b.Weight), 0, 255);
                byte bb = (byte)Math.Clamp((int)(b.SumB / b.Weight), 0, 255);
                result.Add(new ExtractedColor(r, g, bb, (int)Math.Min(b.Weight, int.MaxValue)));
            }
            return result;
        }

        private static void Aggregate(Box b,
            long[,,] w, long[,,] sr, long[,,] sg, long[,,] sb, double[,,] sq)
        {
            long W = 0;
            double SR = 0, SG = 0, SB = 0, SQ = 0;
            for (int x = b.R0; x < b.R1; x++)
                for (int y = b.G0; y < b.G1; y++)
                    for (int z = b.B0; z < b.B1; z++)
                    {
                        var ww = w[x, y, z];
                        if (ww == 0) continue;
                        W += ww;
                        SR += sr[x, y, z];
                        SG += sg[x, y, z];
                        SB += sb[x, y, z];
                        SQ += sq[x, y, z];
                    }
            b.Weight = W;
            b.SumR = SR; b.SumG = SG; b.SumB = SB;
            b.SumSq = SQ;
        }

        private static double Variance(Box b)
        {
            if (b.Weight <= 0) return 0;
            double mean2 = (b.SumR * b.SumR + b.SumG * b.SumG + b.SumB * b.SumB) / (double)b.Weight;
            return b.SumSq - mean2;
        }

        private static (Box, Box) SplitBox(Box src)
        {
            var left = new Box { R0 = src.R0, R1 = src.R1, G0 = src.G0, G1 = src.G1, B0 = src.B0, B1 = src.B1 };
            var right = new Box { R0 = src.R0, R1 = src.R1, G0 = src.G0, G1 = src.G1, B0 = src.B0, B1 = src.B1 };
            switch (src.SplitChan)
            {
                case 0: left.R1 = src.SplitAt; right.R0 = src.SplitAt; break;
                case 1: left.G1 = src.SplitAt; right.G0 = src.SplitAt; break;
                default: left.B1 = src.SplitAt; right.B0 = src.SplitAt; break;
            }
            return (left, right);
        }

        private static void FindBestSplit(Box b,
            long[,,] w, long[,,] sr, long[,,] sg, long[,,] sb, double[,,] sq)
        {
            double parent = Variance(b);
            double best = 0;
            int bestChan = 0, bestAt = 0;

            for (int chan = 0; chan < 3; chan++)
            {
                int lo = chan == 0 ? b.R0 : chan == 1 ? b.G0 : b.B0;
                int hi = chan == 0 ? b.R1 : chan == 1 ? b.G1 : b.B1;
                if (hi - lo < 2) continue;
                for (int s = lo + 1; s < hi; s++)
                {
                    var (left, right) = SplitBox(new Box
                    {
                        R0 = b.R0, R1 = b.R1, G0 = b.G0, G1 = b.G1, B0 = b.B0, B1 = b.B1,
                        SplitChan = chan, SplitAt = s,
                    });
                    Aggregate(left, w, sr, sg, sb, sq);
                    Aggregate(right, w, sr, sg, sb, sq);
                    if (left.Weight == 0 || right.Weight == 0) continue;
                    double gain = parent - Variance(left) - Variance(right);
                    if (gain > best) { best = gain; bestChan = chan; bestAt = s; }
                }
            }

            b.VarianceReduction = best;
            b.SplitChan = bestChan;
            b.SplitAt = bestAt;
        }
    }
}
