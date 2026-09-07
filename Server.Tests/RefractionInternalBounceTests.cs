// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S5 (#406) — internal-reflection bounce budget for the full internal glass march.
// When the internal march reaches the back surface at a grazing angle beyond the
// critical angle, the ray cannot leave there (total internal reflection): physical
// glass reflects it back inside to seek another exit. RefractInternalBounces is how
// many internal segments the march may take (1 = the legacy single front->back
// attempt, which keeps the internal direction on a back-face TIR). Locks: opaque is
// byte-identical for any bounce count; bounces == 1 reproduces the legacy internal
// march exactly; the budget clamps to [1,6]; a higher budget on grazing (diamond-IOR)
// glass changes the render and stays deterministic.

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RefractionInternalBounceTests
{
    private static uint[] Render(double transmission, bool internalMarch, int bounces, double ior = 1.5)
    {
        var fx = LightingFxData.CreateDefault();
        fx.Transmission = transmission;
        fx.Ior = ior;
        fx.AbsorptionColor = 0xFF66CCFFu;   // coloured glass so thickness tint is visible
        fx.AbsorptionDistance = 0.6;
        fx.RefractInternalMarch = internalMarch;
        fx.RefractInternalBounces = bounces;
        fx.ShowSkyBackdrop = true;          // give the refracted ray an environment to see

        var fp = new FractalParameters
        {
            BulbPower = 8,
            BulbIterations = 12,
            BulbCameraDistance = 2.6,
            Lighting = fx,
        };
        var calc = new MandelbulbCalculator(96, 72)
        {
            ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = fp,
            Zoom = 1.0,
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Fact]
    public void Opaque_Is_ByteIdentical_For_Any_BounceCount()
    {
        // Transmission 0 → the glass block is skipped, so the budget can't matter.
        var b1 = Render(transmission: 0.0, internalMarch: true, bounces: 1);
        var b3 = Render(transmission: 0.0, internalMarch: true, bounces: 3);
        var b6 = Render(transmission: 0.0, internalMarch: true, bounces: 6);
        Assert.Equal(b1, b3);
        Assert.Equal(b1, b6);
    }

    [Fact]
    public void BounceOne_Reproduces_Legacy_InternalMarch()
    {
        // The default budget IS 1, so an explicit 1 must render byte-for-byte the
        // same as the pre-bounce internal march (the single front->back attempt,
        // keeping the internal direction on a back-face TIR).
        var legacyDefault = Render(transmission: 0.85, internalMarch: true, bounces: LightingFxData.CreateDefault().RefractInternalBounces);
        var explicitOne = Render(transmission: 0.85, internalMarch: true, bounces: 1);
        Assert.Equal(1, LightingFxData.CreateDefault().RefractInternalBounces);
        Assert.Equal(legacyDefault, explicitOne);
    }

    [Fact]
    public void Budget_Clamps_Above_Six()
    {
        // 6 is the ceiling; anything larger clamps to it → identical render.
        var b6 = Render(transmission: 0.85, internalMarch: true, bounces: 6, ior: 2.4);
        var b99 = Render(transmission: 0.85, internalMarch: true, bounces: 99, ior: 2.4);
        Assert.Equal(b6, b99);
    }

    [Fact]
    public void Budget_Clamps_Below_One()
    {
        // 0 (or negative) clamps up to 1 → identical to the single-attempt march.
        var b1 = Render(transmission: 0.85, internalMarch: true, bounces: 1);
        var b0 = Render(transmission: 0.85, internalMarch: true, bounces: 0);
        Assert.Equal(b1, b0);
    }

    [Fact]
    public void HigherBudget_Changes_Grazing_Glass_And_Is_Deterministic()
    {
        // Diamond IOR (2.4) → critical angle ~24.6°, so a curved surface produces
        // plentiful back-face total-internal-reflection. A larger bounce budget then
        // lets those rays reflect on and reach a different exit → the render moves.
        var oneBounce = Render(transmission: 0.9, internalMarch: true, bounces: 1, ior: 2.4);
        var manyA = Render(transmission: 0.9, internalMarch: true, bounces: 4, ior: 2.4);
        var manyB = Render(transmission: 0.9, internalMarch: true, bounces: 4, ior: 2.4);

        Assert.Equal(manyA, manyB);         // deterministic DE bounce march
        Assert.Equal(oneBounce.Length, manyA.Length);

        int diff = 0;
        for (int i = 0; i < oneBounce.Length; i++)
            if (oneBounce[i] != manyA[i]) diff++;
        Assert.True(diff > 0, "internal-reflection bounces should change grazing (diamond) glass");
    }
}
