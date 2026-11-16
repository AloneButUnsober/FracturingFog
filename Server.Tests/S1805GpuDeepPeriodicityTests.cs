// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1805 (slice S4 of #1800) — the periodicity early-out of the GPU deep kernels:
// CSPerturb, CSPerturbSA (SA + BLA), CSPerturbFX (float-exp) and CSPerturbF32 (scaled
// fp32). Between 32-step blocks a bit-exact repeat of the loop state (δ, m) — δ's float
// mantissa and exponent in the float-exp kernel — puts the pixel in set (#1368's orbit
// kernel check); with BLA the check waits until a skip can't stop at the cap across the
// next cycle. Compiled in under GpuPeriodicity (FF_GPU_PERIODICITY); Diagnostic writes
// smooth = −(the iteration a pixel was caught at). On the Vulkan device (skipped without
// one; the fp64 kernels also need shaderFloat64), the period-3 minibrot with its nucleus
// as a periodic reference (#1614: the reference stops at Z_{p+1}, the loop wraps at p),
// centred and shifted to take in the exterior:
//   • off, on and diagnostic give the same iterations, and the same smooth and final z /
//     dz on escaping pixels;
//   • precondition: the diagnostic catches most in-set pixels;
//   • the D3D11 kernels are covered by --gpudeepperiodprobe.

using System;
using System.Linq;
using FracturingFog.FFMath;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes GpuPeriodicity / PerturbBudgetMs / ForceFloatExpEntry
public sealed class S1805GpuDeepPeriodicityTests(ITestOutputHelper output) : IDisposable
{
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;

    public void Dispose()
    {
        GpuPeriodicity.Reset();
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        VulkanComputeKernel.ForceFloatExpEntry = false;
    }

    public enum Kernel { Plain, SaBla, FloatExp, ScaledF32 }

    private const double C = -1.7548776662466927;
    private const int Dim = 96, MaxIter = 5000, P = 3, N = Dim * Dim;
    private const double EscapeR2 = 512.0 * 512.0;

    private static (double[] Zr, double[] Zi) Reference()
    {
        var zr = new double[P + 1]; var zi = new double[P + 1];
        double x = 0;
        for (int i = 0; i <= P; i++) { zr[i] = x; x = x * x + C; }
        return (zr, zi);
    }

    private sealed class Frame
    {
        public readonly int[] It = new int[N];
        public readonly float[] Sm = new float[N], Zr = new float[N], Zi = new float[N], Dr = new float[N], Di = new float[N];
    }

    private static VulkanComputeKernel Device(Kernel kind)
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (kind is Kernel.Plain or Kernel.SaBla && !k.SupportsPerturbation) { k.Dispose(); Assert.Skip("no shaderFloat64"); }
        return k;
    }

    private static Frame Render(VulkanComputeKernel k, Kernel kind, int mode, double shift, double width)
    {
        var (zr, zi) = Reference();
        var f = new Frame();
        double scale = width / Dim;
        double offX0 = shift - Dim / 2, offY0 = -Dim / 2;
        GpuPeriodicity.Mode = mode;
        try
        {
            switch (kind)
            {
                case Kernel.Plain:
                    k.RunPerturb(Dim, Dim, scale, MaxIter, EscapeR2, offX0, offY0, zr, zi, P + 1, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);
                    break;
                case Kernel.SaBla:
                    var sa = new SeriesApproximation(zr, zi, P + 1);
                    var bla = new BlaTable(zr, zi, P + 1, Math.Sqrt(2.0) * 0.5 * Dim * scale);
                    k.RunPerturbSA(Dim, Dim, scale, MaxIter, EscapeR2, offX0, offY0, zr, zi, P + 1, 1e-3, sa.SafeMax,
                        sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI,
                        f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di, bla.GpuCoefficients, bla.Levels);
                    break;
                default:
                    VulkanComputeKernel.ForceFloatExpEntry = kind == Kernel.FloatExp;
                    k.RunPerturbFloatExp(Dim, Dim, scale, MaxIter, EscapeR2, offX0, offY0, zr, zi, P + 1, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);
                    Assert.Equal(kind == Kernel.ScaledF32, k.LastPerturbFloatExpWasScaledF32);
                    VulkanComputeKernel.ForceFloatExpEntry = false;
                    break;
            }
        }
        finally { GpuPeriodicity.Reset(); }
        return f;
    }

    private static int Bits(float x) => BitConverter.SingleToInt32Bits(x);

    [Theory]
    [InlineData(Kernel.Plain)]
    [InlineData(Kernel.SaBla)]
    [InlineData(Kernel.FloatExp)]
    [InlineData(Kernel.ScaledF32)]
    public void The_Gpu_Check_Changes_No_Pixel_And_Its_Diagnostic_Catches_The_Interior(Kernel kind)
    {
        using var k = Device(kind);
        MandelbrotKernelSource.PerturbBudgetMs = 0;
        // 0.03 wide: the minibrot (about 0.03 across) and its edge; then shifted 30 px.
        foreach (double shift in new[] { 0.0, 30.0 })
        {
            var off = Render(k, kind, GpuPeriodicity.Off, shift, 0.03);
            var on = Render(k, kind, GpuPeriodicity.On, shift, 0.03);
            var diag = Render(k, kind, GpuPeriodicity.Diagnostic, shift, 0.03);
            int inSet = off.It.Count(i => i >= MaxIter), caught = diag.Sm.Count(s => s < 0);
            output.WriteLine($"{kind} shift {shift}: {inSet}/{N} in set, diagnostic caught {caught}");
            Assert.Equal(off.It, on.It);
            Assert.Equal(off.It, diag.It);
            for (int i = 0; i < N; i++)
            {
                if (off.It[i] >= MaxIter) { Assert.True(on.Sm[i] == 0f, $"in-set pixel {i}: smooth {on.Sm[i]} with the check"); continue; }
                Assert.Equal(Bits(off.Sm[i]), Bits(on.Sm[i]));
                Assert.Equal(Bits(off.Sm[i]), Bits(diag.Sm[i]));
                Assert.Equal(Bits(off.Zr[i]), Bits(on.Zr[i]));
                Assert.Equal(Bits(off.Zi[i]), Bits(on.Zi[i]));
                Assert.Equal(Bits(off.Dr[i]), Bits(on.Dr[i]));
                Assert.Equal(Bits(off.Di[i]), Bits(on.Di[i]));
            }
            Assert.True(caught <= inSet);
            Assert.True(inSet > N / 4, $"precondition: only {inSet} in-set pixels");
            Assert.True(caught > inSet * 0.85, $"{kind}: the GPU check caught only {caught} of {inSet} in-set pixels");
            if (shift > 0) Assert.True(N - inSet > N / 10, $"precondition: only {N - inSet} escaping pixels");
        }
    }
}
