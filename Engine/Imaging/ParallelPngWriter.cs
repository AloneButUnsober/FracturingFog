// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ParallelPngWriter.cs
// #1640 — lossless PNG from a straight-alpha BGRA buffer, encoded in parallel.
//
// SkiaSharp's encoder (libpng, one thread, every filter tried per row) took ~0.7 s
// for a 4K frame, about half of a batch image's time outside the render. This
// writer splits the rows into strips and filters + deflates each strip on its own
// thread, the technique of Mark Adler's pigz (https://zlib.net/pigz/):
//   • every strip but the last ends with a deflate sync flush and no final block,
//     so the compressed strips concatenate into one valid deflate stream
//     (RFC 1951, https://www.rfc-editor.org/rfc/rfc1951);
//   • the zlib wrapper (RFC 1950, https://www.rfc-editor.org/rfc/rfc1950) is one
//     two-byte header and the Adler-32 of all filtered bytes, combined from the
//     per-strip checksums with zlib's adler32_combine (Jean-loup Gailly and Mark
//     Adler, zlib licence; see THIRD-PARTY-NOTICES.md);
//   • each strip goes in its own IDAT chunk (PNG allows several, read as one
//     stream), so the chunk CRCs are computed in parallel too.
// Chunk layout, filter type 2 (Up) and the CRC-32 follow the PNG specification
// (W3C Recommendation, 2nd edition, 2003, https://www.w3.org/TR/2003/REC-PNG-20031110/).
//
// Pixels round-trip exactly. An image whose alpha is 255 everywhere is written as
// 8-bit RGB (colour type 2), which decodes to the same RGBA with alpha 255;
// anything else is 8-bit RGBA (colour type 6) with the straight alpha as given.
// At 4K this writes in ~50 ms and is smaller than Skia's file (7.8 MB against 8.2 MB
// on the seahorse bench frame).

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace FracturingFog.Imaging
{
    public static class ParallelPngWriter
    {
        /// <summary>zlib level for the strips: zlib-ng's level 6 is fast and close to
        /// libpng's default size.</summary>
        public const int CompressionLevel = 6;

        // Rows per strip at least this, so a small image is one strip.
        private const int MinStripRows = 64;

        private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

        /// <summary>Writes <paramref name="bgra"/> (straight alpha, 0xAARRGGBB per
        /// pixel) as a PNG file.</summary>
        public static void Write(string path, uint[] bgra, int w, int h)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            Write(fs, bgra, w, h);
        }

        /// <summary>Writes <paramref name="bgra"/> as a PNG stream.</summary>
        public static void Write(Stream output, uint[] bgra, int w, int h)
        {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(bgra);
            if (w <= 0 || h <= 0) throw new ArgumentException("Dimensions must be positive.");
            if (bgra.Length < (long)w * h) throw new ArgumentException("Buffer too small.", nameof(bgra));

            bool opaque = IsOpaque(bgra, w * h);
            int bpp = opaque ? 3 : 4;

            int strips = Math.Clamp(Math.Min(Environment.ProcessorCount * 2, h / MinStripRows), 1, h);
            int rowsPer = (h + strips - 1) / strips;
            strips = (h + rowsPer - 1) / rowsPer;
            var parts = new byte[strips][];
            var adlers = new uint[strips];
            var lengths = new long[strips];

            Parallel.For(0, strips, i =>
            {
                int y0 = i * rowsPer, y1 = Math.Min(h, y0 + rowsPer);
                byte[] filtered = FilterRows(bgra, w, y0, y1, bpp);
                adlers[i] = Adler32(filtered);
                lengths[i] = filtered.Length;
                parts[i] = Deflate(filtered, last: i == strips - 1);
            });

            uint adler = adlers[0];
            for (int i = 1; i < strips; i++) adler = Adler32Combine(adler, adlers[i], lengths[i]);

            // IDAT payloads: zlib header on the first, Adler-32 trailer on the last.
            var idat = new byte[strips][];
            for (int i = 0; i < strips; i++)
            {
                int head = i == 0 ? 2 : 0, tail = i == strips - 1 ? 4 : 0;
                var d = new byte[head + parts[i].Length + tail];
                if (head > 0) { d[0] = 0x78; d[1] = 0x9C; }   // deflate, 32K window, default level
                parts[i].CopyTo(d, head);
                if (tail > 0) BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(d.Length - 4), adler);
                idat[i] = d;
            }
            var crcs = new uint[strips];
            Parallel.For(0, strips, i => crcs[i] = ChunkCrc("IDAT"u8, idat[i]));

            output.Write(Signature);
            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)w);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
            ihdr[8] = 8;                        // bit depth
            ihdr[9] = (byte)(opaque ? 2 : 6);   // truecolour / truecolour + alpha
            // [10] compression 0, [11] filter method 0, [12] no interlace
            WriteChunk(output, "IHDR"u8, ihdr, ChunkCrc("IHDR"u8, ihdr));
            for (int i = 0; i < strips; i++) WriteChunk(output, "IDAT"u8, idat[i], crcs[i]);
            WriteChunk(output, "IEND"u8, [], ChunkCrc("IEND"u8, []));
        }

        /// <summary>True when every one of the first <paramref name="n"/> pixels has alpha 255.</summary>
        public static bool IsOpaque(uint[] px, int n)
        {
            bool opaque = true;
            Parallel.For(0, (n + 65535) / 65536, (k, state) =>
            {
                int end = Math.Min(n, (k + 1) * 65536);
                for (int i = k * 65536; i < end; i++)
                    if (px[i] < 0xFF000000u) { opaque = false; state.Stop(); return; }
            });
            return opaque;
        }

        // Filter type 2 (Up) on every row: each byte minus the byte above it (the row
        // above the image counts as zero). PNG byte order is R, G, B[, A].
        private static byte[] FilterRows(uint[] px, int w, int y0, int y1, int bpp)
        {
            int stride = w * bpp;
            var dst = new byte[(stride + 1) * (y1 - y0)];
            int o = 0;
            for (int y = y0; y < y1; y++)
            {
                dst[o++] = 2;
                int r = y * w, p = (y - 1) * w;
                for (int x = 0; x < w; x++)
                {
                    uint c = px[r + x], u = y > 0 ? px[p + x] : 0u;
                    dst[o] = (byte)((c >> 16) - (u >> 16));
                    dst[o + 1] = (byte)((c >> 8) - (u >> 8));
                    dst[o + 2] = (byte)(c - u);
                    if (bpp == 4) dst[o + 3] = (byte)((c >> 24) - (u >> 24));
                    o += bpp;
                }
            }
            return dst;
        }

        // Raw deflate of one strip. All but the last end at a sync flush (byte aligned,
        // no final block) so the next strip's blocks follow on directly.
        private static byte[] Deflate(byte[] data, bool last)
        {
            var ms = new MemoryStream(data.Length / 2 + 64);
            var ds = new DeflateStream(ms, new ZLibCompressionOptions { CompressionLevel = CompressionLevel }, leaveOpen: true);
            ds.Write(data);
            if (last) { ds.Dispose(); return ms.ToArray(); }
            ds.Flush();   // Z_SYNC_FLUSH
            int cut = (int)ms.Length;
            ds.Dispose(); // appends the final block, which is dropped
            return ms.GetBuffer().AsSpan(0, cut).ToArray();
        }

        private const uint AdlerBase = 65521;

        private static uint Adler32(ReadOnlySpan<byte> data)
        {
            uint a = 1, b = 0;
            while (data.Length > 0)
            {
                int n = Math.Min(data.Length, 5552);   // largest run before b can overflow
                foreach (byte v in data[..n]) { a += v; b += a; }
                a %= AdlerBase; b %= AdlerBase;
                data = data[n..];
            }
            return (b << 16) | a;
        }

        // zlib adler32_combine: the checksum of A‖B from those of A and B and B's length.
        public static uint Adler32Combine(uint adler1, uint adler2, long len2)
        {
            uint rem = (uint)(len2 % AdlerBase);
            uint sum1 = adler1 & 0xFFFF;
            uint sum2 = (uint)((ulong)rem * sum1 % AdlerBase);
            sum1 += (adler2 & 0xFFFF) + AdlerBase - 1;
            sum2 += ((adler1 >> 16) & 0xFFFF) + ((adler2 >> 16) & 0xFFFF) + AdlerBase - rem;
            if (sum1 >= AdlerBase) sum1 -= AdlerBase;
            if (sum1 >= AdlerBase) sum1 -= AdlerBase;
            if (sum2 >= AdlerBase << 1) sum2 -= AdlerBase << 1;
            if (sum2 >= AdlerBase) sum2 -= AdlerBase;
            return sum1 | (sum2 << 16);
        }

        // CRC-32 as in the PNG specification's Annex D (polynomial 0xEDB88320).
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static uint ChunkCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte v in type) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
            foreach (byte v in data) c = CrcTable[(c ^ v) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data, uint crc)
        {
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)data.Length);
            s.Write(u32);
            s.Write(type);
            s.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(u32, crc);
            s.Write(u32);
        }
    }
}
