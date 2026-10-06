// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/GpuRoute.cs
//
// #1173-M (GPU parity plan G0.3) — which way the last frame went: on the GPU,
// or back to the CPU and why. Every GPU-capable calculator falls back to its
// CPU pipeline silently when a gate or device rules the GPU out. That is
// how #1164 (no 3D kernel ran on CUDA at all) went unnoticed. The calculators
// now record a GpuRoute per frame. The render host carries it on
// RenderFrameInfo (status bar tag) and the perf HUD shows the full reason.

namespace FracturingFog.Render
{
    public enum GpuRouteState : byte
    {
        /// <summary>The GPU path was not asked for (backend set to CPU), or
        /// the calculator has no GPU switch at all.</summary>
        NotRequested = 0,
        /// <summary>The frame ran on the GPU.</summary>
        Gpu,
        /// <summary>The GPU was asked for but the frame ran on the CPU.</summary>
        CpuFallback,
    }

    /// <summary>The GPU route the last frame took.</summary>
    /// <param name="State">GPU, CPU fallback, or not requested.</param>
    /// <param name="Reason">Short label, a few words, safe for the status bar:
    /// the fallback cause, or the device for a GPU frame.</param>
    /// <param name="Detail">Longer explanation for the perf HUD (may name the
    /// tracking issue or the device error). Null = same as
    /// <paramref name="Reason"/>.</param>
    public readonly record struct GpuRoute(GpuRouteState State, string? Reason = null, string? Detail = null)
    {
        public static GpuRoute NotRequested => default;

        public static GpuRoute OnGpu(string device, string? detail = null)
            => new(GpuRouteState.Gpu, device, detail);

        public static GpuRoute Cpu(string reason, string? detail = null)
            => new(GpuRouteState.CpuFallback, reason, detail);

        /// <summary>Compact tag for the status bar, or null when the GPU was
        /// not requested (nothing to say).</summary>
        public string? StatusTag => State switch
        {
            GpuRouteState.Gpu => "[GPU]",
            GpuRouteState.CpuFallback => $"[CPU: {Reason}]",
            _ => null,
        };

        /// <summary>One line for the perf HUD.</summary>
        public string HudLine => State switch
        {
            GpuRouteState.Gpu => $"gpu    on: {Detail ?? Reason}",
            GpuRouteState.CpuFallback => $"gpu    CPU fallback: {Detail ?? Reason}",
            _ => "gpu    off (CPU backend)",
        };
    }

    /// <summary>Implemented by calculators that can run on the GPU: the route
    /// their last <c>Calculate</c> took.</summary>
    public interface IGpuRouteSource
    {
        GpuRoute LastGpuRoute { get; }
    }
}
