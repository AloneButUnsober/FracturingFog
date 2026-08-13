// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VLAO audit finding #7 / #16 (issues #297, #301): the Relief-3D GPU raymarch
// is a strict subset of the full CPU volumetric pipeline — fog is driven by the
// key light only and there is NO palette-mapped fog. The parity contract is the
// ReliefUniforms cbuffer twin; these tests lock the subset structurally so a
// future change that adds palette support (or drops a fog field) trips a test
// and forces the VL Guide §6 / Relief3D Cookbook docs to be updated in step.

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
    public void ReliefUniforms_Has_No_PaletteMappedFog_Field()
    {
        // GPU relief has no VolumePaletteStrength in its contract → palette-mapped
        // fog is a CPU-relief-only feature. If you add it to the GPU path, add
        // the field here AND update Volumetric-Lighting-Guide §6 +
        // Relief3D-Cookbook (the "GPU relief only" caveat).
        var names = UniformFieldNames();
        Assert.DoesNotContain("VolumePaletteStrength", names);
        Assert.DoesNotContain("VolumePalette", names);
    }

    [Fact]
    public void ReliefUniforms_Carries_The_Documented_Fog_Subset()
    {
        // The subset GPU relief DOES support: Beer-Lambert fog + single-scatter
        // in-scatter (key light only) + anisotropy + fog color.
        var names = UniformFieldNames();
        Assert.Contains("FogDensity", names);
        Assert.Contains("VolumeSteps", names);
        Assert.Contains("VolAnisotropy", names);
        Assert.Contains("FogColor", names);
    }
}
