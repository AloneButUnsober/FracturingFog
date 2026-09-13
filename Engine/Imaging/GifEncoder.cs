// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/GifEncoder.cs
//
// Portable single-frame GIF89a encoder (#68 slice 2). SkiaSharp decodes GIF
// but cannot ENCODE it, so a "GIF" save used to be PNG bytes written to a
// .gif path. This writes a real GIF:
//
//   1. Median-cut quantization of the BGRA buffer to a <=256-colour palette,
//      reserving one slot for 1-bit transparency when any pixel is (nearly)
//      transparent.
//   2. Nearest-colour index mapping (exact-colour memo so smooth gradients
//      only pay the nearest search once per distinct colour).
//   3. GIF-variant LZW compression of the index stream (ported from giflib's
//      code-width growth rule — no TIFF "early change").
//   4. GIF89a container assembly (LSD + global colour table + optional Graphic
//      Control Extension for transparency + image descriptor + trailer).
//
// No System.Drawing / Skia dependency — operates on a raw BGRA span so it is
// unit-testable headless. Animated GIF (multi-frame) is tracked separately
// (#784); this is stills only.

using System;
using System.Collections.Generic;
using System.IO;

namespace FracturingFog.Imaging
{
    /// <summary>Hand-rolled single-frame GIF89a writer. See file header.</summary>
    public static class GifEncoder
    {
        // Pixels with alpha below this become the transparent index (GIF has
        // only 1-bit transparency — no partial alpha).
        private const int AlphaThreshold = 128;

        /// <summary>Encode a straight-alpha BGRA buffer (row-major, 4 bytes /
        /// pixel, B,G,R,A order — the render buffer's native layout) to a GIF89a
        /// file at <paramref name="path"/>.</summary>
        public static void Write(string path, ReadOnlySpan<byte> bgra, int w, int h)
        {
            if (w <= 0 || h <= 0) throw new ArgumentException("Non-positive dimensions.");
            if (bgra.Length < (long)w * h * 4) throw new ArgumentException("Buffer too small.");

            // ── 1. Histogram of opaque colours + transparency probe ──────────
            var hist = new Dictionary<int, int>();
            bool hasTransparent = false;
            int px = w * h;
            for (int i = 0; i < px; i++)
            {
                int o = i * 4;
                if (bgra[o + 3] < AlphaThreshold) { hasTransparent = true; continue; }
                int rgb = (bgra[o + 2] << 16) | (bgra[o + 1] << 8) | bgra[o]; // R,G,B
                hist.TryGetValue(rgb, out int c);
                hist[rgb] = c + 1;
            }

            int maxColors = hasTransparent ? 255 : 256;

            // ── 2. Palette (median cut, or the distinct colours directly) ────
            byte[] palR, palG, palB;
            BuildPalette(hist, maxColors, out palR, out palG, out palB);
            int colorCount = palR.Length;

            // Transparent index sits just past the real colours.
            int transparentIndex = hasTransparent ? colorCount : -1;
            int usedEntries = colorCount + (hasTransparent ? 1 : 0);
            if (usedEntries < 1) usedEntries = 1;

            // Global colour table size must be a power of two, 2..256.
            int tableSize = 2;
            while (tableSize < usedEntries) tableSize <<= 1;
            int sizeField = Log2(tableSize) - 1;          // 0..7
            int minCodeSize = Math.Max(2, Log2(tableSize)); // GIF requires >= 2

            // ── 3. Map every pixel to a palette index ────────────────────────
            var indices = new byte[px];
            var memo = new Dictionary<int, byte>();
            for (int i = 0; i < px; i++)
            {
                int o = i * 4;
                if (transparentIndex >= 0 && bgra[o + 3] < AlphaThreshold)
                {
                    indices[i] = (byte)transparentIndex;
                    continue;
                }
                int rgb = (bgra[o + 2] << 16) | (bgra[o + 1] << 8) | bgra[o];
                if (!memo.TryGetValue(rgb, out byte idx))
                {
                    idx = NearestIndex((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb,
                                       palR, palG, palB);
                    memo[rgb] = idx;
                }
                indices[i] = idx;
            }

            // ── 4. LZW compress + assemble the file ──────────────────────────
            byte[] lzw = LzwCompress(indices, minCodeSize);

            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            // Header
            bw.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            // Logical Screen Descriptor
            WriteU16(bw, w);
            WriteU16(bw, h);
            // packed: GCT present | colour resolution | sort=0 | GCT size
            bw.Write((byte)(0x80 | (sizeField << 4) | sizeField));
            bw.Write((byte)0); // background colour index
            bw.Write((byte)0); // pixel aspect ratio
            // Global Colour Table (tableSize entries, RGB; unused entries zeroed)
            for (int i = 0; i < tableSize; i++)
            {
                if (i < colorCount) { bw.Write(palR[i]); bw.Write(palG[i]); bw.Write(palB[i]); }
                else { bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); }
            }
            // Graphic Control Extension (only needed for transparency)
            if (hasTransparent)
            {
                bw.Write((byte)0x21); // extension introducer
                bw.Write((byte)0xF9); // graphic control label
                bw.Write((byte)0x04); // block size
                bw.Write((byte)0x01); // packed: transparent colour flag = 1
                WriteU16(bw, 0);      // delay time
                bw.Write((byte)transparentIndex);
                bw.Write((byte)0x00); // block terminator
            }
            // Image Descriptor
            bw.Write((byte)0x2C);
            WriteU16(bw, 0); // left
            WriteU16(bw, 0); // top
            WriteU16(bw, w);
            WriteU16(bw, h);
            bw.Write((byte)0x00); // no local colour table, not interlaced
            // Image data: min code size + LZW sub-blocks + terminator
            bw.Write((byte)minCodeSize);
            for (int off = 0; off < lzw.Length;)
            {
                int chunk = Math.Min(255, lzw.Length - off);
                bw.Write((byte)chunk);
                bw.Write(lzw, off, chunk);
                off += chunk;
            }
            bw.Write((byte)0x00); // block terminator
            // Trailer
            bw.Write((byte)0x3B);
        }

