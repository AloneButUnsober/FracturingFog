// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Direct coverage for the shared F10.5/#96 compositor as the headless batch
// video / slideshow paths call it: in-place (rgb == coverage), a DEFAULT
// FractalParameters (Checkerboard backdrop — those legs don't thread the
// interactive Interior(2D) knobs), and the theme's InSetColor supplying the
// interior coverage.
//
// BatchRenderer itself lives in the Windows-only WinExe assembly this net10.0
// test project can't reference, so we assert the exact contract it relies on
// against the public Engine compositor instead. PosterInteriorAlphaCompositeTests
// covers the same helper through the poster/image export path.

using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Interior2DBackgroundCompositorTests
{
    // Fill a buffer with a translucent-interior pattern: the top half is an
    // in-set pixel carrying the theme's straight alpha (< 255), the bottom half
    // an opaque exterior. Mirrors what a translucent theme (e.g. Cuba Vacation)
    // emits before the software composite.
    private static uint[] MakeBuffer(int w, int h, uint interiorArgb)
    {
        var buf = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                buf[y * w + x] = y < h / 2 ? interiorArgb : 0xFF204060u;
        return buf;
    }

    private static int CountTranslucent(uint[] buf)
    {
        int n = 0;
        foreach (var p in buf)
            if (((p >> 24) & 0xFF) < 255) n++;
        return n;
    }

    [Fact]
    public void DefaultCheckerboard_TranslucentInterior_Composites_To_Opaque()
    {
        const int w = 32, h = 32;
        // A=0 interior — the strongest case; nothing of the interior colour
        // should survive, only the checkerboard backdrop composited opaque.
        var buf = MakeBuffer(w, h, 0x00112233u);
        var fp = new FractalParameters(); // default => Checkerboard

        Interior2DBackgroundCompositor.Composite(
            buf, buf, w, h, fp, inSetArgb: 0x00112233u,
            alphaPreview: false, srcAlreadyProcessed: false);

        Assert.Equal(0, CountTranslucent(buf)); // every pixel opaque now
        // Interior is no longer the source colour — it is the checkerboard grey.
        uint topLeft = buf[0];
        Assert.Equal(0xFFu, (topLeft >> 24) & 0xFF);
        Assert.NotEqual(0x00112233u, topLeft);
    }

    [Fact]
    public void OpaqueTheme_Is_ByteIdentical_NoOp()
    {
        const int w = 16, h = 16;
        var buf = MakeBuffer(w, h, 0xFF000000u); // opaque interior
        var original = (uint[])buf.Clone();
        var fp = new FractalParameters();

        Interior2DBackgroundCompositor.Composite(
            buf, buf, w, h, fp, inSetArgb: 0xFF000000u,
            alphaPreview: false, srcAlreadyProcessed: false);

        Assert.Equal(original, buf); // gate early-returns; nothing touched
    }

    [Fact]
    public void TransparentMode_Keeps_Straight_Alpha()
    {
        const int w = 16, h = 16;
        var buf = MakeBuffer(w, h, 0x00112233u);
        var fp = new FractalParameters
        {
            Interior2DBackground = Interior2DBackgroundMode.Transparent,
        };

        Interior2DBackgroundCompositor.Composite(
            buf, buf, w, h, fp, inSetArgb: 0x00112233u,
            alphaPreview: false, srcAlreadyProcessed: false);

        Assert.True(CountTranslucent(buf) > 0,
            "Transparent mode must preserve straight alpha for a transparent export.");
    }
}
