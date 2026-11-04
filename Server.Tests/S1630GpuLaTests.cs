// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1630 (epic #1623, S7) — the LA stage evaluator in the GPU perturbation kernel
// (LaKernelSource; method from Imagina's HInfLAEvaluator, GitHub user 5E-324,
// AGPL-3.0, see the bibliography #imagina). Run on the Vulkan device (skipped without
// an fp64-capable one), on bench-kit scenes inside the GPU's deep range (fractadyne
// bench-kit, WindySnowOwl, https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit,
// from rith's FF 0.7.1 benchmark report; see the bibliography):
//   • the GPU LA frame against the CPU LA frame (the same evaluator in C#): iteration
//     counts agree on all but a few pixels, with the AT used (p448) and without (path);
//   • independent invariant: exact iteration (fixed-point BigInteger) on sampled
//     pixels — the GPU LA frame matches it at least as often as the GPU SA / BLA frame;
//   • the route: Auto takes the LA kernel at zoom ≥ 1e30, not below; Off, FF_GPU_LA's
//     switch (UseGpuLa) and acceleration off keep the SA / BLA kernels; the label and
//     the route name it.
// Mutants (in LaKernelSource's HLSL, run by hand): caught — next index one step off, the
// validity check on w removed, the AT converted back without 1/A, "+ B" dropped from the
// derivative (the normals check). Not caught — the tail's |z| < |δ| rebase removed (no
// test view glitches without it; design doc §4.6).
// The GPU returns z and dz/dc as floats; past ~1e35 the derivative overflows (#1660),
// so the normals compare only where it is finite.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.Models;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes UseGpuPerturbation / UseGpuLa
public sealed class S1630GpuLaTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly double _savedBudget = FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation, _savedLa = MandelbrotCalculator.UseGpuLa;

    public S1630GpuLaTests(ITestOutputHelper output)
    {
        _out = output;
        // As S88: no too-slow abort under the suite's load on a weak-fp64 card.
        FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs = 0;
    }

    public void Dispose()
    {
        FracturingFog.Rendering.MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotCalculator.UseGpuLa = _savedLa;
    }

    private const int W = 64, H = 36, Bits = 320;

    private sealed record Scene(string Name, double[] X, double[] Y, double KfZoom, int Iter);

    private static readonly Scene P448 = new("p448",
        [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50, -1.0894092006534645e-66, -5.481175082318642e-83, -1.5340609125118276e-99, 1.4285946495395532e-116],
        [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50, -4.820603700098655e-67, -3.1189999875481587e-83, 1.2339668272603997e-99, -9.480108552256602e-117],
        5.176068e33, 13440);
    private static readonly Scene Path = new("path",
        [-0.028041054305504548, 1.3292199503012176e-18, -5.27591145479967e-35, 2.798737894555097e-51, -1.4491927624390092e-67, -6.233008271562717e-84, 4.7734689309385144e-101, 2.9022031733306904e-117],
        [-0.6948927538996524, 5.189489838305484e-17, 5.567870094609155e-34, 1.7818449158374648e-50, 1.8079413574553354e-68, 2.5201987988615068e-85, -7.246511953022119e-102, -6.412330137037785e-118],
        1e33, 2859);

    // FF's built-in "3E47 Test" (Installer/Stage/Resources/regions.json; S1274's view):
    // every deep pixel rebases there, so the tail's |z| < |δ| test is exercised.
    private static readonly Scene E47 = new("3e47",
        [-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51, 0, 0, 0, 0],
        [-5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55, 0, 0, 0, 0],
        0, 20000);

    public static TheoryData<string> Scenes => new() { "p448", "path", "3e47" };
    private static Scene ByName(string n) => n switch { "p448" => P448, "path" => Path, _ => E47 };

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationLa) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        return k;
    }

    private static double FfZoom(Scene s) => s.Name == "3e47" ? 3e47 : s.KfZoom * 3.5 * H / (4.0 * Math.Max(W, H));

    private static MandelbrotCalculator Calc(Scene s, VulkanComputeKernel? k, DeepLaMode mode = DeepLaMode.Auto, double? zoom = null)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = s.X[0], CenterXLo = s.X[1], CenterX2 = s.X[2], CenterX3 = s.X[3], CenterX4 = s.X[4], CenterX5 = s.X[5], CenterX6 = s.X[6], CenterX7 = s.X[7],
            CenterY = s.Y[0], CenterYLo = s.Y[1], CenterY2 = s.Y[2], CenterY3 = s.Y[3], CenterY4 = s.Y[4], CenterY5 = s.Y[5], CenterY6 = s.Y[6], CenterY7 = s.Y[7],
            Zoom = zoom ?? FfZoom(s), MaxIterations = s.Iter, ColorMap = new HsvPalette(), GpuKernel = k, LaMode = mode,
        };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static double Disagree(int[] a, int[] b) => a.Zip(b).Count(p => p.First != p.Second) / (double)a.Length;

    [Theory]
    [MemberData(nameof(Scenes))]
    public void The_Gpu_Frame_Matches_The_Cpu_La_Frame(string name)
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var s = ByName(name);
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = Calc(s, null);
        Assert.True(cpu.LastFrameUsedLa && !cpu.LastFrameUsedGpuPerturbation);   // precondition
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = true;
        var gpu = Calc(s, k);
        Assert.True(gpu.LastFrameUsedGpuPerturbation, $"GPU perturbation did not run: {gpu.LastGpuRoute.Detail}");
        Assert.True(gpu.LastFrameUsedGpuLa);
        Assert.True(gpu.LastFrameUsedLa);
        Assert.EndsWith(" LA", gpu.LastPrecisionLabel);
        Assert.Contains("LA stages", gpu.LastGpuRoute.Detail);
        double d = Disagree(gpu.IterationBuffer, cpu.IterationBuffer);
        int inOut = Enumerable.Range(0, W * H).Count(i => (gpu.IterationBuffer[i] >= s.Iter) != (cpu.IterationBuffer[i] >= s.Iter));
        _out.WriteLine($"{name}: AT {(cpu.LastLaAt is { } a ? $"stage {a.Stage}, {cpu.LastLaAtWork.Pixels} px" : "none")}; GPU vs CPU LA: {d:P2} of px differ, in/out {inOut}");
        Assert.True(d < 0.02, $"{name}: GPU LA vs CPU LA {d:P2}");
        Assert.Equal(0, inOut);
        // The derivative rides through the kernel's steps as through the CPU's: the
        // normals (z over dz/dc, from the GPU's float final z and derivative) agree.
        int same = 0, nBad = 0, inf = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (gpu.IterationBuffer[i] != cpu.IterationBuffer[i] || cpu.IterationBuffer[i] >= s.Iter) continue;
            // The kernel returns z and dz/dc as floats (float4 gFinalZD, as every deep kernel):
            // past ~1e38 the derivative does not fit, so only finite pairs compare.
            if (!float.IsFinite(gpu.FinalDrBuffer[i]) || !float.IsFinite(gpu.FinalDiBuffer[i])) { inf++; continue; }
            same++;
            double dn = Math.Abs(Math.Atan2(gpu.NormalYBuffer[i] * cpu.NormalXBuffer[i] - gpu.NormalXBuffer[i] * cpu.NormalYBuffer[i],
                                            gpu.NormalXBuffer[i] * cpu.NormalXBuffer[i] + gpu.NormalYBuffer[i] * cpu.NormalYBuffer[i]));
            if (dn > 1e-2) nBad++;
        }
        _out.WriteLine($"{name}: normals off by > 0.01 rad at {nBad} of {same} escaped px with a finite float derivative ({inf} not finite)");
        if (same < W * H / 8) return;   // too deep for a float derivative: nothing to compare
        Assert.True(nBad <= same / 100, $"{name}: normals off at {nBad} of {same} px");
    }

    // ── Exact iteration (fixed-point BigInteger) ─────────────────────────────────

    private static BigInteger Fixed(double v)
    {
        if (v == 0) return BigInteger.Zero;
        long b = BitConverter.DoubleToInt64Bits(v);
        int exp = (int)((b >> 52) & 0x7FF);
        long mant = b & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        int shift = exp - 1075 + Bits;
        BigInteger r = shift >= 0 ? new BigInteger(mant) << shift : new BigInteger(mant) >> -shift;
        return v < 0 ? -r : r;
    }

    private static int Exact(BigInteger cr, BigInteger ci, int maxIter)
    {
        BigInteger er2 = new BigInteger(512 * 512) << Bits, zr = 0, zi = 0;
        for (int n = 0; n < maxIter; n++)
        {
            BigInteger zr2 = (zr * zr) >> Bits, zi2 = (zi * zi) >> Bits;
            if (zr2 + zi2 >= er2) return n;
            zi = ((zr * zi) >> (Bits - 1)) + ci;
            zr = zr2 - zi2 + cr;
        }
        return maxIter;
    }

    [Fact]
    public void The_Gpu_La_Frame_Matches_Exact_Iteration_At_Least_As_Well_As_The_Gpu_Bla_Frame()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var s = P448;
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = true;
        var la = Calc(s, k);
        var bla = Calc(s, k, DeepLaMode.Off);
        Assert.True(la.LastFrameUsedGpuLa && !bla.LastFrameUsedGpuLa && bla.LastFrameUsedGpuPerturbation);   // preconditions
        BigInteger cr = s.X.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l)), ci = s.Y.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l));
        double scale = 3.5 / Math.Max(W, H) / FfZoom(s);
        var rng = new Random(1630);
        var idx = Enumerable.Range(0, W * H).OrderBy(_ => rng.Next()).Take(16).ToArray();
        var ex = new int[idx.Length];
        Parallel.For(0, idx.Length, q =>
        {
            int x = idx[q] % W, y = idx[q] / W;
            ex[q] = Exact(cr + Fixed((x - W * 0.5) * scale), ci + Fixed((y - H * 0.5) * scale), s.Iter);
        });
        int okLa = 0, okBla = 0; double errLa = 0, errBla = 0;
        for (int q = 0; q < idx.Length; q++)
        {
            if (la.IterationBuffer[idx[q]] == ex[q]) okLa++;
            if (bla.IterationBuffer[idx[q]] == ex[q]) okBla++;
            errLa += Math.Abs((double)la.IterationBuffer[idx[q]] - ex[q]);
            errBla += Math.Abs((double)bla.IterationBuffer[idx[q]] - ex[q]);
        }
        _out.WriteLine($"exact: GPU LA {okLa}/{idx.Length} (err {errLa:F0}), GPU SA/BLA {okBla} (err {errBla:F0})");
        Assert.True(okLa >= okBla && errLa <= errBla);
    }

    [Fact]
    public void The_Route_Follows_The_Mode_And_The_Switches()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = true;
        // Below 1e30 Auto keeps the SA / BLA kernel.
        var shallow = Calc(P448, k, zoom: 1e28);
        Assert.True(shallow.LastFrameUsedGpuPerturbation && !shallow.LastFrameUsedGpuLa);
        Assert.DoesNotContain("LA", shallow.LastPrecisionLabel);
        // On takes the GPU LA kernel below 1e30 too (the kernel has it).
        var on = Calc(P448, k, DeepLaMode.On, zoom: 1e28);
        Assert.True(on.LastFrameUsedGpuPerturbation && on.LastFrameUsedGpuLa);
        // Off, acceleration off and the switch keep the SA / BLA kernel.
        Assert.False(Calc(P448, k, DeepLaMode.Off).LastFrameUsedGpuLa);
        var noAccel = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme, CenterX = P448.X[0], CenterXLo = P448.X[1], CenterX2 = P448.X[2], CenterX3 = P448.X[3],
            CenterY = P448.Y[0], CenterYLo = P448.Y[1], CenterY2 = P448.Y[2], CenterY3 = P448.Y[3],
            Zoom = FfZoom(P448), MaxIterations = P448.Iter, ColorMap = new HsvPalette(), GpuKernel = k, DisableAcceleration = true,
        };
        noAccel.Calculate(CancellationToken.None);
        Assert.False(noAccel.LastFrameUsedGpuLa);
        MandelbrotCalculator.UseGpuLa = false;
        var off = Calc(P448, k);
        Assert.True(off.LastFrameUsedGpuPerturbation && !off.LastFrameUsedGpuLa);
    }
}
