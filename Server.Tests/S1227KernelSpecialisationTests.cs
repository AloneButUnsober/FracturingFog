// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1227 — the escape-time SP kernel is compiled once per fractal kind. G4.4 put
// every kind's step (Multibrot's inner loop, Phoenix's carried z and dz/dc) in
// one loop selected from the cbuffer at run time; the registers that union
// needed halved plain Mandelbrot on a GT 710. The loop now tests KIND, a
// literal per compiled variant. These pin the source side; the per-kind
// correctness is S1173H / S1173I (they now run the specialised variants), and
// the speed is MandelbrotGpuKernelBench.

using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1227KernelSpecialisationTests
{
    [Fact]
    public void The_Loop_Branches_On_The_Compile_Time_Kind()
    {
        foreach (string src in new[]
                 {
                     MandelbrotKernelSource.BuildBase(),
                     MandelbrotKernelSource.BuildColor("", "return float3(0, 0, 0);"),
                     MandelbrotKernelSource.BuildColorOrbit("", "return float3(0, 0, 0);", 1),
                 })
        {
            Assert.DoesNotContain("gFractalKind ==", src);
            Assert.Contains("KIND == 4", src);                       // the Multibrot step
            Assert.Contains("#define KIND FF_KIND", src);
            Assert.Contains("#define KIND gFractalKind", src);      // unspecialised fallback
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ForKind_Defines_The_Kind_Before_The_Source(int kind)
    {
        string src = MandelbrotKernelSource.BuildBase();
        Assert.Equal($"#define FF_KIND {kind}\n" + src, MandelbrotKernelSource.ForKind(src, kind));
    }
}
