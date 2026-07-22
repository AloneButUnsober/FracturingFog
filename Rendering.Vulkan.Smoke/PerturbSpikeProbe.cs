// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V6 (#82): --vulkanpturbprobe. Golden gate for the GPU perturbation kernel
// (deep-zoom on GPU). See Docs/Deep-Zoom-Perturbation.md §2 and
// Docs/Technical/Vulkan-Compute-DevelopmentPlan.md §13/§14.
//
// Drives the PRODUCTION kernel object — VulkanComputeKernel.RunPerturb, whose
// HLSL is MandelbrotKernelSource.BuildPerturb() (the same source FXC compiles
// for D3D) — exactly as the calculator will, and checks it against a
// self-contained C# mirror of MandelbrotCalculator.ComputePixelPTRebased (the
// default double δ-rebased path). It answers the #82 spike questions:
//
//   (1) LOOP PARITY. GPU double δ loop vs the CPU mirror, pixel-for-pixel.
//   (2) dc PRECISION AT DEPTH. Double dc vs a double-double dc/δ oracle (local
//       TwoProduct/TwoSum — no FMA, per the ILGPU-ICE caution) → is single
//       `double` dc enough here?
//   (3) DXC COMPILES THE DOUBLE MATH. A green run proves DXC -spirv emits the
//       Float64 capability and the driver runs it. (FXC cs_5_0 is checked
//       separately — it is D3D/Windows-only and not wired into this smoke.)
//   (4) CONSUMER-GPU FP64 VIABILITY. No shaderFloat64 → SupportsPerturbation is
//       false → SKIP (exit 0); that absence is itself a finding.
//
// Deep view without many-digit centre arithmetic: centre = the seahorse-valley
// boundary point below, whose orbit ranges up to |Z|≈2 so the amplification
// ∏|2·Zₙ| is large and the tiny dc is lifted back to O(1) detail — a
// non-degenerate frame that exercises the rebase + escape branches. (The
// parabolic root (-0.75,0) fails: its orbit stays small, amplification ≈1, the
// deep neighbourhood is uniformly interior.) A genuine deep boundary point
// beyond double's reach is the full build's OD-centre job, not the gate's.

