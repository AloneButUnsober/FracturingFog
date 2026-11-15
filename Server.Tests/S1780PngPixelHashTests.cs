// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1780 — the visual-regression tool hashed PNG file bytes, so PR #1645's encoder
// change (Skia → ParallelPngWriter, #1640) failed all 40 cases with no pixel changed.
// It now hashes decoded pixels (PngPixelHash). Checked:
//   • the hash's decoder against an independent one (Skia / libpng, decoding to
//     straight BGRA) on files from both encoders, opaque and translucent, and grey /
//     grey + alpha; and against the source on a file with all five filter types;
//   • the same pixels hash the same whichever encoder wrote them — the old Skia route
//     (8f7fa2f0's SaveBgraSkia: an Unpremul Bgra8888 bitmap, SKImage.Encode PNG 100,
//     always RGBA) and ParallelPngWriter (RGB when opaque);
//   • the hash moves with any one pixel's colour or alpha, and with the dimensions;
//   • translucent output (the #1780 question: does the encoder change touch renders
//     with modified alpha?): random straight alpha including 0 and 255 next to each
//     other, alpha-0 pixels with non-zero colour, and a real Mandelbrot render with
//     InteriorAlpha 128 and 0. Both encoders decode to the same straight pixels.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog.Imaging;
using FracturingFog.Models;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

