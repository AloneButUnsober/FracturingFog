// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V3 (#42): --vulkanrenderprobe. Exercise the REAL IGpuKernel backend
// (VulkanComputeKernel) exactly as the calculator does — SetPalette + Run with
// the full per-pixel output-buffer set — and check parity against the same CPU
// mirrors V1/V2 use. Unlike --vulkanprobe / --colorprobe (which hand-roll their
// own one-shot dispatch), this drives the production kernel object and proves
// the bits the renderer relies on:
//   1. colour Run (GPU palette) parity vs the CPU-mirror colour;
//   2. base Run (no colour) iter/smooth parity;
//   3. buffer PERSISTENCE — a second identical Run returns byte-identical
//      output (no realloc, no descriptor-set staleness);
//   4. RESIZE — a Run at a different W×H re-allocates buffers and still matches
//      a CPU reference at the new size.
//
// Reference maths are the deterministic CPU mirrors in RealKernelProbe /
// RealKernelColorProbe; the theme is the real Engine GrayscalePalette (reached
// transitively through the Rendering.Vulkan -> Engine project reference), whose
// HLSL body is the twin of RealKernelColorProbe.PackGrey.

using System;
using FracturingFog.Models;              // GrayscalePalette
using FracturingFog.Rendering;           // FractalKind, IGpuKernel
using FracturingFog.Rendering.Vulkan;    // VulkanComputeKernel, VulkanContext
using Probe = FracturingFog.Rendering.Vulkan.Smoke.RealKernelProbe;

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static class RealKernelRenderProbe
{
    private const float SmoothTol = 0.05f;
    private const int ColorTol = 1;
    private const double MaxDisagreeFrac = 0.02;

    public static int Run(VulkanContext ctx)
    {
        using var kernel = new VulkanComputeKernel(ctx);
        bool ok = true;

        // ── 1 + 3: colour Run at the standard view, twice (persistence). ──────
        var viewA = new Probe.View();
        kernel.SetPalette(new GrayscalePalette());
        if (!kernel.HasGpuPalette)
        {
            Console.Error.WriteLine("vulkanrenderprobe FAIL: SetPalette(Greyscale) did not activate a GPU palette.");
            return 1;
        }

        var f1 = RunColorFrame(kernel, viewA);
        var f2 = RunColorFrame(kernel, viewA);

        ok &= ReportColor("frameA", viewA, f1.color, f1.iter, f1.smooth);

        // Persistence: a second identical dispatch must reproduce the first bit
        // for bit — same buffers, same params, deterministic device.
        bool identical = true;
        for (int i = 0; i < f1.color.Length && identical; i++)
            if (f1.color[i] != f2.color[i] || f1.iter[i] != f2.iter[i]) identical = false;
        Console.WriteLine($"vulkanrenderprobe persistence(frameA x2): {(identical ? "identical" : "DIFFER")}");
        if (!identical)
        {
            Console.Error.WriteLine("vulkanrenderprobe FAIL: repeat Run diverged (buffer/descriptor persistence bug).");
            ok = false;
        }

        // ── 4: resize to a different, non-square view. ────────────────────────
        var viewB = new Probe.View { Width = 96, Height = 160, MaxIter = 200, CenterX = -0.5, CenterY = 0.0 };
        viewB.Scale = 3.5 / Math.Max(viewB.Width, viewB.Height);
        var fB = RunColorFrame(kernel, viewB);
        ok &= ReportColor("frameB(resize)", viewB, fB.color, fB.iter, fB.smooth);

        // ── 2: base Run (no colour) iter/smooth parity. ───────────────────────
        kernel.SetPalette(null);
        var basef = RunBaseFrame(kernel, viewA);
        ok &= ReportBase("base(frameA)", viewA, basef.iter, basef.smooth);

        Console.WriteLine(ok
            ? $"vulkanrenderprobe OK: {ctx.PickedType} {ctx.PickedName}"
            : "vulkanrenderprobe FAIL: one or more checks outside band.");
        return ok ? 0 : 1;
    }

    private readonly record struct Frame(uint[] color, int[] iter, float[] smooth);

    private static Frame RunColorFrame(IGpuKernel kernel, Probe.View v)
    {
        int n = v.Width * v.Height;
        var (iter, smooth, zr, zi, dr, di) = Alloc(n);
        var color = new uint[n];
        kernel.Run(v.Width, v.Height, v.CenterX, v.CenterY, v.Scale, v.MaxIter, v.Bailout2,
            iter, smooth, zr, zi, dr, di, null, FractalKind.Mandelbrot, 0f, 0f, color);
        return new Frame(color, iter, smooth);
    }

    private static Frame RunBaseFrame(IGpuKernel kernel, Probe.View v)
    {
        int n = v.Width * v.Height;
        var (iter, smooth, zr, zi, dr, di) = Alloc(n);
        kernel.Run(v.Width, v.Height, v.CenterX, v.CenterY, v.Scale, v.MaxIter, v.Bailout2,
            iter, smooth, zr, zi, dr, di, null, FractalKind.Mandelbrot, 0f, 0f, null);
        return new Frame(Array.Empty<uint>(), iter, smooth);
    }

    private static (int[], float[], float[], float[], float[], float[]) Alloc(int n)
        => (new int[n], new float[n], new float[n], new float[n], new float[n], new float[n]);

    // Colour parity vs the CPU-mirror colour + iter/smooth vs the CPU reference.
    private static bool ReportColor(string label, Probe.View v, uint[] gpuColor, int[] gpuIter, float[] gpuSmooth)
    {
        uint[] cpuColor = RealKernelColorProbe.CpuReferenceColor(v);
        Probe.CpuReference(v, out uint[] cpuIter, out float[] cpuSmooth);
        int n = v.Width * v.Height;

        int colDisagree = 0, alphaBad = 0, iterDisagree = 0;
        var seen = new System.Collections.Generic.HashSet<uint>();
        for (int i = 0; i < n; i++)
        {
            uint g = gpuColor[i], c = cpuColor[i];
            seen.Add(g);
            if ((g >> 24) != 0xFF) alphaBad++;
            if (!ChannelsWithin(g, c, ColorTol)) colDisagree++;
            if (!IterAgree(gpuIter[i], (int)cpuIter[i], gpuSmooth[i], cpuSmooth[i], v.MaxIter)) iterDisagree++;
        }
        double colFrac = (double)colDisagree / n, iterFrac = (double)iterDisagree / n;
        Console.WriteLine(
            $"vulkanrenderprobe {label} {v.Width}x{v.Height} maxIter={v.MaxIter}: " +
            $"distinct={seen.Count} alphaBad={alphaBad} colour={colDisagree}/{n} ({colFrac:P3}) " +
            $"iter={iterDisagree}/{n} ({iterFrac:P3})");
        return seen.Count >= 3 && alphaBad == 0 && colFrac <= MaxDisagreeFrac && iterFrac <= MaxDisagreeFrac;
    }

    private static bool ReportBase(string label, Probe.View v, int[] gpuIter, float[] gpuSmooth)
    {
        Probe.CpuReference(v, out uint[] cpuIter, out float[] cpuSmooth);
        int n = v.Width * v.Height, disagree = 0, inSet = 0;
        for (int i = 0; i < n; i++)
        {
            if (gpuIter[i] == v.MaxIter) inSet++;
            if (!IterAgree(gpuIter[i], (int)cpuIter[i], gpuSmooth[i], cpuSmooth[i], v.MaxIter)) disagree++;
        }
        double frac = (double)disagree / n;
        Console.WriteLine($"vulkanrenderprobe {label} {v.Width}x{v.Height}: in-set={inSet} disagree={disagree}/{n} ({frac:P3})");
        return inSet > 0 && inSet < n && frac <= MaxDisagreeFrac;
    }

    private static bool ChannelsWithin(uint a, uint b, int tol)
    {
        int ar = (int)((a >> 16) & 0xFF), ag = (int)((a >> 8) & 0xFF), ab = (int)(a & 0xFF);
        int br = (int)((b >> 16) & 0xFF), bg = (int)((b >> 8) & 0xFF), bb = (int)(b & 0xFF);
        return Math.Abs(ar - br) <= tol && Math.Abs(ag - bg) <= tol && Math.Abs(ab - bb) <= tol;
    }

    private static bool IterAgree(int gi, int ci, float gs, float cs, int maxIter)
    {
        if (gi != ci) return false;
        if (gi < maxIter && MathF.Abs(gs - cs) > SmoothTol) return false;
        return true;
    }
}
