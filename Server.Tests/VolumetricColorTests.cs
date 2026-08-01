using System;
using Xunit;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Server.Tests;

// Vol-color slice A (#177) — the volumetric in-scatter loop now accumulates
// every emitting directional light's own color, not just Light1. These lock
// in (a) disabled lights (Intensity 0, the default for Light2/3) contribute
// nothing so single-light output is unchanged, and (b) an enabled second light
// injects its color into the fog.
public class VolumetricColorTests
{
    // Sphere DE (radius 1 at origin). Only consulted by SoftShadow, which is
    // off here (ShadowSteps == 0) — its presence just satisfies the hasDe gate.
    private static readonly DistanceEstimator SphereDe =
        (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - 1.0;

    // One surface hit on the unit sphere, view ray pointing +Y, fog active.
    private static uint ShadeOnce(in LightingFxData fx)
    {
        var inp = new ShadingInputs(
            px: 0, py: 1, pz: 0,      // surface hit (top of the sphere)
            nx: 0, ny: 1, nz: 0,      // normal up
            rdx: 0, rdy: 1, rdz: 0,   // view ray dir
            totalT: 3.0, hitDist: 0.0, hitStep: 1, epsilon: 1e-4);
        return ShadingPipeline.Shade(in inp, 0xFF808080u, in fx, SphereDe);
    }

    private static LightingFxData VolFx()
    {
        var fx = LightingFxData.CreateDefault();  // Light1 on, Light2/3 Intensity 0
        fx.FogDensity = 0.5;
        fx.VolumeSteps = 16;
        return fx;
    }

    private static int R(uint bgra) => (int)((bgra >> 16) & 0xFF);

    // A disabled light must not leak color into the in-scatter, regardless of
    // its packed Color — proves the Intensity>0 gate keeps single-light output
    // bit-identical with the pre-multi-light path.
    [Fact]
    public void DisabledLight_Color_Does_Not_Affect_InScatter()
    {
        uint baseline = ShadeOnce(VolFx());

        var fx = VolFx();
        // Intensity stays 0 (default); only the color changes.
        fx.Light2.Color = 0xFF0000FFu;  // blue
        fx.Light3.Color = 0xFF00FF00u;  // green
        uint recolored = ShadeOnce(fx);

        Assert.Equal(baseline, recolored);
    }

    // Enabling a second, red light adds red single-scatter into the fog: the
    // pixel changes and its red channel does not decrease.
    [Fact]
    public void SecondLight_Injects_Its_Color_Into_The_Fog()
    {
        uint off = ShadeOnce(VolFx());

        var on = VolFx();
        on.Light2 = new DirectionalLight(
            theta: Math.PI * 1.25, phi: Math.PI * 0.55,
            intensity: 1.0, color: 0xFFFF0000u);  // red key-fill
        uint lit = ShadeOnce(on);

        Assert.NotEqual(off, lit);
        Assert.True(R(lit) >= R(off),
            $"red second light did not raise the red channel ({R(off)} → {R(lit)})");
    }
}
