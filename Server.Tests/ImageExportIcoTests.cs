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
/// #68 slice 1 — Windows .ico export. SkiaSharp cannot encode ICO, so
/// ImageExport hand-rolls the container: an ICONDIR + one ICONDIRENTRY per
/// image + PNG-encoded blobs, center-cropped to a square and downscaled to a
/// multi-resolution set. These tests parse the container structure directly
/// (independent of Skia's ICO *decoder*) and decode each embedded PNG blob.
/// </summary>
public sealed class ImageExportIcoTests
{
    private static uint[] MakeBuffer(int w, int h, uint argb)
    {
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = argb;
        return px;
    }

    // Minimal ICONDIR/ICONDIRENTRY reader: returns (declaredWidth, size, offset,
    // length) per entry. declaredWidth is the raw byte (0 == 256).
    private static (byte declaredW, int bytes, int offset)[] ParseIco(byte[] ico)
    {
        Assert.True(ico.Length >= 6, "ICONDIR present");
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0)); // idReserved
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2)); // idType = icon
        int n = BitConverter.ToUInt16(ico, 4);
        Assert.True(n >= 1, "at least one entry");

        var entries = new (byte, int, int)[n];
        for (int i = 0; i < n; i++)
        {
            int e = 6 + i * 16;
            byte declaredW = ico[e];               // width (0 = 256)
            int bytes = (int)BitConverter.ToUInt32(ico, e + 8);
            int offset = (int)BitConverter.ToUInt32(ico, e + 12);
            Assert.True(offset + bytes <= ico.Length, "blob within file");
            entries[i] = (declaredW, bytes, offset);
        }
        return entries;
    }

    [Fact]
    public void SaveIco_WritesValidContainer_WithDecodablePngFrames()
    {
        int w = 64, h = 48; // side = 48 → sizes {16, 32, 48}
        var px = MakeBuffer(w, h, 0xFF2080C0u);
        string path = Path.Combine(Path.GetTempPath(), $"ff-ico-{Guid.NewGuid():N}.ico");
        try
        {
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Ico, (WatermarkRender?)null);

            Assert.True(File.Exists(path));
            byte[] ico = File.ReadAllBytes(path);
            var entries = ParseIco(ico);

            // Every embedded blob must be a decodable PNG whose dimensions match
            // the entry's declared size (square).
            foreach (var (declaredW, bytes, offset) in entries)
            {
                int expected = declaredW == 0 ? 256 : declaredW;
                using var frame = SKBitmap.Decode(new ReadOnlySpan<byte>(ico, offset, bytes).ToArray());
                Assert.NotNull(frame);
                Assert.Equal(expected, frame!.Width);
                Assert.Equal(expected, frame.Height);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveIco_LargeFrame_IncludesFull256Entry()
    {
        int w = 300, h = 300; // side = 300 → {16, 32, 48, 256}
        var px = MakeBuffer(w, h, 0xFF10FF40u);
        string path = Path.Combine(Path.GetTempPath(), $"ff-ico-big-{Guid.NewGuid():N}.ico");
        try
        {
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Ico, (WatermarkRender?)null);
            var entries = ParseIco(File.ReadAllBytes(path));

            Assert.Equal(4, entries.Length);
            // 256 is encoded as a width byte of 0.
            Assert.Contains(entries, e => e.declaredW == 0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveIco_TinyFrame_SingleEntryAtSourceSize()
    {
        int w = 8, h = 8; // no standard size ≤ 8 → single entry at 8
        var px = MakeBuffer(w, h, 0xFFFFFFFFu);
        string path = Path.Combine(Path.GetTempPath(), $"ff-ico-tiny-{Guid.NewGuid():N}.ico");
        try
        {
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Ico, (WatermarkRender?)null);
            var entries = ParseIco(File.ReadAllBytes(path));

            Assert.Single(entries);
            Assert.Equal(8, entries[0].declaredW);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveIco_AutoFromExtension_MatchesExplicitToken()
    {
        int w = 40, h = 40;
        var px = MakeBuffer(w, h, 0xFF804020u);
        string autoPath = Path.Combine(Path.GetTempPath(), $"ff-ico-auto-{Guid.NewGuid():N}.ico");
        string tokenPath = Path.Combine(Path.GetTempPath(), $"ff-ico-tok-{Guid.NewGuid():N}.ico");
        try
        {
            // Auto infers ICO from the .ico extension; explicit token forces it.
            ImageExport.SavePixelsToFile(px, w, h, autoPath, ImageFileFormat.Auto, (WatermarkRender?)null);
            ImageExport.SavePixelsToFile(px, w, h, tokenPath, ImageFileFormat.Ico, (WatermarkRender?)null);

            var a = ParseIco(File.ReadAllBytes(autoPath));
            var t = ParseIco(File.ReadAllBytes(tokenPath));
            Assert.Equal(t.Length, a.Length); // both produced the same entry set
        }
        finally
        {
            if (File.Exists(autoPath)) File.Delete(autoPath);
            if (File.Exists(tokenPath)) File.Delete(tokenPath);
        }
    }
}
