// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/GpuComputeSelection.cs
//
// #1455 — whether the shell attaches the Vulkan compute kernels, and the one-line
// status that says so (window title + stderr). Linux used to compute everything
// on the CPU unless the user passed --renderer vulkan, and even then the kernel
// attached without a DXC to compile its shaders (they compile on first use, so
// every frame failed over to the CPU) and accepted a CPU Vulkan driver (llvmpipe)
// as the "GPU". The rule:
//   • Windows' D3D11 hook owns compute there: nothing to decide.
//   • Vulkan is wanted for --renderer vulkan anywhere, and by default (Auto) on
//     Linux. --renderer silk / skia keep the CPU (the escape hatch).
//   • It needs a non-CPU Vulkan device with a compute queue. A CPU driver only
//     counts with FF_VULKAN_ALLOW_CPU=1 (testing on lavapipe).
//   • It needs a working dxc (the bundled copy, DXC_PATH, the Vulkan SDK or PATH).
// Pure: the caller probes the devices and DXC; the tests drive every branch.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace FracturingFog.Hosting;

/// <summary>#1455 — the outcome: whether the GPU computes, on which device, and why.</summary>
/// <param name="UseGpu">The GPU computes: attach the Vulkan compute / relief / froxel kernels
/// (<see cref="GpuComputeSelection.Decide"/>), or the D3D11 kernel attached
/// (<see cref="GpuComputeSelection.FromAttachedKernel"/>, #1457).</param>
/// <param name="Device">The device the kernels will run on (null when off).</param>
/// <param name="Status">One line for the window title and stderr; null when there
/// is nothing to report (Decide on Windows' D3D path, where FromAttachedKernel
/// reports instead; macOS by default).</param>
public readonly record struct GpuComputeDecision(bool UseGpu, string? Device, string? Status);

public static class GpuComputeSelection
{
    /// <summary>Set to 1 to let a CPU Vulkan driver (llvmpipe / lavapipe) run the
    /// compute kernels — for testing only; it is slower than the CPU path.</summary>
    public const string AllowCpuEnv = "FF_VULKAN_ALLOW_CPU";

    /// <param name="isLinux">The host OS is Linux.</param>
    /// <param name="preferred">The --renderer choice.</param>
    /// <param name="hasD3dHook">The Windows bootstrap installed its D3D11 kernel factory.</param>
    /// <param name="devices">The Vulkan devices; null when the loader / instance failed.</param>
    /// <param name="probeDxc">Runs dxc --version (only called when it matters).</param>
    /// <param name="allowCpu"><see cref="AllowCpuEnv"/> is set.</param>
    public static GpuComputeDecision Decide(bool isLinux, RendererBackend preferred, bool hasD3dHook,
        IReadOnlyList<VulkanContext.DeviceInfo>? devices, Func<(bool Ok, string Detail)> probeDxc, bool allowCpu)
    {
        if (hasD3dHook) return new(false, null, null);
        bool wanted = preferred == RendererBackend.Vulkan || (preferred == RendererBackend.Auto && isLinux);
        if (!wanted)
            return new(false, null, isLinux ? $"GPU compute off (--renderer {preferred.ToString().ToLowerInvariant()})" : null);

        var compute = devices?.Where(d => d.HasCompute).ToList() ?? new List<VulkanContext.DeviceInfo>();
        if (compute.Count == 0)
            return new(false, null, devices == null
                ? "GPU compute off: no Vulkan driver (install your GPU's Vulkan driver)"
                : "GPU compute off: no Vulkan device with compute");

        var best = compute.OrderByDescending(d => VulkanContext.RankType(d.Type)).First();
        bool cpuOnly = best.Type == PhysicalDeviceType.Cpu;
        if (cpuOnly && !allowCpu)
            return new(false, null,
                $"GPU compute off: the only Vulkan device is a CPU driver ({best.Name}); install your GPU's Vulkan driver");

        var (ok, detail) = probeDxc();
        if (!ok)
            return new(false, null, $"GPU compute off: no DXC shader compiler ({detail}); install dxc or set DXC_PATH");

        return new(true, best.Name, cpuOnly
            ? $"GPU compute: Vulkan on a CPU driver ({best.Name}, {AllowCpuEnv})"
            : $"GPU compute: Vulkan ({best.Name})");
    }

    /// <summary>The Vulkan devices, or null when the loader / instance fails. Never throws.</summary>
    public static IReadOnlyList<VulkanContext.DeviceInfo>? ProbeDevices()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            return ctx.EnumerateDevices();
        }
        catch { return null; }
        finally { ctx?.Dispose(); }
    }

    /// <summary>#1457 — the status of a backend whose kernel the host already
    /// attached (Windows' D3D11 hook): it computes on the kernel's device, or, with
    /// no kernel (e.g. the DirectX 12 renderer), the CPU computes.</summary>
    public static GpuComputeDecision FromAttachedKernel(IGpuKernel? kernel)
    {
        if (kernel == null)
            return new(false, null, "GPU compute off: this renderer has no GPU compute kernel");
        string? name = kernel.DeviceName;
        return new(true, name, $"GPU compute: {kernel.BackendLabel} ({name ?? "unknown device"})");
    }

    public static bool AllowCpuFromEnvironment()
        => Environment.GetEnvironmentVariable(AllowCpuEnv) is "1" or "true" or "TRUE" or "yes";
}
