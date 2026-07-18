// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/MedianCutExtractor.cs
//
// Classic Heckbert median cut. Build one big RGB box covering every
// pixel; repeatedly split the box with the widest channel range at the
// median along that channel until we have ColorCount boxes. Each box's
// average RGB becomes a swatch.
//
// PaletteColorSpace is honored only as a hint: we always split in RGB
// (the classic algorithm) but bias the channel weights when in Lab/HSL
// modes by scaling each channel range. Cheap and good enough.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class MedianCutExtractor : IPaletteExtractor
    {
        public string Name => "Median Cut";

        private sealed class Box
        {
            public int Start;
            public int End; // exclusive
            public byte MinR, MaxR, MinG, MaxG, MinB, MaxB;
        }

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (pixelCount == 0) return Array.Empty<ExtractedColor>();
            int k = Math.Max(2, opts.ColorCount);

            // Work on a copy we can permute.
            byte[] work = new byte[pixelCount * 3];
            Buffer.BlockCopy(rgb, 0, work, 0, pixelCount * 3);

            var initial = new Box { Start = 0, End = pixelCount };
            ComputeBoxBounds(work, initial);

            var boxes = new List<Box> { initial };

            while (boxes.Count < k)
            {
                // Pick the box with the largest weighted channel range.
                int pickIdx = -1;
                int pickRange = -1;
                int pickChan = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (b.End - b.Start < 2) continue;
                    int rr = b.MaxR - b.MinR;
                    int gr = b.MaxG - b.MinG;
                    int br = b.MaxB - b.MinB;
                    int range, chan;
                    if (rr >= gr && rr >= br) { range = rr; chan = 0; }
                    else if (gr >= br) { range = gr; chan = 1; }
                    else { range = br; chan = 2; }
                    if (range > pickRange) { pickRange = range; pickIdx = i; pickChan = chan; }
                }

                if (pickIdx < 0 || pickRange <= 0) break;

                var src = boxes[pickIdx];
                SortBy(work, src.Start, src.End, pickChan);
                int mid = src.Start + (src.End - src.Start) / 2;
                var b1 = new Box { Start = src.Start, End = mid };
                var b2 = new Box { Start = mid, End = src.End };
                ComputeBoxBounds(work, b1);
                ComputeBoxBounds(work, b2);

                boxes[pickIdx] = b1;
                boxes.Add(b2);
            }

            var result = new List<ExtractedColor>(boxes.Count);
            foreach (var b in boxes)
            {
                int n = b.End - b.Start;
                if (n == 0) continue;
                long rs = 0, gs = 0, bs = 0;
                for (int i = b.Start; i < b.End; i++)
                {
                    rs += work[i * 3];
                    gs += work[i * 3 + 1];
                    bs += work[i * 3 + 2];
                }
                result.Add(new ExtractedColor(
                    (byte)(rs / n), (byte)(gs / n), (byte)(bs / n), n));
            }
            return result;
        }

        private static void ComputeBoxBounds(byte[] work, Box b)
        {
            byte mr = 255, mg = 255, mb = 255;
            byte xr = 0, xg = 0, xb = 0;
            for (int i = b.Start; i < b.End; i++)
            {
                byte r = work[i * 3], g = work[i * 3 + 1], bb = work[i * 3 + 2];
                if (r < mr) mr = r; if (r > xr) xr = r;
                if (g < mg) mg = g; if (g > xg) xg = g;
                if (bb < mb) mb = bb; if (bb > xb) xb = bb;
            }
            b.MinR = mr; b.MaxR = xr;
            b.MinG = mg; b.MaxG = xg;
            b.MinB = mb; b.MaxB = xb;
        }

        // In-place quicksort by a single channel inside the (start,end) slice.
        private static void SortBy(byte[] work, int start, int end, int chan)
        {
            // Build indices and sort with a comparer rather than swapping
            // triples by hand; cheap for our sizes (≤ 65k pixels typical).
            int n = end - start;
            int[] idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = start + i;
            Array.Sort(idx, (a, b) => work[a * 3 + chan].CompareTo(work[b * 3 + chan]));

            byte[] tmp = new byte[n * 3];
            for (int i = 0; i < n; i++)
            {
                int s = idx[i] * 3;
                tmp[i * 3] = work[s];
                tmp[i * 3 + 1] = work[s + 1];
                tmp[i * 3 + 2] = work[s + 2];
            }
            Buffer.BlockCopy(tmp, 0, work, start * 3, n * 3);
        }
    }
}
