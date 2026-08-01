using Xunit;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Calculators.Gpu;

namespace FracturingFog.Server.Tests;

// Vol-color slice E (#181) — GPU parity for the colored multi-light /
// Henyey-Greenstein phase / medium-color volumetric in-scatter. The per-pixel
// volume march lives inline in each of the 8 per-fractal ILGPU kernels (ILGPU
// can't take a struct-generic DE through LoadAutoGroupedStreamKernel), so the
// on-device pixel output is validated by CLI probe + user smoke like the rest
// of the GPU shade path. What is unit-testable here is the CPU→GPU bridge:
// GpuShadingParams.Build must carry the slice A/B/C knobs into the kernel-ready
// struct, and the defaults must resolve to the bit-identical no-op values the
// kernel loop keys off (anisotropy 0 → phase skipped, white fog → ×1 tint,
// lights 2/3 off → single-light path).
public class GpuVolumetricColorParityTests
{
    // The stock LightingFxData must build a GpuShadingParams whose new fields
    // are the pass-through values — this is the premise that keeps a default
    // GPU render pixel-identical with the pre-slice-E single-light kernel.
    [Fact]
    public void Default_Build_Resolves_To_BitIdentical_Premise()
    {
        var gp = GpuShadingParams.Build(LightingFxData.CreateDefault());

        // Slice B: isotropic → the kernel skips the HG phase term entirely.
        Assert.Equal(0.0, gp.VolumeAnisotropy);
        // Slice C: white medium → inR * (255/255) == inR, a ×1 no-op.
        Assert.Equal(255.0, gp.FogR);
        Assert.Equal(255.0, gp.FogG);
        Assert.Equal(255.0, gp.FogB);
        // Slice A: lights 2/3 dark by default → single-light in-scatter.
        Assert.Equal(0.0, gp.L2I);
        Assert.Equal(0.0, gp.L3I);
    }

    // Slice B/C: anisotropy and the packed medium color survive the bridge with
    // the channels unpacked to bytes-as-double, so the kernel doesn't bit-unpack
    // per pixel.
    [Fact]
    public void Build_Carries_Anisotropy_And_FogColor()
    {
        var fx = LightingFxData.CreateDefault();
        fx.VolumeAnisotropy = 0.5;
        fx.FogColor = 0xFFFFCC00u;   // amber: R full, G 0.8, B 0
        var gp = GpuShadingParams.Build(in fx);

        Assert.Equal(0.5, gp.VolumeAnisotropy);
        Assert.Equal(255.0, gp.FogR);
        Assert.Equal(204.0, gp.FogG);
        Assert.Equal(0.0, gp.FogB);
    }

    // Slice A: an enabled, colored second light reaches the kernel with its own
    // intensity + unpacked color, so the GPU in-scatter loop injects the same
    // per-light hue the CPU pipe does.
    [Fact]
    public void Build_Carries_Second_Light_Color_And_Intensity()
    {
        var fx = LightingFxData.CreateDefault();
        fx.Light2 = new DirectionalLight(
            theta: System.Math.PI * 1.25, phi: System.Math.PI * 0.55,
            intensity: 1.0, color: 0xFF0000FFu);   // blue fill
        var gp = GpuShadingParams.Build(in fx);

        Assert.Equal(1.0, gp.L2I);
        Assert.Equal(0.0, gp.L2R);
        Assert.Equal(0.0, gp.L2G);
        Assert.Equal(255.0, gp.L2B);
    }

    // Slice D (#180) GPU parity: the palette-map strength gate reaches the
    // kernel through the shading struct (default 0 = the kernel's bit-identical
    // no-op). The theme LUT itself is uploaded as a separate ArrayView kernel
    // arg — it can't ride on this blittable struct — and the kernel/LUT math is
    // validated on-device (CLI probe + user smoke), same as the rest of the GPU
    // shade path. Here we lock the CPU->GPU strength bridge + its default.
    [Fact]
    public void Build_Carries_PaletteStrength_And_Defaults_Off()
    {
        Assert.Equal(0.0, GpuShadingParams.Build(LightingFxData.CreateDefault()).VolumePaletteStrength);

        var fx = LightingFxData.CreateDefault();
        fx.VolumePaletteStrength = 0.75;
        Assert.Equal(0.75, GpuShadingParams.Build(in fx).VolumePaletteStrength);
    }
}
