// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// IFroxelVolumeKernel.cs — Roadmap S6 (#389 / #408) GPU-froxel host-wiring seam.
//
// The Engine-side contract the host uses to composite the froxel volume on the
// GPU without referencing any backend — the froxel analogue of
// IReliefRaymarchKernel (#162). The concrete kernel lives in the Windows-only
// Rendering.D3D (FroxelGpuKernel, this slice); a cross-platform Rendering.Vulkan
// kernel is the documented follow-up (the shared HLSL FroxelKernelSource is
// already one-source/two-compiler-ready, mirroring ReliefRaymarchKernelSource).
// Engine cannot reference those projects (they reference Engine), so the platform
// installer hands the host a factory that constructs one — mirroring
// ReliefKernelFactory.
//
// Contract: Composite populates + integrates the camera-framed froxel volume
// described by the uniforms, then composites it over the fog-free beauty by the
// render's own per-pixel WORLD depth — the GPU twin of
// FroxelVolumePass.Populate + FroxelCameraVolume.Apply's CompositeWorldDepth.
// Correctness is proven against that pure-CPU pass by the --froxelgpu WARP gate.
// Both work from the SAME FroxelGrid + FroxelMedium (via FroxelGpuUniforms), so
// exact bit-equality is not expected (double CPU vs float shader) — the gate
// tolerates a small mean channel diff, as the relief gate does.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Backend-agnostic GPU composite of the froxel volume over a beauty
/// buffer (roadmap S6, #408). See the file header for the parity contract.</summary>
public interface IFroxelVolumeKernel : IDisposable
{
    /// <summary>Populate + integrate the froxel volume for <paramref name="u"/>
    /// and composite it over <paramref name="beauty"/> (packed ARGB, fog-free)
    /// by <paramref name="worldDepth"/> (ray distance from the camera, one per
    /// pixel — the relief render's depth AOV), writing the result into
    /// <paramref name="dst"/>. All pixel buffers are length ≥ <paramref name="w"/>·
    /// <paramref name="h"/>; alpha is preserved. The GPU twin of the CPU
    /// <see cref="FroxelCameraVolume.Apply"/>.</summary>
    void Composite(in FroxelGpuUniforms u, uint[] beauty, float[] worldDepth, int w, int h, uint[] dst);
}
