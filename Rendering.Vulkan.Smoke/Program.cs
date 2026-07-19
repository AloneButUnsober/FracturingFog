// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// vulkan-smoke entry point.
//
// V0 spike (issue #39) — headless HLSL->SPIR-V compute proof for Linux GPU
// rendering. Mirrors Compute.Smoke / Rendering.Silk.Smoke:
//   1. Vk instance, enumerate + log every physical device (--list).
//   2. Pick a compute-capable device, create device + compute queue.
//   3. DXC-compile a trivial HLSL CS (uv gradient -> packed BGRA) to SPIR-V,
//      dispatch 64x64, vkMapMemory read-back.        [added in phase 2]
//   4. Histogram sanity: >=3 distinct values + corner pixels bit-exact vs the
//      CPU pack. Exit 0/1/2.                          [added in phase 2]
//
// Exit codes (match Compute.Smoke):
//   0 — pipeline ran, read-back looks correct.
//   1 — ran but the read-back is degenerate (fails histogram sanity).
//   2 — exception during init / compile / dispatch.

using System;

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static class Program
{
    private static int Main(string[] args)
    {
        bool listOnly = Array.Exists(args, a => string.Equals(a, "--list", StringComparison.OrdinalIgnoreCase));

        try
        {
            using var ctx = VulkanContext.CreateInstance();

            var devices = ctx.EnumerateDevices();
            Console.WriteLine($"vulkan-smoke devices ({devices.Count}):");
            foreach (var d in devices)
                Console.WriteLine($"  {d.Type,-14} {(d.HasCompute ? "compute" : "no-compute")}  {d.Name}");

            if (listOnly) return 0;

            if (devices.Count == 0)
            {
                Console.Error.WriteLine(
                    "vulkan-smoke FAIL: no Vulkan devices. Install a loader/ICD " +
                    "(Mesa lavapipe for CPU-only CI).");
                return 2;
            }

            ctx.CreateComputeDevice();
            Console.WriteLine($"vulkan-smoke picked: {ctx.PickedType,-14} {ctx.PickedName}");

            // Phase 2 wires the DXC->SPIR-V dispatch + read-back + histogram
            // here and returns 0/1 accordingly.
            Console.WriteLine("vulkan-smoke OK: device + compute queue ready");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vulkan-smoke FAIL: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }
}
