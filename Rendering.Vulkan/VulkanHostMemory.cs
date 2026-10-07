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

using System;

using Silk.NET.Vulkan;

namespace FracturingFog.Rendering.Vulkan;

public static unsafe class VulkanHostMemory
{
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
}
