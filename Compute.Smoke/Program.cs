// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// compute-smoke entry point.
//
// Phase X.5 / Slice 5.1 — minimal ILGPU device probe. Mirrors the role of
// Rendering.Silk.Smoke for the GPU compute path:
//   1. Spin up Context.Create(b => b.Default()).
//   2. Enumerate Devices and log every one (kind + name).
//   3. Pick the preferred non-CPU device when available; fall back to CPU.
//   4. Load a 64×64 Mandelbrot kernel, run it, synchronise.
//   5. Sanity-check the result has a non-trivial iteration histogram
//      (at least three distinct iter counts and at least one in-set sample).
//
// Exit codes:
//   0 — kernel ran, histogram looks correct.
//   1 — kernel ran but the histogram is degenerate (all zero / all max).
//   2 — exception thrown during init / kernel launch.

using System;
using System.Collections.Generic;
using System.Linq;
using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Compute.Smoke;

internal static class Program
{
    private const int Width = 64;
    private const int Height = 64;
    private const int MaxIter = 256;

    private static int Main(string[] args)
    {
        bool listOnly = Array.Exists(args, a => string.Equals(a, "--list", StringComparison.OrdinalIgnoreCase));

        try
        {
            using var ctx = Context.Create(b => b.Default());

            var devices = ctx.Devices.ToList();
            Console.WriteLine($"compute-smoke devices ({devices.Count}):");
            foreach (var d in devices)
                Console.WriteLine($"  {d.AcceleratorType}  {d.Name}");

            if (listOnly) return 0;

            // Prefer a non-CPU accelerator when ILGPU advertises one. macOS
            // legs and any host without GPU drivers fall back to CPU; that is
            // expected for the smoke and not a failure.
            Device picked = devices.FirstOrDefault(d => d.AcceleratorType != AcceleratorType.CPU)
                            ?? ctx.GetPreferredDevice(preferCPU: true);
            Console.WriteLine($"compute-smoke picked: {picked.AcceleratorType}  {picked.Name}");

            using var accel = picked.CreateAccelerator(ctx);

            var kernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<int>, int, int, int>(
                MandelbrotKernel);

            using var buf = accel.Allocate1D<int>(Width * Height);
            kernel(buf.IntExtent, buf.View, Width, Height, MaxIter);
            accel.Synchronize();

            int[] iters = buf.GetAsArray1D();

            var histogram = new Dictionary<int, int>();
            foreach (int it in iters)
                histogram[it] = histogram.GetValueOrDefault(it) + 1;

            int distinct = histogram.Count;
            int inSet = histogram.GetValueOrDefault(MaxIter);
            int escapeMin = iters.Min();
            int escapeMax = iters.Max();

            Console.WriteLine(
                $"compute-smoke iters: distinct={distinct} in-set={inSet} " +
                $"range=[{escapeMin}..{escapeMax}]");

            // Healthy histogram: at least 3 distinct iter counts, at least one
            // pixel inside the set (iters == MaxIter), and at least one pixel
            // outside (escape < MaxIter).
            if (distinct < 3 || inSet == 0 || escapeMin >= MaxIter)
            {
                Console.Error.WriteLine(
                    "compute-smoke FAIL: degenerate histogram. " +
                    "Expected diverse iter counts; kernel likely did not execute correctly.");
                return 1;
            }

            Console.WriteLine($"compute-smoke OK: {picked.AcceleratorType}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"compute-smoke FAIL: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    // Per-pixel Mandelbrot escape-time kernel. Single-precision so it runs on
    // every accelerator type ILGPU exposes (some embedded CPU devices reject
    // double). 64×64 keeps the dispatch trivially small — the goal is "does
    // the kernel run without crashing", not perf.
    private static void MandelbrotKernel(Index1D index, ArrayView<int> iters,
                                         int width, int height, int maxIter)
    {
        int px = index % width;
        int py = index / width;

        float cx = (px / (float)width) * 3.5f - 2.5f;
        float cy = (py / (float)height) * 2.0f - 1.0f;

        float zx = 0f, zy = 0f;
        int i = 0;
        for (; i < maxIter; i++)
        {
            float zx2 = zx * zx;
            float zy2 = zy * zy;
            if (zx2 + zy2 > 4.0f) break;
            float xn = zx2 - zy2 + cx;
            float yn = 2f * zx * zy + cy;
            zx = xn;
            zy = yn;
        }
        iters[index] = i;
    }
}
