// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VLAO audit finding #7 / #16 (issues #297, #301): the Relief-3D GPU raymarch
// was a strict subset of the full CPU volumetric pipeline — fog driven by the
// key light only, and NO palette-mapped fog. #185 (slice D) brought the palette
// map to the relief path across all backends; #388 brought the full three-light
// in-scatter, so the relief fog now matches the CPU pipeline. The parity contract
// is the ReliefUniforms cbuffer twin; these tests lock the supported set
// structurally so a change that drops a field trips a test and forces the VL
// Guide §6 / Relief3D Cookbook docs to be updated in step.

using System.Linq;
using System.Reflection;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefGpuVolumetricParityTests
{
    private static string[] UniformFieldNames() =>
        typeof(ReliefUniforms)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Select(f => f.Name)
            .ToArray();

    [Fact]
    public void ReliefUniforms_Carries_PaletteMappedFog_Field()
    {
        // #185 (slice D) — the relief contract now carries the palette-map strength
        // + the baked theme-ramp LUT, so the D3D / Vulkan kernels and the parity
        // twin all hue-remap the in-scatter through the active 3D theme. If you
        // drop these, update Volumetric-Lighting-Guide §6 + Relief3D-Cookbook.
        var names = UniformFieldNames();
        Assert.Contains("VolPaletteStrength", names);
        Assert.Contains("VolPalette", names);
    }

    [Fact]
    public void ReliefUniforms_Carries_The_Documented_Fog_Subset()
    {
        // The GPU relief fog: Beer-Lambert fog + three-light in-scatter (#388) +
        // anisotropy + fog color — all carried by the ReliefUniforms twin.
        var names = UniformFieldNames();
        Assert.Contains("FogDensity", names);
        Assert.Contains("VolumeSteps", names);
        Assert.Contains("VolAnisotropy", names);
        Assert.Contains("FogColor", names);
    }
}
