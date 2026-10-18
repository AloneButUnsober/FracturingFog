// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrEncoder.cs
//
// #1305 / S1 (#1396) - QR Code Model 2 encoder (ISO/IEC 18004), byte mode,
// versions 1..40, error correction L/M/Q/H, all 8 masks. Written from the
// spec rather than taken from a package so the fractal-QR work can reach the
// pieces a black-box encoder hides: the function-pattern mask (which modules
// must stay crisp), a forced mask / version, and the raw codeword layer that
// S7's QArt-style data shaping (#1402) edits. ZXing.Net is used only to
// DECODE (QrVerifier), so a passing round trip is two independent
// implementations agreeing, never the encoder agreeing with itself.
//
// The tables and the block / interleave / placement order follow the spec;
// the structure mirrors the well-known reference layout (Project Nayuki's
// QR Code generator, MIT) re-expressed in this codebase's idiom.

using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>An encoded QR symbol: the module grid (no quiet zone) and which
    /// modules are function patterns (finders + separators, timing, alignment,
    /// format / version info, the dark module).</summary>
    public sealed class QrCode
    {
        public QrCode(int version, QrErrorCorrection ecc, int mask, bool[,] dark, bool[,] isFunction)
        {
            Version = version;
            ErrorCorrection = ecc;
            Mask = mask;
            Dark = dark;
            IsFunction = isFunction;
        }

        public int Version { get; }
        public QrErrorCorrection ErrorCorrection { get; }
        public int Mask { get; }
        /// <summary>Modules per side (17 + 4 * version).</summary>
        public int Size => Dark.GetLength(0);
        /// <summary>[y, x] - true = dark module.</summary>
        public bool[,] Dark { get; }
        /// <summary>[y, x] - true = function-pattern module (not data).</summary>
        public bool[,] IsFunction { get; }

        /// <summary>Required light border, in modules, on every side.</summary>
        public const int QuietZone = 4;
    }

    public static class QrEncoder
    {
        public const int MinVersion = 1;
        public const int MaxVersion = 40;

        // [ecc][version] - error-correction codewords per block.
        private static readonly int[][] EccCodewordsPerBlock =
        {
            new[] { -1,  7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 }, // L
            new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 }, // M
            new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 }, // Q
            new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 }, // H
        };

        // [ecc][version] - number of error-correction blocks.
        private static readonly int[][] NumErrorCorrectionBlocks =
        {
            new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },             // L
            new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },    // M
            new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 }, // Q
            new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 }, // H
        };

        // Format-info ECC level bits (not the enum order).
        private static int FormatBits(QrErrorCorrection ecc) => ecc switch
        {
            QrErrorCorrection.L => 1,
            QrErrorCorrection.M => 0,
            QrErrorCorrection.Q => 3,
            _                   => 2,
        };

        /// <summary>Encode <paramref name="text"/> (UTF-8, byte mode; an ECI
        /// header is added only when the text is not plain ASCII) at the smallest
        /// version that fits. <paramref name="mask"/> -1 = pick by the spec's
        /// penalty score.</summary>
        public static QrCode Encode(string text, QrErrorCorrection ecc,
            int minVersion = MinVersion, int maxVersion = MaxVersion, int mask = -1)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            bool ascii = true;
            foreach (byte b in bytes) if (b >= 0x80) { ascii = false; break; }
            return EncodeBytes(bytes, ecc, minVersion, maxVersion, mask, utf8Eci: !ascii);
        }

        /// <summary>Encode raw bytes in byte mode.</summary>
        public static QrCode EncodeBytes(byte[] data, QrErrorCorrection ecc,
            int minVersion = MinVersion, int maxVersion = MaxVersion, int mask = -1, bool utf8Eci = false)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (minVersion < MinVersion || maxVersion > MaxVersion || minVersion > maxVersion)
                throw new ArgumentOutOfRangeException(nameof(minVersion));
            if (mask < -1 || mask > 7) throw new ArgumentOutOfRangeException(nameof(mask));

            int version = ChooseVersion(data.Length, ecc, minVersion, maxVersion, utf8Eci);
            byte[] codewords = BuildDataCodewords(data, version, ecc, utf8Eci);
            return EncodeCodewords(codewords, version, ecc, mask);
        }

        /// <summary>The smallest version in [<paramref name="minVersion"/>,
        /// <paramref name="maxVersion"/>] that holds a byte-mode payload of
        /// <paramref name="byteCount"/> bytes. Throws ArgumentException when none does.</summary>
        public static int ChooseVersion(int byteCount, QrErrorCorrection ecc,
            int minVersion = MinVersion, int maxVersion = MaxVersion, bool utf8Eci = false)
        {
            if (minVersion < MinVersion || maxVersion > MaxVersion || minVersion > maxVersion)
                throw new ArgumentOutOfRangeException(nameof(minVersion));
            for (int v = minVersion; v <= maxVersion; v++)
                if (DataBitsNeeded(byteCount, v, utf8Eci) <= NumDataCodewords(v, ecc) * 8) return v;
            throw new ArgumentException(
                $"Payload of {byteCount} bytes does not fit a version {minVersion}..{maxVersion} QR code at ECC {ecc}.");
        }

        /// <summary>Largest payload (bytes, byte mode, no ECI) a version holds.</summary>
        public static int CapacityBytes(int version, QrErrorCorrection ecc)
        {
            int bits = NumDataCodewords(version, ecc) * 8 - 4 - CharCountBits(version);
            return Math.Max(0, bits / 8);
        }

        private static int CharCountBits(int version) => version <= 9 ? 8 : 16;

        internal static int DataBitsNeeded(int byteCount, int version, bool eci)
        {
            if (byteCount >= (1 << CharCountBits(version))) return int.MaxValue;
            return (eci ? 12 : 0) + 4 + CharCountBits(version) + byteCount * 8;
        }

        /// <summary>The data codewords (before error correction) for a byte-mode
        /// payload at a fixed version: header, data, terminator, pad bytes.</summary>
        public static byte[] BuildDataCodewords(byte[] data, int version, QrErrorCorrection ecc, bool utf8Eci = false)
        {
            int capacityBits = NumDataCodewords(version, ecc) * 8;
            var bb = new BitBuffer();
            if (utf8Eci)
            {
                bb.Append(0x7, 4);   // ECI mode
                bb.Append(26, 8);    // assignment 26 = UTF-8
            }
            bb.Append(0x4, 4);       // byte mode
            bb.Append(data.Length, CharCountBits(version));
            foreach (byte b in data) bb.Append(b, 8);
            if (bb.Count > capacityBits)
                throw new ArgumentException("Payload does not fit the chosen version.", nameof(data));

            bb.Append(0, Math.Min(4, capacityBits - bb.Count));   // terminator
            bb.Append(0, (8 - bb.Count % 8) % 8);                  // byte align
            for (int pad = 0xEC; bb.Count < capacityBits; pad ^= 0xEC ^ 0x11) bb.Append(pad, 8);
            return bb.ToBytes();
        }

        /// <summary>#1402 — the data codewords for a payload with everything after
        /// the terminator left FREE (zero here): header, payload, a 4-bit
        /// terminator, then <c>capacity - FixedBits</c> bits a decoder never reads
        /// (it stops at the terminator). QArt shaping chooses those bits.</summary>
        public static (byte[] Data, int FixedBits) BuildShapeableData(byte[] data, int version, QrErrorCorrection ecc, bool utf8Eci = false)
        {
            int capacityBits = NumDataCodewords(version, ecc) * 8;
            var bb = new BitBuffer();
            if (utf8Eci)
            {
                bb.Append(0x7, 4);
                bb.Append(26, 8);
            }
            bb.Append(0x4, 4);
            bb.Append(data.Length, CharCountBits(version));
            foreach (byte b in data) bb.Append(b, 8);
            if (bb.Count > capacityBits)
                throw new ArgumentException("Payload does not fit the chosen version.", nameof(data));
            bb.Append(0, Math.Min(4, capacityBits - bb.Count));   // terminator (always fixed)
            int fixedBits = bb.Count;
            bb.Append(0, capacityBits - bb.Count);
            return (bb.ToBytes(), fixedBits);
        }

        /// <summary>#1402 — module index (y * size + x) of every raw codeword bit in
        /// placement order: bit <c>8c + j</c> is codeword <c>c</c>'s bit j (MSB first).</summary>
        public static int[] CodewordBitModules(int version)
        {
            int size = version * 4 + 17;
            var dark = new bool[size, size];
            var isFunc = new bool[size, size];
            DrawFunctionPatterns(version, QrErrorCorrection.L, dark, isFunc);
            var result = new int[NumRawDataModules(version) / 8 * 8];
            int i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                bool upward = ((right + 1) & 2) == 0;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        int y = upward ? size - 1 - vert : vert;
                        if (isFunc[y, x] || i >= result.Length) continue;
                        result[i++] = y * size + x;
                    }
            }
            return result;
        }

        /// <summary>#1402 — where each data and ECC codeword lands after block
        /// splitting and interleaving (mirrors AddEccAndInterleave).</summary>
        public sealed class BlockMap
        {
            /// <summary>Interleaved codeword index of data codeword k.</summary>
            public required int[] DataToCodeword { get; init; }
            /// <summary>Block of data codeword k, and its position in that block.</summary>
            public required int[] DataBlock { get; init; }
            public required int[] DataPos { get; init; }
            /// <summary>[block][e] = interleaved codeword index of ECC byte e.</summary>
            public required int[][] EccToCodeword { get; init; }
            public required int[] BlockDataLength { get; init; }
            public required int EccLength { get; init; }
            public required byte[] Divisor { get; init; }
        }

        public static BlockMap MapBlocks(int version, QrErrorCorrection ecc)
        {
            int numBlocks = NumErrorCorrectionBlocks[(int)ecc][version];
            int blockEccLen = EccCodewordsPerBlock[(int)ecc][version];
            int rawCodewords = NumRawDataModules(version) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;
            int numData = NumDataCodewords(version, ecc);

            var blockLen = new int[numBlocks];
            var blockStart = new int[numBlocks];
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                blockLen[i] = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                blockStart[i] = k;
                k += blockLen[i];
            }
            var dataToCw = new int[numData];
            var dataBlock = new int[numData];
            var dataPos = new int[numData];
            var eccToCw = new int[numBlocks][];
            for (int b = 0; b < numBlocks; b++) eccToCw[b] = new int[blockEccLen];

            int cw = 0;
            int eccStart = shortBlockLen + 1 - blockEccLen;
            for (int i = 0; i < shortBlockLen + 1; i++)
                for (int j = 0; j < numBlocks; j++)
                {
                    if (i == shortBlockLen - blockEccLen && j < numShortBlocks) continue;   // short-block placeholder
                    if (i < blockLen[j])
                    {
                        int k = blockStart[j] + i;
                        dataToCw[k] = cw; dataBlock[k] = j; dataPos[k] = i;
                    }
                    else eccToCw[j][i - eccStart] = cw;
                    cw++;
                }
            return new BlockMap
            {
                DataToCodeword = dataToCw, DataBlock = dataBlock, DataPos = dataPos,
                EccToCodeword = eccToCw, BlockDataLength = blockLen, EccLength = blockEccLen,
                Divisor = ReedSolomon.ComputeDivisor(blockEccLen),
            };
        }

        /// <summary>Build the symbol from a full set of data codewords (exactly
        /// <see cref="NumDataCodewords"/> long): add error correction, interleave,
        /// place, mask, and write format / version info.</summary>
        public static QrCode EncodeCodewords(byte[] dataCodewords, int version, QrErrorCorrection ecc, int mask = -1)
        {
            if (dataCodewords.Length != NumDataCodewords(version, ecc))
                throw new ArgumentException("Wrong number of data codewords for the version / ECC.", nameof(dataCodewords));

            int size = version * 4 + 17;
            var dark = new bool[size, size];
            var isFunc = new bool[size, size];
            DrawFunctionPatterns(version, ecc, dark, isFunc);

            byte[] all = AddEccAndInterleave(dataCodewords, version, ecc);
            DrawCodewords(all, dark, isFunc);

            if (mask < 0)
            {
                int best = 0, bestPenalty = int.MaxValue;
                for (int m = 0; m < 8; m++)
                {
                    ApplyMask(m, dark, isFunc);
                    DrawFormatBits(ecc, m, dark, isFunc);
                    int p = PenaltyScore(dark) + PseudoFinderPenalty(dark, isFunc);
                    if (p < bestPenalty) { best = m; bestPenalty = p; }
                    ApplyMask(m, dark, isFunc);   // XOR undoes it
                }
                mask = best;
            }
            ApplyMask(mask, dark, isFunc);
            DrawFormatBits(ecc, mask, dark, isFunc);
            return new QrCode(version, ecc, mask, dark, isFunc);
        }

        // ── capacity ───────────────────────────────────────────────────────

        /// <summary>Modules available for data + ECC bits (everything except the
        /// function patterns), including the 0..7 remainder bits.</summary>
        public static int NumRawDataModules(int version)
        {
            int result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                int numAlign = version / 7 + 2;
                result -= (25 * numAlign - 10) * numAlign - 55;
                if (version >= 7) result -= 36;
            }
            return result;
        }

        public static int NumDataCodewords(int version, QrErrorCorrection ecc)
            => NumRawDataModules(version) / 8
               - EccCodewordsPerBlock[(int)ecc][version] * NumErrorCorrectionBlocks[(int)ecc][version];

        // ── function patterns ──────────────────────────────────────────────

        private static void Set(bool[,] dark, bool[,] isFunc, int x, int y, bool isDark)
        {
            dark[y, x] = isDark;
            isFunc[y, x] = true;
        }

        private static void DrawFunctionPatterns(int version, QrErrorCorrection ecc, bool[,] dark, bool[,] isFunc)
        {
            int size = dark.GetLength(0);
            for (int i = 0; i < size; i++)
            {
                Set(dark, isFunc, 6, i, i % 2 == 0);
                Set(dark, isFunc, i, 6, i % 2 == 0);
            }

            DrawFinder(3, 3, dark, isFunc);
            DrawFinder(size - 4, 3, dark, isFunc);
            DrawFinder(3, size - 4, dark, isFunc);

            int[] align = AlignmentPositions(version);
            int n = align.Length;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == n - 1) || (i == n - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            Set(dark, isFunc, align[i] + dx, align[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }

            DrawFormatBits(ecc, 0, dark, isFunc);   // reserve; rewritten after masking
            DrawVersion(version, dark, isFunc);
        }

        private static void DrawFinder(int cx, int cy, bool[,] dark, bool[,] isFunc)
        {
            int size = dark.GetLength(0);
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || x >= size || y < 0 || y >= size) continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(dark, isFunc, x, y, dist != 2 && dist != 4);
                }
        }

        /// <summary>Centre coordinates of the alignment patterns along one axis.</summary>
        public static int[] AlignmentPositions(int version)
        {
            if (version == 1) return Array.Empty<int>();
            int numAlign = version / 7 + 2;
            int step = version == 32 ? 26 : (version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
            var result = new int[numAlign];
            result[0] = 6;
            for (int i = numAlign - 1, pos = version * 4 + 17 - 7; i >= 1; i--, pos -= step) result[i] = pos;
            return result;
        }

        private static void DrawFormatBits(QrErrorCorrection ecc, int mask, bool[,] dark, bool[,] isFunc)
        {
            int size = dark.GetLength(0);
            int data = FormatBits(ecc) << 3 | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = (data << 10 | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) Set(dark, isFunc, 8, i, Bit(bits, i));
            Set(dark, isFunc, 8, 7, Bit(bits, 6));
            Set(dark, isFunc, 8, 8, Bit(bits, 7));
            Set(dark, isFunc, 7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) Set(dark, isFunc, 14 - i, 8, Bit(bits, i));

            for (int i = 0; i < 8; i++) Set(dark, isFunc, size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) Set(dark, isFunc, 8, size - 15 + i, Bit(bits, i));
            Set(dark, isFunc, 8, size - 8, true);   // the dark module
        }

        private static void DrawVersion(int version, bool[,] dark, bool[,] isFunc)
        {
            if (version < 7) return;
            int size = dark.GetLength(0);
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            int bits = version << 12 | rem;
            for (int i = 0; i < 18; i++)
            {
                bool b = Bit(bits, i);
                int a = size - 11 + i % 3, c = i / 3;
                Set(dark, isFunc, a, c, b);
                Set(dark, isFunc, c, a, b);
            }
        }

        private static bool Bit(int x, int i) => ((x >> i) & 1) != 0;

        // ── error correction + placement ───────────────────────────────────

        private static byte[] AddEccAndInterleave(byte[] data, int version, QrErrorCorrection ecc)
        {
            int numBlocks = NumErrorCorrectionBlocks[(int)ecc][version];
            int blockEccLen = EccCodewordsPerBlock[(int)ecc][version];
            int rawCodewords = NumRawDataModules(version) / 8;
            int numShortBlocks = numBlocks - rawCodewords % numBlocks;
            int shortBlockLen = rawCodewords / numBlocks;

            byte[] divisor = ReedSolomon.ComputeDivisor(blockEccLen);
            var blocks = new byte[numBlocks][];
            for (int i = 0, k = 0; i < numBlocks; i++)
            {
                int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
                var dat = new byte[datLen];
                Array.Copy(data, k, dat, 0, datLen);
                k += datLen;
                byte[] eccBytes = ReedSolomon.ComputeRemainder(dat, divisor);
                // Short blocks get a placeholder byte so all blocks align for interleaving.
                var block = new byte[shortBlockLen + 1];
                Array.Copy(dat, 0, block, 0, datLen);
                Array.Copy(eccBytes, 0, block, block.Length - blockEccLen, blockEccLen);
                blocks[i] = block;
            }

            var result = new List<byte>(rawCodewords);
            for (int i = 0; i < shortBlockLen + 1; i++)
                for (int j = 0; j < numBlocks; j++)
                    if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                        result.Add(blocks[j][i]);
            return result.ToArray();
        }

        private static void DrawCodewords(byte[] data, bool[,] dark, bool[,] isFunc)
        {
            int size = dark.GetLength(0);
            int i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;   // skip the vertical timing column
                bool upward = ((right + 1) & 2) == 0;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        int y = upward ? size - 1 - vert : vert;
                        if (isFunc[y, x] || i >= data.Length * 8) continue;
                        dark[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                        i++;
                    }
            }
        }

        /// <summary>True when mask <paramref name="mask"/> flips module (x, y).</summary>
        public static bool MaskFlips(int mask, int x, int y) => mask switch
        {
            0 => (x + y) % 2 == 0,
            1 => y % 2 == 0,
            2 => x % 3 == 0,
            3 => (x + y) % 3 == 0,
            4 => (x / 3 + y / 2) % 2 == 0,
            5 => x * y % 2 + x * y % 3 == 0,
            6 => (x * y % 2 + x * y % 3) % 2 == 0,
            7 => ((x + y) % 2 + x * y % 3) % 2 == 0,
            _ => throw new ArgumentOutOfRangeException(nameof(mask)),
        };

        private static void ApplyMask(int mask, bool[,] dark, bool[,] isFunc)
        {
            int size = dark.GetLength(0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (!isFunc[y, x] && MaskFlips(mask, x, y)) dark[y, x] = !dark[y, x];
        }

        /// <summary>The score <see cref="EncodeCodewords"/> minimises when it picks
        /// the mask: the spec penalty plus the 2-D finder-lookalike term. Lower is
        /// better; callers that search masks themselves (QrExport) order by it.</summary>
        public static int MaskPenalty(QrCode code) => PenaltyScore(code.Dark) + PseudoFinderPenalty(code.Dark, code.IsFunction);

        // ── mask penalty (spec 7.8.3) ──────────────────────────────────────

        private const int PenaltyN1 = 3, PenaltyN2 = 3, PenaltyN3 = 40, PenaltyN4 = 10;

        internal static int PenaltyScore(bool[,] dark)
        {
            int size = dark.GetLength(0);
            int result = 0;

            // N1: runs of 5+ same-colour modules, rows and columns.
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < size; a++)
                {
                    int run = 1;
                    for (int b = 1; b <= size; b++)
                    {
                        bool same = b < size && Get(dark, pass, a, b) == Get(dark, pass, a, b - 1);
                        if (same) { run++; continue; }
                        if (run >= 5) result += PenaltyN1 + (run - 5);
                        run = 1;
                    }
                }

            // N2: 2x2 same-colour blocks.
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                {
                    bool c = dark[y, x];
                    if (c == dark[y, x + 1] && c == dark[y + 1, x] && c == dark[y + 1, x + 1]) result += PenaltyN2;
                }

            // N3: finder-like 1:1:3:1:1 with 4 light on either side (outside = light).
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < size; a++)
                    for (int b = 0; b + 7 <= size; b++)
                        if (Matches(dark, pass, a, b, size)) result += PenaltyN3;

            // N4: dark proportion away from 50%.
            int darkCount = 0;
            foreach (bool d in dark) if (d) darkCount++;
            int total = size * size;
            int k = (Math.Abs(darkCount * 20 - total * 10) + total - 1) / total - 1;
            result += Math.Max(0, k) * PenaltyN4;
            return result;
        }

        // ── decodability: 2-D finder lookalikes ───────────────────────────
        //
        // The spec's N3 term scores 1:1:3:1:1 runs one axis at a time. A real
        // detector (ZXing's FinderPatternFinder) confirms a candidate by cross-
        // checking the vertical run through the horizontal run's centre, and
        // tolerates a centre of 2..5 modules. A data-region cross that passes
        // both axes competes with the true finders and can win - measured: ~1 in
        // 8 masks at full capacity failed to detect on a clean, perfect render.
        // The mask is free (the decoder reads it from the format bits), so steer
        // away from masks that draw such crosses. At large versions random data
        // puts some into every mask, so this lowers the odds rather than ruling
        // it out; QrExport therefore verifies candidate masks with a real decoder.

        private const int PenaltyPseudoFinder = 1000;

        internal static int PseudoFinderPenalty(bool[,] dark, bool[,] isFunc)
        {
            int size = dark.GetLength(0);
            int count = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (!dark[y, x] || isFunc[y, x]) continue;
                    if (FinderLikeThrough(dark, 0, y, x, size) && FinderLikeThrough(dark, 1, x, y, size)) count++;
                }
            return count * PenaltyPseudoFinder;
        }

        // Is the dark run containing (line a, index c) the centre of a
        // dark1 light1 dark{2..5} light1 dark1 pattern? (ZXing's ratio test,
        // with the module size estimated from the run total, admits these.)
        private static bool FinderLikeThrough(bool[,] dark, int pass, int a, int c, int size)
        {
            int lo = c, hi = c;
            while (lo > 0 && Get(dark, pass, a, lo - 1)) lo--;
            while (hi < size - 1 && Get(dark, pass, a, hi + 1)) hi++;
            int k = hi - lo + 1;
            if (k < 2 || k > 5) return false;
            return lo - 2 >= 0 && hi + 2 < size
                && !Get(dark, pass, a, lo - 1) && Get(dark, pass, a, lo - 2)
                && !Get(dark, pass, a, hi + 1) && Get(dark, pass, a, hi + 2)
                && (lo - 3 < 0 || !Get(dark, pass, a, lo - 3))
                && (hi + 3 >= size || !Get(dark, pass, a, hi + 3));
        }

        // 1011101 with four light modules before or after it (outside = light).
        private static readonly bool[] FinderLike = { true, false, true, true, true, false, true };

        private static bool Matches(bool[,] dark, int pass, int a, int start, int size)
        {
            for (int i = 0; i < 7; i++)
                if (Get(dark, pass, a, start + i) != FinderLike[i]) return false;
            bool lightBefore = true, lightAfter = true;
            for (int i = 1; i <= 4; i++)
            {
                int bb = start - i, ba = start + 6 + i;
                if (bb >= 0 && bb < size && Get(dark, pass, a, bb)) lightBefore = false;
                if (ba >= 0 && ba < size && Get(dark, pass, a, ba)) lightAfter = false;
            }
            return lightBefore || lightAfter;
        }

        private static bool Get(bool[,] dark, int pass, int a, int b) => pass == 0 ? dark[a, b] : dark[b, a];

        private sealed class BitBuffer
        {
            private readonly List<bool> _bits = new();
            public int Count => _bits.Count;

            public void Append(int value, int len)
            {
                for (int i = len - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
            }

            public byte[] ToBytes()
            {
                var r = new byte[(_bits.Count + 7) / 8];
                for (int i = 0; i < _bits.Count; i++)
                    if (_bits[i]) r[i >> 3] |= (byte)(0x80 >> (i & 7));
                return r;
            }
        }
    }

    /// <summary>GF(256) Reed-Solomon over the QR polynomial x^8+x^4+x^3+x^2+1.</summary>
    public static class ReedSolomon
    {
        public static byte Multiply(byte x, byte y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        /// <summary>Generator polynomial coefficients (highest power implicit,
        /// then descending) for <paramref name="degree"/> ECC codewords.</summary>
        public static byte[] ComputeDivisor(int degree)
        {
            if (degree < 1 || degree > 255) throw new ArgumentOutOfRangeException(nameof(degree));
            var result = new byte[degree];
            result[degree - 1] = 1;
            byte root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < result.Length; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < result.Length) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 0x02);
            }
            return result;
        }

        public static byte[] ComputeRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                byte factor = (byte)(b ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[^1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
            }
            return result;
        }
    }
}
