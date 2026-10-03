// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;

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
            // Two distinct colours fit the palette exactly and LZW is lossless,
            // so every pixel must decode to its source colour.
            string? err = RoundTripError(px, w, h, path);
            Assert.True(err == null, err);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>#1073 — exact-round-trip check that, on failure, reports WHAT went
    /// wrong (first bad pixel, decoded colour type, how many pixels differ, and the
    /// file bytes), so an intermittent failure is diagnosable from the log alone.
    /// Null = every pixel decoded to its source RGB.</summary>
    private static string? RoundTripError(uint[] px, int w, int h, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var img = SKBitmap.Decode(path);
        if (img == null) return $"decode returned null; file {bytes.Length} B: {Convert.ToHexString(bytes)}";
        if (img.Width != w || img.Height != h) return $"decoded {img.Width}x{img.Height}, expected {w}x{h}";
        int bad = 0; string? first = null;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint src = px[y * w + x];
                var c = img.GetPixel(x, y);
                if (c.Red == (byte)(src >> 16) && c.Green == (byte)(src >> 8) && c.Blue == (byte)src) continue;
                bad++;
                first ??= $"({x},{y}) decoded {c} expected #{src & 0xFFFFFF:X6}";
            }
        return bad == 0 ? null
            : $"{bad}/{w * h} pixels wrong, first {first}; decoded {img.ColorType}/{img.AlphaType}; file {bytes.Length} B: {Convert.ToHexString(bytes)}";
    }

    [Fact]
    public void Gif_ParallelRoundTrips_AllExact()
    {
        // #1073 — the checkerboard round trip failed once in a full parallel suite
        // run and never reproduced. Hammer the same path from many threads at once
        // (as the suite does) and require every round trip to be exact.
        int w = 16, h = 16;
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = ((i % w ^ i / w) & 1) == 0 ? 0xFF102030u : 0xFFE0C0A0u;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        System.Threading.Tasks.Parallel.For(0, 256, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            string path = TempGif();
            try
            {
                SaveGif(px, w, h, path);
                if (RoundTripError(px, w, h, path) is { } e) errors.Add(e);
            }
            catch (Exception ex) { errors.Add(ex.ToString()); }
            finally { try { File.Delete(path); } catch { } }
        });
        Assert.True(errors.IsEmpty, $"{errors.Count} failed; first: {errors.FirstOrDefault()}");
    }

    [Fact]
    public void Gif_DirectBufferEncode_IsByteIdenticalToTheSkiaImagePath()
    {
        // #1073 — SavePixelsToFile now hands the BGRA buffer straight to GifEncoder
        // instead of copying it through an SKImage first. The bytes must be what
        // the Skia-image path (EncodeImageToFile, still used by other callers)
        // writes for the same pixels — including partial / zero alpha.
        int w = 37, h = 23;
        var rng = new Random(1073);
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++)
        {
            uint a = (i % 7) switch { 0 => 0u, 1 => 0x40u, _ => 0xFFu };
            px[i] = (a << 24) | (uint)rng.Next(0, 1 << 24);
        }
        string direct = TempGif(), viaSkia = TempGif();
        try
        {
            SaveGif(px, w, h, direct);

            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using (var bmp = new SKBitmap(info))
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(px.AsSpan()).ToArray(), 0, bmp.GetPixels(), w * h * 4);
                using var image = SKImage.FromBitmap(bmp);
                ImageExport.EncodeImageToFile(image, SKEncodedImageFormat.Gif, 100, viaSkia);
            }

            Assert.Equal(File.ReadAllBytes(viaSkia), File.ReadAllBytes(direct));
        }
        finally
        {
            if (File.Exists(direct)) File.Delete(direct);
            if (File.Exists(viaSkia)) File.Delete(viaSkia);
        }
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
