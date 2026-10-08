// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1166 — the GPU QD reference orbit.
//   • It now runs in bounded launches (a single launch of a long orbit tripped
//     the OS watchdog on a weak-fp64 card). Splitting must not change a bit: for
//     any launch size — one iteration per launch included, and an orbit that
//     escapes — the orbit equals the single-launch orbit limb for limb, and
//     matches the CPU's ComputeReferenceOrbitQD to QD round-off over the early
//     slots (the GPU's split-based TwoProduct and the CPU's FMA differ in the
//     last bit of the lowest limb, and chaos amplifies that along the orbit).
//   • MandelbrotCalculator says where the orbit was built
//     (LastFrameBuiltGpuReferenceOrbit), for the bench's refusal to time a CPU
//     fallback as a GPU number.
// The device is CUDA when present, else ILGPU's CPU accelerator: the same kernel
// code either way, so the split is checked everywhere.
// UseGpuReferenceOrbit is process-wide, hence the non-parallel collection.

using System;
using System.Reflection;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GpuReferenceOrbitCollection { public const string Name = "GpuReferenceOrbitStatic"; }

[Collection(GpuReferenceOrbitCollection.Name)]
public sealed class S1166ReferenceOrbitTests
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
    private const double EscapeRadius2 = 512.0 * 512.0;   // MandelbrotCalculator's

    // Bounded and chaotic (real c in [-2, 0.25] never escapes; -1.9 is in the
    // chaotic band), and two points that escape (283 and ~2800 iterations).
    private static readonly (double Cx, double Cy)[] Centres =
    {
        (-1.9, 0.0),
        (-1.1726999042772253, -0.2968356710071185),
        (-0.7436438870371587, 0.13182590420531197),
    };

    private static double[][] CpuOrbit(double cx, double cy, int maxIter, out int len)
    {
        bool saved = MandelbrotCalculator.UseGpuReferenceOrbit;
        MandelbrotCalculator.UseGpuReferenceOrbit = false;
        try
        {
            var c = new MandelbrotCalculator(8, 8)
            {
                ColorMap = new HsvPalette(), CenterX = cx, CenterY = cy, Zoom = 1e30, MaxIterations = maxIter,
            };
            len = c.BuildReferenceOrbit();
            Assert.False(c.LastFrameBuiltGpuReferenceOrbit);
            string[] names = { "_refZr", "_refZrLo", "_refZrX2", "_refZrX3", "_refZi", "_refZiLo", "_refZiX2", "_refZiX3" };
            var o = new double[8][];
            for (int i = 0; i < 8; i++) o[i] = (double[])typeof(MandelbrotCalculator).GetField(names[i], F)!.GetValue(c)!;
            return o;
        }
        finally { MandelbrotCalculator.UseGpuReferenceOrbit = saved; }
    }

    private static double[][] GpuOrbit(MandelbrotRefOrbitGpu gpu, double cx, double cy, int maxIter,
                                       int launchIterations, out int len, out bool escaped)
    {
        var o = new double[8][];
        for (int i = 0; i < 8; i++) o[i] = new double[maxIter + 1];
        int saved = MandelbrotRefOrbitGpu.FixedLaunchIterations;
        MandelbrotRefOrbitGpu.FixedLaunchIterations = launchIterations;
        try
        {
            bool ok = gpu.Compute(cx, 0, 0, 0, cy, 0, 0, 0, maxIter, EscapeRadius2,
                o[0], o[1], o[2], o[3], o[4], o[5], o[6], o[7], out len, out escaped);
            Assert.True(ok, gpu.LastError);
        }
        finally { MandelbrotRefOrbitGpu.FixedLaunchIterations = saved; }
        return o;
    }

    [Theory]
    [InlineData(0)]       // adaptive
    [InlineData(1)]       // one iteration per launch
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(100_000)] // one launch
    public void The_Split_Orbit_Equals_The_Cpu_Orbit_Limb_For_Limb(int launchIterations)
    {
        using var gpu = new MandelbrotRefOrbitGpu();
        foreach (var (cx, cy) in Centres)
        {
            const int maxIter = 3000;
            var one = GpuOrbit(gpu, cx, cy, maxIter, maxIter + 1, out int oneLen, out bool oneEscaped);
            Assert.Equal(1, gpu.LastLaunchCount);
            var g = GpuOrbit(gpu, cx, cy, maxIter, launchIterations, out int gpuLen, out bool escaped);
            Assert.Equal(oneLen, gpuLen);
            Assert.Equal(oneEscaped, escaped);
            Assert.Equal(gpuLen < maxIter, escaped);
            for (int limb = 0; limb < 8; limb++)
                for (int k = 0; k <= gpuLen; k++)
                    Assert.True(one[limb][k].Equals(g[limb][k]),
                        $"c=({cx},{cy}) launch={launchIterations}: limb {limb} slot {k}: one launch {one[limb][k]:R}, split {g[limb][k]:R}");

            // Against the CPU: same length, and QD round-off agreement early on.
            var cpu = CpuOrbit(cx, cy, maxIter, out int cpuLen);
            Assert.Equal(cpuLen, gpuLen);
            for (int k = 0; k <= Math.Min(40, gpuLen); k++)
                foreach (int part in new[] { 0, 4 })   // re, im: limbs [part, part + 3]
                {
                    double d = (cpu[part][k] - g[part][k]) + (cpu[part + 1][k] - g[part + 1][k])
                             + (cpu[part + 2][k] - g[part + 2][k]) + (cpu[part + 3][k] - g[part + 3][k]);
                    Assert.True(Math.Abs(d) <= 1e-55 * Math.Max(1.0, Math.Abs(cpu[part][k])),
                        $"c=({cx},{cy}) slot {k}: GPU vs CPU differ by {d:E2}");
                }
        }
    }

    [Fact]
    public void A_Long_Orbit_Takes_Several_Bounded_Launches()
    {
        using var gpu = new MandelbrotRefOrbitGpu();
        var (cx, cy) = Centres[0];
        GpuOrbit(gpu, cx, cy, 2000, 0, out _, out _);         // learns the device speed
        GpuOrbit(gpu, cx, cy, 2000, 250, out int len, out _);
        Assert.Equal(2000, len);
        Assert.Equal(8, gpu.LastLaunchCount);                  // 2000 / 250
    }

    [Fact]
    public void The_Calculator_Says_Where_The_Orbit_Was_Built()
    {
        bool saved = MandelbrotCalculator.UseGpuReferenceOrbit;
        try
        {
            var (cx, cy) = Centres[0];
            var c = new MandelbrotCalculator(8, 8)
            {
                ColorMap = new HsvPalette(), CenterX = cx, CenterY = cy, Zoom = 1e30, MaxIterations = 2000,
            };
            MandelbrotCalculator.UseGpuReferenceOrbit = false;
            c.BuildReferenceOrbit();
            Assert.False(c.LastFrameBuiltGpuReferenceOrbit);

            MandelbrotCalculator.UseGpuReferenceOrbit = true;
            c.CenterX3 = 1e-60;                                   // a new centre
            c.BuildReferenceOrbit();
            Assert.True(c.LastFrameBuiltGpuReferenceOrbit);
            Assert.NotEqual("", c.GpuReferenceOrbitDevice);

            c.BuildReferenceOrbit();                              // cached: not rebuilt
            Assert.False(c.LastFrameBuiltGpuReferenceOrbit);

            c.Zoom = 1e10;                                        // DD tier: never the GPU
            c.CenterX3 = 0;
            c.BuildReferenceOrbit();
            Assert.False(c.LastFrameBuiltGpuReferenceOrbit);
        }
        finally { MandelbrotCalculator.UseGpuReferenceOrbit = saved; }
    }
}
