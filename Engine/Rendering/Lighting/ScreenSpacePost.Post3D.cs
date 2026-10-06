// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ScreenSpacePost.Post3D.cs — #1172 (GPU parity plan G1.2 / G2.1): the 3D
// calculators' screen-space post stack, in one place.
//
// Every 3D raymarcher ran the same tail after its CPU trace: SSAO → froxel
// composite → screen-space HDR DoF → tonemap + bloom → edge ink, bracketed by
// BeginGpuFrame / EndGpuFrame (the GpuPostKernels seam). The GPU trace skipped
// all of it — the "P7c" gap: GPU frames lost SSAO, tonemap, bloom, DoF and
// edge ink. The ILGPU kernels now emit the same depth / normal / pre-clamp HDR
// G-buffers the CPU Shade writes, so the GPU path runs this exact stack too.
// The thin-lens rule matches the CPU tails: thin-lens DoF integrates the lens
// itself and skips the depth/normal G-buffer, so SSAO / screen DoF / edge ink
// are bypassed while it is on; tonemap/bloom still run.

using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

public static partial class ScreenSpacePost
{
    /// <summary>Run the 3D post stack over a traced frame. Null buffers skip the
    /// passes that need them (same guards as the per-calculator CPU tails).</summary>
    public static void ApplyPost3D<TDe>(uint[] color, float[]? hdr, float[]? depth, float[]? normal,
        int w, int h, bool thinLensDof, in LightingFxData fx,
        in Froxel3DView froxelView, in LightingFxData froxelFx, in TDe de)
        where TDe : struct, IDistanceEstimator
    {
        BeginGpuFrame(color, w, h, in fx);
        if (depth is not null && normal is not null && !thinLensDof)
            ApplySsao(color, depth, normal, w, h, in fx);
        ApplyFroxel3D(color, hdr, depth, w, h, in froxelView, in froxelFx, in de);
        if (hdr is not null && depth is not null && !thinLensDof)
            ApplyHdrDof(hdr, depth, w, h, in fx);
        if (hdr is not null)
            ApplyToneMapBloom(color, hdr, w, h, in fx);
        if (depth is not null && normal is not null && !thinLensDof)
            ApplyEdgeInk(color, depth, normal, w, h, in fx);
        EndGpuFrame(in fx);
    }

    /// <summary>#1172 — true when the post stack needs the pre-clamp HDR beauty
    /// (tonemap / bloom; screen-space DoF reads it too). Same condition the 3D
    /// calculators use to allocate their CPU HDR buffer.</summary>
    public static bool WantsHdrPost(in LightingFxData fx)
        => fx.ToneMap != ToneMapOperator.None || fx.BloomStrength > 0;
}
