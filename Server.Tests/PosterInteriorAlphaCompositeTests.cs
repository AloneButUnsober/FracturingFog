// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Regression for the "poster/wallpaper washed out" bug: PosterRenderer wrote the
// calculator buffer with straight (Unpremul) alpha, so a theme with a translucent
// interior (InSetColor alpha < 255 — e.g. Cuba Vacation on the Sandbox family)
// exported a PNG whose interior was transparent. The live D3D window ignores alpha
// (always opaque) and composites the translucent interior over the chosen
// Interior2DBackground, so the on-screen frame looked vibrant while the export
// washed out over a viewer's white background.
//
// These render a Mandelbrot poster (large guaranteed interior) with a translucent-
// inset colour map and assert:
//   • non-Transparent modes composite the interior to opaque (no A<255 pixels),
//     matching UploadProcessedBuffer's F10.5/#96 block, and
//   • Transparent mode deliberately keeps the straight alpha for a transparent PNG.

using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PosterInteriorAlphaCompositeTests
{
    // Colour map with a fully-transparent interior (alpha 0) and an opaque
    // exterior — the minimal reproduction of a translucent-inset theme.
    private sealed class TranslucentInteriorMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0x00000000u;               // transparent interior
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)0xFF808040u);                  // opaque exterior
    }

    // Opaque interior but TRANSLUCENT exterior — the per-colour-stop alpha
    // feature. Exercises that the compositor honours exterior coverage too, not
    // just the interior.
    private sealed class TranslucentExteriorMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;               // opaque interior
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)0x64808040u);                  // A=100 translucent exterior
    }

    private static int CountTransparentPixels(
        Interior2DBackgroundMode mode, IColorMap? map = null)
    {
        var fp = new FractalParameters { Interior2DBackground = mode };
        string path = Path.Combine(
            Path.GetTempPath(), "ff-poster-ialpha-" + System.Guid.NewGuid().ToString("N") + ".png");
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0.0,
            Zoom = 0.8,                       // whole set in frame → large interior
            MaxIterations = 200,
            Width = 120, Height = 90,
            ColorMap = map ?? new TranslucentInteriorMap(),
            Quality = QualityPreset.Standard,
            FractalParameters = fp,
            Path = path,
            Format = ImageFileFormat.Png,
        };
        try
        {
            PosterRenderer.RenderToFile(req, default);
            using var bmp = SKBitmap.Decode(path);
            Assert.NotNull(bmp);
            int transparent = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).Alpha < 255) transparent++;
            return transparent;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Theory]
    [InlineData(Interior2DBackgroundMode.Checkerboard)]
    [InlineData(Interior2DBackgroundMode.SolidColor)]
    [InlineData(Interior2DBackgroundMode.Gradient)]
    public void TranslucentInterior_Composites_To_Opaque(Interior2DBackgroundMode mode)
    {
        int transparent = CountTransparentPixels(mode);
        Assert.Equal(0, transparent);   // interior composited over the backdrop → opaque
    }

    [Fact]
    public void TranslucentExteriorStops_Composite_Over_ExplicitBackdrop()
    {
        // Per-stop exterior alpha with an explicit backdrop composites opaque —
        // same gate as the on-screen present (explicitBackdrop || interiorTranslucent).
        int transparent = CountTransparentPixels(
            Interior2DBackgroundMode.SolidColor, new TranslucentExteriorMap());
        Assert.Equal(0, transparent);
    }

    [Fact]
    public void TransparentMode_Keeps_Straight_Alpha()
    {
        int transparent = CountTransparentPixels(Interior2DBackgroundMode.Transparent);
        Assert.True(transparent > 0,
            "Transparent mode must preserve the translucent interior for a transparent-PNG export.");
    }
}
