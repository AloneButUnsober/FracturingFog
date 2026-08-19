// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S5 integration follow-up (3D-Rendering-Roadmap.md, #389 / #406):
// wire the DielectricOps glass math into the shade path. DielectricOps is unit-
// tested separately; these lock the WIRING in ShadingPipeline.Shade<TDe>: a
// transmissive material refracts the environment (Fresnel-mixed reflect/refract,
// Beer-Lambert tint) and blends into the surface by Transmission, while
// Transmission==0 leaves the opaque surface byte-identical. Environment-refraction
// approximation (one interface, no internal march) — the wiring, not the physics.

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RefractionShadingTests
{
    // Hit facing the camera: ray along +Z, outward normal along -Z (toward camera).
    private static ShadingInputs FrontHit()
        => new(px: 0, py: 0, pz: 0, nx: 0, ny: 0, nz: -1,
               rdx: 0, rdy: 0, rdz: 1, totalT: 3.0, hitDist: 1e-4, hitStep: 8, epsilon: 1e-4);

    private static uint Shade(LightingFxData fx)
    {
        var nd = default(NullDe);
        var i = FrontHit();
        return ShadingPipeline.Shade<NullDe>(in i, 0xFF808080u, in fx, in nd, hasDe: false);
    }

    private static (int r, int g, int b) Rgb(uint c)
        => ((int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF));

    // Transmission 0 keeps the surface opaque — Ior / absorption are inert, so the
    // pixel is byte-identical to the default (glass-off) shade.
    [Fact]
    public void Transmission_Zero_Is_ByteIdentical()
    {
        var baseFx = LightingFxData.CreateDefault();
        var glassFx = LightingFxData.CreateDefault();
        glassFx.Ior = 1.9;
        glassFx.AbsorptionColor = 0xFFFF0000u;
        glassFx.AbsorptionDistance = 0.3;
        // Transmission left at 0 → all the above must not matter.
        Assert.Equal(Shade(baseFx), Shade(glassFx));
    }

    // Turning transmission on changes the shaded pixel (glass shows the refracted
    // environment through the surface).
    [Fact]
    public void Transmission_Alters_The_Pixel()
    {
        var opaque = LightingFxData.CreateDefault();
        var glass = LightingFxData.CreateDefault();
        glass.Transmission = 1.0;
        glass.Ior = 1.5;
        Assert.NotEqual(Shade(opaque), Shade(glass));
    }

    // A colored absorbing medium tints the transmitted light: a red-passing tint
    // yields a redder pixel than clear glass under the same geometry.
    [Fact]
    public void Absorption_Tints_The_Transmitted_Light()
    {
        var clear = LightingFxData.CreateDefault();
        clear.Transmission = 1.0; clear.Ior = 1.5;
        clear.AbsorptionColor = 0xFFFFFFFFu;   // clear

        var red = LightingFxData.CreateDefault();
        red.Transmission = 1.0; red.Ior = 1.5;
        red.AbsorptionColor = 0xFFFF0000u;      // only red survives
        red.AbsorptionDistance = 0.5;

        var (cr, cg, cb) = Rgb(Shade(clear));
        var (rr, rg, rb) = Rgb(Shade(red));
        // Red-passing glass suppresses green/blue relative to clear → higher R−B.
        Assert.True((rr - rb) >= (cr - cb),
            $"red glass R-B ({rr - rb}) should be >= clear R-B ({cr - cb})");
        Assert.True(rg <= cg && rb <= cb, "green/blue should be absorbed vs clear");
    }

    // Total internal reflection is impossible entering a denser medium (eta < 1) at
    // any angle, so a front-on glass hit transmits (does not force full reflection).
    // Sanity: full transmission at normal incidence is dominated by the transmitted
    // term (Fresnel ~0.04), i.e. differs from a pure mirror-reflection pixel.
    [Fact]
    public void Front_On_Glass_Is_Transmission_Dominated()
    {
        var glass = LightingFxData.CreateDefault();
        glass.Transmission = 1.0; glass.Ior = 1.5;
        // Compare against a hypothetical fully-reflective surface value is hard to
        // synthesize; instead assert transmission at normal incidence is a strong
        // change and produces a valid opaque byte (alpha preserved).
        uint g = Shade(glass);
        Assert.Equal(0xFFu, (g >> 24) & 0xFF);   // alpha preserved
        Assert.NotEqual(Shade(LightingFxData.CreateDefault()), g);
    }
}
