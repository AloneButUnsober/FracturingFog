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
        bool colorProbe = Array.Exists(args, a => string.Equals(a, "--colorprobe", StringComparison.OrdinalIgnoreCase));
        bool colorRegen = Array.Exists(args, a => string.Equals(a, "regen", StringComparison.OrdinalIgnoreCase));
        bool renderProbe = Array.Exists(args, a => string.Equals(a, "--vulkanrenderprobe", StringComparison.OrdinalIgnoreCase));
        bool pturbProbe = Array.Exists(args, a => string.Equals(a, "--vulkanpturbprobe", StringComparison.OrdinalIgnoreCase));

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

            if (colorProbe)
                return RunColorProbe(ctx, colorRegen);

            if (renderProbe)
                return RealKernelRenderProbe.Run(ctx);

            if (pturbProbe)
                return PerturbSpikeProbe.Run(ctx);

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

    // V2 (#41): --colorprobe. Run the colour-emitting kernel (Greyscale theme)
    // on Vulkan and gate the packed BGRA against a deterministic CPU mirror.
    //
    // Two-part gate (see RealKernelColorProbe header):
    //   1. embedded golden digest of the CPU mirror colour (reference stability);
    //   2. GPU-vs-mirror byte-disagreement band (cross-vendor robustness).
    // `--colorprobe regen` prints the freshly computed CPU-mirror digest to paste
    // into GoldenColorDigest when the view/theme/pack intentionally changes.
    //
    // A pixel "agrees" when all three RGB channels are within ColorTol and the
    // alpha byte is exactly 0xFF. Set-boundary pixels flip black<->grey between
    // the GPU iter and the CPU-mirror iter, so a small fraction legitimately
    // disagrees — the band bounds that fraction, not any per-pixel max.
    private const int ColorTol = 1;                 // ±1 per channel absorbs rounding-at-noise
    private const double MaxColorDisagreeFrac = 0.02; // <=2% of pixels may disagree

    // Golden digest of the DETERMINISTIC CPU-mirror colour for the fixed probe
    // view + Greyscale theme. Regenerate deliberately with `--colorprobe regen`
    // only when a colour change is intended and reviewed. Empty => not pinned
    // (gate fails and tells you to regen).
    private const string GoldenColorDigest =
        "4e725df95c7e776418f31ad29e456c66b24f61a2a9c12b0ac78ddff6ae0df111";

    private static int RunColorProbe(VulkanContext ctx, bool regen)
    {
        var view = new RealKernelProbe.View();

        uint[] cpuColor = RealKernelColorProbe.CpuReferenceColor(view);
        string cpuDigest = RealKernelColorProbe.Digest(cpuColor);

        if (regen)
        {
            Console.WriteLine("colorprobe REGEN — paste into Program.GoldenColorDigest:");
            Console.WriteLine($"    \"{cpuDigest}\"");
            // Still run the GPU pass so a regen also surfaces obvious breakage.
        }

        RealKernelColorProbe.RunVulkan(ctx, view, RealKernelColorProbe.GreyBody, out uint[] gpuColor);

        int n = view.Width * view.Height;
        int disagree = 0, alphaBad = 0, distinctGpu;
        var seen = new System.Collections.Generic.HashSet<uint>();
        for (int i = 0; i < n; i++)
        {
            uint g = gpuColor[i], c = cpuColor[i];
            seen.Add(g);

            if ((g >> 24) != 0xFF) { alphaBad++; disagree++; continue; }

            int gr = (int)((g >> 16) & 0xFF), gg = (int)((g >> 8) & 0xFF), gb = (int)(g & 0xFF);
            int cr = (int)((c >> 16) & 0xFF), cg = (int)((c >> 8) & 0xFF), cb = (int)(c & 0xFF);
            if (Math.Abs(gr - cr) > ColorTol || Math.Abs(gg - cg) > ColorTol || Math.Abs(gb - cb) > ColorTol)
                disagree++;
        }
        distinctGpu = seen.Count;

        double disagreeFrac = (double)disagree / n;
        Console.WriteLine(
            $"colorprobe {view.Width}x{view.Height} theme=Greyscale kind=color(real): " +
            $"distinct(gpu)={distinctGpu} alphaBad={alphaBad} " +
            $"disagree={disagree}/{n} ({disagreeFrac:P3}, tol=±{ColorTol}); " +
            $"cpu-mirror digest={cpuDigest}");

        if (regen) return 0;

        // Reference-stability check: the deterministic CPU mirror must match the
        // pinned golden. Guards against a silent view/theme/pack change.
        if (string.IsNullOrEmpty(GoldenColorDigest))
        {
            Console.Error.WriteLine("colorprobe FAIL: GoldenColorDigest not pinned — run `--colorprobe regen` and paste the digest.");
            return 1;
        }
        if (!string.Equals(GoldenColorDigest, cpuDigest, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("colorprobe FAIL: CPU-mirror colour drifted from golden (reference changed).");
            Console.Error.WriteLine($"  expected {GoldenColorDigest}");
            Console.Error.WriteLine($"  actual   {cpuDigest}");
            return 1;
        }

        // Degenerate guard: a solid frame means the colour kernel did not really
        // run the view (e.g. gColor unbound -> all zeros, or pack collapsed).
        if (distinctGpu < 3)
        {
            Console.Error.WriteLine($"colorprobe FAIL: degenerate GPU colour (only {distinctGpu} distinct values).");
            return 1;
        }

        if (alphaBad > 0)
        {
            Console.Error.WriteLine($"colorprobe FAIL: {alphaBad} pixels without opaque alpha (pack lost 0xFF top byte).");
            return 1;
        }

        if (disagreeFrac > MaxColorDisagreeFrac)
        {
            Console.Error.WriteLine(
                $"colorprobe FAIL: outside band (disagree<={MaxColorDisagreeFrac:P0}). " +
                "Likely a real EvalPalette / pack / binding regression.");
            return 1;
        }

        Console.WriteLine($"colorprobe OK: {ctx.PickedType} {ctx.PickedName}");
        return 0;
    }
}
