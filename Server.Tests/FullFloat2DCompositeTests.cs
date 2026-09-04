// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 (#396) — the FULL-FLOAT 2D composite producer. The 2D output
// path now composites the interior backdrop inside a LinearFloatImage (linear
// light) then tonemaps the whole image, instead of tonemapping the fractal in
// 8-bit and injecting the backdrop untonemapped afterwards. These lock:
//   * the parity anchor — opaque / no-backdrop frames reduce to
//     FromBgra → transform → ToBgra, byte-identical to the old 8-bit
//     ViewTransformOps.Apply (so the default look is preserved);
//   * CompositeLinear's gate matches the 8-bit Composite gate; and
//   * a translucent frame really blends in LINEAR (a different, higher-energy
//     result than the 8-bit sRGB blend).

using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FullFloat2DCompositeTests
{
    // A deterministic opaque BGRA buffer spanning the byte range.
    private static uint[] MakeOpaque(int w, int h)
    {
        var buf = new uint[w * h];
        for (int i = 0; i < buf.Length; i++)
        {
            byte r = (byte)((i * 37) & 0xFF);
            byte g = (byte)((i * 91 + 5) & 0xFF);
            byte b = (byte)((i * 53 + 17) & 0xFF);
            buf[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return buf;
    }

    [Theory]
    [InlineData(ViewTransform.Reinhard, 0f)]
    [InlineData(ViewTransform.AcesFilmic, 0f)]
    [InlineData(ViewTransform.AgX, 2f)]
    [InlineData(ViewTransform.Filmic, -1.5f)]
    public void OpaqueFrame_FullFloatPath_ByteIdentical_To_8bit(ViewTransform vt, float ev)
    {
        const int w = 24, h = 18;
        var src = MakeOpaque(w, h);

        // Old 8-bit path.
        var expected = (uint[])src.Clone();
        ViewTransformOps.Apply(expected, w * h, vt, ev);

        // New full-float 2D path (default params => nothing to composite).
        var fp = new FractalParameters();
        var img = LinearFloatImage.FromBgra(src, w, h);
        bool composited = Interior2DBackgroundCompositor.CompositeLinear(
            img, src, fp, inSetArgb: 0xFF000000u, alphaPreview: false);
        var actual = img.ApplyViewTransform(vt, ev).ToBgra();

        Assert.False(composited);          // opaque + checkerboard => gate off
        Assert.Equal(expected, actual);    // byte-for-byte
    }

    [Fact]
    public void CompositeLinear_OpaqueTheme_NoOp_ReturnsFalse()
    {
        const int w = 16, h = 16;
        var buf = MakeOpaque(w, h);
        var img = LinearFloatImage.FromBgra(buf, w, h);
        var before = (float[])img.Rgb.Clone();

        bool composited = Interior2DBackgroundCompositor.CompositeLinear(
            img, buf, new FractalParameters(), inSetArgb: 0xFF000000u, alphaPreview: false);

        Assert.False(composited);
        Assert.Equal(before, img.Rgb);
    }

    [Fact]
    public void CompositeLinear_TransparentMode_ReturnsFalse_EvenWhenTranslucent()
    {
        const int w = 8, h = 8;
        // Fully-transparent interior coverage everywhere.
        var buf = new uint[w * h];
        for (int i = 0; i < buf.Length; i++) buf[i] = 0x00808080u;
        var fp = new FractalParameters { Interior2DBackground = Interior2DBackgroundMode.Transparent };
        var img = LinearFloatImage.FromBgra(buf, w, h);

        bool composited = Interior2DBackgroundCompositor.CompositeLinear(
            img, buf, fp, inSetArgb: 0x00808080u, alphaPreview: false);

        Assert.False(composited); // Transparent is a deliberate no-op, both paths
    }

    [Fact]
    public void CompositeLinear_TranslucentOverSolid_BlendsInLinear_AndGoesOpaque()
    {
        const int w = 4, h = 4;
        // Fractal = pure white, coverage = 50% (a=128). Backdrop = solid black.
        // 8-bit sRGB blend: (255*128 + 0*127)/255 = 128.
        // Linear blend: 1.0 * (128/255) ≈ 0.502 linear → encoded sRGB ≈ 188.
        // So the linear result MUST be markedly brighter than the 8-bit 128.
        var buf = new uint[w * h];
        for (int i = 0; i < buf.Length; i++) buf[i] = 0x80FFFFFFu; // a=128, white
        var fp = new FractalParameters
        {
            Interior2DBackground = Interior2DBackgroundMode.SolidColor,
            Interior2DBgTop = 0xFF000000u,     // black backdrop
            Interior2DBgBottom = 0xFF000000u,
        };

        var img = LinearFloatImage.FromBgra(buf, w, h);
        bool composited = Interior2DBackgroundCompositor.CompositeLinear(
            img, buf, fp, inSetArgb: 0xFF000000u, alphaPreview: false);
        var outp = img.ToBgra(); // no view transform — isolate the blend

        Assert.True(composited);
        uint px = outp[0];
        Assert.Equal(0xFFu, (px >> 24) & 0xFF);      // composited pixel is opaque
        int r = (int)((px >> 16) & 0xFF);
        Assert.InRange(r, 183, 193);                  // ≈188, linear (not 128)

        // The 8-bit path on the same inputs yields 128 — prove they differ.
        var eight = (uint[])buf.Clone();
        Interior2DBackgroundCompositor.Composite(
            eight, eight, w, h, fp, inSetArgb: 0xFF000000u,
            alphaPreview: false, srcAlreadyProcessed: false);
        int r8 = (int)((eight[0] >> 16) & 0xFF);
        Assert.InRange(r8, 126, 130);                 // ≈128
        Assert.True(r > r8 + 40, "linear blend must be brighter than the 8-bit blend");
    }

    [Fact]
    public void FromBgra_CoverageOverload_TakesAlpha_FromCoverageBuffer()
    {
        const int w = 2, h = 2;
        var color = new uint[w * h];
        var coverage = new uint[w * h];
        for (int i = 0; i < color.Length; i++)
        {
            color[i] = 0xFF204060u;   // opaque colour (post-FX forced alpha)
            coverage[i] = 0x40204060u; // authored coverage a=0x40
        }
        var img = LinearFloatImage.FromBgra(color, coverage, w, h);
        Assert.Equal(0x40 / 255f, img.Alpha[0], 5);
    }
}
