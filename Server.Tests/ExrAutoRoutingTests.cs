// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S7 (#394) — GUI/batch EXR surface. The interactive screenshot save
// (FractalRenderHost.SaveLastFrame) and the `--batch --out foo.exr` path both
// rely on ImageExport routing a `.exr` extension to the scene-linear
// OpenExrWriter instead of Skia, EVEN under ImageFileFormat.Auto (the screenshot
// picker passes Auto; batch maps `.exr` → Exr). These lock that:
//   • Auto + a `.exr` path writes a real EXR (magic + reader-decodable), not a
//     PNG mislabelled `.exr` (the pre-fix bug: Png was hardcoded / guessed).
//   • Auto + a `.png` path still Skia-encodes a PNG (Auto does not over-route).

using System;
using System.IO;
using FracturingFog.Imaging;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ExrAutoRoutingTests
{
    private static uint[] MakeBuffer(int w, int h)
    {
        var px = new uint[w * h];
        for (int i = 0; i < px.Length; i++)
            px[i] = 0xFF000000u | ((uint)(i * 3 % 256) << 16)
                  | ((uint)(i * 7 % 256) << 8) | (uint)(i * 11 % 256);
        return px;
    }

    // Auto + `.exr` → OpenExrWriter, not Skia. Proven by the EXR magic (0x76 2f
    // 31 01) and a successful decode through the reader at the right dimensions.
    [Fact]
    public void AutoFormat_DotExr_WritesRealExr()
    {
        int w = 8, h = 6;
        var px = MakeBuffer(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-auto-{Guid.NewGuid():N}.exr");
        try
        {
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Auto,
                (WatermarkRender?)null);

            var head = File.ReadAllBytes(path);
            Assert.True(head.Length >= 4);
            Assert.Equal(0x76, head[0]); Assert.Equal(0x2f, head[1]);
            Assert.Equal(0x31, head[2]); Assert.Equal(0x01, head[3]);

            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // The explicit Exr token forces the writer regardless of extension (batch's
    // GuessImageFormat maps `.exr` → Exr; --aov-exr forces it too).
    [Fact]
    public void ExrToken_WritesRealExr()
    {
        int w = 4, h = 4;
        var px = MakeBuffer(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-tok-{Guid.NewGuid():N}.exr");
        try
        {
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Exr,
                (WatermarkRender?)null);

            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Auto must NOT over-route: a `.png` path still Skia-encodes a PNG (magic
    // 0x89 'P' 'N' 'G'), and the EXR reader rejects it.
    [Fact]
    public void AutoFormat_DotPng_StaysPng()
    {
        int w = 8, h = 8;
        var px = MakeBuffer(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-auto-{Guid.NewGuid():N}.png");
        try
        {
            ImageExport.SavePixelsToFile(px, w, h, path, ImageFileFormat.Auto,
                (WatermarkRender?)null);

            var head = File.ReadAllBytes(path);
            Assert.True(head.Length >= 4);
            Assert.Equal(0x89, head[0]); Assert.Equal((byte)'P', head[1]);
            Assert.Equal((byte)'N', head[2]); Assert.Equal((byte)'G', head[3]);

            Assert.False(HdriRegistry.TryLoadFromFile(path, out _),
                "a PNG must not decode as an EXR");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
