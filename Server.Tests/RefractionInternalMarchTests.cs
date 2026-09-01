// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S5 (#406) — full internal glass march. On a transmissive hit the shader can now
// march the DE through the solid to the back surface, so Beer-Lambert absorption
// runs over the REAL thickness and the ray refracts a second time on exit — vs the
// single-interface env approximation (a nominal one-unit slab). Locks: opaque is
// byte-identical regardless; the toggle is a no-op while opaque; and on transmissive
// glass the internal march changes the render (real thickness + exit refraction) and
// stays deterministic.

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RefractionInternalMarchTests
{
    private static uint[] Render(double transmission, bool internalMarch)
    {
        var fx = LightingFxData.CreateDefault();
        fx.Transmission = transmission;
        fx.Ior = 1.5;
        fx.AbsorptionColor = 0xFF66CCFFu;   // coloured glass so thickness tint is visible
        fx.AbsorptionDistance = 0.6;
        fx.RefractInternalMarch = internalMarch;
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
    public void Opaque_InternalMarchToggle_Is_ByteIdentical()
    {
        // Transmission 0 → the whole glass block is skipped, so the toggle can't
        // change anything.
        var off = Render(transmission: 0.0, internalMarch: false);
        var on = Render(transmission: 0.0, internalMarch: true);
        Assert.Equal(off, on);
    }

    [Fact]
    public void EnvApprox_Is_Deterministic()
    {
        var a = Render(transmission: 0.8, internalMarch: false);
        var b = Render(transmission: 0.8, internalMarch: false);
        Assert.Equal(a, b);
    }

    [Fact]
    public void InternalMarch_Changes_Glass_And_Is_Deterministic()
    {
        var envApprox = Render(transmission: 0.8, internalMarch: false);
        var march1 = Render(transmission: 0.8, internalMarch: true);
        var march2 = Render(transmission: 0.8, internalMarch: true);

        Assert.Equal(march1.Length, envApprox.Length);
        Assert.Equal(march1, march2);   // deterministic DE march

        // Real-thickness Beer-Lambert + exit refraction differ from the nominal
        // single-interface slab on the glass pixels.
        int diff = 0;
        for (int i = 0; i < envApprox.Length; i++)
            if (envApprox[i] != march1[i]) diff++;
        Assert.True(diff > 0, "internal glass march should change the transmissive render");
    }
}
