// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 (#396) — DEFAULT-LOOK VALIDATION through the real
// PosterRenderer.RenderToFile (the exact path the Image button / batch / server use).
// Locks the S2 contract end-to-end after all the producer/composite/read-back work:
//   * ViewTransform.None is byte-identical across runs (the transform + full-float
//     2D composite paths are truly skipped — the default look is preserved);
//   * exposure is inert while None is selected (the transform gate); and
//   * each transform actually reaches the rendered output.
// Covers a plain opaque 2D scene and a translucent-interior scene (the full-float
// 2D composite path, PR #659). The CI twin of the --viewtransformprobe gate.

using System;
using System.IO;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2DefaultLookTests
{
    private const int W = 160, H = 120;

    // A simple opaque ramp map — a near-black (but non-zero, so it tonemaps) interior
    // and a smooth-varying mid exterior, so a transform visibly changes the output.
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF101010u;
        public int Map(float smooth, float distance, int iterations)
        {
            byte v = (byte)(((int)(smooth * 8f)) & 0xFF);
            uint b = (uint)(200 - v / 2);
            return unchecked((int)(0xFF000000u | ((uint)v << 16) | (128u << 8) | b));
        }
    }

    private static FractalParameters MakeParams(bool translucent)
    {
        var fp = new FractalParameters();
        if (translucent)
        {
            fp.InteriorAlpha = 128;
            fp.Interior2DBackground = Interior2DBackgroundMode.SolidColor;
            fp.Interior2DBgTop = 0xFF3060A0u;
            fp.Interior2DBgBottom = 0xFF3060A0u;
        }
        return fp;
    }

    private static uint[] Render(ViewTransform vt, float ev, bool translucent)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-s2look-{Guid.NewGuid():N}.png");
        var req = new PosterRequest
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 0.9,
            MaxIterations = 200,
            FractalType = FractalType.Mandelbrot,
            Quality = QualityPreset.Standard,
            Width = W, Height = H,
            FractalParameters = MakeParams(translucent),
            ColorMap = new RampMap(),
            Path = path,
            Format = ImageFileFormat.Png,
            ViewTransform = vt,
            ViewExposureEv = ev,
        };
        try
        {
            PosterRenderer.RenderToFile(req, default);
            using var bmp = SKBitmap.Decode(path);
            Assert.NotNull(bmp);
            var buf = new uint[bmp!.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    buf[y * bmp.Width + x] = ((uint)p.Red << 16) | ((uint)p.Green << 8) | p.Blue;
                }
            return buf;
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static long Diff(uint[] a, uint[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long d = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) d++;
        return d;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void None_IsByteIdentical_AcrossRuns(bool translucent)
    {
        Assert.Equal(0, Diff(Render(ViewTransform.None, 0f, translucent),
                             Render(ViewTransform.None, 0f, translucent)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void None_IgnoresExposure(bool translucent)
    {
        // Exposure only bites through a transform; with None selected the display
        // buffer must be untouched (the ViewTransform gate).
        Assert.Equal(0, Diff(Render(ViewTransform.None, 0f, translucent),
                             Render(ViewTransform.None, 4f, translucent)));
    }

    [Theory]
    [InlineData(ViewTransform.Reinhard, false)]
    [InlineData(ViewTransform.AcesFilmic, false)]
    [InlineData(ViewTransform.AgX, false)]
    [InlineData(ViewTransform.Filmic, false)]
    [InlineData(ViewTransform.AcesFilmic, true)]
    [InlineData(ViewTransform.AgX, true)]
    public void Transform_ReachesOutput(ViewTransform vt, bool translucent)
    {
        var none = Render(ViewTransform.None, 0f, translucent);
        var toned = Render(vt, 0f, translucent);
        Assert.True(Diff(none, toned) > 0, $"{vt} must change the rendered output");
    }
}
