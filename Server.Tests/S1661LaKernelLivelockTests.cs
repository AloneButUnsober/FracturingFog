// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1661 — the LA stage kernel (LaKernelSource, #1630) hung the device under D3D11 on a
// GeForce GT 710 (TDR, even on an 8x8 frame). The cause was a livelock in the kernel's
// plain tail, not the driver: at reference index 0, Z_0 = 0, so z = δ and the rebase
// test |z|² < |δ|² must be false. NVIDIA's D3D11 compiler rounded the two sums
// differently, the test came out true by an ulp, and the pixel climbed back to the
// stages without a step, was handed straight back, and cycled forever. The kernel now
// skips the magnitude test at index 0.
//
// Checked:
//   • (no GPU) the kernel source carries the index-0 guard, and the test hook
//     (SkewPlainMagnitudesForTests) compiles the forced rounding difference in;
//   • (Vulkan; skipped without a device or fp64) with that difference forced at index 0
//     — |δ|² nudged up, so the old test would be true at every such step — the kernel
//     finishes and its frame is the unskewed frame bit for bit: iterations, smooth
//     values, final z and dz/dc. The views are ones whose pixels run the plain tail
//     (the CPU evaluator's own counter); on the p448 view the unguarded kernel left
//     40 % of a 64x64 frame's pixels cycling on the GT 710.
//
// The mutation (guard removed) is a hang by construction, so it is checked outside the
// suite: --gpulaprobe with FF_GPULA_WARP=1 FF_GPULA_SKEW=1 on the software D3D11 device
// (no TDR) must time out with the guard removed and finish with it. On D3D11 hardware
// --gpulaprobe itself is the check (the GT 710 has the rounding difference built in).

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes UseGpuPerturbation / UseGpuLa / PerturbBudgetMs / the skew hook
public sealed class S1661LaKernelLivelockTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation, _savedLa = MandelbrotCalculator.UseGpuLa;
    private readonly bool _savedSkew = LaKernelSource.SkewPlainMagnitudesForTests;

    public S1661LaKernelLivelockTests(ITestOutputHelper output)
    {
        _out = output;
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // no too-slow abort under the suite's load
    }

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotCalculator.UseGpuLa = _savedLa;
        LaKernelSource.SkewPlainMagnitudesForTests = _savedSkew;
    }

    private const int W = 64, H = 36;

    // The S1630 views (bench-kit p448 and path; FF's built-in "3E47 Test").
    private sealed record Scene(string Name, double[] X, double[] Y, double Zoom, int Iter);

    private static readonly Scene P448 = new("p448",
        [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50, -1.0894092006534645e-66, -5.481175082318642e-83, -1.5340609125118276e-99, 1.4285946495395532e-116],
        [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50, -4.820603700098655e-67, -3.1189999875481587e-83, 1.2339668272603997e-99, -9.480108552256602e-117],
        5.176068e33 * 3.5 * H / (4.0 * W), 13440);
    private static readonly Scene Path = new("path",
        [-0.028041054305504548, 1.3292199503012176e-18, -5.27591145479967e-35, 2.798737894555097e-51, -1.4491927624390092e-67, -6.233008271562717e-84, 4.7734689309385144e-101, 2.9022031733306904e-117],
        [-0.6948927538996524, 5.189489838305484e-17, 5.567870094609155e-34, 1.7818449158374648e-50, 1.8079413574553354e-68, 2.5201987988615068e-85, -7.246511953022119e-102, -6.412330137037785e-118],
        1e33 * 3.5 * H / (4.0 * W), 2859);
    private static readonly Scene E47 = new("3e47",
        [-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51, 0, 0, 0, 0],
        [-5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55, 0, 0, 0, 0],
        3e47, 20000);

    public static TheoryData<string> Scenes => new() { "p448", "path", "3e47" };
    private static Scene ByName(string n) => n switch { "p448" => P448, "path" => Path, _ => E47 };

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationLa) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        return k;
    }

    private static MandelbrotCalculator Calc(Scene s, VulkanComputeKernel? k)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = s.X[0], CenterXLo = s.X[1], CenterX2 = s.X[2], CenterX3 = s.X[3], CenterX4 = s.X[4], CenterX5 = s.X[5], CenterX6 = s.X[6], CenterX7 = s.X[7],
            CenterY = s.Y[0], CenterYLo = s.Y[1], CenterY2 = s.Y[2], CenterY3 = s.Y[3], CenterY4 = s.Y[4], CenterY5 = s.Y[5], CenterY6 = s.Y[6], CenterY7 = s.Y[7],
            Zoom = s.Zoom, MaxIterations = s.Iter, ColorMap = new HsvPalette(), GpuKernel = k, LaMode = DeepLaMode.Auto,
        };
        c.Calculate(CancellationToken.None);
        return c;
    }

    // ── No GPU ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_Kernel_Source_Guards_The_Magnitude_Rebase_At_Index_Zero()
    {
        bool saved = LaKernelSource.SkewPlainMagnitudesForTests;
        try
        {
            LaKernelSource.SkewPlainMagnitudesForTests = false;
            string plain = LaKernelSource.Build();
            Assert.Contains("(j > 0 && zmag2 < dmag2) || j + 1 >= gRefLen", plain);
            // No unguarded form left beside it.
            Assert.DoesNotContain("if (zmag2 < dmag2", plain);
            Assert.DoesNotContain("#define LA_SKEW_J0", plain);

            LaKernelSource.SkewPlainMagnitudesForTests = true;
            string skewed = LaKernelSource.Build();
            Assert.Contains("#define LA_SKEW_J0", skewed);
            Assert.Contains("#ifdef LA_SKEW_J0", skewed);
            // The hook changes nothing else: the same text after the define.
            Assert.Equal(plain, skewed.Replace("#define LA_SKEW_J0 1\n", ""));
            // The skew sits before the rebase test it is meant to upset.
            Assert.True(skewed.IndexOf("#ifdef LA_SKEW_J0", StringComparison.Ordinal)
                        < skewed.IndexOf("(j > 0 && zmag2 < dmag2)", StringComparison.Ordinal));
        }
        finally { LaKernelSource.SkewPlainMagnitudesForTests = saved; }
    }

    // ── Vulkan ───────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Scenes))]
    public void A_Rounding_Difference_At_Index_Zero_Neither_Hangs_Nor_Changes_The_Frame(string name)
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        var s = ByName(name);

        // Precondition, from the CPU evaluator's own counter: pixels of this view run the
        // plain tail, which is where the old kernel cycled.
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = Calc(s, null);
        Assert.True(cpu.LastFrameUsedLa && !cpu.LastFrameUsedGpuPerturbation);
        _out.WriteLine($"{name}: CPU LA plain steps {cpu.LastLaWork.PlainSteps}");
        Assert.True(cpu.LastLaWork.PlainSteps > 0, "the view never reaches the plain tail");

        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = true;

        MandelbrotCalculator Gpu(bool skew)
        {
            LaKernelSource.SkewPlainMagnitudesForTests = skew;   // read when the kernel first compiles
            using var k = Device();
            var c = Calc(s, k);
            Assert.True(c.LastFrameUsedGpuLa, $"the LA kernel did not run: {c.LastGpuRoute.Detail}");
            return c;
        }

        var plain = Gpu(false);
        var skewed = Gpu(true);   // returning at all is the first half of the check

        Assert.Equal(plain.IterationBuffer, skewed.IterationBuffer);
        Assert.True(plain.SmoothBuffer.AsSpan(0, W * H).SequenceEqual(skewed.SmoothBuffer.AsSpan(0, W * H)), "smooth values differ");
        Assert.True(plain.FinalZrBuffer.AsSpan(0, W * H).SequenceEqual(skewed.FinalZrBuffer.AsSpan(0, W * H)), "final z (re) differs");
        Assert.True(plain.FinalZiBuffer.AsSpan(0, W * H).SequenceEqual(skewed.FinalZiBuffer.AsSpan(0, W * H)), "final z (im) differs");
        Assert.True(Bits(plain.FinalDrBuffer).SequenceEqual(Bits(skewed.FinalDrBuffer)), "final dz/dc (re) differs");
        Assert.True(Bits(plain.FinalDiBuffer).SequenceEqual(Bits(skewed.FinalDiBuffer)), "final dz/dc (im) differs");

        // And the frame is still the right one (the S1630 bound against CPU LA).
        double d = skewed.IterationBuffer.Zip(cpu.IterationBuffer).Count(p => p.First != p.Second) / (double)(W * H);
        Assert.True(d < 0.02, $"{name}: skewed GPU LA vs CPU LA {d:P2}");
        Assert.Contains(skewed.IterationBuffer, v => v > 0 && v < s.Iter);   // not a blank frame
    }

    // The derivative overflows a float on deep views (inf / NaN): compare bit patterns.
    private static int[] Bits(float[] a) => a.Take(W * H).Select(BitConverter.SingleToInt32Bits).ToArray();
}
