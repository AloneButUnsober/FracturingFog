// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #382: extend the global interior-alpha knob to the DSL escape-time families
// (Sandbox + UserEquation). Both share the Mandelbrot in-set invariant
// (iter >= maxIt -> write ColorMap.InSetColor); the knob pre-scales that colour's
// alpha so the interior can composite over Interior2DBackground, mirroring
// MandelbrotCalculator.StampInteriorAlpha. Assert:
//   • InteriorAlpha 255 leaves in-set pixels bit-identical (opaque InSetColor),
//   • InteriorAlpha 128 halves the in-set alpha while leaving RGB + every
//     exterior pixel untouched.

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslInteriorAlphaTests
{
    private const int W = 64, H = 48;
    private const double Cx = -0.5, Cy = 0.0, Zoom = 0.8;
    private const int MaxIter = 120;
    private const string Z2 = "z*z + c";   // Mandelbrot dynamics -> large interior

    private static (uint[] a255, uint[] a128, uint inSet) RenderPair(bool userEquation)
    {
        IColorMap map255 = new HsvPalette();
        IColorMap map128 = new HsvPalette();
        uint inSet = map255.InSetColor;

        uint[] Render(IColorMap map, int interiorAlpha)
        {
            var fp = new FractalParameters { SandboxSource = Z2, UserEquationSource = Z2 };
            if (userEquation)
            {
                var c = new UserEquationCalculator(W, H)
                {
                    CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
                    ColorMap = map, FractalParameters = fp, InteriorAlpha = interiorAlpha,
                };
                c.Compile(Z2);
                c.Calculate(default);
                return (uint[])c.ColorBuffer.Clone();
            }
            else
            {
                var c = new SandboxCalculator(W, H)
                {
                    CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
                    ColorMap = map, FractalParameters = fp, InteriorAlpha = interiorAlpha,
                };
                c.Compile(Z2);
                c.Calculate(default);
                return (uint[])c.ColorBuffer.Clone();
            }
        }

        return (Render(map255, 255), Render(map128, 128), inSet);
    }

    [Theory]
    [InlineData(false)] // Sandbox
    [InlineData(true)]  // UserEquation
    public void GlobalKnob_Scales_Interior_Alpha_Only(bool userEquation)
    {
        var (a255, a128, inSet) = RenderPair(userEquation);
        Assert.Equal(a255.Length, a128.Length);

        uint inSetRgb = inSet & 0x00FFFFFFu;
        uint inSetA = (inSet >> 24) & 0xFFu;
        uint expected128 = inSetRgb | (((inSetA * 128u) / 255u) << 24);

        int inSetCount = 0, exteriorCount = 0;
        for (int i = 0; i < a255.Length; i++)
        {
            if (a255[i] == inSet)
            {
                // In-set: 255 render is the opaque InSetColor; 128 render is the
                // same RGB with alpha halved.
                inSetCount++;
                Assert.Equal(expected128, a128[i]);
            }
            else
            {
                // Exterior: knob must not touch it — byte-identical.
                exteriorCount++;
                Assert.Equal(a255[i], a128[i]);
            }
        }

        Assert.True(inSetCount > 0, "frame should contain interior pixels");
        Assert.True(exteriorCount > 0, "frame should contain exterior pixels");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Alpha255_Is_Opaque_Interior(bool userEquation)
    {
        var (a255, _, inSet) = RenderPair(userEquation);
        Assert.Equal(0xFFu, (inSet >> 24) & 0xFFu); // sanity: theme interior opaque
        foreach (var p in a255)
            Assert.Equal(0xFFu, (p >> 24) & 0xFFu); // no translucency at knob = 255
    }
}