        // ── Palette construction ─────────────────────────────────────────────

        private static void BuildPalette(
            Dictionary<int, int> hist, int maxColors,
            out byte[] palR, out byte[] palG, out byte[] palB)
        {
            int d = hist.Count;
            if (d == 0)
            {
                // Fully transparent (or empty) image — one dummy entry.
                palR = new byte[] { 0 }; palG = new byte[] { 0 }; palB = new byte[] { 0 };
                return;
            }

            // Distinct colours + counts as parallel arrays.
            var cr = new byte[d]; var cg = new byte[d]; var cb = new byte[d];
            var cnt = new long[d];
            int k = 0;
            foreach (var kv in hist)
            {
                cr[k] = (byte)(kv.Key >> 16);
                cg[k] = (byte)(kv.Key >> 8);
                cb[k] = (byte)kv.Key;
                cnt[k] = kv.Value;
                k++;
            }

            if (d <= maxColors)
            {
                palR = cr; palG = cg; palB = cb;
                return;
            }

            // Median cut over an index array we partition into boxes.
            var order = new int[d];
            for (int i = 0; i < d; i++) order[i] = i;

            var boxes = new List<(int lo, int hi)> { (0, d) };
            while (boxes.Count < maxColors)
            {
                // Pick the splittable box with the largest colour volume.
                int best = -1; long bestVol = -1;
                for (int b = 0; b < boxes.Count; b++)
                {
                    var (lo, hi) = boxes[b];
                    if (hi - lo < 2) continue;
                    long vol = BoxVolume(order, lo, hi, cr, cg, cb);
                    if (vol > bestVol) { bestVol = vol; best = b; }
                }
                if (best < 0) break; // nothing left to split

                var (blo, bhi) = boxes[best];
                int channel = WidestChannel(order, blo, bhi, cr, cg, cb);
                SortRangeByChannel(order, blo, bhi, channel, cr, cg, cb);

                long total = 0;
                for (int i = blo; i < bhi; i++) total += cnt[order[i]];
                long half = total / 2, acc = 0;
                int mid = blo + 1;
                for (int i = blo; i < bhi; i++)
                {
                    acc += cnt[order[i]];
                    if (acc >= half) { mid = i + 1; break; }
                }
                if (mid <= blo) mid = blo + 1;
                if (mid >= bhi) mid = bhi - 1;

                boxes[best] = (blo, mid);
                boxes.Add((mid, bhi));
            }

            int n = boxes.Count;
            palR = new byte[n]; palG = new byte[n]; palB = new byte[n];
            for (int b = 0; b < n; b++)
            {
                var (lo, hi) = boxes[b];
                long sr = 0, sg = 0, sb = 0, sc = 0;
                for (int i = lo; i < hi; i++)
                {
                    int c = order[i]; long wgt = cnt[c];
                    sr += (long)cr[c] * wgt; sg += (long)cg[c] * wgt; sb += (long)cb[c] * wgt;
                    sc += wgt;
                }
                if (sc == 0) sc = 1;
                palR[b] = (byte)Math.Clamp(sr / sc, 0, 255);
                palG[b] = (byte)Math.Clamp(sg / sc, 0, 255);
                palB[b] = (byte)Math.Clamp(sb / sc, 0, 255);
            }
        }