using System;
using FracturingFog.Rendering.Vulkan;    // VulkanComputeKernel, VulkanContext

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static class PerturbSpikeProbe
{
    // Same escape radius the CPU perturbation path uses (512² for smooth shading).
    private const double EscapeR2 = 512.0 * 512.0;

    private const int Dim = 96;
    private const int MaxIter = 6000;
    private const double Zoom = 1e6;
    private const double CenterX = -0.743643887037151, CenterY = 0.13182590420533;

    public static int Run(VulkanContext ctx)
    {
        using var kernel = new VulkanComputeKernel(ctx);
        if (!kernel.SupportsPerturbation)
        {
            Console.WriteLine(
                $"vulkanpturbprobe SKIP: {ctx.PickedType} {ctx.PickedName} has no " +
                "shaderFloat64. The double GPU perturbation kernel cannot run here " +
                "(finding for #82 checkbox 4 — needs an FP64-capable device).");
            return 0;
        }

        double scale = 3.5 / (Math.Max(Dim, Dim) * Zoom);
        double offX0 = -0.5 * Dim, offY0 = -0.5 * Dim;

        // Reference orbit at the (exact) centre, in double. Bounded/escapes as
        // the location dictates; refLen == escape iteration (or MaxIter).
        BuildReferenceOrbit(CenterX, CenterY, MaxIter, out double[] refZr, out double[] refZi, out int refLen);

        // CPU mirrors: default double path (GPU-matched) + DD oracle (checkbox 2).
        int[] cpuDouble = CpuMirrorDouble(scale, offX0, offY0, refZr, refZi, refLen);
        int[] cpuDd = CpuMirrorDd(scale, offX0, offY0, refZr, refZi, refLen);

        // GPU: the production kernel object, exactly as the calculator drives it.
        int n = Dim * Dim;
        var gpu = new int[n];
        var smooth = new float[n];
        var fzr = new float[n]; var fzi = new float[n];
        var fdr = new float[n]; var fdi = new float[n];
        kernel.RunPerturb(Dim, Dim, scale, MaxIter, EscapeR2, offX0, offY0,
            refZr, refZi, refLen, gpu, smooth, fzr, fzi, fdr, fdi);

        // Non-degeneracy: a deep frame on an amplifying centre must show structure.
        var distinct = new System.Collections.Generic.HashSet<int>(cpuDouble);
        int inSet = 0; for (int i = 0; i < n; i++) if (cpuDouble[i] >= MaxIter) inSet++;

        int gpuDisagree = 0, maxIterDelta = 0;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Abs(gpu[i] - cpuDouble[i]);
            if (d != 0) { gpuDisagree++; if (d > maxIterDelta) maxIterDelta = d; }
        }
        double gpuFrac = (double)gpuDisagree / n;

        int ddDisagree = 0, ddMaxDelta = 0;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Abs(cpuDouble[i] - cpuDd[i]);
            if (d != 0) { ddDisagree++; if (d > ddMaxDelta) ddMaxDelta = d; }
        }
        double ddFrac = (double)ddDisagree / n;

        Console.WriteLine(
            $"vulkanpturbprobe {Dim}x{Dim} zoom={Zoom:0e+0} maxIter={MaxIter} refLen={refLen} " +
            $"scale={scale:0.000e+00} dc~{Math.Abs(0.5 * Dim * scale):0.0e+00}");
        Console.WriteLine(
            $"  non-degeneracy: distinct={distinct.Count} inSet={inSet}/{n}");
        // maxΔiter is large on filament pixels straddling the escape-time knife-
        // edge — a sub-ULP rounding difference flips them by many iters (doc §2:
        // the QD path disagrees with ITSELF by the same order). So the metric is
        // the disagreement FRACTION vs the CPU precision NOISE FLOOR (double-vs-
        // DD), not maxΔ — same ULP-band philosophy as --vulkanprobe.
        Console.WriteLine(
            $"  (1) GPU vs CPU-double:      disagree={gpuDisagree}/{n} ({gpuFrac:P3}) maxΔiter={maxIterDelta} (boundary chaos)");
        Console.WriteLine(
            $"  (2) CPU-double vs DD oracle: disagree={ddDisagree}/{n} ({ddFrac:P3}) maxΔiter={ddMaxDelta} " +
            $"→ single-double dc {(ddFrac <= 0.02 ? "SUFFICES" : "INSUFFICIENT")} at this depth (δ noise floor)");
        bool atFloor = gpuFrac <= Math.Max(0.02, 4.0 * ddFrac);
        Console.WriteLine(
            $"  GPU disagreement {(atFloor ? "AT" : "ABOVE")} the CPU precision noise floor " +
            $"→ {(atFloor ? "no GPU dialect gap" : "GPU-SPECIFIC divergence")}");

        bool nonDegenerate = distinct.Count >= 8 && inSet < n;
        bool parity = atFloor;

        if (!nonDegenerate)
        {
            Console.Error.WriteLine(
                "vulkanpturbprobe FAIL: degenerate frame (dc underflowed or centre " +
                "orbit amplification too low) — the parity check would be vacuous.");
            return 1;
        }
        if (!parity)
        {
            Console.Error.WriteLine(
                "vulkanpturbprobe FAIL: GPU double perturbation loop diverged from the " +
                "CPU mirror ABOVE the precision noise floor — a real dialect/precision gap.");
            return 1;
        }

        Console.WriteLine($"vulkanpturbprobe OK: {ctx.PickedType} {ctx.PickedName}");
        return 0;
    }

    // ── Reference orbit (double, exact centre) ────────────────────────────────
    private static void BuildReferenceOrbit(
        double cr, double ci, int maxIter, out double[] zr, out double[] zi, out int refLen)
    {
        zr = new double[maxIter];
        zi = new double[maxIter];
        double x = 0.0, y = 0.0;
        double ampLog10 = 0.0;   // Σ log₁₀|2·Zₙ| — the detail-depth amplification (doc §3)
        int n = 0;
        for (; n < maxIter; n++)
        {
            zr[n] = x; zi[n] = y;
            double x2 = x * x, y2 = y * y;
            double mag2 = x2 + y2;
            if (mag2 >= EscapeR2) { n++; break; }
            if (mag2 > 0) ampLog10 += 0.5 * Math.Log10(4.0 * mag2); // log10|2Z| = ½log10(4|Z|²)
            double xy = x * y;
            x = x2 - y2 + cr;
            y = xy + xy + ci;
        }
        refLen = n;
        Console.WriteLine($"  ref orbit: len={n} escaped={(n < maxIter)} amplification≈1e{ampLog10:0.0} (maxUseful proxy)");
    }

    // ── CPU mirror: exact double twin of ComputePixelPTRebased (iteration count
    // only). Keep in sync with MandelbrotCalculator.cs / BuildPerturb HLSL. ────
    private static int[] CpuMirrorDouble(double scale, double offX0, double offY0, double[] refZr, double[] refZi, int refLen)
    {
        int[] outIter = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
        for (int px = 0; px < Dim; px++)
        {
            double dcR = (offX0 + px) * scale;
            double dcI = (offY0 + py) * scale;

            double dr = 0.0, di = 0.0;
            int m = 0;
            double zr = 0.0, zi = 0.0;
            int iter;
            for (iter = 0; iter < MaxIter; iter++)
            {
                double Zr = refZr[m], Zi = refZi[m];
                zr = Zr + dr; zi = Zi + di;
                double zmag2 = zr * zr + zi * zi;
                if (zmag2 >= EscapeR2) break;
                double dmag2 = dr * dr + di * di;
                if (zmag2 < dmag2 || m + 1 >= refLen) { dr = zr; di = zi; Zr = 0.0; Zi = 0.0; m = 0; }
                double a = 2.0 * Zr + dr, b = 2.0 * Zi + di;
                double newDr = a * dr - b * di + dcR;
                double newDi = a * di + b * dr + dcI;
                dr = newDr; di = newDi; m++;
            }
            outIter[py * Dim + px] = iter;
        }
        return outIter;
    }

    // ── DD oracle: same loop with double-double dc + δ (ref stays Hi-only, per
    // the default path). Answers "is single-double dc enough at this depth?" ──
    private static int[] CpuMirrorDd(double scale, double offX0, double offY0, double[] refZr, double[] refZi, int refLen)
    {
        int[] outIter = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
        for (int px = 0; px < Dim; px++)
        {
            Dd dcR = Dd.FromProduct(offX0 + px, scale);
            Dd dcI = Dd.FromProduct(offY0 + py, scale);

            Dd dr = default, di = default;
            int m = 0;
            int iter;
            for (iter = 0; iter < MaxIter; iter++)
            {
                Dd Zr = new Dd(refZr[m]), Zi = new Dd(refZi[m]);
                Dd zr = Zr + dr, zi = Zi + di;
                double zmag2 = zr.Hi * zr.Hi + zi.Hi * zi.Hi;
                if (zmag2 >= EscapeR2) break;
                double dmag2 = dr.Hi * dr.Hi + di.Hi * di.Hi;
                if (zmag2 < dmag2 || m + 1 >= refLen) { dr = zr; di = zi; Zr = default; Zi = default; m = 0; }
                Dd a = Zr * 2.0 + dr, b = Zi * 2.0 + di;
                Dd newDr = a * dr - b * di + dcR;
                Dd newDi = a * di + b * dr + dcI;
                dr = newDr; di = newDi; m++;
            }
            outIter[py * Dim + px] = iter;
        }
        return outIter;
    }

    // ── minimal double-double (no FMA — Dekker TwoProduct/TwoSum) ────────────
    private readonly struct Dd
    {
        public readonly double Hi, Lo;
        public Dd(double hi) { Hi = hi; Lo = 0.0; }
        private Dd(double hi, double lo) { Hi = hi; Lo = lo; }

        private static void TwoSum(double a, double b, out double s, out double e)
        {
            s = a + b;
            double bb = s - a;
            e = (a - (s - bb)) + (b - bb);
        }

        private static void Split(double a, out double hi, out double lo)
        {
            double t = 134217729.0 * a;   // 2^27 + 1
            hi = t - (t - a);
            lo = a - hi;
        }

        private static void TwoProd(double a, double b, out double p, out double e)
        {
            p = a * b;
            Split(a, out double ah, out double al);
            Split(b, out double bh, out double bl);
            e = ((ah * bh - p) + ah * bl + al * bh) + al * bl;
        }

        public static Dd FromProduct(double a, double b)
        {
            TwoProd(a, b, out double p, out double e);
            return new Dd(p, e);
        }

        public static Dd operator +(Dd a, Dd b)
        {
            TwoSum(a.Hi, b.Hi, out double s, out double e);
            e += a.Lo + b.Lo;
            TwoSum(s, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }

        public static Dd operator -(Dd a, Dd b) => a + new Dd(-b.Hi, -b.Lo);

        public static Dd operator *(Dd a, double b)
        {
            TwoProd(a.Hi, b, out double p, out double e);
            e += a.Lo * b;
            TwoSum(p, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }

        public static Dd operator *(Dd a, Dd b)
        {
            TwoProd(a.Hi, b.Hi, out double p, out double e);
            e += a.Hi * b.Lo + a.Lo * b.Hi;
            TwoSum(p, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }
    }
}
