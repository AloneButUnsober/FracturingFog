// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/FroxelGpuUniforms.cs
//
// Roadmap slice S6 (3D-Rendering-Roadmap.md, parent #389 / issue #408) — the
// GPU-froxel-compute seam. The froxel volume (populate → integrate → composite)
// has always been a pure-CPU post-pass (FroxelVolumePass + FroxelCameraVolume);
// this is the backend-agnostic uniform bundle a GPU compute kernel consumes to
// reproduce that pass on the device.
//
// It carries the SAME two objects FroxelCameraVolume.Apply builds on the CPU — a
// framed FroxelGrid and a populated FroxelMedium — so the GPU kernel and the CPU
// oracle work from byte-identical scene numbers by construction. That makes the
// --froxelgpu WARP gate a like-for-like diff (float shader vs double CPU pass),
// exactly as ReliefUniforms does for the relief raymarch kernel (#160/#162).

namespace FracturingFog.Rendering.Lighting;

/// <summary>Backend-agnostic input bundle for the GPU froxel compute pass
/// (roadmap S6, #408): the framed <see cref="FroxelGrid"/> + the populated
/// <see cref="FroxelMedium"/> — the identical objects the CPU
/// <see cref="FroxelCameraVolume.Apply"/> builds, so GPU == CPU by
/// construction. See <see cref="IFroxelVolumeKernel"/> for the dispatch
/// contract.</summary>
public readonly struct FroxelGpuUniforms
{
    /// <summary>The camera-framed froxel grid (exponential near→far depth
    /// slices), from <see cref="FroxelCameraVolume.BuildGrid"/>.</summary>
    public FroxelGrid Grid { get; }

    /// <summary>The fog medium + up to three scene lights, from
    /// <see cref="FroxelCameraVolume.BuildMedium"/>.</summary>
    public FroxelMedium Medium { get; }

    private FroxelGpuUniforms(FroxelGrid grid, FroxelMedium medium)
    {
        Grid = grid;
        Medium = medium;
    }

    /// <summary>Build the uniforms for a relief scene: frame the grid over the
    /// oblique camera + slab and populate the medium from the fog knobs + all
    /// three lights — the same two calls <see cref="FroxelCameraVolume.Apply"/>
    /// makes, so the GPU pass and the CPU oracle frame the identical
    /// volume.</summary>
    public static FroxelGpuUniforms Build(in HeightfieldRaymarch2D.ReliefCamera cam, in LightingFxData fx)
        => Build(in cam, in fx, FracturingFog.Models.FroxelQuality.Balanced);

    /// <summary>As <see cref="Build(in HeightfieldRaymarch2D.ReliefCamera,in LightingFxData)"/>,
    /// at a chosen <paramref name="quality"/> froxel resolution (roadmap S6, #408) — the
    /// GPU kernel reads the dims off the grid, so it scales in lock-step with the CPU
    /// oracle. Balanced → byte-identical.</summary>
    public static FroxelGpuUniforms Build(in HeightfieldRaymarch2D.ReliefCamera cam, in LightingFxData fx,
        FracturingFog.Models.FroxelQuality quality)
        => new(FroxelCameraVolume.BuildGrid(in cam, quality), FroxelCameraVolume.BuildMedium(in cam, in fx));
}
