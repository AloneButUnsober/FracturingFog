// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrQArt.cs
//
// #1305 / S7 (#1402) - shape the CODE toward the image instead of only forcing
// the image onto the code (after Russ Cox, "QArt Codes", 2012).
//
// A decoder stops reading data at the terminator, so every data bit after it
// is free. Reed-Solomon is linear over GF(2^8), hence over GF(2) bit by bit:
// each module of the symbol is the base pattern (free bits = 0) XOR a fixed
// combination of the free bits. Walking the modules in order of importance
// (strong image contrast first), Gaussian elimination over GF(2) adds one
// equation "this module = the image's shade" per module until the free bits
// are used up; the solution sets those modules exactly. The result is a valid,
// error-free code whose own pattern follows the image - the compositor then
// barely has to move them.
//
// How much can be shaped is the free share of the data (and so of every
// block): lower error correction and a version larger than the payload needs
// both buy free bits. Function patterns are never touched.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>What shaping achieved.</summary>
    public sealed record QrShapeStats(int FreeBits, int Controlled, int DataModules, double AgreementBefore, double AgreementAfter)
    {
        /// <summary>e.g. "shaped 812 of 2163 modules; image agreement 51% -> 79%".</summary>
        public string Summary =>
            $"shaped {Controlled} of {DataModules} modules ({FreeBits} free bits); image agreement {AgreementBefore:P0} -> {AgreementAfter:P0}";
    }

    public static class QrQArt
    {
        /// <summary>Shape a byte-mode code for <paramref name="payload"/> at a fixed
        /// version and mask. <paramref name="target"/> gives, per module (x, y),
        /// the shade the image wants and how much it matters (weight &gt;= 0;
        /// modules are claimed in descending weight).</summary>
        public static QrCode Shape(byte[] payload, bool utf8Eci, QrErrorCorrection ecc, int version, int mask,
            Func<int, int, (bool Dark, double Weight)> target, out QrShapeStats stats)
        {
            if (mask is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(mask));
            var (data, fixedBits) = QrEncoder.BuildShapeableData(payload, version, ecc, utf8Eci);
            int size = version * 4 + 17;
            int freeBits = data.Length * 8 - fixedBits;

            var baseCode = QrEncoder.EncodeCodewords(data, version, ecc, mask);
            var map = QrEncoder.MapBlocks(version, ecc);
            int[] bitModule = QrEncoder.CodewordBitModules(version);

            // Each free variable -> the modules it flips. Variable v is data bit
            // fixedBits + v: one data module, plus the ECC modules of its block.
            int words = (freeBits + 63) / 64;
            var rows = new Dictionary<int, ulong[]>();   // module -> dependence on the free bits
            for (int v = 0; v < freeBits; v++)
            {
                int bit = fixedBits + v;
                int k = bit >> 3, j = bit & 7;
                Toggle(rows, bitModule[map.DataToCodeword[k] * 8 + j], v, words);

                int b = map.DataBlock[k];
                var unit = new byte[map.BlockDataLength[b]];
                unit[map.DataPos[k]] = (byte)(0x80 >> j);
                byte[] eccBytes = ReedSolomon.ComputeRemainder(unit, map.Divisor);
                for (int e = 0; e < eccBytes.Length; e++)
                {
                    byte eb = eccBytes[e];
                    if (eb == 0) continue;
                    int cw = map.EccToCodeword[b][e];
                    for (int t = 0; t < 8; t++)
                        if ((eb & (0x80 >> t)) != 0) Toggle(rows, bitModule[cw * 8 + t], v, words);
                }
            }

            // Data modules by descending importance.
            var order = new List<(int Module, bool Dark, double Weight)>();
            double totalW = 0, agreeBefore = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (baseCode.IsFunction[y, x]) continue;
                    var (want, w) = target(x, y);
                    w = Math.Max(0, w);
                    order.Add((y * size + x, want, w));
                    totalW += w;
                    if (baseCode.Dark[y, x] == want) agreeBefore += w;
                }
            // Stable sort: equal weights keep raster order, so the result is deterministic.
            var idx = new int[order.Count];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            Array.Sort(idx, (a, c) => { int r = order[c].Weight.CompareTo(order[a].Weight); return r != 0 ? r : a.CompareTo(c); });

            // Greedy elimination: pivots in insertion order, each reduced by all earlier ones.
            var pivotRow = new List<ulong[]>();
            var pivotCol = new List<int>();
            var pivotRhs = new List<bool>();
            foreach (int oi in idx)
            {
                if (pivotRow.Count >= freeBits) break;
                var (module, want, _) = order[oi];
                if (!rows.TryGetValue(module, out var dep)) continue;   // no free bit reaches it
                var r = (ulong[])dep.Clone();
                bool rhs = want ^ baseCode.Dark[module / size, module % size];
                for (int p = 0; p < pivotRow.Count; p++)
                {
                    int c = pivotCol[p];
                    if ((r[c >> 6] >> (c & 63) & 1UL) == 0) continue;
                    var pr = pivotRow[p];
                    for (int wi = 0; wi < words; wi++) r[wi] ^= pr[wi];
                    rhs ^= pivotRhs[p];
                }
                int col = LowestSetBit(r);
                if (col < 0) continue;   // already determined by earlier claims
                pivotRow.Add(r); pivotCol.Add(col); pivotRhs.Add(rhs);
            }

            // Back-substitute (later pivots first; unconstrained bits stay 0).
            var x0 = new ulong[words];
            for (int p = pivotRow.Count - 1; p >= 0; p--)
            {
                var r = pivotRow[p];
                int c = pivotCol[p];
                bool val = pivotRhs[p];
                for (int wi = 0; wi < words; wi++)
                {
                    ulong m = r[wi] & x0[wi];
                    if (wi == c >> 6) m &= ~(1UL << (c & 63));
                    if ((System.Numerics.BitOperations.PopCount(m) & 1) != 0) val = !val;
                }
                if (val) x0[c >> 6] |= 1UL << (c & 63);
            }

            var shaped = (byte[])data.Clone();
            for (int v = 0; v < freeBits; v++)
                if ((x0[v >> 6] >> (v & 63) & 1UL) != 0)
                {
                    int bit = fixedBits + v;
                    shaped[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                }
            var code = QrEncoder.EncodeCodewords(shaped, version, ecc, mask);

            double agreeAfter = 0;
            foreach (var (module, want, w) in order)
                if (code.Dark[module / size, module % size] == want) agreeAfter += w;
            stats = new QrShapeStats(freeBits, pivotRow.Count, order.Count,
                totalW > 0 ? agreeBefore / totalW : 0, totalW > 0 ? agreeAfter / totalW : 0);
            return code;
        }

        /// <summary>Shape a text payload (UTF-8; ECI only when not ASCII) at the
        /// smallest version &gt;= <paramref name="minVersion"/> that holds it.</summary>
        public static QrCode ShapeText(string text, QrErrorCorrection ecc, int minVersion, int mask,
            Func<int, int, (bool Dark, double Weight)> target, out QrShapeStats stats)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            bool ascii = true;
            foreach (byte b in bytes) if (b >= 0x80) { ascii = false; break; }
            int version = QrEncoder.ChooseVersion(bytes.Length, ecc, minVersion, QrEncoder.MaxVersion, !ascii);
            return Shape(bytes, !ascii, ecc, version, mask, target, out stats);
        }

        private static void Toggle(Dictionary<int, ulong[]> rows, int module, int v, int words)
        {
            if (!rows.TryGetValue(module, out var r)) rows[module] = r = new ulong[words];
            r[v >> 6] ^= 1UL << (v & 63);
        }

        private static int LowestSetBit(ulong[] r)
        {
            for (int i = 0; i < r.Length; i++)
                if (r[i] != 0) return i * 64 + System.Numerics.BitOperations.TrailingZeroCount(r[i]);
            return -1;
        }
    }
}
