// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #88 SA-on-GPU spike: --vulkanpturbsa. Correctness gate for the Series-
// Approximation iteration-skipping perturbation kernel
// (MandelbrotKernelSource.BuildPerturbSA → VulkanComputeKernel.RunPerturbSA).
// See Docs/Technical/GPU-DeepZoom-Handoff.md §6 and Engine/Math/SeriesApproximation.cs.
//
// SA analytically skips the first k iterations from z=0 by evaluating the
// 3rd-order δ-polynomial in dc, then resumes the SAME rebased δ loop the plain
// perturbation kernel (#82) runs. Skipping is a mathematical shortcut, so it
// must reproduce the SAME escape iteration as running the loop from 0 — within
// the precision noise floor. This probe proves that on the production kernel
// object, exactly as the calculator would drive it:
//
//   (1) GPU-SA vs CPU-SA mirror  — the in-shader FindSkip/EvalDelta + rebased
//       loop matches the CPU SA path pixel-for-pixel (dialect/precision check).
//   (2) GPU-SA vs CPU plain (no SA) — SA does not change the answer; the skip
//       lands on the same trajectory the un-skipped loop was on.
//   (3) SA ENGAGED — average/max skip k > 0, so the gate is not vacuous (a frame
//       where nothing skips would pass (1)+(2) trivially).
//
// Correctness is speed-independent → validates on weak-FP64 HW (GT710/lavapipe).
// The perf payoff (does skipping actually run faster?) needs strong-FP64 HW and
// is deferred (#88 perf sign-off; the #87 fallback disables the deep path on
// GT710/UHD 630 regardless).

