// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #607 / GPU parity plan G4.6 — orbit accumulation on the GPU deep-zoom path.
// Past the high-precision threshold an orbit ColorGen theme ran on the CPU deep
// orbit path (#609) even with GPU perturbation on. The orbit-accumulating
// perturbation kernel (MandelbrotKernelSource.BuildPerturbOrbit) runs the same
// rebased loop and samples the mask'd inputs on z = Z[m] + δ; the calculator
// colours from the GPU means through the CPU path's FinalizeOrbitPixel. These
// tests run on the Vulkan device (skipped without an FP64-capable one):
//   • the kernel's 11 means against a CPU oracle of the same loop sampled with the
//     production InterpretedOrbitColorMap.Sample (double), plus range invariants
//     each input must satisfy by construction, and a shifted-view contrast so a
//     loose match can't pass as parity;
//   • the calculator routes a deep orbit frame to the GPU, reports it, and matches
//     the CPU deep orbit frame far more closely than the direct-double frame (the
//     mush the deep path exists to avoid);
//   • the opt-outs and gates keep the frame on the CPU, and a following shallow
//     frame does not inherit the deep route.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S607GpuDeepOrbitTests : IDisposable
{
    // Same budget hardening as S88GpuPerturbSaTests: a weak-fp64 card under full-suite
    // load trips the too-slow abort, which would latch GPU perturbation off.
    private readonly double _savedBudget = FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedGpuOrbit = InterpretedOrbitColorMap.GpuEnabled;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
    private readonly bool _savedDeepOrbit = MandelbrotCalculator.UseGpuDeepOrbit;

    public S607GpuDeepOrbitTests()
    {
        FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs = 0;
        InterpretedOrbitColorMap.GpuEnabled = true;
    }

    public void Dispose()
    {
        FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        InterpretedOrbitColorMap.GpuEnabled = _savedGpuOrbit;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotCalculator.UseGpuDeepOrbit = _savedDeepOrbit;
    }

    private const int Dim = 96, MaxIter = 3000;
    private const double EscapeR2 = 512.0 * 512.0;
    private const double Cx = -0.743643887037151, Cy = 0.13182590420533, Zoom = 1e6;

    private static readonly string[] Inputs =
    {
        "trapMin", "trapCross", "trapRing", "trapHyperbola", "trapHexagon",
        "stripeAvg", "tiaAvg", "curvature", "lyapunov", "gaussian", "expSmooth",
    };

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationOrbit) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        return k;
    }

    private static InterpretedOrbitColorMap Theme(string src)
    {
        var t = InterpretedColorMap.TryCreate(src, null, out var err) as InterpretedOrbitColorMap;
        Assert.True(t != null, $"theme did not compile as an orbit theme: {err}");
        return t!;
    }

    // Every input, so all 11 slots and their order are exercised.
    private static InterpretedOrbitColorMap AllInputsTheme()
        => Theme("return hsv(saturate(" + string.Join(" + ", Inputs) + "), 0.9, 1.0);");

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

    // CPU oracle: the rebased loop in double, sampled with the theme's production
    // Sample on the reconstructed z (pre-update, iter > 0); means as MapWithOrbit reads them.
    private static (int[] Iter, double[][] Means) Oracle(InterpretedOrbitColorMap theme, double scale,
        double offX0, double offY0, (double[] Zr, double[] Zi, int Len) r, bool rebase = true)
    {
        var it = new int[Dim * Dim];
        var means = Inputs.Select(_ => new double[Dim * Dim]).ToArray();
        for (int py = 0; py < Dim; py++)
            for (int px = 0; px < Dim; px++)
            {
                double dcR = (offX0 + px) * scale, dcI = (offY0 + py) * scale;
                double cr = Cx + dcR, ci = Cy + dcI;
                theme.InitOrbit(out var acc);
                double dr = 0, di = 0; int m = 0, iter;
                for (iter = 0; iter < MaxIter; iter++)
                {
                    double Zr = r.Zr[m], Zi = r.Zi[m];
                    double zr = Zr + dr, zi = Zi + di;
                    double zm2 = zr * zr + zi * zi;
                    if (zm2 >= EscapeR2) break;
                    if (iter > 0) theme.Sample(ref acc, zr, zi, cr, ci, iter);
                    if ((rebase && zm2 < dr * dr + di * di) || m + 1 >= r.Len) { dr = zr; di = zi; Zr = 0; Zi = 0; m = 0; }
                    double a = 2 * Zr + dr, b = 2 * Zi + di;
                    double ndr = a * dr - b * di + dcR, ndi = a * di + b * dr + dcI;
                    dr = ndr; di = ndi; m++;
                }
                int i = py * Dim + px;
                it[i] = iter;
                if (iter >= MaxIter) continue;   // the kernel writes 0 for in-set pixels
                double T(float v) => v == float.MaxValue ? 0 : v;
                double Avg(double s, int c) => c > 0 ? s / c : 0;
                double[] v =
                {
                    T(acc.TrapMin), T(acc.TrapMin2), T(acc.TrapMin3), T(acc.TrapMin4), T(acc.TrapMin5),
                    Avg(acc.StripeSum, acc.StripeCount), Avg(acc.TiaSum, acc.TiaCount),
                    Avg(acc.CurvatureSum, acc.CurvatureCount), Avg(acc.LyapunovSum, acc.LyapunovCount),
                    Avg(acc.GaussianSum, acc.GaussianCount), Avg(acc.ExpSum, acc.ExpCount),
                };
                for (int k = 0; k < v.Length; k++) means[k][i] = v[k];
            }
        return (it, means);
    }

    private static (int[] Iter, float[] Means) Gpu(VulkanComputeKernel k, int mask, double scale,
        double offX0, double offY0, (double[] Zr, double[] Zi, int Len) r)
    {
        int n = Dim * Dim;
        var it = new int[n]; var sm = new float[n];
        var a = new float[n]; var b = new float[n]; var c = new float[n]; var d = new float[n];
        var means = new float[n * System.Numerics.BitOperations.PopCount((uint)mask)];
        Array.Fill(means, float.NaN);
        k.RunPerturbOrbit(Dim, Dim, scale, MaxIter, EscapeR2, offX0, offY0, r.Zr, r.Zi, r.Len,
            mask, Cx, Cy, it, sm, a, b, c, d, means);
        return (it, means);
    }

    private static double Median(IEnumerable<double> xs)
    {
        var s = xs.OrderBy(v => v).ToArray();
        return s.Length == 0 ? double.NaN : s[s.Length / 2];
    }

    private static double P95(IEnumerable<double> xs)
    {
        var s = xs.OrderBy(v => v).ToArray();
        return s.Length == 0 ? double.NaN : s[(int)(s.Length * 0.95)];
    }

    [Fact]
    public void Kernel_Orbit_Means_Match_The_Cpu_Sampling_Of_The_Same_Loop()
    {
        using var k = Device();
        var theme = AllInputsTheme();
        int mask = (int)theme.OrbitInputs;
        Assert.Equal((1 << 11) - 1, mask);
        var r = ReferenceOrbit();
        double scale = 3.5 / (Dim * Zoom), off0 = -0.5 * Dim;

        var g = Gpu(k, mask, scale, off0, off0, r);
        var o = Oracle(theme, scale, off0, off0, r);
        // Contrast: the same oracle a quarter-frame to the right.
        var shifted = Oracle(theme, scale, off0 + Dim / 4, off0, r);

        int n = Dim * Dim, escaped = 0, shared = 0;
        Assert.DoesNotContain(g.Means, float.IsNaN);   // every slot of every pixel written
        for (int i = 0; i < n; i++)
        {
            if (g.Iter[i] >= MaxIter)
            {
                for (int s = 0; s < Inputs.Length; s++)
                    Assert.True(g.Means[i * Inputs.Length + s] == 0f, $"in-set pixel {i} has {Inputs[s]} = {g.Means[i * Inputs.Length + s]}");
                continue;
            }
            escaped++;
            if (g.Iter[i] == o.Iter[i]) shared++;
            // Range invariants that hold by construction of each input.
            double cr = Cx + (off0 + i % Dim) * scale, ci = Cy + (off0 + i / Dim) * scale;
            float M(int s) => g.Means[i * Inputs.Length + s];
            Assert.InRange(M(0), 0f, (float)(Math.Sqrt(cr * cr + ci * ci) * (1 + 1e-5)));   // z_1 = c is sampled
            Assert.InRange(M(1), 0f, (float)(Math.Min(Math.Abs(cr), Math.Abs(ci)) * (1 + 1e-5)));
            for (int s = 2; s <= 4; s++) Assert.True(M(s) >= 0f, $"{Inputs[s]} < 0");
            Assert.InRange(M(5), 0f, 1f);                 // 0.5 + 0.5 sin
            Assert.InRange(M(6), -1e-4f, 1f + 1e-4f);     // triangle inequality
            Assert.InRange(M(7), 0f, (float)Math.PI);     // |turn angle|
            Assert.InRange(M(9), 0f, 0.7072f);            // distance to the nearest Gaussian integer
            Assert.InRange(M(10), 0f, 1f);                // e^-|z|
        }
        Assert.True(escaped > n / 4, $"only {escaped} escaped pixels");
        // Rebasing (|z| < |δ|) changes the escape iteration of a small share of pixels
        // here; the GPU must follow the rebased loop, not the bare one.
        var bare = Oracle(theme, scale, off0, off0, r, rebase: false);
        double disRebased = Disagree(g.Iter, o.Iter), disBare = Disagree(g.Iter, bare.Iter);
        Assert.True(Disagree(o.Iter, bare.Iter) > 0.005, $"rebasing changes only {Disagree(o.Iter, bare.Iter):P2} of pixels here");
        Assert.True(disRebased < 0.003 && disBare > 3 * disRebased,
            $"GPU iterations vs the rebased loop {disRebased:P3}, vs the bare loop {disBare:P3}");

        for (int s = 0; s < Inputs.Length; s++)
        {
            var parity = new List<double>(); var contrast = new List<double>();
            for (int i = 0; i < n; i++)
            {
                if (g.Iter[i] >= MaxIter || g.Iter[i] != o.Iter[i]) continue;
                double c = o.Means[s][i];
                parity.Add(Math.Abs(g.Means[i * Inputs.Length + s] - c) / (1 + Math.Abs(c)));
                if (shifted.Iter[i] < MaxIter)
                    contrast.Add(Math.Abs(shifted.Means[s][i] - c) / (1 + Math.Abs(c)));
            }
            double med = Median(parity), p95 = P95(parity), con = Median(contrast);
            Assert.True(med < 1e-4 && p95 < 1e-2, $"{Inputs[s]}: GPU vs oracle median {med:E2} p95 {p95:E2}");
            Assert.True(con > 20 * med, $"{Inputs[s]}: the shifted view differs by only {con:E2} (parity {med:E2})");
        }
    }

    [Fact]
    public void Kernel_Writes_Only_The_Masked_Inputs_In_Bit_Order()
    {
        using var k = Device();
        var r = ReferenceOrbit();
        double scale = 3.5 / (Dim * Zoom), off0 = -0.5 * Dim;
        var all = AllInputsTheme();
        var full = Gpu(k, (int)all.OrbitInputs, scale, off0, off0, r);
        // tiaAvg (bit 6) + trapMin (bit 0): slot 0 = trapMin, slot 1 = tiaAvg.
        var two = Theme("return hsv(saturate(tiaAvg), 0.9, saturate(trapMin));");
        Assert.Equal((int)(FracturingFog.Interefaces.GpuOrbitInputs.TrapMin | FracturingFog.Interefaces.GpuOrbitInputs.TiaAvg),
            (int)two.OrbitInputs);
        var g = Gpu(k, (int)two.OrbitInputs, scale, off0, off0, r);
        Assert.Equal(Dim * Dim * 2, g.Means.Length);
        for (int i = 0; i < Dim * Dim; i++)
        {
            // Separate shader variants may contract float ops differently: near-equal, not bitwise.
            Assert.True(Math.Abs(full.Means[i * 11 + 0] - g.Means[i * 2 + 0]) <= 1e-5f * (1 + Math.Abs(full.Means[i * 11 + 0])), $"pixel {i}: slot 0 is not trapMin");
            Assert.True(Math.Abs(full.Means[i * 11 + 6] - g.Means[i * 2 + 1]) <= 1e-5f * (1 + Math.Abs(full.Means[i * 11 + 6])), $"pixel {i}: slot 1 is not tiaAvg");
        }
    }

    // The S88 --saprobe deep view (multi-limb centre, zoom 1e15).
    private static MandelbrotCalculator DeepCalc(InterpretedOrbitColorMap map, VulkanComputeKernel? k,
        QualityPreset? quality = null) => new(128, 128)
    {
        Quality = quality ?? QualityPreset.Extreme,
        CenterX = -1.1726999042772253, CenterXLo = 8.9529605787776783E-17,
        CenterY = -0.2968356710071185, CenterYLo = -2.3536240906562374E-18,
        Zoom = 1e15, MaxIterations = 4096, ColorMap = map, GpuKernel = k,
    };

    // At this depth every pixel shares the long early orbit, so the means vary
    // little across the frame; the x40 + fract spreads that variation over the hue.
    private const string DeepTheme = "return hsv(fract(stripeAvg * 40.0 + tiaAvg * 40.0), 0.85, 1.0);";

    private static double MeanChannelDiff(uint[] a, uint[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            for (int sh = 0; sh < 24; sh += 8)
                sum += Math.Abs((int)((a[i] >> sh) & 0xFF) - (int)((b[i] >> sh) & 0xFF));
        return sum / (3.0 * a.Length);
    }

    private static double Disagree(int[] x, int[] y) => x.Zip(y).Count(p => p.First != p.Second) / (double)x.Length;

    [Fact]
    public void Calculator_Runs_Deep_Orbit_Frames_On_The_Gpu_And_Matches_The_Cpu_Deep_Path()
    {
        using var k = Device();
        var theme = Theme(DeepTheme);
        MandelbrotCalculator.UseGpuDeepOrbit = true;

        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = DeepCalc(theme, null);
        cpu.Calculate(CancellationToken.None);
        Assert.True(cpu.IsHighPrecisionActive, "the view is not on the deep orbit path");
        var direct = DeepCalc(theme, null, new QualityPreset { Name = "direct", AllowHighPrecision = false });
        direct.Calculate(CancellationToken.None);

        MandelbrotCalculator.UseGpuPerturbation = true;
        var g = DeepCalc(theme, k);
        Array.Fill(g.TrapBuffer, float.NaN);
        Array.Fill(g.StripeBuffer, float.NaN);
        g.Calculate(CancellationToken.None);
        Assert.True(g.LastFrameUsedGpuOrbitPerturbation, $"the GPU deep orbit kernel did not run: {g.LastGpuRoute.Detail}");
        Assert.True(g.LastFrameUsedGpuPerturbation);
        Assert.Contains("deep-zoom orbit perturbation", g.LastGpuRoute.Detail);
        Assert.DoesNotContain(g.StripeBuffer, float.IsNaN);

        double iterDis = Disagree(g.IterationBuffer, cpu.IterationBuffer);
        double vsCpu = MeanChannelDiff(g.ColorBuffer, cpu.ColorBuffer);
        double vsDirect = MeanChannelDiff(cpu.ColorBuffer, direct.ColorBuffer);
        var stripeD = new List<double>(); var tiaD = new List<double>();
        for (int i = 0; i < g.IterationBuffer.Length; i++)
        {
            if (g.IterationBuffer[i] != cpu.IterationBuffer[i] || g.IterationBuffer[i] >= g.MaxIterations) continue;
            stripeD.Add(Math.Abs(g.StripeBuffer[i] - cpu.StripeBuffer[i]));
            tiaD.Add(Math.Abs(g.TiaBuffer[i] - cpu.TiaBuffer[i]));
        }
        Assert.True(iterDis < 0.02, $"iteration disagreement {iterDis:P3}");
        Assert.True(Median(stripeD) < 1e-5 && Median(tiaD) < 1e-5,
            $"stripe median {Median(stripeD):E2}, tia median {Median(tiaD):E2}");
        Assert.All(g.TrapBuffer, v => Assert.Equal(0f, v));   // trapMin is not read: zero, as on the CPU
        Assert.True(vsCpu < 1.0, $"GPU vs CPU deep orbit colour differs by {vsCpu:F2} levels per channel");
        Assert.True(vsDirect > 10 * vsCpu + 1, $"direct double differs by only {vsDirect:F2} (GPU {vsCpu:F2}) — the view does not need the deep path");
    }

    [Fact]
    public void Opt_Outs_And_Gates_Keep_Deep_Orbit_Frames_On_The_Cpu()
    {
        using var k = Device();
        var theme = Theme(DeepTheme);
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuDeepOrbit = true;

        MandelbrotCalculator.UseGpuDeepOrbit = false;
        var off = DeepCalc(theme, k);
        off.Calculate(CancellationToken.None);
        Assert.False(off.LastFrameUsedGpuPerturbation, "UseGpuDeepOrbit off still ran on the GPU");
        MandelbrotCalculator.UseGpuDeepOrbit = true;

        var tiled = DeepCalc(theme, k);
        tiled.PerRowMaxIter = Enumerable.Repeat(4096, 128).ToArray();
        tiled.Calculate(CancellationToken.None);
        Assert.False(tiled.LastFrameUsedGpuPerturbation, "a per-tile cap ran on the GPU");
        Assert.Equal("per-tile iter cap", tiled.LastGpuRoute.Reason);

        InterpretedOrbitColorMap.GpuEnabled = false;
        var cpuTheme = Theme(DeepTheme);   // GPU orbit off at creation -> no orbit mask
        InterpretedOrbitColorMap.GpuEnabled = true;
        Assert.Equal(FracturingFog.Interefaces.GpuOrbitInputs.None, cpuTheme.OrbitInputs);
        var noMask = DeepCalc(cpuTheme, k);
        noMask.Calculate(CancellationToken.None);
        Assert.False(noMask.LastFrameUsedGpuPerturbation, "a theme without an orbit mask ran on the GPU");

        // A GPU deep orbit frame, then a shallow frame on the same calculator: the
        // shallow route must not inherit the deep one.
        var c = DeepCalc(theme, k);
        c.Calculate(CancellationToken.None);
        Assert.True(c.LastFrameUsedGpuOrbitPerturbation, $"deep frame: {c.LastGpuRoute.Detail}");
        c.CenterX = -0.5; c.CenterXLo = 0; c.CenterY = 0; c.CenterYLo = 0; c.Zoom = 1;
        c.Calculate(CancellationToken.None);
        Assert.False(c.LastFrameUsedGpuOrbitPerturbation || c.LastFrameUsedGpuPerturbation, "the shallow frame kept the deep flags");
        Assert.False(c.IsHighPrecisionActive);
        Assert.DoesNotContain("deep-zoom", c.LastGpuRoute.Detail ?? "");
    }
}
