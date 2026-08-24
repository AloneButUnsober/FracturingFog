// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/OpenExrWriter.cs
//
// Minimal OpenEXR scanline-image ENCODER — the mirror of OpenExrReader.
// Pure managed (no native libIlmImf, no NuGet) so the shell ships unchanged on
// every platform per the CLAUDE.md cross-platform mandate.
//
// Roadmap slice S7 (3D-Rendering-Roadmap.md, parent #389): float / multi-layer
// EXR is the enabler for AOV passes (S1), the linear/HDR intermediate (S2) and
// HDR volumetrics (S6). This is the first slice — the writer itself, plus an
// 8-bit-BGRA → EXR bridge so `.exr` export works today. Real float AOV layers
// feed in when S1 lands; the writer already takes arbitrary named channels.
//
// What we encode
//   * Single-part scanline EXR (the format Blender / oiiotool / DJV / Nuke all
//     read). RGB, RGBA, or any set of named channels (Z, normal.X, albedo.R …).
//   * HALF (IEEE-754 binary16) and FLOAT (32-bit) channels, per-channel.
//   * NONE (uncompressed, the default) or ZIP (16-line zlib blocks with the EXR
//     predictor + interleave — the exact inverse of OpenExrReader's decode).
//     NONE is deterministic byte-for-byte — the parity contract the roadmap
//     names for this slice; ZIP is smaller and lossless but NOT byte-stable
//     (DeflateStream's bytes vary across runtimes), so it is opt-in.
//
// What we do NOT encode: tiled, deep, multi-part, lossy codecs. Channels are
// emitted alphabetically per spec (A < B < G < R), so OpenExrReader and every
// conformant reader locate them by name regardless of the order handed in.
//
// File layout mirrors OpenExrReader's parser exactly — see that file's header
// comment for the byte grammar. All multi-byte integers are little-endian.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FracturingFog.Imaging;

/// <summary>Compression codec for <see cref="OpenExrWriter"/>. Values match the
/// EXR spec compression ids (and <c>OpenExrReader</c>'s enum).</summary>
public enum ExrCompression
{
    /// <summary>Uncompressed. Deterministic byte-for-byte output — the roadmap's
    /// byte-stability contract. The default.</summary>
    None = 0,

    /// <summary>ZIP: 16-scanline zlib(deflate) blocks with the EXR byte predictor
    /// + interleave (spec id 3). Smaller files, but DeflateStream's bytes are not
    /// stable across runtimes, so output is NOT byte-stable — opt-in when size
    /// matters. Lossless; reads in Blender / oiiotool / our own reader.</summary>
    Zip = 3,
}

/// <summary>One named channel of an <see cref="OpenExrWriter"/> image: a
/// full-frame plane of <c>width*height</c> float samples, written as HALF or
/// FLOAT.</summary>
public sealed class ExrChannel
{
    public ExrChannel(string name, float[] data, bool half = true)
    {
        Name = name;
        Data = data;
        Half = half;
    }

    /// <summary>Spec channel name. Bare "R"/"G"/"B"/"A" for the beauty pass;
    /// dotted "layer.R" / "normal.X" / "Z" for AOVs (S1).</summary>
    public string Name { get; }

    /// <summary>Row-major <c>width*height</c> samples.</summary>
    public float[] Data { get; }

    /// <summary>true → HALF (2 bytes/sample), false → FLOAT (4 bytes).</summary>
    public bool Half { get; }
}

/// <summary>Minimal cross-platform OpenEXR scanline encoder. Mirror of
/// <c>OpenExrReader</c>.</summary>
public static class OpenExrWriter
{
    private const uint MagicNumber = 0x01312f76;
    private const int PixelTypeHalf = 1;
    private const int PixelTypeFloat = 2;

