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
// G4.5b — BLA in the same kernel:
//   • against the oracle extended with the production BlaTable.Lookup and the
//     Bla coefficients (independent of the GPU packing), with BLA skipping most
//     of the iterations so the check is not vacuous;
//   • a table with A scaled by 1.5 must wreck the frame when passed with its
//     levels and change nothing with blaLevels 0 — the kernel reads and applies
//     the table, and only when asked;
//   • the calculator's SA x BLA toggle matrix (BLA alone runs with safeMax 0).

using System;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S88GpuPerturbSaTests : IDisposable
{
    // The perturbation dispatches abort to the CPU (and the calculator disables GPU
    // perturbation for the session) when band 0 extrapolates past the budget. Under
    // the full suite's load on a weak-fp64 card that trips spuriously, so these
    // correctness tests run without it (0 = off) and restore it afterwards.
    private readonly double _savedBudget = FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs;
    public S88GpuPerturbSaTests() => FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs = 0;
    public void Dispose() => FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;

    private const int Dim = 96, MaxIter = 6000;
    private const double EscapeR2 = 512.0 * 512.0, Tol = 1e-3;
    // The --vulkanpturbsa view: the seahorse-valley centre at zoom 1e6 (orbit up to |Z| ~ 2).
    private const double Cx = -0.743643887037151, Cy = 0.13182590420533, Zoom = 1e6;
    // BLA validity radii are ~1e-6 |Z|: it only skips once |delta| is that small, i.e. deep.
    // Oracle and GPU share the double reference orbit, so a plain-double centre is fine here.
    private const double BlaZoom = 1e12;

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

    // CPU oracle: the shader's per-pixel loop in double, seeded by the production SA,
    // with the production BLA lookup on the reference index (G4.5b).
    private static int[] Oracle(double scale, double off0, (double[] Zr, double[] Zi, int Len) r,
                                SeriesApproximation? sa, double tol)
        => Oracle(scale, off0, r, sa, tol, null, out _);

    private static int[] Oracle(double scale, double off0, (double[] Zr, double[] Zi, int Len) r,
                                SeriesApproximation? sa, double tol, BlaTable? bla, out double blaSkippedFrac)
    {
        long skipped = 0, total = 0;
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
                    if (bla != null)
                    {
                        // Kernel bounds: iter + L <= maxIter and m + L < refLen.
                        int cap = Math.Min(m + (MaxIter - iter), r.Len - 1);
                        int bi = bla.Lookup(m, dr * dr + di * di, cap);
                        if (bi >= 0 && bla.Data[bi].L >= 2)
                        {
                            var e = bla.Data[bi];
                            double ndr0 = e.ARe * dr - e.AIm * di + (e.BRe * dcR - e.BIm * dcI);
                            double ndi0 = e.ARe * di + e.AIm * dr + (e.BRe * dcI + e.BIm * dcR);
                            dr = ndr0; di = ndi0;
                            m += e.L; iter += e.L - 1; skipped += e.L;
                            continue;
                        }
                    }
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
                total += iter;
            }
        blaSkippedFrac = total > 0 ? skipped / (double)total : 0;
        return it;
    }

    private static int[] Gpu(VulkanComputeKernel k, double scale, double off0,
                             (double[] Zr, double[] Zi, int Len) r, SeriesApproximation? sa, double tol,
                             double[]? bla = null, int blaLevels = 0, int safeMax = -1)
    {
        int n = Dim * Dim;
        var it = new int[n]; var sm = new float[n];
        var a = new float[n]; var b = new float[n]; var c = new float[n]; var d = new float[n];
        if (sa == null)
            k.RunPerturb(Dim, Dim, scale, MaxIter, EscapeR2, off0, off0, r.Zr, r.Zi, r.Len, it, sm, a, b, c, d);
        else
            k.RunPerturbSA(Dim, Dim, scale, MaxIter, EscapeR2, off0, off0, r.Zr, r.Zi, r.Len, tol,
                safeMax >= 0 ? safeMax : sa.SafeMax,
                sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI, it, sm, a, b, c, d, bla, blaLevels);
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

    private static BlaTable Table(double scale, (double[] Zr, double[] Zi, int Len) r)
        => new(r.Zr, r.Zi, r.Len, Math.Sqrt(2.0) * 0.5 * Dim * scale);   // corner |dc|, as the calculator

    [Fact]
    public void Bla_Kernel_Reproduces_The_Cpu_Bla_Answer()
    {
        using var k = Device();
        var r = ReferenceOrbit();
        var sa = new SeriesApproximation(r.Zr, r.Zi, r.Len);
        double scale = 3.5 / (Dim * BlaZoom), off0 = -0.5 * Dim;
        var table = Table(scale, r);
        Assert.True(table.Levels > 4, $"BLA table has {table.Levels} levels");

        // SA + BLA, and BLA alone (safeMax 0).
        var oSaBla = Oracle(scale, off0, r, sa, Tol, table, out _);
        var oBla = Oracle(scale, off0, r, null, Tol, table, out double skipBla);
        Assert.True(skipBla > 0.3, $"BLA skips only {skipBla:P1} of the iterations here — the check would be weak");
        var gSaBla = Gpu(k, scale, off0, r, sa, Tol, table.GpuCoefficients, table.Levels);
        var gBla = Gpu(k, scale, off0, r, sa, Tol, table.GpuCoefficients, table.Levels, safeMax: 0);
        Assert.True(Disagree(gSaBla, oSaBla) < 0.005, $"GPU SA+BLA vs oracle: {Disagree(gSaBla, oSaBla):P3}");
        Assert.True(Disagree(gBla, oBla) < 0.005, $"GPU BLA vs oracle: {Disagree(gBla, oBla):P3}");
    }

    [Fact]
    public void Bla_Table_Is_Read_And_Applied_Only_When_Asked()
    {
        using var k = Device();
        var r = ReferenceOrbit();
        var sa = new SeriesApproximation(r.Zr, r.Zi, r.Len);
        double scale = 3.5 / (Dim * BlaZoom), off0 = -0.5 * Dim;
        var table = Table(scale, r);
        var good = table.GpuCoefficients;
        var bad = (double[])good.Clone();
        for (int e = 0; e < bad.Length; e += 5) { bad[e] *= 1.5; bad[e + 1] *= 1.5; }   // A x 1.5

        var plainSa = Gpu(k, scale, off0, r, sa, Tol);
        var right = Gpu(k, scale, off0, r, sa, Tol, good, table.Levels);
        var wrong = Gpu(k, scale, off0, r, sa, Tol, bad, table.Levels);
        var off = Gpu(k, scale, off0, r, sa, Tol, bad, 0);
        Assert.True(Disagree(off, plainSa) < 0.001, $"blaLevels 0 still read the table: {Disagree(off, plainSa):P3}");
        Assert.True(Disagree(wrong, right) > 0.10, $"a corrupted table changed only {Disagree(wrong, right):P2} of pixels");
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
        bool savedBla = MandelbrotCalculator.UseGpuBla;
        try
        {
            var cpu = DeepCalc(null);
            cpu.Calculate(CancellationToken.None);

            MandelbrotCalculator.UseGpuPerturbation = true;
            // (label, setup, expect SA, expect BLA) — default = both, as the CPU.
            foreach (var (label, setup, wantSa, wantBla) in new (string, Action<MandelbrotCalculator>, bool, bool)[]
            {
                ("defaults", _ => { }, true, true),
                ("DisableSeriesApproximation", c => c.DisableSeriesApproximation = true, false, true),
                ("DisableAcceleration", c => c.DisableAcceleration = true, false, false),
                ("UseGpuSeriesApproximation off", _ => MandelbrotCalculator.UseGpuSeriesApproximation = false, false, true),
                ("UseGpuBla off", _ => MandelbrotCalculator.UseGpuBla = false, true, false),
            })
            {
                MandelbrotCalculator.UseGpuSeriesApproximation = true;
                MandelbrotCalculator.UseGpuBla = true;
                var g = DeepCalc(k);
                setup(g);
                g.Calculate(CancellationToken.None);
                Assert.True(g.LastFrameUsedGpuPerturbation, $"{label}: GPU perturbation did not run: {g.LastGpuRoute.Detail}");
                Assert.Equal((wantSa, wantBla), (g.LastFrameUsedGpuSeriesApproximation, g.LastFrameUsedGpuBla));
                Assert.Equal(wantSa, g.LastGpuRoute.Detail!.Contains("series approximation"));
                Assert.Equal(wantBla, g.LastGpuRoute.Detail!.Contains("BLA"));
                Assert.True(Disagree(g.IterationBuffer, cpu.IterationBuffer) < 0.02,
                    $"{label}: GPU frame vs CPU {Disagree(g.IterationBuffer, cpu.IterationBuffer):P3}");
            }
        }
        finally
        {
            MandelbrotCalculator.UseGpuPerturbation = savedPerturb;
            MandelbrotCalculator.UseGpuSeriesApproximation = savedSa;
            MandelbrotCalculator.UseGpuBla = savedBla;
        }
    }
}
