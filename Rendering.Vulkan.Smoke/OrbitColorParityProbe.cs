// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// F16 (#603) — --vulkanorbitprobe. On-device parity gate for GPU orbit-accumulator
// ColorGen themes (slice 1: shallow-escape kernel, exterior pixels).
//
// Unlike the hand-rolled --colorprobe, this drives the FULL production path both
// ways so the gate proves exactly what a user sees:
//   • CPU: MandelbrotCalculator with GpuKernel off → CalculateOrbitAware (the
//     scalar orbit path, double precision — the reference).
//   • GPU: the same calculator with UseGpuCompute + the real VulkanComputeKernel
//     → TryRunGpuOrbit → BuildColorOrbit kernel (float).
//
// Parity is tolerance-compared over EXTERIOR pixels only: GPU colours in-set via
// EvalPalette(isInSet=1) while the CPU exterior-orbit theme fills in-set with a
// flat InSetColor (interior-on-GPU is a later slice), so in-set pixels are
// excluded by construction. Float-vs-double accumulator noise + boundary escape
// flips mean a small fraction legitimately disagrees — the gate bounds the
// disagreement FRACTION at a per-channel tolerance, like the other kernel probes.
//
// Exit 0 = every theme within band; 1 = a theme drifted / degenerate.

using System;
using FracturingFog.Interefaces;         // GpuOrbitInputs, IGpuOrbitPalette
using FracturingFog.Models;              // InterpretedColorMap, MandelbrotCalculator
using FracturingFog.Rendering;           // IGpuKernel

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static class OrbitColorParityProbe
{
    private const int W = 192, H = 144, MaxIter = 400;
    private const double CenterX = -0.745, CenterY = 0.113, Zoom = 8.0;

    // Parity is gated two ways. MeanDiff (mean per-pixel max-channel delta over
    // the exterior) is the robust central measure — it stays ~1-3 for correct
    // float-vs-double orbit noise and blows up for a real kernel bug. The
    // disagree FRACTION (pixels past ±ColorTol) is a secondary gross-breakage
    // guard; it's looser than --colorprobe (±1 / 2%) because the orbit sums run
    // in float on the GPU vs double on the CPU AND a fract()-banded hue amplifies
    // the tail near band edges.
    private const int ColorTol = 8;
    private const double MaxDisagreeFrac = 0.06;
    private const double MaxMeanDiff = 4.0;

    private static readonly (string label, string dsl)[] Corpus =
    {
        // fract() on the hue so each theme always produces a gradient (a
        // saturate() that clips to a constant would trip the degenerate guard
        // even at perfect parity).
        ("trapMin",     "return hsv(fract(trapMin*4.0), 0.9, 1.0);"),
        ("stripeAvg",   "return hsv(fract(stripeAvg), 0.9, 1.0);"),
        ("trapHexagon", "return hsv(fract(trapHexagon*3.0), 0.9, 1.0);"),
        ("curv+lyap",   "return hsv(fract(curvature*3.0 + lyapunov*0.1), 0.85, 1.0);"),
        ("gauss+exp",   "return hsv(fract(gaussian*1.5 + expSmooth), 0.85, 1.0);"),
    };

    public static int Run(VulkanContext ctx)
    {
        bool prevEnabled = InterpretedOrbitColorMap.GpuEnabled;
        InterpretedOrbitColorMap.GpuEnabled = true;
        try
        {
            using var kernel = new VulkanComputeKernel(ctx);
            bool allOk = true;
            foreach (var (label, dsl) in Corpus)
                allOk &= RunTheme(kernel, label, dsl);

            Console.WriteLine(allOk
                ? $"vulkanorbitprobe OK: {ctx.PickedType} {ctx.PickedName}"
                : "vulkanorbitprobe FAIL: one or more themes outside band.");
            return allOk ? 0 : 1;
        }
        finally { InterpretedOrbitColorMap.GpuEnabled = prevEnabled; }
    }

    private static bool RunTheme(IGpuKernel kernel, string label, string dsl)
    {
        var opts = new FracturingFog.ColorGen.GenerateOptions { ThemeName = label, Category = "Probe" };
        var map = InterpretedColorMap.TryCreate(dsl, opts, out string? err);
        if (map is not IGpuOrbitPalette orbit || orbit.OrbitInputs == GpuOrbitInputs.None)
        {
            Console.Error.WriteLine($"vulkanorbitprobe FAIL [{label}]: not orbit-aware (GpuEnabled honoured?) err={err}");
            return false;
        }

        // Compile the orbit kernel on-device up front so a compile failure is a
        // clear FAIL rather than a silent CPU fallback inside the calculator.
        kernel.SetPalette((IGpuHlslPalette)map);
        if (!kernel.HasGpuPalette)
        {
            Console.Error.WriteLine($"vulkanorbitprobe FAIL [{label}]: orbit kernel did not compile on-device.");
            return false;
        }

        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = CenterX, CenterY = CenterY, Zoom = Zoom,
            MaxIterations = MaxIter, ColorMap = map,
        };

        // CPU reference (double-precision orbit path).
        calc.UseGpuCompute = false; calc.GpuKernel = null;
        calc.Calculate();
        uint[] cpu = (uint[])calc.ColorBuffer.Clone();
        int[] cpuIter = (int[])calc.IterationBuffer.Clone();

        // GPU (production TryRunGpuOrbit path).
        calc.UseGpuCompute = true; calc.GpuKernel = kernel;
        calc.Calculate();
        uint[] gpu = (uint[])calc.ColorBuffer.Clone();

        int n = W * H;
        int exterior = 0, disagree = 0, alphaBad = 0, maxDiff = 0;
        long sumDiff = 0;
        var seen = new System.Collections.Generic.HashSet<uint>();
        for (int i = 0; i < n; i++)
        {
            if (cpuIter[i] >= MaxIter) continue;   // in-set — excluded (slice-1 scope)
            exterior++;
            uint g = gpu[i], c = cpu[i];
            seen.Add(g);
            if ((g >> 24) != 0xFF) { alphaBad++; disagree++; continue; }
            int d = MaxChannelDiff(g, c);
            maxDiff = Math.Max(maxDiff, d);
            sumDiff += d;
            if (d > ColorTol) disagree++;
        }

        double frac = exterior > 0 ? (double)disagree / exterior : 1.0;
        double mean = exterior > 0 ? (double)sumDiff / exterior : 0.0;
        Console.WriteLine(
            $"vulkanorbitprobe [{label}] {W}x{H} maxIter={MaxIter} mask={orbit.OrbitInputs}: " +
            $"exterior={exterior} distinct={seen.Count} alphaBad={alphaBad} " +
            $"disagree={disagree}/{exterior} ({frac:P3}, tol=±{ColorTol}) maxDiff={maxDiff} meanDiff={mean:F2}");

        if (exterior < 100 || seen.Count < 3)
        {
            Console.Error.WriteLine($"vulkanorbitprobe FAIL [{label}]: degenerate frame (exterior={exterior}, distinct={seen.Count}).");
            return false;
        }
        if (alphaBad > 0)
        {
            Console.Error.WriteLine($"vulkanorbitprobe FAIL [{label}]: {alphaBad} exterior pixels without opaque alpha.");
            return false;
        }
        if (mean > MaxMeanDiff)
        {
            Console.Error.WriteLine($"vulkanorbitprobe FAIL [{label}]: meanDiff {mean:F2} > {MaxMeanDiff:F1} (real GPU-vs-CPU orbit drift).");
            return false;
        }
        if (frac > MaxDisagreeFrac)
        {
            Console.Error.WriteLine($"vulkanorbitprobe FAIL [{label}]: outside band (disagree<={MaxDisagreeFrac:P0}).");
            return false;
        }
        return true;
    }

    private static int MaxChannelDiff(uint a, uint b)
    {
        int ar = (int)((a >> 16) & 0xFF), ag = (int)((a >> 8) & 0xFF), ab = (int)(a & 0xFF);
        int br = (int)((b >> 16) & 0xFF), bg = (int)((b >> 8) & 0xFF), bb = (int)(b & 0xFF);
        return Math.Max(Math.Abs(ar - br), Math.Max(Math.Abs(ag - bg), Math.Abs(ab - bb)));
    }
}
