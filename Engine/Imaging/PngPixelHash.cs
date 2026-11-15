// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PngPixelHash.cs
// #1780 — a hash of a PNG's decoded pixels, not its bytes.
//
// The visual-regression tool (Tools/VisualRegression) used to SHA-256 the PNG file.
// PR #1645 (#1640) changed the encoder (Skia → ParallelPngWriter: opaque frames are
// now 8-bit RGB instead of RGBA, other filters and deflate), which changed every
// file and no pixel, so every case read as a regression. This hash covers what a
// render is: width, height and the straight RGBA of every pixel. An RGB file
// decodes with alpha 255, so the same pixels hash the same in either colour type,
// whatever the filters, compression, chunk split or ancillary chunks.
//
// Dependency-free on purpose: the tool links this file (it has no engine
// references) and it must not share a decoder with the app's encoder. It reads
// 8-bit greyscale, grey + alpha, RGB and RGBA, non-interlaced, with the five
// filter types: the layouts of the PNG specification (W3C Recommendation, 2nd
// edition, 2003, https://www.w3.org/TR/2003/REC-PNG-20031110/), §6 (colour types),
// §9 (filters, the Paeth predictor in §9.4) and §10 (zlib stream across IDATs).
// Anything else (palette, 16-bit, Adam7) throws: FF writes none of them.

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace FracturingFog.Imaging
{
    public static class PngPixelHash
    {
        private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

        /// <summary>Lower-case hex SHA-256 of the file's decoded pixels (see the file header).</summary>
        public static string OfFile(string path) => Of(File.ReadAllBytes(path));

        /// <summary>Lower-case hex SHA-256 over width and height (big-endian uint32 each)
        /// and then the straight RGBA bytes of every pixel, row by row.</summary>
        public static string Of(byte[] png)
        {
            byte[] rgba = DecodeRgba(png, out int w, out int h);
            using var sha = SHA256.Create();
            Span<byte> dims = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(dims, (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(dims[4..], (uint)h);
            sha.TransformBlock(dims.ToArray(), 0, 8, null, 0);
            sha.TransformFinalBlock(rgba, 0, rgba.Length);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        /// <summary>Decodes an 8-bit, non-interlaced greyscale / grey+alpha / RGB / RGBA
        /// PNG to straight RGBA bytes (R, G, B, A per pixel; alpha 255 where the file
        /// has none).</summary>
        public static byte[] DecodeRgba(byte[] png, out int w, out int h)
        {
            ArgumentNullException.ThrowIfNull(png);
            if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
                throw new InvalidDataException("Not a PNG.");

            w = h = 0;
            int colourType = -1;
            var idat = new MemoryStream();
            int pos = 8;
            while (pos + 8 <= png.Length)
            {
                int len = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos)));
                var type = png.AsSpan(pos + 4, 4);
                if (pos + 12L + len > png.Length) throw new InvalidDataException("Truncated chunk.");
                var data = png.AsSpan(pos + 8, len);
                if (type.SequenceEqual("IHDR"u8))
                {
                    w = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                    h = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
                    int depth = data[8];
                    colourType = data[9];
                    if (depth != 8) throw new NotSupportedException($"Bit depth {depth}.");
                    if (colourType is not (0 or 2 or 4 or 6)) throw new NotSupportedException($"Colour type {colourType}.");
                    if (data[12] != 0) throw new NotSupportedException("Interlaced.");
                }
                else if (type.SequenceEqual("IDAT"u8)) idat.Write(data);
                else if (type.SequenceEqual("IEND"u8)) break;
                pos += 12 + len;
            }
            if (colourType < 0 || w <= 0 || h <= 0) throw new InvalidDataException("No IHDR.");

            int channels = colourType switch { 0 => 1, 4 => 2, 2 => 3, _ => 4 };
            int stride = w * channels;
            var raw = new byte[(long)(stride + 1) * h];
            idat.Position = 0;
            using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n == 0) throw new InvalidDataException("Image data too short.");
                    got += n;
                }
            }

            var cur = new byte[stride];
            var prev = new byte[stride];
            var rgba = new byte[(long)w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int o = y * (stride + 1);
                byte filter = raw[o];
                Array.Copy(raw, o + 1, cur, 0, stride);
                Unfilter(filter, cur, prev, channels);
                int d = y * w * 4;
                for (int x = 0; x < w; x++, d += 4)
                {
                    int s = x * channels;
                    switch (channels)
                    {
                        case 1: rgba[d] = rgba[d + 1] = rgba[d + 2] = cur[s]; rgba[d + 3] = 255; break;
                        case 2: rgba[d] = rgba[d + 1] = rgba[d + 2] = cur[s]; rgba[d + 3] = cur[s + 1]; break;
                        case 3: rgba[d] = cur[s]; rgba[d + 1] = cur[s + 1]; rgba[d + 2] = cur[s + 2]; rgba[d + 3] = 255; break;
                        default: rgba[d] = cur[s]; rgba[d + 1] = cur[s + 1]; rgba[d + 2] = cur[s + 2]; rgba[d + 3] = cur[s + 3]; break;
                    }
                }
                (cur, prev) = (prev, cur);
            }
            return rgba;
        }

        // PNG §9.2: undo one row's filter in place. bpp = bytes per complete pixel
        // (1 for sub-byte depths, which this reader does not take).
        private static void Unfilter(byte filter, byte[] row, byte[] up, int bpp)
        {
            switch (filter)
            {
                case 0: return;
                case 1: for (int i = bpp; i < row.Length; i++) row[i] += row[i - bpp]; return;
                case 2: for (int i = 0; i < row.Length; i++) row[i] += up[i]; return;
                case 3:
                    for (int i = 0; i < row.Length; i++)
                        row[i] += (byte)(((i >= bpp ? row[i - bpp] : 0) + up[i]) >> 1);
                    return;
                case 4:
                    for (int i = 0; i < row.Length; i++)
                        row[i] += Paeth(i >= bpp ? row[i - bpp] : (byte)0, up[i], i >= bpp ? up[i - bpp] : (byte)0);
                    return;
                default: throw new InvalidDataException($"Filter type {filter}.");
            }
        }

        // PNG §9.4.
        private static byte Paeth(byte a, byte b, byte c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
