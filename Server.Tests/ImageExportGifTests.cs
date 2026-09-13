// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;

using FracturingFog.Imaging;
using FracturingFog.Models;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #68 slice 2 — real GIF encode (GifEncoder). Round-trips through SkiaSharp's
/// GIF *decoder* to validate the container, LZW stream, palette and 1-bit
/// transparency the hand-rolled encoder produces.
/// </summary>
public sealed class ImageExportGifTests
{
    private static void SaveGif(uint[] px, int w, int h, string path) =>
        ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Gif, (WatermarkRender?)null);

    private static string TempGif() =>
        Path.Combine(Path.GetTempPath(), $"ff-gif-{Guid.NewGuid():N}.gif");

    [Fact]
    public void Gif_TwoColourCheckerboard_LosslessRoundTrip()
    {
        int w = 16, h = 16;
        const uint a = 0xFF102030u, b = 0xFFE0C0A0u;
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = ((x ^ y) & 1) == 0 ? a : b;

        string path = TempGif();
        try
        {
            SaveGif(px, w, h, path);
            using var img = SKBitmap.Decode(path);
            Assert.NotNull(img);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);

            // Two distinct colours fit the palette exactly and LZW is lossless,
            // so every pixel must decode to its source colour.
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    uint src = px[y * w + x];
                    var c = img.GetPixel(x, y);
                    Assert.Equal((byte)(src >> 16), c.Red);
                    Assert.Equal((byte)(src >> 8), c.Green);
                    Assert.Equal((byte)src, c.Blue);
                }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Gif_ManyColours_QuantizesWithinTolerance()
    {
        // A horizontal gradient with far more than 256 distinct colours forces
        // median-cut quantization; the error on a smooth 1-D ramp stays small.
        int w = 512, h = 4;
        var px = new uint[w * h];
        for (int x = 0; x < w; x++)
        {
            byte r = (byte)(x * 255 / (w - 1));
            byte g = (byte)(255 - r);
            byte bl = (byte)((x * 2) & 0xFF);
            uint c = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | bl;
            for (int y = 0; y < h; y++) px[y * w + x] = c;
        }

        string path = TempGif();
        try
        {
            SaveGif(px, w, h, path);
            using var img = SKBitmap.Decode(path);
            Assert.NotNull(img);
            Assert.Equal(w, img!.Width);

            long err = 0;
            for (int x = 0; x < w; x++)
            {
                uint src = px[x];
                var c = img.GetPixel(x, 0);
                err += Math.Abs((int)((src >> 16) & 0xFF) - c.Red);
                err += Math.Abs((int)((src >> 8) & 0xFF) - c.Green);
                err += Math.Abs((int)(src & 0xFF) - c.Blue);
            }
            double meanErr = err / (double)(w * 3);
            Assert.True(meanErr < 8.0, $"mean per-channel quantization error {meanErr:F2} too high");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Gif_Transparency_PreservedThroughDecode()
    {
        int w = 8, h = 8;
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                // Left half opaque red, right half fully transparent.
                px[y * w + x] = x < w / 2 ? 0xFFFF0000u : 0x00000000u;

        string path = TempGif();
        try
        {
            SaveGif(px, w, h, path);
            using var img = SKBitmap.Decode(path);
            Assert.NotNull(img);

            // Opaque side keeps its colour; transparent side decodes alpha 0.
            var opaque = img!.GetPixel(1, 1);
            Assert.Equal(255, opaque.Alpha);
            Assert.Equal(255, opaque.Red);

            var clear = img.GetPixel(w - 1, 1);
            Assert.Equal(0, clear.Alpha);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Gif_LargeVariedImage_ExercisesLzwGrowthAndDecodes()
    {
        // 128×128 with a busy pattern: enough varied index runs to grow the LZW
        // code width (and possibly reset the table). Decoding intact proves the
        // width-growth timing matches the decoder.
        int w = 128, h = 128;
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte r = (byte)((x * 7 + y * 3) & 0xFF);
                byte g = (byte)((x * 3 + y * 11) & 0xFF);
                byte b = (byte)((x * 13 + y * 5) & 0xFF);
                px[y * w + x] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | b;
            }

        string path = TempGif();
        try
        {
            SaveGif(px, w, h, path);
            using var img = SKBitmap.Decode(path);
            Assert.NotNull(img);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Gif_AutoFromExtension_ProducesGif()
    {
        int w = 12, h = 12;
        var px = new uint[w * h];
        Array.Fill(px, 0xFF3366AAu);
        string path = TempGif();
        try
        {
            // Auto infers GIF from the .gif extension.
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Auto, (WatermarkRender?)null);
            byte[] head = File.ReadAllBytes(path);
            Assert.Equal((byte)'G', head[0]);
            Assert.Equal((byte)'I', head[1]);
            Assert.Equal((byte)'F', head[2]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