public class S1780PngPixelHashTests
{
    private static uint[] Pattern(int w, int h, bool opaque, int seed = 1780)
    {
        var rng = new Random(seed);
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint r = (uint)((x * 255 / Math.Max(1, w - 1) + rng.Next(8)) & 0xFF);
                uint g = (uint)((y * 255 / Math.Max(1, h - 1) + rng.Next(8)) & 0xFF);
                uint b = (uint)rng.Next(256);
                uint a = opaque ? 255u : (uint)rng.Next(256);
                px[y * w + x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        return px;
    }

    // The pre-#1645 PNG route (ImageExport.SaveBgraSkia at 8f7fa2f0).
    private static byte[] EncodeSkiaOld(uint[] px, int w, int h)
    {
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var bytes = new byte[w * h * 4];
        Buffer.BlockCopy(px, 0, bytes, 0, bytes.Length);
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] EncodeParallel(uint[] px, int w, int h)
    {
        var ms = new MemoryStream();
        ParallelPngWriter.Write(ms, px, w, h);
        return ms.ToArray();
    }

    // Independent decoder: Skia (libpng) to straight BGRA, repacked as R, G, B, A bytes.
    private static byte[] DecodeSkiaRgba(byte[] png, out int w, out int h)
    {
        using var codec = SKCodec.Create(new MemoryStream(png));
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bmp.GetPixels()));
        w = info.Width; h = info.Height;
        var outB = new byte[w * h * 4];
        var px = bmp.Pixels;
        for (int i = 0; i < px.Length; i++)
        {
            outB[4 * i] = px[i].Red; outB[4 * i + 1] = px[i].Green; outB[4 * i + 2] = px[i].Blue; outB[4 * i + 3] = px[i].Alpha;
        }
        return outB;
    }

    private static byte ColourType(byte[] png) => png[25];

    // ── The decoder ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(5, 3, false)]
    [InlineData(64, 64, true)]
    [InlineData(257, 130, false)]
    [InlineData(300, 301, true)]
    public void The_Decoder_Matches_Skia_On_Both_Encoders(int w, int h, bool opaque)
    {
        var src = Pattern(w, h, opaque);
        foreach (var png in new[] { EncodeSkiaOld(src, w, h), EncodeParallel(src, w, h) })
        {
            var mine = PngPixelHash.DecodeRgba(png, out int mw, out int mh);
            var skia = DecodeSkiaRgba(png, out int sw, out int sh);
            Assert.Equal((w, h), (mw, mh));
            Assert.Equal((sw, sh), (mw, mh));
            Assert.Equal(skia, mine);
        }
    }

    // A PNG whose rows use filter type y % 5 (None, Sub, Up, Average, Paeth), filtered
    // here forward from the PNG specification §9.2–9.4 (Skia's encoder picks only
    // some types per image, so it can't be relied on to exercise all five).
    private static byte[] EncodeEveryFilter(byte[] rgba, int w, int h)
    {
        int stride = w * 4;
        var raw = new MemoryStream();
        var prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int f = y % 5;
            raw.WriteByte((byte)f);
            for (int i = 0; i < stride; i++)
            {
                int x = rgba[y * stride + i], a = i >= 4 ? rgba[y * stride + i - 4] : 0, b = prev[i], c = i >= 4 ? prev[i - 4] : 0;
                int pred = f switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    _ => PaethRef(a, b, c),
                };
                raw.WriteByte((byte)(x - pred));
            }
            Array.Copy(rgba, y * stride, prev, 0, stride);
        }
        var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(raw.ToArray());
        var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int PaethRef(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len); s.Write(t); s.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (byte v in t.Concat(data))
        {
            crc ^= v;
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, ~crc);
        s.Write(len);
    }

    [Fact]
    public void The_Decoder_Handles_Every_Filter_Type()
    {
        const int w = 37, h = 25;   // five rows of each filter type
        var rgba = new byte[w * h * 4];
        new Random(1780).NextBytes(rgba);
        var png = EncodeEveryFilter(rgba, w, h);
        Assert.Equal(rgba, PngPixelHash.DecodeRgba(png, out int dw, out int dh));
        Assert.Equal((w, h), (dw, dh));
        Assert.Equal(DecodeSkiaRgba(png, out _, out _), rgba);   // the test file is a valid PNG
    }

    [Fact]
    public void Grey_And_Grey_Alpha_Files_Decode_Like_Skia()
    {
        foreach (var (ct, alpha) in new[] { (SKColorType.Gray8, SKAlphaType.Opaque), (SKColorType.Alpha8, SKAlphaType.Premul) })
        {
            _ = alpha;
            var info = new SKImageInfo(37, 23, ct, ct == SKColorType.Gray8 ? SKAlphaType.Opaque : SKAlphaType.Premul);
            using var bmp = new SKBitmap(info);
            var rng = new Random(7);
            var bytes = new byte[info.BytesSize];
            rng.NextBytes(bytes);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bmp.GetPixels(), bytes.Length);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            var png = data.ToArray();
            Assert.Equal(DecodeSkiaRgba(png, out _, out _), PngPixelHash.DecodeRgba(png, out _, out _));
        }
    }

    [Fact]
    public void Unsupported_Layouts_Throw_Instead_Of_Hashing_Garbage()
    {
        Assert.Throws<InvalidDataException>(() => PngPixelHash.DecodeRgba(new byte[] { 1, 2, 3 }, out _, out _));
        var png = EncodeParallel(Pattern(8, 8, true), 8, 8);
        png[24] = 16;   // bit depth
        Assert.Throws<NotSupportedException>(() => PngPixelHash.DecodeRgba(png, out _, out _));
    }

    // ── The hash ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Same_Pixels_Hash_The_Same_From_Either_Encoder(bool opaque)
    {
        const int w = 256, h = 256;   // the visual-regression case size
        var src = Pattern(w, h, opaque);
        byte[] oldPng = EncodeSkiaOld(src, w, h), newPng = EncodeParallel(src, w, h);
        Assert.NotEqual(oldPng, newPng);   // precondition: the files differ
        if (opaque)
        {
            Assert.Equal(6, ColourType(oldPng));   // Skia: RGBA
            Assert.Equal(2, ColourType(newPng));   // ParallelPngWriter: RGB
        }
        Assert.Equal(PngPixelHash.Of(oldPng), PngPixelHash.Of(newPng));
    }

    [Fact]
    public void The_Hash_Moves_With_One_Pixel_Or_The_Shape()
    {
        const int w = 64, h = 48;
        var src = Pattern(w, h, opaque: true);
        string baseHash = PngPixelHash.Of(EncodeParallel(src, w, h));
        foreach (uint flip in new[] { 0x00000001u, 0x00000100u, 0x00010000u, 0x01000000u })
        {
            var p = (uint[])src.Clone();
            p[w * h / 2 + 3] ^= flip;   // one channel bit of one pixel (the last: alpha 255 → 254)
            Assert.NotEqual(baseHash, PngPixelHash.Of(EncodeParallel(p, w, h)));
        }
        // Same bytes, other shape.
        Assert.NotEqual(baseHash, PngPixelHash.Of(EncodeParallel(src, h, w)));
    }

    // ── Alpha: does the encoder change touch translucent renders? ─────────────

    [Fact]
    public void Translucent_Pixels_Decode_The_Same_From_Either_Encoder()
    {
        const int w = 64, h = 64;
        var src = Pattern(w, h, opaque: false);
        // Edge cases: alpha 0 with colour, alpha 1, alpha 254, next to opaque pixels.
        src[0] = 0x00FF8040u; src[1] = 0x01FF8040u; src[2] = 0xFEFF8040u; src[3] = 0xFFFF8040u;
        src[w] = 0x00000000u; src[w + 1] = 0x00FFFFFFu;
        var oldPx = DecodeSkiaRgba(EncodeSkiaOld(src, w, h), out _, out _);
        var newPx = DecodeSkiaRgba(EncodeParallel(src, w, h), out _, out _);
        Assert.Equal(oldPx, newPx);
        // And both are the buffer itself (straight alpha kept, alpha-0 colour kept).
        Assert.Equal(0x40, newPx[2]); Assert.Equal(0x00, newPx[3]);
        Assert.Equal(0xFF, newPx[4 * (w + 1)]); Assert.Equal(0x00, newPx[4 * (w + 1) + 3]);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(0)]
    public void A_Render_With_Interior_Alpha_Decodes_The_Same_From_Either_Encoder(int interiorAlpha)
    {
        const int w = 160, h = 128;
        var c = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0, Zoom = 0.6, MaxIterations = 300,
            ColorMap = new HsvPalette(), InteriorAlpha = interiorAlpha,
        };
        c.Calculate(CancellationToken.None);
        var px = c.ColorBuffer.Take(w * h).ToArray();
        // Precondition: the frame really is translucent, and has opaque pixels too.
        Assert.Contains(px, p => (p >> 24) == (uint)interiorAlpha);
        Assert.Contains(px, p => (p >> 24) == 255u);

        byte[] oldPng = EncodeSkiaOld(px, w, h), newPng = EncodeParallel(px, w, h);
        Assert.Equal(6, ColourType(newPng));   // translucent stays RGBA
        Assert.Equal(DecodeSkiaRgba(oldPng, out _, out _), DecodeSkiaRgba(newPng, out _, out _));
        Assert.Equal(PngPixelHash.Of(oldPng), PngPixelHash.Of(newPng));
    }
}
