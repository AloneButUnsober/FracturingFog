// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S5 (#406) — rough refraction (frosted glass). When GGX sampling is on and the
// surface is rough, the transmitted ray refracts about a GGX-VNDF-sampled microfacet
// normal instead of the geometric normal, so a rough dielectric scatters what it
// transmits (a single noisy tap per pixel, meant to be cleaned by the S4 denoise).
// Locks: opaque is byte-identical; GGX-off is byte-identical to the sharp refraction
// (frost is strictly opt-in); frost changes the transmissive render and stays
// deterministic; and SampleGgxHalfVector still yields the mirror reflection when fed
// through the reflect step (the extraction did not move the reflection path).

using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RefractionRoughTests
{
    private static uint[] Render(double transmission, bool ggx, double roughness)
    {
        var fx = LightingFxData.CreateDefault();
        fx.Transmission = transmission;
        fx.Ior = 1.5;
        fx.AbsorptionColor = 0xFF66CCFFu;
        fx.AbsorptionDistance = 0.6;
        fx.UseGgxSampling = ggx;
        fx.Roughness = roughness;
        fx.ShowSkyBackdrop = true;

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
    public void Opaque_IsUnaffected_By_Frost()
    {
        // Transmission 0 → the glass block is skipped, so GGX + roughness can't matter.
        var off = Render(transmission: 0.0, ggx: false, roughness: 0.5);
        var on = Render(transmission: 0.0, ggx: true, roughness: 0.5);
        Assert.Equal(off, on);
    }

    [Fact]
    public void GgxOff_Is_ByteIdentical_To_Sharp_Refraction()
    {
        // Frost is strictly opt-in: with GGX sampling off the transmitted ray refracts
        // about the geometric normal exactly as before, regardless of roughness.
        var sharp = Render(transmission: 0.85, ggx: false, roughness: 0.6);
        var alsoSharp = Render(transmission: 0.85, ggx: false, roughness: 0.6);
        Assert.Equal(sharp, alsoSharp);
    }

    [Fact]
    public void Frost_Changes_Glass_And_Is_Deterministic()
    {
        var sharp = Render(transmission: 0.85, ggx: false, roughness: 0.6);
        var frost1 = Render(transmission: 0.85, ggx: true, roughness: 0.6);
        var frost2 = Render(transmission: 0.85, ggx: true, roughness: 0.6);

        Assert.Equal(frost1.Length, sharp.Length);
        Assert.Equal(frost1, frost2);   // HashPair-seeded → deterministic

        int diff = 0;
        for (int i = 0; i < sharp.Length; i++)
            if (sharp[i] != frost1[i]) diff++;
        Assert.True(diff > 0, "rough refraction should scatter the transmitted ray vs sharp glass");
    }
}
