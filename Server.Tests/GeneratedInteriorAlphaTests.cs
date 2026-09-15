// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #831: extend the global interior-alpha knob to the source-generated
// polynomial calcs (MandelbrotZ2..Z5, generated Tricorn / BurningShip), which
// share the Mandelbrot in-set invariant (IterationBuffer[idx] >= MaxIterations
// -> write ColorMap.InSetColor). The shared InteriorAlphaStamp post-pass runs
// at the end of Calculate (and after the histogram-EQ recolor). MandelbrotZ2 is
// classic z^2 + c, so the cardioid + bulbs give a large interior. Assert:
//   • InteriorAlpha 255 leaves every pixel opaque + bit-identical,
//   • InteriorAlpha 128 halves the in-set alpha only, RGB + exterior untouched.

using FracturingFog.Calculators.Generated;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class GeneratedInteriorAlphaTests
{
    private const int W = 64, H = 48;
    private const double Cx = -0.5, Cy = 0.0, Zoom = 0.8;
    private const int MaxIter = 200;

    private static uint[] Render(int interiorAlpha, out uint inSet)
    {
        IColorMap map = new HsvPalette();
        inSet = map.InSetColor;
        var c = new MandelbrotZ2Calculator(W, H)
        {
            CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
            Quality = QualityPreset.Standard, ColorMap = map,
            UseGpu = false, UsePerturbation = false,
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
