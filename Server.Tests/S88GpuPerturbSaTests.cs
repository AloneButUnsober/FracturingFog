// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #88 / GPU parity plan G4.5 — Series Approximation on the GPU perturbation path.
// The SA kernel (MandelbrotKernelSource.BuildPerturbSA) was a correctness-green
// spike reachable only from the Vulkan smoke harness; GPU deep-zoom frames ran the
// plain rebased loop for every pixel while the CPU deep path skipped with SA. The
// kernel is now an IGpuKernel member on both backends and TryRunGpuPerturbation
// uses it under the CPU's own SA conditions. These tests run on the Vulkan device
// (skipped without an FP64-capable one):
//   • the kernel against an independent CPU oracle built on the production
//     SeriesApproximation (FindSkip / EvalDelta / EvalDDelta + the same rebased
//     loop): it reproduces the SA answer, not the plain one — SA moves enough
//     pixels at this view that the two are distinguishable;
//   • tolerance 0 never skips, so it must equal RunPerturb; a huge tolerance
//     skips as far as the coefficients allow and must change the frame;
//   • the calculator routes a deep frame through SA, reports it, honours
//     DisableSeriesApproximation / DisableAcceleration / UseGpuSeriesApproximation,
//     and stays within the CPU frame's bounds.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S88GpuPerturbSaTests
{
    private const int Dim = 96, MaxIter = 6000;
    private const double EscapeR2 = 512.0 * 512.0, Tol = 1e-3;
    // The --vulkanpturbsa view: the seahorse-valley centre at zoom 1e6 (orbit up to |Z| ~ 2).
    private const double Cx = -0.743643887037151, Cy = 0.13182590420533, Zoom = 1e6;

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationSA) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        return k;
    }

    private static (double[] Zr, double[] Zi, int Len) ReferenceOrbit()
    {
        var zr = new double[MaxIter + 1]; var zi = new double[MaxIter + 1];
        double x = 0, y = 0; int len = MaxIter;
        for (int n = 0; n < MaxIter; n++)
        {
            zr[n] = x; zi[n] = y;
            if (x * x + y * y >= EscapeR2) { len = n; break; }
            double nx = x * x - y * y + Cx; y = 2 * x * y + Cy; x = nx;
        }
        return (zr, zi, len);
    }

    // CPU oracle: the shader's per-pixel loop in double, seeded by the production SA.
    private static int[] Oracle(double scale, double off0, (double[] Zr, double[] Zi, int Len) r,
                                SeriesApproximation? sa, double tol)
    {
        var it = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
            for (int px = 0; px < Dim; px++)
            {
                double dcR = (off0 + px) * scale, dcI = (off0 + py) * scale;
                double dr = 0, di = 0; int m = 0, start = 0;
                if (sa != null)
                {
                    int k = sa.FindSkip(dcR, dcI, tol, Math.Min(sa.SafeMax, MaxIter - 1));
                    if (k >= 16 && k <= r.Len) { sa.EvalDelta(k, dcR, dcI, out dr, out di); m = k; start = k; }
                }
                int iter;
                for (iter = start; iter < MaxIter; iter++)
                {
                    double Zr = r.Zr[m], Zi = r.Zi[m];
                    double zr = Zr + dr, zi = Zi + di;
                    double zm2 = zr * zr + zi * zi;
                    if (zm2 >= EscapeR2) break;
                    if (zm2 < dr * dr + di * di || m + 1 >= r.Len) { dr = zr; di = zi; Zr = 0; Zi = 0; m = 0; }
                    double a = 2 * Zr + dr, b = 2 * Zi + di;
                    double ndr = a * dr - b * di + dcR, ndi = a * di + b * dr + dcI;
                    dr = ndr; di = ndi; m++;
                }
                it[py * Dim + px] = iter;
            }
        return it;
    }

    private static int[] Gpu(VulkanComputeKernel k, double scale, double off0,
                             (double[] Zr, double[] Zi, int Len) r, SeriesApproximation? sa, double tol)
    {
        int n = Dim * Dim;
        var it = new int[n]; var sm = new float[n];
        var a = new float[n]; var b = new float[n]; var c = new float[n]; var d = new float[n];
        if (sa == null)
            k.RunPerturb(Dim, Dim, scale, MaxIter, EscapeR2, off0, off0, r.Zr, r.Zi, r.Len, it, sm, a, b, c, d);
        else
            k.RunPerturbSA(Dim, Dim, scale, MaxIter, EscapeR2, off0, off0, r.Zr, r.Zi, r.Len, tol, sa.SafeMax,
                sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI, it, sm, a, b, c, d);
        return it;
    }

    private static double Disagree(int[] x, int[] y) => x.Zip(y).Count(p => p.First != p.Second) / (double)x.Length;

    [Fact]
    public void Sa_Kernel_Reproduces_The_Cpu_Sa_Answer_Not_The_Plain_One()
    {
        using var k = Device();
        var r = ReferenceOrbit();
        var sa = new SeriesApproximation(r.Zr, r.Zi, r.Len);
        Assert.True(sa.SafeMax >= 16, $"SA SafeMax {sa.SafeMax}: no usable skip, the test would be vacuous");
        double scale = 3.5 / (Dim * Zoom), off0 = -0.5 * Dim;

        var oracleSa = Oracle(scale, off0, r, sa, Tol);
        var oraclePlain = Oracle(scale, off0, r, null, Tol);
        var gpuSa = Gpu(k, scale, off0, r, sa, Tol);

        double saEffect = Disagree(oracleSa, oraclePlain);
        double vsSa = Disagree(gpuSa, oracleSa);
        double vsPlain = Disagree(gpuSa, oraclePlain);
        Assert.True(saEffect > 0.02, $"SA moves only {saEffect:P2} of pixels here — the comparison would not tell SA from plain");
        Assert.True(vsSa < 0.005, $"GPU SA vs CPU SA oracle: {vsSa:P3} of pixels disagree");
        Assert.True(vsPlain > 4 * vsSa + 0.01, $"GPU SA is as close to plain ({vsPlain:P2}) as to SA ({vsSa:P2})");
    }

    [Fact]
    public void Zero_Tolerance_Never_Skips_And_A_Huge_One_Does()
    {
        using var k = Device();
        var r = ReferenceOrbit();
        var sa = new SeriesApproximation(r.Zr, r.Zi, r.Len);
        double scale = 3.5 / (Dim * Zoom), off0 = -0.5 * Dim;
        var plain = Gpu(k, scale, off0, r, null, 0);
        var zero = Gpu(k, scale, off0, r, sa, 0.0);
        var huge = Gpu(k, scale, off0, r, sa, 1e30);
        Assert.True(Disagree(zero, plain) < 0.001, $"tolerance 0 differs from RunPerturb on {Disagree(zero, plain):P3}");
        Assert.True(Disagree(huge, plain) > 0.10, $"a maximal skip changed only {Disagree(huge, plain):P2} of pixels");
    }

    // The --saprobe deep view (multi-limb centre, zoom 1e15) through the calculator.
    private static MandelbrotCalculator DeepCalc(VulkanComputeKernel? k) => new(128, 128)
    {
        Quality = QualityPreset.Extreme,
        CenterX = -1.1726999042772253, CenterXLo = 8.9529605787776783E-17,
        CenterY = -0.2968356710071185, CenterYLo = -2.3536240906562374E-18,
        Zoom = 1e15, MaxIterations = 4096, ColorMap = new HsvPalette(), GpuKernel = k,
    };

    [Fact]
    public void Calculator_Routes_Deep_Frames_Through_Sa_Under_The_Cpu_Conditions()
    {
        using var k = Device();
        bool savedPerturb = MandelbrotCalculator.UseGpuPerturbation, savedSa = MandelbrotCalculator.UseGpuSeriesApproximation;
        try
        {
            var cpu = DeepCalc(null);
            cpu.Calculate(CancellationToken.None);

            MandelbrotCalculator.UseGpuPerturbation = true;
            MandelbrotCalculator.UseGpuSeriesApproximation = true;
            var g = DeepCalc(k);
            g.Calculate(CancellationToken.None);
            Assert.True(g.LastFrameUsedGpuPerturbation, $"GPU perturbation did not run: {g.LastGpuRoute.Detail}");
            Assert.True(g.LastFrameUsedGpuSeriesApproximation);
            Assert.Contains("series approximation", g.LastGpuRoute.Detail);
            Assert.True(Disagree(g.IterationBuffer, cpu.IterationBuffer) < 0.02,
                $"GPU SA frame vs CPU: {Disagree(g.IterationBuffer, cpu.IterationBuffer):P3}");

            foreach (var (label, setup) in new (string, Action<MandelbrotCalculator>)[]
            {
                ("DisableSeriesApproximation", c => c.DisableSeriesApproximation = true),
                ("DisableAcceleration", c => c.DisableAcceleration = true),
                ("UseGpuSeriesApproximation off", _ => MandelbrotCalculator.UseGpuSeriesApproximation = false),
            })
            {
                MandelbrotCalculator.UseGpuSeriesApproximation = true;
                var off = DeepCalc(k);
                setup(off);
                off.Calculate(CancellationToken.None);
                Assert.True(off.LastFrameUsedGpuPerturbation, $"{label}: GPU perturbation did not run");
                Assert.False(off.LastFrameUsedGpuSeriesApproximation, $"{label}: SA still engaged");
                Assert.DoesNotContain("series approximation", off.LastGpuRoute.Detail);
            }
        }
        finally
        {
            MandelbrotCalculator.UseGpuPerturbation = savedPerturb;
            MandelbrotCalculator.UseGpuSeriesApproximation = savedSa;
        }
    }
}