    /// <summary>Write a multi-channel EXR to <paramref name="stream"/>. Channels
    /// may mix HALF and FLOAT; each must carry exactly <c>width*height</c>
    /// samples. <paramref name="compression"/> defaults to NONE (uncompressed,
    /// deterministic); <see cref="ExrCompression.Zip"/> produces smaller,
    /// lossless — but not byte-stable — files.</summary>
    public static void Write(Stream stream, int width, int height, IReadOnlyList<ExrChannel> channels,
        ExrCompression compression = ExrCompression.None)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("EXR: width/height must be positive.");
        if (channels == null || channels.Count == 0) throw new ArgumentException("EXR: at least one channel required.");
        long need = (long)width * height;
        foreach (var c in channels)
            if (c.Data.Length < need)
                throw new ArgumentException($"EXR: channel '{c.Name}' has {c.Data.Length} samples, need {need}.");

        // Channels must be emitted alphabetically by name (spec + our reader
        // walks them in file order). Sort a copy so the caller's order is free.
        var ordered = new List<ExrChannel>(channels);
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        using var bw = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        bw.Write(MagicNumber);
        bw.Write((uint)2);   // version 2, flags 0 = single-part scanline

        byte compByte = (byte)compression;
        WriteChannelsAttr(bw, ordered);
        WriteAttr(bw, "compression", "compression", w => w.Write(compByte));                  // NONE / ZIP
        WriteBox2iAttr(bw, "dataWindow", 0, 0, width - 1, height - 1);
        WriteBox2iAttr(bw, "displayWindow", 0, 0, width - 1, height - 1);
        WriteAttr(bw, "lineOrder", "lineOrder", static w => w.Write((byte)0));                // INCREASING_Y
        WriteAttr(bw, "pixelAspectRatio", "float", static w => w.Write(1.0f));
        WriteAttr(bw, "screenWindowCenter", "v2f", static w => { w.Write(0.0f); w.Write(0.0f); });
        WriteAttr(bw, "screenWindowWidth", "float", static w => w.Write(1.0f));
        bw.Write((byte)0);   // end-of-header sentinel

        // Per-scanline byte size = sum over channels of width * bytesPerSample.
        int rowBytes = 0;
        foreach (var c in ordered) rowBytes += width * (c.Half ? 2 : 4);

        if (compression == ExrCompression.None)
        {
            // Offset table: one i64 per scanline (NONE → chunkLines = 1). Each
            // chunk on disk is [scanY:i32][dataSize:i32][pixels]. Fixed stride, so
            // offsets are computed up front. Byte-identical to the pre-ZIP writer.
            long offsetTableBytes = (long)height * 8;
            long firstChunk = bw.BaseStream.Position + offsetTableBytes;
            long chunkStride = 8 + rowBytes;   // i32 scanY + i32 dataSize + pixels
            for (int y = 0; y < height; y++)
                bw.Write(firstChunk + y * chunkStride);

            // Chunks. Pixel data is channel-major within each scanline, channels
            // in the same alphabetical order as the chlist.
            for (int y = 0; y < height; y++)
            {
                bw.Write(y);          // scanY (data-window-relative; ymin = 0)
                bw.Write(rowBytes);   // uncompressed dataSize
                WriteRawLines(bw, ordered, width, y, 1);
            }
            return;
        }

        // ── ZIP: 16-scanline blocks ──────────────────────────────────────────
        // Chunk sizes vary, so build every chunk's on-disk payload first, then
        // lay down the offset table from the accumulated sizes.
        const int chunkLines = 16;
        int chunkCount = (height + chunkLines - 1) / chunkLines;
        var payloads = new byte[chunkCount][];
        var scanYs = new int[chunkCount];
        for (int ci = 0; ci < chunkCount; ci++)
        {
            int scanY = ci * chunkLines;
            int lines = Math.Min(chunkLines, height - scanY);
            scanYs[ci] = scanY;

            // Raw channel-major bytes for this block (the bytes a NONE chunk holds).
            byte[] raw;
            using (var rm = new MemoryStream(rowBytes * lines))
            {
                using (var rw = new BinaryWriter(rm, Encoding.ASCII, leaveOpen: true))
                    for (int li = 0; li < lines; li++)
                        WriteRawLines(rw, ordered, width, scanY + li, 1);
                raw = rm.ToArray();
            }

            byte[] zipped = ZipCompressBlock(raw);
            // Spec rule: if the compressed block is not smaller than the raw
            // block, store the raw bytes verbatim (chunkSize == rawSize signals
            // this to the reader, which then skips inflate + predictor/interleave).
            payloads[ci] = zipped.Length < raw.Length ? zipped : raw;
        }

