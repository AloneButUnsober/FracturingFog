// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1640 — the parallel PNG writer (per-strip deflate + sync flush, Adler-32 combine,
// one IDAT per strip) must give a valid PNG whose decoded pixels equal the buffer,
// and the one-pass watermark must paint what the old encode / decode / paint /
// re-encode route painted. Checked independently of the writer:
//   • Skia (libpng) decodes the file to the source pixels, opaque and translucent,
//     at sizes that give one strip, many strips and a short last strip;
//   • a separate inflate of the concatenated IDAT data (ZLibStream, which checks the
//     Adler-32) gives the same filtered bytes length, and every chunk CRC matches a
//     bit-by-bit CRC-32 (not the writer's table);
//   • Adler32Combine equals the direct checksum of the concatenation;
//   • the watermarked PNG decodes to the same pixels as the old two-pass route.
// Mutants: dropping the sync flush, the combine (using the last strip's Adler only)
// or the Up filter's "row above" term each fail the round-trip tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using FracturingFog.Imaging;
using FracturingFog.Models;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

public class S1640ParallelPngTests
{
    private static uint[] Pattern(int w, int h, bool opaque, int seed = 1640)
    {
        var rng = new Random(seed);
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // Smooth gradient plus noise: compressible like a frame, but every byte varies.
                uint r = (uint)((x * 255 / Math.Max(1, w - 1) + rng.Next(8)) & 0xFF);
                uint g = (uint)((y * 255 / Math.Max(1, h - 1) + rng.Next(8)) & 0xFF);
                uint b = (uint)rng.Next(256);
                uint a = opaque ? 255u : (uint)rng.Next(256);
                px[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        return px;
    }

    private static uint[] DecodeSkia(byte[] png, out int w, out int h)
    {
        using var codec = SKCodec.Create(new MemoryStream(png));
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bmp.GetPixels()));
        w = info.Width; h = info.Height;
        return Array.ConvertAll(bmp.Pixels, c => (uint)c);
    }

    private static byte[] Encode(uint[] px, int w, int h)
    {
        var ms = new MemoryStream();
        ParallelPngWriter.Write(ms, px, w, h);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(7, 3, false)]
    [InlineData(64, 63, true)]          // under one strip
    [InlineData(257, 700, true)]        // many strips
    [InlineData(301, 1001, false)]      // many strips, short last strip, translucent
    [InlineData(3840, 2160, true)]      // the bench size
    public void Decodes_To_The_Source_Pixels(int w, int h, bool opaque)
    {
        var src = Pattern(w, h, opaque);
        var got = DecodeSkia(Encode(src, w, h), out int dw, out int dh);
        Assert.Equal((w, h), (dw, dh));
        Assert.Equal(src, got);
    }

    [Fact]
    public void Opaque_Is_Rgb_And_Translucent_Is_Rgba()
    {
        Assert.Equal(2, Encode(Pattern(16, 16, opaque: true), 16, 16)[25]);
        var t = Pattern(16, 16, opaque: true);
        t[5] &= 0x7FFFFFFFu;
        Assert.Equal(6, Encode(t, 16, 16)[25]);
    }

    // Bit-by-bit CRC-32 (PNG spec polynomial), independent of the writer's table.
    private static uint BitCrc(ReadOnlySpan<byte> bytes)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte v in bytes)
        {
            c ^= v;
            for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1));
        }
        return ~c;
    }

    [Fact]
    public void Chunks_Have_Valid_Crcs_And_The_Zlib_Stream_Inflates_With_Its_Adler()
    {
        const int w = 500, h = 900;
        var png = Encode(Pattern(w, h, opaque: false), w, h);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var idat = new MemoryStream();
        var types = new List<string>();
        int pos = 8;
        while (pos < png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var typeAndData = png.AsSpan(pos + 4, 4 + len);
            uint crc = (uint)((png[pos + 8 + len] << 24) | (png[pos + 9 + len] << 16) | (png[pos + 10 + len] << 8) | png[pos + 11 + len]);
            string type = System.Text.Encoding.ASCII.GetString(typeAndData[..4]);
            types.Add(type);
            Assert.True(BitCrc(typeAndData) == crc, $"{type} CRC");
            if (type == "IDAT") idat.Write(typeAndData[4..]);
            pos += 12 + len;
        }
        Assert.Equal("IHDR", types[0]);
        Assert.Equal("IEND", types[^1]);
        Assert.True(types.FindAll(t => t == "IDAT").Count > 1, "precondition: several strips");

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);   // throws on a bad Adler-32 or a broken deflate stream
        Assert.Equal((long)(w * 4 + 1) * h, raw.Length);
    }

    private static uint DirectAdler(ReadOnlySpan<byte> d)
    {
        uint a = 1, b = 0;
        foreach (byte v in d) { a = (a + v) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 1)]
    [InlineData(5552, 70000)]
    [InlineData(123457, 65521)]
    public void Adler32_Combine_Equals_The_Direct_Checksum(int lenA, int lenB)
    {
        var rng = new Random(lenA ^ lenB);
        var a = new byte[lenA]; var b = new byte[lenB];
        rng.NextBytes(a); rng.NextBytes(b);
        var ab = new byte[lenA + lenB]; a.CopyTo(ab, 0); b.CopyTo(ab, lenA);
        Assert.Equal(DirectAdler(ab), ParallelPngWriter.Adler32Combine(DirectAdler(a), DirectAdler(b), lenB));
    }

    [Fact]
    public void One_Pass_Watermark_Matches_The_Old_Decode_And_Repaint_Route()
    {
        const int w = 640, h = 360;
        var src = Pattern(w, h, opaque: true);
        var wm = new WatermarkRender
        {
            TopText = "Fracturing Fog 1640", SubText = "x = -0.74364 y = 0.13183",
            TextColor = new RgbDef(255, 204, 0),
            Placement = WatermarkPlacement.Bottom, Justify = WatermarkJustify.Right,
        };
        string dir = Path.Combine(Path.GetTempPath(), "ff1640_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string newPath = Path.Combine(dir, "new.png"), oldPath = Path.Combine(dir, "old.png");
            ImageExport.SavePixelsToFile(src, w, h, newPath, ImageFileFormat.Png, wm);

            // Old route: plain save, then decode + paint + re-encode from the file.
            ImageExport.SavePixelsToFile(src, w, h, oldPath, ImageFileFormat.Png, wm: null);
            typeof(ImageExport).GetMethod("CompositeWatermarkRenderSkia", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [oldPath, ImageFileFormat.Png, wm, true]);

            var got = DecodeSkia(File.ReadAllBytes(newPath), out _, out _);
            var want = DecodeSkia(File.ReadAllBytes(oldPath), out _, out _);
            Assert.NotEqual(src, want);   // precondition: the watermark changed pixels
            Assert.Equal(want, got);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