        private static long BoxVolume(int[] order, int lo, int hi,
            byte[] cr, byte[] cg, byte[] cb)
        {
            byte rmin = 255, rmax = 0, gmin = 255, gmax = 0, bmin = 255, bmax = 0;
            for (int i = lo; i < hi; i++)
            {
                int c = order[i];
                if (cr[c] < rmin) rmin = cr[c]; if (cr[c] > rmax) rmax = cr[c];
                if (cg[c] < gmin) gmin = cg[c]; if (cg[c] > gmax) gmax = cg[c];
                if (cb[c] < bmin) bmin = cb[c]; if (cb[c] > bmax) bmax = cb[c];
            }
            return (long)(rmax - rmin + 1) * (gmax - gmin + 1) * (bmax - bmin + 1);
        }

        private static int WidestChannel(int[] order, int lo, int hi,
            byte[] cr, byte[] cg, byte[] cb)
        {
            byte rmin = 255, rmax = 0, gmin = 255, gmax = 0, bmin = 255, bmax = 0;
            for (int i = lo; i < hi; i++)
            {
                int c = order[i];
                if (cr[c] < rmin) rmin = cr[c]; if (cr[c] > rmax) rmax = cr[c];
                if (cg[c] < gmin) gmin = cg[c]; if (cg[c] > gmax) gmax = cg[c];
                if (cb[c] < bmin) bmin = cb[c]; if (cb[c] > bmax) bmax = cb[c];
            }
            int dr = rmax - rmin, dg = gmax - gmin, db = bmax - bmin;
            if (dr >= dg && dr >= db) return 0;
            return dg >= db ? 1 : 2;
        }

        private static void SortRangeByChannel(int[] order, int lo, int hi,
            int channel, byte[] cr, byte[] cg, byte[] cb)
        {
            byte[] key = channel == 0 ? cr : channel == 1 ? cg : cb;
            Array.Sort(order, lo, hi - lo, Comparer<int>.Create((a, b) => key[a] - key[b]));
        }

        private static byte NearestIndex(byte r, byte g, byte b,
            byte[] palR, byte[] palG, byte[] palB)
        {
            int best = 0; long bestD = long.MaxValue;
            for (int i = 0; i < palR.Length; i++)
            {
                int dr = r - palR[i], dg = g - palG[i], db = b - palB[i];
                long dist = (long)dr * dr + (long)dg * dg + (long)db * db;
                if (dist < bestD) { bestD = dist; best = i; if (dist == 0) break; }
            }
            return (byte)best;
        }

        // ── GIF-variant LZW ──────────────────────────────────────────────────

        private static byte[] LzwCompress(byte[] indices, int minCodeSize)
        {
            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;

            var bits = new BitWriter();
            int runningBits = minCodeSize + 1;
            int maxCode = 1 << runningBits;      // first code needing a wider width
            int runningCode = endCode + 1;       // next code to assign
            var table = new Dictionary<int, int>();

            bits.Write(clearCode, runningBits);

            if (indices.Length == 0)
            {
                bits.Write(endCode, runningBits);
                return bits.ToArray();
            }

            int crnt = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int pixel = indices[i];
                int key = (crnt << 8) | pixel;
                if (table.TryGetValue(key, out int code))
                {
                    crnt = code;
                    continue;
                }

                bits.Write(crnt, runningBits);
                crnt = pixel;

                if (runningCode >= 4095)
                {
                    // Table full — reset (no early change; matches giflib).
                    bits.Write(clearCode, runningBits);
                    runningBits = minCodeSize + 1;
                    maxCode = 1 << runningBits;
                    runningCode = endCode + 1;
                    table.Clear();
                }
                else
                {
                    if (runningCode >= maxCode)
                    {
                        runningBits++;
                        maxCode = 1 << runningBits;
                    }
                    table[key] = runningCode++;
                }
            }

            bits.Write(crnt, runningBits);
            bits.Write(endCode, runningBits);
            return bits.ToArray();
        }

        /// <summary>LSB-first bit packer for the LZW code stream.</summary>
        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = new();
            private int _buffer;
            private int _count;

            public void Write(int code, int nbits)
            {
                _buffer |= code << _count;
                _count += nbits;
                while (_count >= 8)
                {
                    _bytes.Add((byte)(_buffer & 0xFF));
                    _buffer >>= 8;
                    _count -= 8;
                }
            }

            public byte[] ToArray()
            {
                if (_count > 0)
                {
                    _bytes.Add((byte)(_buffer & 0xFF));
                    _buffer = 0; _count = 0;
                }
                return _bytes.ToArray();
            }
        }

        // ── Small helpers ────────────────────────────────────────────────────

        private static void WriteU16(BinaryWriter bw, int v)
        {
            bw.Write((byte)(v & 0xFF));
            bw.Write((byte)((v >> 8) & 0xFF));
        }

        private static int Log2(int v)
        {
            int b = 0;
            while ((1 << b) < v) b++;
            return b;
        }
    }
}
