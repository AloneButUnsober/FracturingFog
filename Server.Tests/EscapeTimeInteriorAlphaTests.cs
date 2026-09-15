// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #97: extend the global interior-alpha knob to the native escape-time families
// (EscapeTimeCalculator: Julia / BurningShip / Tricorn / Multibrot / Phoenix /
// Magnet1 / Magnet2 / Glynn / Spider). They share the Mandelbrot in-set
// invariant (iter >= maxIt -> write ColorMap.InSetColor), and the shared
// InteriorAlphaStamp post-pass scales that colour's alpha so the interior can
// composite over Interior2DBackground. Julia with c = 0 gives z -> z^2 whose
// bounded set is the closed unit disk — a large, reliable interior. Assert:
//   • InteriorAlpha 255 leaves every pixel opaque + bit-identical (no stamp),
//   • InteriorAlpha 128 halves the in-set alpha while leaving RGB + every
//     exterior pixel untouched.

using System.Numerics;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class EscapeTimeInteriorAlphaTests
{
    private const int W = 64, H = 48;
    private const double Cx = 0.0, Cy = 0.0, Zoom = 0.5;
    private const int MaxIter = 200;

    private static uint[] Render(int interiorAlpha, out uint inSet)
    {
        IColorMap map = new HsvPalette();
        inSet = map.InSetColor;
        var c = new EscapeTimeCalculator(W, H)
        {
            CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
            Quality = QualityPreset.Standard,
            ColorMap = map,
            FractalType = FractalType.Julia,
            FractalParameters = new FractalParameters { JuliaC = new Complex(0.0, 0.0) },
            InteriorAlpha = interiorAlpha,
        };
        c.Calculate(default);
        return (uint[])c.ColorBuffer.Clone();
    }

    [Fact]
    public void GlobalKnob_Scales_Interior_Alpha_Only()
    {
        var a255 = Render(255, out uint inSet);
        var a128 = Render(128, out _);
        Assert.Equal(a255.Length, a128.Length);

        uint inSetRgb = inSet & 0x00FFFFFFu;
        uint inSetA = (inSet >> 24) & 0xFFu;
        uint expected128 = inSetRgb | (((inSetA * 128u) / 255u) << 24);

        int inSetCount = 0, exteriorCount = 0;
        for (int i = 0; i < a255.Length; i++)
        {
            if (a255[i] == inSet)
            {
                inSetCount++;
                Assert.Equal(expected128, a128[i]);   // in-set: alpha halved, RGB kept
            }
            else
            {
                exteriorCount++;
                Assert.Equal(a255[i], a128[i]);        // exterior: untouched
            }
        }

        Assert.True(inSetCount > 0, "frame should contain interior pixels");
        Assert.True(exteriorCount > 0, "frame should contain exterior pixels");
    }

    [Fact]
    public void Alpha255_Is_Opaque_And_Unstamped()
    {
        var a255 = Render(255, out uint inSet);
        Assert.Equal(0xFFu, (inSet >> 24) & 0xFFu);    // sanity: theme interior opaque
        foreach (var p in a255)
            Assert.Equal(0xFFu, (p >> 24) & 0xFFu);    // no translucency at knob = 255
    }
}
