// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #830: extend the global interior-alpha knob to the Newton family
// (NewtonCalculator [also Nova], HalleyCalculator, SecantCalculator). These
// calcs have no IterationBuffer, and their "interior" is basin non-convergence
// (basin < 0) known only at the write site, so the knob is applied inline via
// InteriorAlphaStamp.ScaleArgbAlpha rather than the buffer post-pass. With a
// plain (non-INewtonColorMap) palette the non-converged pixel is written as
// ColorMap.InSetColor, so a translucent knob halves that colour's alpha while
// every converged (exterior) basin pixel stays byte-identical. A low iteration
// cap guarantees a band of non-converged boundary pixels. Assert:
//   • InteriorAlpha 255 leaves every pixel opaque + bit-identical,
//   • InteriorAlpha 128 halves the in-set alpha only, RGB + exterior untouched.

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class NewtonInteriorAlphaTests
{
    private const int W = 64, H = 48;
    private const double Cx = 0.0, Cy = 0.0, Zoom = 0.5;
    private const int MaxIter = 8;   // low cap -> a real band of non-converged pixels

    public enum Kind { Newton, Halley, Secant }

    private static uint[] Render(Kind kind, int interiorAlpha, out uint inSet)
    {
        IColorMap map = new HsvPalette();   // NOT an INewtonColorMap -> InSetColor branch
        inSet = map.InSetColor;
        var fp = new FractalParameters { NewtonExponent = 3, NewtonRelaxation = 1.0 };

        switch (kind)
        {
            case Kind.Newton:
            {
                var c = new NewtonCalculator(W, H)
                {
                    CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
                    Quality = QualityPreset.Standard, ColorMap = map,
                    FractalParameters = fp, InteriorAlpha = interiorAlpha,
                };
                c.Calculate(default);
                return (uint[])c.ColorBuffer.Clone();
            }
            case Kind.Halley:
            {
                var c = new HalleyCalculator(W, H)
                {
                    CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
                    Quality = QualityPreset.Standard, ColorMap = map,
                    FractalParameters = fp, InteriorAlpha = interiorAlpha,
                };
                c.Calculate(default);
                return (uint[])c.ColorBuffer.Clone();
            }
            default:
            {
                var c = new SecantCalculator(W, H)
                {
                    CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
                    Quality = QualityPreset.Standard, ColorMap = map,
                    FractalParameters = fp, InteriorAlpha = interiorAlpha,
                };
                c.Calculate(default);
                return (uint[])c.ColorBuffer.Clone();
            }
        }
    }

    [Theory]
    [InlineData(Kind.Newton)]
    [InlineData(Kind.Halley)]
    [InlineData(Kind.Secant)]
    public void GlobalKnob_Scales_NonConverged_Alpha_Only(Kind kind)
    {
        var a255 = Render(kind, 255, out uint inSet);
        var a128 = Render(kind, 128, out _);
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
                Assert.Equal(expected128, a128[i]);   // non-converged: alpha halved, RGB kept
            }
            else
            {
                exteriorCount++;
                Assert.Equal(a255[i], a128[i]);        // converged basin: untouched
            }
        }

        Assert.True(inSetCount > 0, "frame should contain non-converged (in-set) pixels");
        Assert.True(exteriorCount > 0, "frame should contain converged (exterior) pixels");
    }

    [Theory]
    [InlineData(Kind.Newton)]
    [InlineData(Kind.Halley)]
    [InlineData(Kind.Secant)]
    public void Alpha255_Is_Opaque(Kind kind)
    {
        var a255 = Render(kind, 255, out uint inSet);
        Assert.Equal(0xFFu, (inSet >> 24) & 0xFFu);    // sanity: theme interior opaque
        foreach (var p in a255)
            Assert.Equal(0xFFu, (p >> 24) & 0xFFu);    // no translucency at knob = 255
    }
}
