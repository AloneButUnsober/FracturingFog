// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #583 / #584 — interior (non-escaping) orbit-accumulator colouring on the
// User-Equation/DSL path.
//
// The DSL renderer already samples the orbit every iteration for orbit-aware
// themes and colours escaped/converged pixels through MapWithOrbit. Before this
// feature, in-set (bounded) pixels discarded the accumulator and painted a flat
// InSetColor. With FractalParameters.UserEquationColorInterior on, bounded
// pixels of an orbit-aware theme are coloured from the accumulated orbit
// (Fragmentarium-style all-pixel colouring). These tests pin the contract:
//   * flag off  ⇒ interior byte-identical to the flat InSetColor (regression).
//   * flag on + orbit theme ⇒ interior coloured, opaque, non-uniform (lace).
//   * flag on + non-orbit theme ⇒ unchanged (no orbit data to colour with).
//   * InteriorAlpha scales the interior-orbit colour's alpha.

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslInteriorOrbitColoringTests
{
    private const int W = 64, H = 48;
    // Centre pixel maps to c = (CenterX, CenterY) = (-0.5, 0): deep inside the
    // main cardioid of z*z + c ⇒ a guaranteed in-set pixel at maxIter=120.
    private const int CenterIdx = (H / 2) * W + (W / 2);

    private static uint[] Render(IColorMap theme, bool colorInterior, int interiorAlpha = 255)
    {
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
            ColorMap = theme,
            InteriorAlpha = interiorAlpha,
            FractalParameters = new FractalParameters
            {
                UserEquationSource = "z*z + c",
                UserCodeOrigin = UserCodeOrigin.Interactive,
                UserEquationColorInterior = colorInterior,
            },
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    // Flag OFF ⇒ the known interior pixel is the flat theme InSetColor, and the
    // whole buffer is byte-identical whether the theme is orbit-aware or not for
    // that pixel. Guards byte-identical default behaviour.
    [Fact]
    public void FlagOff_InteriorIsFlatInSetColor()
    {
        var theme = new OrbitTrapPointMap();
        uint inSet = ((IColorMap)theme).InSetColor;
        var off = Render(theme, colorInterior: false);
        Assert.Equal(inSet, off[CenterIdx]);
    }

    // Flag ON + orbit-aware theme ⇒ the interior pixel is no longer the flat
    // InSetColor, the buffer differs from the flag-off render, and the interior
    // region is non-uniform (real orbit lace, not a single recolour).
    [Fact]
    public void FlagOn_OrbitTheme_ColoursInteriorNonUniformly()
    {
        var theme = new OrbitTrapPointMap();
        uint inSet = ((IColorMap)theme).InSetColor;
        var off = Render(theme, colorInterior: false);
        var on = Render(theme, colorInterior: true);

        Assert.NotEqual(off, on);                       // interior changed
        Assert.NotEqual(inSet, on[CenterIdx]);          // no longer flat fill
        Assert.Equal(0xFFu, (on[CenterIdx] >> 24) & 0xFFu); // opaque

        // Among the pixels that were flat InSetColor before, the ON render must
        // show more than one colour (lace), not a uniform recolour.
        var interiorColours = new System.Collections.Generic.HashSet<uint>();
        for (int i = 0; i < off.Length; i++)
            if (off[i] == inSet) interiorColours.Add(on[i]);
        Assert.True(interiorColours.Count >= 2,
            $"interior should be non-uniform, saw {interiorColours.Count} colour(s)");
    }

    // Flag ON but a non-orbit-aware theme ⇒ no accumulator exists, so the
    // interior stays the flat InSetColor (feature is a no-op).
    [Fact]
    public void FlagOn_NonOrbitTheme_LeavesInteriorFlat()
    {
        var theme = new HsvPalette();
        uint inSet = ((IColorMap)theme).InSetColor;
        var off = Render(theme, colorInterior: false);
        var on = Render(theme, colorInterior: true);

        Assert.Equal(off, on);                  // no effect without orbit data
        Assert.Equal(inSet, on[CenterIdx]);
    }

    // InteriorAlpha scales the alpha of the interior-orbit colour, matching the
    // flat-path #382 behaviour.
    [Fact]
    public void FlagOn_OrbitTheme_HonoursInteriorAlpha()
    {
        var opaque = Render(new OrbitTrapPointMap(), colorInterior: true, interiorAlpha: 255);
        var faded  = Render(new OrbitTrapPointMap(), colorInterior: true, interiorAlpha: 128);

        uint baseAlpha = (opaque[CenterIdx] >> 24) & 0xFFu;
        uint expected = baseAlpha * 128u / 255u;
        uint actual = (faded[CenterIdx] >> 24) & 0xFFu;
        Assert.Equal(expected, actual);
        // RGB unchanged by the alpha scale.
        Assert.Equal(opaque[CenterIdx] & 0x00FFFFFFu, faded[CenterIdx] & 0x00FFFFFFu);
    }

    // The flag round-trips through Clone.
    [Fact]
    public void ColorInterior_SurvivesClone()
    {
        var p = new FractalParameters { UserEquationColorInterior = true };
        Assert.True(p.Clone().UserEquationColorInterior);
    }
}
