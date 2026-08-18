// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #384: the global InteriorAlpha slider was applied live only — PosterRenderer
// never set MandelbrotCalculator.InteriorAlpha, so image/poster export ignored
// the knob (interiors exported opaque even when the window showed them
// translucent). Export honored only a translucent theme InSetColor.A (baked into
// the buffer at the in-set write), not the global slider.
//
// These render a Mandelbrot poster with an OPAQUE theme and an explicit
// InteriorAlpha, using the Transparent background so the compositor is a no-op
// and the straight alpha survives to the PNG — isolating that the knob itself
// reached the export calculator.

using System.IO;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MandelbrotExportInteriorAlphaKnobTests
{
    private static SKBitmap RenderMandelbrot(int interiorAlpha)
    {
        var fp = new FractalParameters
        {
            InteriorAlpha = interiorAlpha,
            Interior2DBackground = Interior2DBackgroundMode.Transparent, // no composite
        };
        string path = Path.Combine(
            Path.GetTempPath(), "ff-mandel-knob-" + System.Guid.NewGuid().ToString("N") + ".png");
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0.0,
            Zoom = 0.8,                       // whole set in frame -> large interior
            MaxIterations = 200,
            Width = 120, Height = 90,
            ColorMap = new HsvPalette(),       // opaque interior (InSetColor.A == 255)
            Quality = QualityPreset.Standard,
            FractalParameters = fp,
            Path = path,
            Format = ImageFileFormat.Png,
        };
        try
        {
            PosterRenderer.RenderToFile(req, default);
            var bmp = SKBitmap.Decode(path);
            Assert.NotNull(bmp);
            return bmp!;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static int CountAlpha(SKBitmap bmp, System.Func<byte, bool> pred)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (pred(bmp.GetPixel(x, y).Alpha)) n++;
        return n;
    }

    [Fact]
    public void Knob_Below_255_Reaches_Export()
    {
        using var bmp = RenderMandelbrot(128);
        // Interior pixels carry the halved alpha; a translucent-only theme is not
        // involved, so any A ~128 pixel proves the global slider reached export.
        int halved = CountAlpha(bmp, a => a >= 124 && a <= 132);
        Assert.True(halved > 0,
            "InteriorAlpha=128 must make the exported interior ~half-transparent (knob reached export).");
    }

    [Fact]
    public void Knob_255_Exports_Fully_Opaque()
    {
        using var bmp = RenderMandelbrot(255);
        int translucent = CountAlpha(bmp, a => a < 255);
        Assert.Equal(0, translucent);   // opaque theme + knob 255 -> no translucency
    }
}
