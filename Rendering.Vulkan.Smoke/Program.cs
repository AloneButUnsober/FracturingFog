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
        bool vulkanProbe = Array.Exists(args, a => string.Equals(a, "--vulkanprobe", StringComparison.OrdinalIgnoreCase));

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

            if (vulkanProbe)
                return RunVulkanProbe(ctx);

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

    // V1 (#40): --vulkanprobe. Run the real Mandelbrot base kernel on Vulkan and
    // the C# reference over a fixed view, compare iter/smooth.
    //
    // A pixel "agrees" when iter is exactly equal AND (if escaped) smooth is
    // within SmoothTol. Set-boundary pixels are chaotic — a 1-ULP FMA /
    // transcendental spread between the GPU and the CPU mirror explodes the
    // escape iteration there — so a *small fraction* of pixels legitimately
    // disagree by a lot. The gate therefore bounds the DISAGREEMENT FRACTION
    // (robust to boundary chaos) plus the in-set count drift, not any per-pixel
    // max. A broken kernel (wrong bindings/endianness/math) disagrees on a huge
    // fraction and trips this immediately; correct cross-vendor float noise sits
    // at a few hundredths of a percent. See dev-plan §V1.
    private const float SmoothTol = 0.05f;
    private const double MaxDisagreeFrac = 0.01;   // ≤1% of pixels may disagree
    private const double MaxInSetDriftFrac = 0.01; // |inSetGpu-inSetCpu|/n ≤ 1%

    private static int RunVulkanProbe(VulkanContext ctx)
    {
        var view = new RealKernelProbe.View();
        RealKernelProbe.RunVulkan(ctx, view, out uint[] gIter, out float[] gSmooth);
        RealKernelProbe.CpuReference(view, out uint[] cIter, out float[] cSmooth);

        int n = view.Width * view.Height;
        uint maxIter = (uint)view.MaxIter;

        int disagree = 0, iterDiff = 0, smoothDiff = 0, inSetGpu = 0, inSetCpu = 0;
        for (int i = 0; i < n; i++)
        {
            if (gIter[i] == maxIter) inSetGpu++;
            if (cIter[i] == maxIter) inSetCpu++;

            bool ok;
            if (gIter[i] != cIter[i]) { iterDiff++; ok = false; }
            else if (gIter[i] < maxIter && MathF.Abs(gSmooth[i] - cSmooth[i]) > SmoothTol) { smoothDiff++; ok = false; }
            else ok = true;
            if (!ok) disagree++;
        }

        double disagreeFrac = (double)disagree / n;
        double inSetDriftFrac = Math.Abs(inSetGpu - inSetCpu) / (double)n;
        Console.WriteLine(
            $"vulkanprobe {view.Width}x{view.Height} maxIter={maxIter} kernel=base(real): " +
            $"in-set gpu={inSetGpu} cpu={inSetCpu} (drift {inSetDriftFrac:P3}); " +
            $"disagree={disagree}/{n} ({disagreeFrac:P3}) [iter={iterDiff} smooth={smoothDiff}]");

        // Degenerate guard: an all-in-set or all-escaped frame means the kernel
        // did not really run the view (e.g. bindings unbound → zeros).
        if (inSetGpu == 0 || inSetGpu == n)
        {
            Console.Error.WriteLine("vulkanprobe FAIL: degenerate GPU frame (all in-set or all escaped).");
            return 1;
        }

        if (disagreeFrac > MaxDisagreeFrac || inSetDriftFrac > MaxInSetDriftFrac)
        {
            Console.Error.WriteLine(
                $"vulkanprobe FAIL: outside band (disagree≤{MaxDisagreeFrac:P0}, " +
                $"in-set drift≤{MaxInSetDriftFrac:P0}). Likely a real kernel/binding regression.");
            return 1;
        }

        Console.WriteLine($"vulkanprobe OK: {ctx.PickedType} {ctx.PickedName}");
        return 0;
    }
}
