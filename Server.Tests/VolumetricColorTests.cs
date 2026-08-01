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
    private static uint ShadeOnce(in LightingFxData fx, uint albedo = 0xFF808080u)
    {
        var inp = new ShadingInputs(
            px: 0, py: 1, pz: 0,      // surface hit (top of the sphere)
            nx: 0, ny: 1, nz: 0,      // normal up
            rdx: 0, rdy: 1, rdz: 0,   // view ray dir (+Y)
            totalT: 3.0, hitDist: 0.0, hitStep: 1, epsilon: 1e-4);
        return ShadingPipeline.Shade(in inp, albedo, in fx, SphereDe);
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

    // ── Slice B (#178): Henyey-Greenstein phase ───────────────────────────

    // Dim, low-fog config so the in-scatter stays well below the 255 clamp and
    // the phase term's effect on brightness is measurable. Light1 points +Y,
    // aligned with the view ray, so cosθ = 1 (peak forward / trough back).
    private static LightingFxData AlignedPhaseFx(double g)
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.05;
        fx.VolumeSteps = 12;
        fx.VolumeAnisotropy = g;
        // Light straight up (phi = 0), matching the +Y view ray.
        fx.Light1 = new DirectionalLight(theta: 0.0, phi: 0.0, intensity: 1.0, color: 0xFFFFFFFFu);
        return fx;
    }

    // Forward scatter (g > 0) with the light aligned to the view ray brightens
    // the in-scatter vs isotropic (g = 0) — the god-ray halo toward the light.
    [Fact]
    public void ForwardPhase_Brightens_InScatter_Toward_Light()
    {
        uint iso = ShadeOnce(AlignedPhaseFx(0.0), 0xFF202020u);
        uint fwd = ShadeOnce(AlignedPhaseFx(0.5), 0xFF202020u);
        Assert.True(R(fwd) > R(iso),
            $"forward phase did not brighten aligned in-scatter ({R(iso)} → {R(fwd)})");
    }

    // Back scatter (g < 0) with the light aligned to the view ray dims the
    // in-scatter vs isotropic — confirming g = 0 is the neutral midpoint.
    [Fact]
    public void BackPhase_Dims_InScatter_Toward_Light()
    {
        uint iso = ShadeOnce(AlignedPhaseFx(0.0), 0xFF202020u);
        uint bak = ShadeOnce(AlignedPhaseFx(-0.5), 0xFF202020u);
        Assert.True(R(bak) < R(iso),
            $"back phase did not dim aligned in-scatter ({R(iso)} → {R(bak)})");
    }

    // ── Slice C (#179): medium color / scattering albedo ──────────────────

    private static int G(uint bgra) => (int)((bgra >> 8) & 0xFF);
    private static int B(uint bgra) => (int)(bgra & 0xFF);

    // White FogColor (the default) must leave the in-scatter untinted — a
    // multiply-by-1 that keeps the pre-slice-C output bit-identical.
    [Fact]
    public void WhiteFogColor_Is_BitIdentical()
    {
        var baseline = VolFx();                     // FogColor defaults to white
        var white = VolFx(); white.FogColor = 0xFFFFFFFFu;
        Assert.Equal(ShadeOnce(baseline), ShadeOnce(white));
    }

    // A colored medium tints the fog independently of the (white) lights: an
    // amber FogColor keeps the red channel but suppresses blue.
    [Fact]
    public void ColoredFogColor_Tints_The_Medium()
    {
        uint white = ShadeOnce(VolFx());            // untinted white medium
        var amberFx = VolFx();
        amberFx.FogColor = 0xFFFFCC00u;             // amber: R full, G 0.8, B 0
        uint amber = ShadeOnce(amberFx);

        Assert.NotEqual(white, amber);
        // Blue in-scatter is zeroed by the amber tint → amber's blue channel
        // cannot exceed the white medium's.
        Assert.True(B(amber) <= B(white),
            $"amber fog did not suppress the blue in-scatter ({B(white)} → {B(amber)})");
    }

    // ── Slice D (#180): palette-mapped volumetric ─────────────────────────

    // Strength > 0 but no baked LUT (VolumePalette == null, the default) must be
    // a no-op — the guard keeps the in-scatter bit-identical with slice C, so a
    // theme that never bakes a ramp costs nothing and changes nothing.
    [Fact]
    public void PaletteStrength_Without_Lut_Is_BitIdentical()
    {
        uint baseline = ShadeOnce(VolFx());

        var fx = VolFx();
        fx.VolumePaletteStrength = 1.0;   // strength on, but VolumePalette stays null
        uint noLut = ShadeOnce(fx);

        Assert.Equal(baseline, noLut);
    }

    // A full-strength pure-green palette redistributes the in-scatter's own
    // energy onto the green channel (energy-preserving hue remap): green rises,
    // red and blue fall versus the untinted white-light in-scatter.
    [Fact]
    public void Palette_Remaps_InScatter_Toward_The_Theme_Hue()
    {
        uint white = ShadeOnce(VolFx());   // white in-scatter, no palette

        var green = VolFx();
        green.VolumePaletteStrength = 1.0;
        green.VolumePalette = new uint[] { 0xFF00FF00u, 0xFF00FF00u };  // pure-green ramp
        uint tinted = ShadeOnce(green);

        Assert.NotEqual(white, tinted);
        Assert.True(G(tinted) >= G(white),
            $"palette did not push in-scatter energy into green ({G(white)} → {G(tinted)})");
        Assert.True(R(tinted) <= R(white),
            $"green palette did not suppress the red in-scatter ({R(white)} → {R(tinted)})");
        Assert.True(B(tinted) <= B(white),
            $"green palette did not suppress the blue in-scatter ({B(white)} → {B(tinted)})");
    }
}