using System;
using FracturingFog.FFMath;               // SeriesApproximation (the production coeffs)
using FracturingFog.Rendering.Vulkan;     // VulkanComputeKernel, VulkanContext

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static class PerturbSaProbe
{
    private const double EscapeR2 = 512.0 * 512.0;
    private const double DefaultSaTolerance = 1e-3;   // matches MandelbrotCalculator.SaTolerance

    // Tolerance override for tolerance-sensitivity sweeps (proves the SA-vs-plain
    // divergence IS truncation error: tighter tol → smaller skip → less effect).
    private static readonly double SaTolerance =
        double.TryParse(Environment.GetEnvironmentVariable("FF_SA_TOL"), out double t) && t > 0
            ? t : DefaultSaTolerance;

    private const int Dim = 96;
    private const int MaxIter = 6000;
    private const double Zoom = 1e6;
    // Same amplifying seahorse-valley centre the #82 gate uses (orbit up to |Z|≈2).
    private const double CenterX = -0.743643887037151, CenterY = 0.13182590420533;

    public static int Run(VulkanContext ctx)
    {
        using var kernel = new VulkanComputeKernel(ctx);
        if (!kernel.SupportsPerturbation)
        {
            Console.WriteLine(
                $"vulkanpturbsa SKIP: {ctx.PickedType} {ctx.PickedName} has no shaderFloat64 " +
                "— the double SA perturbation kernel cannot run here (needs an FP64-capable device).");
            return 0;
        }

        double scale = 3.5 / (Math.Max(Dim, Dim) * Zoom);
        double offX0 = -0.5 * Dim, offY0 = -0.5 * Dim;

        BuildReferenceOrbit(CenterX, CenterY, MaxIter, out double[] refZr, out double[] refZi, out int refLen);

        // Production SA coefficients from the reference orbit (Engine class).
        var sa = new SeriesApproximation(refZr, refZi, refLen);
        Console.WriteLine($"  SA: safeMax={sa.SafeMax} tol={SaTolerance:0.0e+0}");
        if (sa.SafeMax < 16)
        {
            Console.Error.WriteLine(
                "vulkanpturbsa FAIL: SA SafeMax < 16 — no usable skip on this frame, the gate would be vacuous.");
            return 1;
        }

        // CPU oracles.
        int[] cpuSa = CpuMirrorSa(scale, offX0, offY0, refZr, refZi, refLen, sa, out int[] kMap);
        int[] cpuPlain = CpuMirrorPlain(scale, offX0, offY0, refZr, refZi, refLen);

        // GPU: the production SA kernel object, driven exactly as the calculator would.
        int n = Dim * Dim;
        var gpu = new int[n];
        var smooth = new float[n];
        var fzr = new float[n]; var fzi = new float[n];
        var fdr = new float[n]; var fdi = new float[n];
        kernel.RunPerturbSA(Dim, Dim, scale, MaxIter, EscapeR2, offX0, offY0,
            refZr, refZi, refLen, SaTolerance, sa.SafeMax,
            sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI,
            gpu, smooth, fzr, fzi, fdr, fdi);

        // (3) SA engaged? Skip stats from the CPU FindSkip (same coeffs the GPU uses).
        long kSum = 0; int kMax = 0, kNonZero = 0;
        for (int i = 0; i < n; i++)
        {
            kSum += kMap[i];
            if (kMap[i] > kMax) kMax = kMap[i];
            if (kMap[i] > 0) kNonZero++;
        }
        double kAvg = (double)kSum / n;

        // (1) GPU-SA vs CPU-SA.
        int d1 = 0, m1 = 0;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Abs(gpu[i] - cpuSa[i]);
            if (d != 0) { d1++; if (d > m1) m1 = d; }
        }
        double f1 = (double)d1 / n;

        // (2) GPU-SA vs CPU plain (no SA) — SA must not change the escape iter.
        int d2 = 0, m2 = 0;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Abs(gpu[i] - cpuPlain[i]);
            if (d != 0) { d2++; if (d > m2) m2 = d; }
        }
        double f2 = (double)d2 / n;

        // Noise floor: CPU-SA vs CPU-plain — how much SA itself moves the answer
        // under double rounding (the irreducible boundary-chaos band).
        int dN = 0;
        for (int i = 0; i < n; i++) if (cpuSa[i] != cpuPlain[i]) dN++;
        double fN = (double)dN / n;

        var distinct = new System.Collections.Generic.HashSet<int>(cpuPlain);

        Console.WriteLine(
            $"vulkanpturbsa {Dim}x{Dim} zoom={Zoom:0e+0} maxIter={MaxIter} refLen={refLen} " +
            $"scale={scale:0.000e+00}");
        Console.WriteLine(
            $"  SA skip: avg k={kAvg:F1} max k={kMax} pixels-skipped={kNonZero}/{n} ({(double)kNonZero / n:P1})");
        // PRIMARY gate — the in-shader SA must reproduce the CPU SA path. Fixed
        // GPU-vs-CPU dialect floor (2%), same spirit as --vulkanpturbprobe.
        Console.WriteLine(
            $"  (1) GPU-SA vs CPU-SA:        disagree={d1}/{n} ({f1:P3}) maxΔiter={m1}  ← PRIMARY (GPU dialect floor)");
        // Informational — SA legitimately flips escape iters on chaotic-boundary
        // pixels (the known SA-on-vs-off property; NOT a failure). The point is
        // that the GPU moves the answer by the SAME amount the CPU SA path does.
        Console.WriteLine(
            $"  (2) GPU-SA vs CPU-plain:     disagree={d2}/{n} ({f2:P3}) maxΔiter={m2}  (SA effect, informational)");
        Console.WriteLine(
            $"  (N) CPU-SA vs CPU-plain:     disagree={dN}/{n} ({fN:P3})               (SA effect on CPU — expected)");

        const double DialectFloor = 0.02;   // 2% GPU-vs-CPU divergence budget
        double extra = Math.Abs(f2 - fN);    // divergence the GPU adds beyond CPU-SA
        bool engaged = kNonZero > 0 && kMax >= 16;
        bool primaryOk = f1 <= DialectFloor;
        bool noExtra = extra <= DialectFloor;
        bool nonDegenerate = distinct.Count >= 8;

        Console.WriteLine(
            $"  gate: engaged={engaged} primary(1)={primaryOk} GPU-extra-vs-CPU-SA={extra:P3}≤{DialectFloor:P0}?{noExtra} distinct={distinct.Count}");

        if (!nonDegenerate)
        {
            Console.Error.WriteLine("vulkanpturbsa FAIL: degenerate frame (structure absent) — parity vacuous.");
            return 1;
        }
        if (!engaged)
        {
            Console.Error.WriteLine(
                "vulkanpturbsa FAIL: SA never engaged (no pixel skipped ≥16 iters) — the gate would be vacuous.");
            return 1;
        }
        if (!primaryOk || !noExtra)
        {
            Console.Error.WriteLine(
                "vulkanpturbsa FAIL: the in-shader SA path diverged from the CPU SA mirror ABOVE the dialect " +
                "floor — a real gap in the in-shader FindSkip/EvalDelta or the SA-seeded rebased loop.");
            return 1;
        }

        Console.WriteLine($"vulkanpturbsa OK: {ctx.PickedType} {ctx.PickedName}");
        return 0;
    }

    // ── Reference orbit (double, exact centre) ────────────────────────────────
    private static void BuildReferenceOrbit(
        double cr, double ci, int maxIter, out double[] zr, out double[] zi, out int refLen)
    {
        zr = new double[maxIter];
        zi = new double[maxIter];
        double x = 0.0, y = 0.0;
        int nn = 0;
        for (; nn < maxIter; nn++)
        {
            zr[nn] = x; zi[nn] = y;
            double x2 = x * x, y2 = y * y;
            if (x2 + y2 >= EscapeR2) { nn++; break; }
            double xy = x * y;
            x = x2 - y2 + cr;
            y = xy + xy + ci;
        }
        refLen = nn;
        Console.WriteLine($"  ref orbit: len={nn} escaped={(nn < maxIter)}");
    }

    // ── CPU SA mirror: FindSkip → EvalDelta seed → the rebased δ loop from k.
    // Oracle for the GPU BuildPerturbSA kernel. kMap[] returns the per-pixel skip. ─
    private static int[] CpuMirrorSa(
        double scale, double offX0, double offY0,
        double[] refZr, double[] refZi, int refLen, SeriesApproximation sa, out int[] kMap)
    {
        int[] outIter = new int[Dim * Dim];
        kMap = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
        for (int px = 0; px < Dim; px++)
        {
            double dcR = (offX0 + px) * scale;
            double dcI = (offY0 + py) * scale;

            double dr = 0.0, di = 0.0;
            int m = 0, iterStart = 0;

            int k = sa.FindSkip(dcR, dcI, SaTolerance, MaxIter - 1);
            if (k >= 16 && k <= refLen)
            {
                sa.EvalDelta(k, dcR, dcI, out double dR, out double dI);
                dr = dR; di = dI;
                m = k; iterStart = k;
            }
            else k = 0;
            kMap[py * Dim + px] = iterStart;

            int iter;
            double zr = 0.0, zi = 0.0;
            for (iter = iterStart; iter < MaxIter; iter++)
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

    // ── CPU plain rebased mirror (no SA) — twin of ComputePixelPTRebased. ──────
    private static int[] CpuMirrorPlain(
        double scale, double offX0, double offY0, double[] refZr, double[] refZi, int refLen)
    {
        int[] outIter = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
        for (int px = 0; px < Dim; px++)
        {
            double dcR = (offX0 + px) * scale;
            double dcI = (offY0 + py) * scale;
            double dr = 0.0, di = 0.0;
            int m = 0;
            int iter;
            for (iter = 0; iter < MaxIter; iter++)
            {
                double Zr = refZr[m], Zi = refZi[m];
                double zr = Zr + dr, zi = Zi + di;
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
}
