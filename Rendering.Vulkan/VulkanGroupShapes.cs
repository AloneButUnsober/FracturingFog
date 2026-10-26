// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering.Vulkan/VulkanGroupShapes.cs
//
// #1339 / #1377 — the Vulkan side of the thread-group shape tuner
// (GpuGroupShapes): the store's device key and the candidates a device can run,
// shared by every Vulkan kernel that tunes a slot (the 2D kernel, the relief
// raymarch, the froxel passes).

using System.Collections.Generic;
using FracturingFog.Rendering;

namespace FracturingFog.Rendering.Vulkan;

internal static class VulkanGroupShapes
{
    /// <summary>"vulkan|adapter|vendor:device|driver", the store's device key.</summary>
    public static string DeviceKey(VulkanContext ctx) =>
        $"vulkan|{ctx.PickedName}|{ctx.VendorId:X4}:{ctx.DeviceId:X4}|{ctx.DriverVersion:X8}";

    /// <summary>The candidates within the device's work-group limits.</summary>
    public static List<GpuGroupShape> Candidates(VulkanContext ctx)
    {
        var list = new List<GpuGroupShape>();
        foreach (var c in GpuGroupShapes.Candidates)
            if (c.Threads <= ctx.MaxComputeWorkGroupInvocations && c.X <= ctx.MaxComputeWorkGroupSizeX && c.Y <= ctx.MaxComputeWorkGroupSizeY)
                list.Add(c);
        return list;
    }
}
