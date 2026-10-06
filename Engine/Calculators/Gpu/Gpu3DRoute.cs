// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Gpu3DRoute.cs — #1173-M (GPU parity plan G0.3): name the reason a 3D
// raymarch frame did not run on the GPU.
//
// The 3D calculators share one GPU gate (UseGpuRender, Beauty AOV, not a
// low-res preview, ScreenSpacePost.GpuTraceAllowed) plus a per-family
// condition (KIFS fold, Kleinian group shape, ...). Gate() walks the same
// conditions in the same order and returns the first one that fails, so the
// reason shown is the one that actually kept the frame on the CPU. AfterRender
// turns the GPU calculator's outcome into the route: the device on success,
// or the device / kernel error on failure.

using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Calculators.Gpu;

public static class Gpu3DRoute
{
    /// <summary>The shared 3D GPU gate. Returns null when the frame may try
    /// the GPU; otherwise the route that explains why not.
    /// <paramref name="familyReason"/> / <paramref name="familyDetail"/>
    /// carry the family's own CPU-only condition (null = none).</summary>
    public static GpuRoute? Gate(in LightingFxData fx, bool lowRes,
        string? familyReason = null, string? familyDetail = null)
    {
        if (!fx.UseGpuRender) return GpuRoute.NotRequested;
        if (fx.DebugAov != AovView.Beauty)
            return GpuRoute.Cpu("AOV view", $"the '{fx.DebugAov}' AOV view renders on the CPU only (#323)");
        if (lowRes)
            return GpuRoute.Cpu("preview frame", "low-res preview frames render on the CPU");
        if (!ScreenSpacePost.GpuTraceAllowed(in fx))
        {
            return ScreenSpacePost.WantsDepthOutput(in fx)
                ? GpuRoute.Cpu("stereo depth", "stereo / autostereogram output needs the CPU depth buffer")
                : GpuRoute.Cpu("froxel + thin-lens", "3D froxel fog with thin-lens DoF renders on the CPU only");
        }
        if (familyReason != null) return GpuRoute.Cpu(familyReason, familyDetail);
        return null;
    }

    /// <summary>Route for a frame that reached the GPU calculator:
    /// <paramref name="rendered"/> is its Render result,
    /// <paramref name="lastError"/> its LastError.</summary>
    public static GpuRoute AfterRender(bool rendered, string? lastError)
    {
        if (rendered) return GpuRoute.OnGpu(GpuAcceleratorHost.DeviceLabel ?? "GPU");
        return DeviceFailure(lastError);
    }

    /// <summary>Route for a GPU attempt that failed: device latch, no
    /// device, or a kernel error.</summary>
    public static GpuRoute DeviceFailure(string? lastError)
    {
        string detail = string.IsNullOrEmpty(lastError) ? GpuAcceleratorHost.LastError : lastError!;
        if (GpuAcceleratorHost.IsFaulted)
            return GpuRoute.Cpu("device faulted", string.IsNullOrEmpty(detail) ? null : detail);
        if (string.IsNullOrEmpty(detail)) detail = "GPU render failed";
        if (detail.StartsWith("GPU too slow", System.StringComparison.Ordinal))   // #1169
            return GpuRoute.Cpu("GPU too slow", detail);
        if (detail.Contains("no Float64-capable GPU", System.StringComparison.Ordinal))
            return GpuRoute.Cpu("no fp64 GPU", detail);
        if (detail.Contains("kernel load failed", System.StringComparison.Ordinal))
            return GpuRoute.Cpu("kernel load failed", detail);
        return GpuRoute.Cpu("GPU error", detail);
    }
}
