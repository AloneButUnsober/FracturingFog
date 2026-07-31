// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// IReliefRaymarchKernel.cs — Relief 3D Slice 3d (#162) host-wiring boundary.
//
// The Engine-side contract the host uses to dispatch the Relief 3D sphere-trace
// on the GPU without referencing any backend. The concrete kernels live in the
// Windows-only Rendering.D3D (ReliefRaymarchGpuKernel, #160) and the
// cross-platform Rendering.Vulkan (ReliefRaymarchVulkanKernel, #161); both
// already expose this exact Run signature, so implementing the interface is a
// no-op beyond the declaration. Engine cannot reference those projects (they
// reference Engine), so the platform installer hands the host a factory that
// constructs one — mirroring IGpuKernel / GpuKernelFactory for escape-time.
//
// Contract: Run raymarches the height field described by <paramref name="u"/>
// (a ReliefUniforms built from the SAME cached hbuf + camera the CPU
// HeightfieldRaymarch2D.Render uses) and writes packed-ARGB into dst. Scope is
// the Slice-3 shader subset (flat three-light Lambert + ambient + gradient
// sky); the full ShadingPipeline FX the CPU Render applies is Slice 4 (#158),
// which is why the GPU path is opt-in (FractalParameters.Relief2DGpuRaymarch)
// until it reaches fidelity parity. The CPU Render stays the fallback + oracle.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Backend-agnostic GPU dispatch of the Relief 3D raymarch (#162). See
/// the file header for the parity contract and the Slice-3 shader scope.</summary>
public interface IReliefRaymarchKernel : IDisposable
{
    /// <summary>Raymarch the height field for <paramref name="u"/> and write the
    /// packed-ARGB result into <paramref name="dst"/> (length ≥ u.W·u.H). The
    /// compressed field (<paramref name="hbuf"/>, u.Hw·u.Hh cells), optional cull
    /// mask (<paramref name="keep"/>) and flat albedo are the same buffers the CPU
    /// <see cref="HeightfieldRaymarch2D"/> render feeds its sphere trace.</summary>
    void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst);
}
