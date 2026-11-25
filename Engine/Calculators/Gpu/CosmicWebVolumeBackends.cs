// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// CosmicWebVolumeBackends.cs (#2024, Cosmic Web epic #2020)
//
// The D3D11 and Vulkan backends of the Cosmic Web volume raymarch (one HLSL source,
// Rendering.D3D/CosmicWebVolumeKernelSource; the CPU reference is CosmicWeb/VolumeRaymarch). The
// backends live in Rendering.D3D / Rendering.Vulkan (the Engine does not reference them): the host
// and the batch set the factories (the #1972 black hole pattern), each kernel owns its device /
// context, and a backend that cannot start is remembered as unavailable for the session.
//
// Choice (FF_GPU_COSMIC_WEB, or CosmicWebVolumeBackends.Selection for tests):
//   auto    D3D11, else Vulkan, else the CPU;
//   d3d11 / vulkan  that backend only (the CPU when it is unavailable);
//   cpu     always the CPU reference.

using System;
using System.Threading;

using FracturingFog.CosmicWeb;

namespace FracturingFog.Calculators.Gpu;

/// <summary>#2024 — a GPU backend for the Cosmic Web volume raymarch.</summary>
public interface ICosmicWebVolumeKernel : IDisposable
{
    /// <summary>"D3D11" or "Vulkan".</summary>
    string Backend { get; }
    string DeviceName { get; }
    string LastError { get; }
    /// <summary>Raymarch a frame; null on failure or cancellation (the caller marches on the CPU).
    /// The grid is uploaded once per <see cref="CosmicWebDensityGrid"/> instance.</summary>
    CosmicWebVolumeFrame? Render(in CosmicWebVolumeParams p, CosmicWebDensityGrid grid, uint[] lut, CancellationToken ct = default);
}

public static class CosmicWebVolumeBackends
{
    public enum Choice { Auto, Cpu, D3D11, Vulkan }

    public static Func<ICosmicWebVolumeKernel?>? D3D11Factory { get; set; }
    public static Func<ICosmicWebVolumeKernel?>? VulkanFactory { get; set; }

    private static readonly object s_gate = new();
    private static ICosmicWebVolumeKernel? s_d3d11, s_vulkan;
    private static bool s_d3d11Failed, s_vulkanFailed;

    /// <summary>The backend choice (env FF_GPU_COSMIC_WEB); a set value overrides for the calling thread only (tests).</summary>
    public static Choice Selection
    {
        get => t_selection ?? s_env;
        set => t_selection = value;
    }
    [ThreadStatic] private static Choice? t_selection;
    private static readonly Choice s_env = Parse(Environment.GetEnvironmentVariable("FF_GPU_COSMIC_WEB"));

    public static Choice Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "cpu" or "off" or "0" => Choice.Cpu,
        "d3d11" or "d3d" or "dx11" => Choice.D3D11,
        "vulkan" or "vk" => Choice.Vulkan,
        _ => Choice.Auto,
    };

    public static ICosmicWebVolumeKernel? D3D11 => Get(ref s_d3d11, ref s_d3d11Failed, D3D11Factory);
    public static ICosmicWebVolumeKernel? Vulkan => Get(ref s_vulkan, ref s_vulkanFailed, VulkanFactory);

    private static ICosmicWebVolumeKernel? Get(ref ICosmicWebVolumeKernel? slot, ref bool failed, Func<ICosmicWebVolumeKernel?>? factory)
    {
        lock (s_gate)
        {
            if (slot != null || failed || factory == null) return slot;
            try { slot = factory(); }
            catch (Exception) { slot = null; }
            if (slot == null) failed = true;
            return slot;
        }
    }

    /// <summary>The kernel for <paramref name="choice"/>, or null for the CPU.</summary>
    public static ICosmicWebVolumeKernel? KernelFor(Choice choice) => choice switch
    {
        Choice.Cpu => null,
        Choice.D3D11 => D3D11,
        Choice.Vulkan => Vulkan,
        _ => D3D11 ?? Vulkan,
    };

    /// <summary>Reset the cached kernels (tests).</summary>
    public static void ResetForTests()
    {
        lock (s_gate)
        {
            try { s_d3d11?.Dispose(); } catch { }
            try { s_vulkan?.Dispose(); } catch { }
            s_d3d11 = s_vulkan = null;
            s_d3d11Failed = s_vulkanFailed = false;
        }
    }
}
