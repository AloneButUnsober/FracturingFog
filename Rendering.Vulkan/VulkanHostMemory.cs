// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VulkanHostMemory.cs — #1173-L / GPU parity plan G4.1: the memory type for the
// compute kernels' host-visible buffers.
//
// Every buffer used to take the FIRST HostVisible | HostCoherent type. On NVIDIA
// that is uncached, write-combined system memory: fine for buffers the CPU writes
// and the GPU reads (params, height fields, palettes), but CPU *reads* from it run
// uncached — reading back a 1080p Mandelbrot frame (iter + smooth + final-z, ~58 MB)
// took ~780 ms on a GT 710 while the dispatch itself took ~22 ms. That was the whole
// "Vulkan SP is ~20x slower than D3D11 SP" gap (889 vs 44 ms, #1173-L).
//
// Buffers the CPU reads back now prefer a HostVisible | HostCoherent | HostCached
// type (the GPU's writes are snooped into the CPU cache; reads run at memory speed),
// falling back to the plain coherent type where the device has no cached one.
// Upload buffers keep the write-combined type.
//
// #1228 — on a discrete card those host types are system RAM, so a shader that
// reads or writes a buffer in them does it over PCIe. FindDeviceLocalType picks
// the VRAM type for a device-local buffer that a host-visible staging twin feeds
// or drains with vkCmdCopyBuffer (VulkanComputeKernel's staged buffers;
// VulkanBufferPlacement says which those are).

using System;

using Silk.NET.Vulkan;

namespace FracturingFog.Rendering.Vulkan;

/// <summary>#1228 — where <see cref="VulkanComputeKernel"/> keeps its buffers.</summary>
public enum VulkanBufferPlacement
{
    /// <summary>Shader inputs (orbit, SA / BLA) in VRAM; the frame outputs and the
    /// Buddhabrot histogram in host-cached memory.</summary>
    Auto,
    /// <summary>Every buffer host-visible (the pre-#1228 layout).</summary>
    HostOnly,
    /// <summary><see cref="Auto"/> plus the outputs and histogram in VRAM, copied
    /// out after the dispatch — for judging the trade on fast hardware.</summary>
    DeviceOutputs,
}

public static unsafe class VulkanHostMemory
{
    /// <summary>#1228 — FF_VULKAN_MEMORY: <c>host</c> → <see cref="VulkanBufferPlacement.HostOnly"/>,
    /// <c>device-outputs</c> → <see cref="VulkanBufferPlacement.DeviceOutputs"/>,
    /// anything else → <see cref="VulkanBufferPlacement.Auto"/>.</summary>
    public static VulkanBufferPlacement PlacementFromEnvironment() =>
        ParsePlacement(Environment.GetEnvironmentVariable("FF_VULKAN_MEMORY"));

    public static VulkanBufferPlacement ParsePlacement(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "host" => VulkanBufferPlacement.HostOnly,
        "device-outputs" => VulkanBufferPlacement.DeviceOutputs,
        _ => VulkanBufferPlacement.Auto,
    };

    /// <summary>The host-visible, coherent memory type for a buffer whose
    /// <c>MemoryTypeBits</c> are <paramref name="typeBits"/>: host-cached when
    /// <paramref name="readback"/> and the device offers one, else the first coherent type.</summary>
    public static uint FindType(Vk vk, PhysicalDevice physicalDevice, uint typeBits, bool readback)
    {
        const MemoryPropertyFlags coherent = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        PhysicalDeviceMemoryProperties memProps;
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, &memProps);
        var types = (MemoryType*)&memProps.MemoryTypes;
        if (readback)
        {
            const MemoryPropertyFlags cached = coherent | MemoryPropertyFlags.HostCachedBit;
            for (uint i = 0; i < memProps.MemoryTypeCount; i++)
                if ((typeBits & (1u << (int)i)) != 0 && (types[i].PropertyFlags & cached) == cached)
                    return i;
        }
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 && (types[i].PropertyFlags & coherent) == coherent)
                return i;
        throw new InvalidOperationException($"no memory type with {coherent} for typeBits 0x{typeBits:X}");
    }

    /// <summary>#1228 — a device-local memory type for a buffer whose
    /// <c>MemoryTypeBits</c> are <paramref name="typeBits"/>, preferring one that
    /// is not host-visible (the small BAR window is left alone), or null when the
    /// device offers none.</summary>
    public static uint? FindDeviceLocalType(Vk vk, PhysicalDevice physicalDevice, uint typeBits)
    {
        PhysicalDeviceMemoryProperties memProps;
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, &memProps);
        var types = (MemoryType*)&memProps.MemoryTypes;
        uint? any = null;
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0 || (types[i].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) == 0) continue;
            if ((types[i].PropertyFlags & MemoryPropertyFlags.HostVisibleBit) == 0) return i;
            any ??= i;
        }
        return any;
    }

    /// <summary>True when memory type <paramref name="memType"/> lives on a
    /// device-local heap (VRAM on a discrete card; all memory on a unified one).</summary>
    public static bool IsOnDeviceLocalHeap(Vk vk, PhysicalDevice physicalDevice, uint memType)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties props);
        uint heap = props.MemoryTypes[(int)memType].HeapIndex;
        return (props.MemoryHeaps[(int)heap].Flags & MemoryHeapFlags.DeviceLocalBit) != 0;
    }
}