        long offTableBytes = (long)chunkCount * 8;
        long running = bw.BaseStream.Position + offTableBytes;
        for (int ci = 0; ci < chunkCount; ci++)
        {
            bw.Write(running);
            running += 8 + payloads[ci].Length;   // i32 scanY + i32 size + data
        }
        for (int ci = 0; ci < chunkCount; ci++)
        {
            bw.Write(scanYs[ci]);
            bw.Write(payloads[ci].Length);
            bw.Write(payloads[ci]);
        }
    }

    /// <summary>Emit <paramref name="lines"/> scanlines starting at row
    /// <paramref name="y0"/> in EXR channel-major / row-major byte order (each
    /// channel's full row, channels in the caller's already-alphabetical order).
    /// Shared by the NONE and ZIP paths.</summary>
    private static void WriteRawLines(BinaryWriter bw, List<ExrChannel> ordered, int width, int y0, int lines)
    {
        for (int li = 0; li < lines; li++)
        {
            int rowOffset = (y0 + li) * width;
            foreach (var c in ordered)
            {
                var data = c.Data;
                if (c.Half)
                {
                    for (int x = 0; x < width; x++)
                        bw.Write(BitConverter.HalfToUInt16Bits((Half)data[rowOffset + x]));
                }
                else
                {
                    for (int x = 0; x < width; x++)
                        bw.Write(data[rowOffset + x]);
                }
            }
        }
    }

    // ── ZIP block codec (exact inverse of OpenExrReader's decode) ────────────

    /// <summary>Compress one raw EXR scanline block to a zlib-wrapped deflate
    /// stream, applying the EXR reorder (interleave) + delta (predictor) the
    /// reader reverses. Encode order is the mirror of decode
    /// (inflate → un-delta → de-interleave): interleave → delta → deflate.</summary>
    private static byte[] ZipCompressBlock(byte[] raw)
    {
        int count = raw.Length;

        // 1. Reorder: split into even-index bytes (first half) + odd-index bytes
        //    (second half). Inverse of the reader's Interleave.
        var reordered = new byte[count];
        int t1 = 0;
        int t2 = (count + 1) / 2;
        int s = 0;
        while (true)
        {
            if (s < count) reordered[t1++] = raw[s++]; else break;
            if (s < count) reordered[t2++] = raw[s++]; else break;
        }

        // 2. Delta: forward difference the reordered bytes. Inverse of the
        //    reader's Predictor (which does out[i] = out[i-1] + in[i] - 128).
        var deltad = new byte[count];
        if (count > 0) deltad[0] = reordered[0];
        for (int i = 1; i < count; i++)
            deltad[i] = (byte)(reordered[i] - reordered[i - 1] + 128);

        // 3. Deflate + zlib wrap (2-byte header, 4-byte big-endian adler32 of the
        //    UNCOMPRESSED bytes). The reader strips the header and trusts the
        //    predictor to surface corruption, but Blender / oiiotool validate the
        //    adler, so it must be correct.
        byte[] deflated;
        using (var ms = new MemoryStream())
        {
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(deltad, 0, count);
            deflated = ms.ToArray();
        }

        var outBuf = new byte[2 + deflated.Length + 4];
        outBuf[0] = 0x78;   // CMF: deflate, 32K window
        outBuf[1] = 0x01;   // FLG: (0x7801 % 31 == 0), no preset dict
        Buffer.BlockCopy(deflated, 0, outBuf, 2, deflated.Length);
        uint adler = Adler32(deltad, count);
        int tp = 2 + deflated.Length;
        outBuf[tp + 0] = (byte)(adler >> 24);
        outBuf[tp + 1] = (byte)(adler >> 16);
        outBuf[tp + 2] = (byte)(adler >> 8);
        outBuf[tp + 3] = (byte)adler;
        return outBuf;
    }

    /// <summary>Adler-32 (RFC 1950) over the first <paramref name="count"/> bytes.</summary>
    private static uint Adler32(byte[] data, int count)
    {
        const uint mod = 65521;
        uint a = 1, b = 0;
        for (int i = 0; i < count; i++)
        {
            a = (a + data[i]) % mod;
            b = (b + a) % mod;
        }
        return (b << 16) | a;
    }

    /// <summary>Write an EXR file (creates/overwrites <paramref name="path"/>).</summary>
    public static void WriteFile(string path, int width, int height, IReadOnlyList<ExrChannel> channels,
        ExrCompression compression = ExrCompression.None)
    {
        using var fs = File.Create(path);
        Write(fs, width, height, channels, compression);
    }

    /// <summary>Bridge: promote an 8-bit straight-alpha BGRA <c>uint[]</c> render
    /// buffer to a float RGBA EXR. This is the interim path so `.exr` export
    /// works before float AOV sources exist (S1). When
    /// <paramref name="linearize"/> is true the RGB is un-gamma'd (sRGB → linear)
    /// so the EXR is scene-linear — the correct space for compositing; alpha is
    /// never gamma'd. HALF channels keep files small and lossless for 8-bit
    /// sources.</summary>
    public static void WriteBgra8(string path, uint[] bgra, int width, int height,
        bool linearize = true, bool half = true, bool includeAlpha = true,
        ExrCompression compression = ExrCompression.None)
    {
        long n = (long)width * height;
        if (bgra.Length < n) throw new ArgumentException("EXR: BGRA buffer smaller than width*height.");

        var r = new float[n];
        var g = new float[n];
        var b = new float[n];
        var a = includeAlpha ? new float[n] : null;
        for (int i = 0; i < n; i++)
        {
            uint p = bgra[i];
            float rf = ((p >> 16) & 0xFF) / 255f;
            float gf = ((p >> 8) & 0xFF) / 255f;
            float bf = (p & 0xFF) / 255f;
            if (linearize) { rf = SrgbToLinear(rf); gf = SrgbToLinear(gf); bf = SrgbToLinear(bf); }
            r[i] = rf; g[i] = gf; b[i] = bf;
            if (a != null) a[i] = ((p >> 24) & 0xFF) / 255f;
        }

        var channels = new List<ExrChannel>(4)
        {
            new("R", r, half),
            new("G", g, half),
            new("B", b, half),
        };
        if (a != null) channels.Add(new ExrChannel("A", a, half));
        WriteFile(path, width, height, channels, compression);
    }

    /// <summary>sRGB → linear (IEC 61966-2-1). Matches the standard transfer used
    /// by Blender / OCIO on EXR import so a linearized export round-trips.</summary>
    private static float SrgbToLinear(float c) =>
        c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    // ── header attribute writers ──────────────────────────────────────────

    private static void WriteAttr(BinaryWriter bw, string name, string type, Action<BinaryWriter> writeValue)
    {
        using var ms = new MemoryStream();
        using (var vb = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
            writeValue(vb);
        byte[] value = ms.ToArray();
        WriteNullString(bw, name);
        WriteNullString(bw, type);
        bw.Write(value.Length);
        bw.Write(value);
    }

    private static void WriteBox2iAttr(BinaryWriter bw, string name, int xmin, int ymin, int xmax, int ymax) =>
        WriteAttr(bw, name, "box2i", w => { w.Write(xmin); w.Write(ymin); w.Write(xmax); w.Write(ymax); });

    private static void WriteChannelsAttr(BinaryWriter bw, List<ExrChannel> ordered) =>
        WriteAttr(bw, "channels", "chlist", w =>
        {
            foreach (var c in ordered)
            {
                WriteNullString(w, c.Name);
                w.Write(c.Half ? PixelTypeHalf : PixelTypeFloat);   // pixelType i32
                w.Write((byte)0);                                   // pLinear
                w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); // reserved[3]
                w.Write(1);                                         // xSampling
                w.Write(1);                                         // ySampling
            }
            w.Write((byte)0);   // chlist terminator
        });

    private static void WriteNullString(BinaryWriter bw, string s)
    {
        foreach (char ch in s) bw.Write((byte)ch);
        bw.Write((byte)0);
    }
}
