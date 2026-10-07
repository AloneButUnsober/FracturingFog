// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-L / GPU parity plan G4.1 — "Vulkan SP is ~20x slower than D3D11 SP" was the
// readback, not the shader: every buffer took the first HostVisible | HostCoherent
// memory type, which on NVIDIA is uncached write-combined system memory, and the CPU
// read the 1080p result buffers out of it at ~780 ms (dispatch: ~22 ms). Readback
// buffers now prefer a host-cached type (VulkanHostMemory), and the frame takes
// ~28 ms. These tests pin the type choice on whatever Vulkan device the host has
// (skipped without one) and check a 1080p Mandelbrot frame's readback is no longer
// pathological.

using System;

using FracturingFog.Rendering.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1173LVulkanReadbackMemoryTests
{
    private static VulkanContext? TryContext()
    {
        try
        {
            var ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return ctx;
        }
        catch { return null; }
    }

    [Fact]
    public void Readback_Buffers_Prefer_Host_Cached_Memory()
    {
        using var ctx = TryContext();
        if (ctx is null) Assert.Skip("no Vulkan device on this host");
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out PhysicalDeviceMemoryProperties mp);
        var types = mp.MemoryTypes.AsSpan();
        uint all = mp.MemoryTypeCount >= 32 ? uint.MaxValue : (1u << (int)mp.MemoryTypeCount) - 1u;
        const MemoryPropertyFlags coherent = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        bool anyCached = false;
        for (int i = 0; i < mp.MemoryTypeCount; i++)
            if ((types[i].PropertyFlags & (coherent | MemoryPropertyFlags.HostCachedBit)) == (coherent | MemoryPropertyFlags.HostCachedBit))
                anyCached = true;

        uint rb = VulkanHostMemory.FindType(ctx.Vk, ctx.PhysicalDevice, all, readback: true);
        uint up = VulkanHostMemory.FindType(ctx.Vk, ctx.PhysicalDevice, all, readback: false);
        Assert.Equal(coherent, types[(int)rb].PropertyFlags & coherent);
        Assert.Equal(coherent, types[(int)up].PropertyFlags & coherent);
        if (anyCached)
            Assert.True((types[(int)rb].PropertyFlags & MemoryPropertyFlags.HostCachedBit) != 0,
                $"{ctx.PickedName}: offers a host-cached type, but readback got type {rb} ({types[(int)rb].PropertyFlags})");
    }

    [Fact]
    public void Mandelbrot_Readback_Is_Not_Pathological()
    {
        using var probe = TryContext();
        if (probe is null) Assert.Skip("no Vulkan device on this host");
        if (probe.PickedType == PhysicalDeviceType.Cpu) Assert.Skip("software Vulkan device; readback cost is not representative");
        using var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("Vulkan compute kernel unavailable");
        int w = 1920, h = 1080, n = w * h;
        var it = new int[n]; var sm = new float[n];
        var zr = new float[n]; var zi = new float[n]; var dr = new float[n]; var di = new float[n];
        double best = double.MaxValue, dispatch = 0;
        for (int rep = 0; rep < 3; rep++)
        {
            k.Run(w, h, -0.5, 0.0, 3.0 / w, 256, 4.0, it, sm, zr, zi, dr, di);
            if (k.LastReadbackMs < best) { best = k.LastReadbackMs; dispatch = k.LastDispatchMs; }
        }
        // Before #1173-L the GT 710 read back in ~780 ms against a ~22 ms dispatch; cached
        // memory reads ~58 MB in ~5 ms. A generous bound that only the uncached regression trips.
        Assert.True(best < 200.0, $"1080p readback {best:F1} ms (dispatch {dispatch:F1} ms) — uncached host memory?");
    }
}
