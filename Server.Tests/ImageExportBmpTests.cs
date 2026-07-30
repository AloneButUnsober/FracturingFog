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
/// Regression guard for the "object reference not set to an instance of an
/// object" crash when saving a poster / image as BMP. SkiaSharp's encoders
/// cover PNG/JPEG/WEBP only — BMP and GIF are DECODE-ONLY, so SKImage.Encode
/// returns a null SKData and the old code NRE'd at data.SaveTo. ImageExport now
/// writes BMP by hand and PNG-fallbacks any other decode-only format.
/// </summary>
public sealed class ImageExportBmpTests
{
    // ARGB uint (0xAARRGGBB) BGRA buffer, top-left red, filled solid.
    private static uint[] MakeBuffer(int w, int h, uint argb)
    {
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = argb;
        return px;
    }

    [Fact]
    public void SaveBmp_NoWatermark_WritesDecodableFile()
    {
        int w = 8, h = 6;
        const uint red = 0xFFFF0000u;
        var px = MakeBuffer(w, h, red);
        string path = Path.Combine(Path.GetTempPath(),
            $"ff-bmp-{Guid.NewGuid():N}.bmp");
        try
        {
            // Must not throw (was NRE at data.SaveTo before the fix).
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Bmp,
                (WatermarkRender?)null);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 54, "BMP header + pixels expected.");

            using var decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(w, decoded!.Width);
            Assert.Equal(h, decoded.Height);

            // Round-trip the colour (orientation preserved: top-left stays red).
            var c = decoded.GetPixel(0, 0);
            Assert.Equal(255, c.Red);
            Assert.Equal(0, c.Green);
            Assert.Equal(0, c.Blue);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveBmp_WithWatermark_DoesNotThrow()
    {
        int w = 64, h = 48;
        var px = MakeBuffer(w, h, 0xFF203040u);
        string path = Path.Combine(Path.GetTempPath(),
            $"ff-bmp-wm-{Guid.NewGuid():N}.bmp");
        var wm = new WatermarkRender
        {
            TopText = "Region - Theme",
            SubText = "FracturingFog v1",
            TextColor = new RgbDef(255, 255, 255),
        };
        try
        {
            // Composite re-encode also hit the null-SKData NRE on BMP.
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Bmp,
                wm, poster: true);

            Assert.True(File.Exists(path));
            using var decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(w, decoded!.Width);
            Assert.Equal(h, decoded.Height);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveGif_FallsBackToPng_NoThrow()
    {
        int w = 8, h = 8;
        var px = MakeBuffer(w, h, 0xFF00FF00u);
        string path = Path.Combine(Path.GetTempPath(),
            $"ff-gif-{Guid.NewGuid():N}.gif");
        try
        {
            // GIF is decode-only in SkiaSharp too — must fall back to PNG bytes
            // instead of NRE. File still lands at the requested path.
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Gif,
                (WatermarkRender?)null);

            Assert.True(File.Exists(path));
            using var decoded = SKBitmap.Decode(path);
            Assert.NotNull(decoded);
            Assert.Equal(w, decoded!.Width);
            Assert.Equal(h, decoded.Height);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
