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
using System.Collections.Generic;

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

            // DXC-compile the trivial kernel, dispatch 64x64, map back.
            uint[] pixels = ComputeSmoke.Run(ctx);

            // Sanity, mirroring Compute.Smoke's histogram check but for the
            // colour path: a real gradient must have many distinct packed
            // values, and the four corners (uv exactly 0/1 -> no rounding
            // ambiguity) must be bit-exact against the CPU pack.
            var distinctValues = new HashSet<uint>(pixels);
            int distinct = distinctValues.Count;

            (int x, int y)[] corners =
            {
                (0, 0),
                (ComputeSmoke.Width - 1, 0),
                (0, ComputeSmoke.Height - 1),
                (ComputeSmoke.Width - 1, ComputeSmoke.Height - 1),
            };

            bool cornersOk = true;
            foreach (var (cx, cy) in corners)
            {
                uint got = pixels[cy * ComputeSmoke.Width + cx];
                uint want = ComputeSmoke.ExpectedAt(cx, cy);
                if (got != want)
                {
                    Console.Error.WriteLine(
                        $"  corner ({cx},{cy}) got 0x{got:X8} want 0x{want:X8}");
                    cornersOk = false;
                }
            }

            Console.WriteLine(
                $"vulkan-smoke pixels: distinct={distinct} corners={(cornersOk ? "ok" : "MISMATCH")}");

            if (distinct < 3 || !cornersOk)
            {
                Console.Error.WriteLine(
                    "vulkan-smoke FAIL: degenerate read-back. Expected a diverse " +
                    "gradient with bit-exact corners; kernel likely did not run correctly.");
                return 1;
            }

            Console.WriteLine($"vulkan-smoke OK: {ctx.PickedType} {ctx.PickedName}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vulkan-smoke FAIL: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }
}
