// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #623 (GPU parity plan, Phase 5) — the shipped #615 Phase 1 surround colour on
// the GPU path. The surround is a CPU pass over the finished frame
// (ApplyOutOfBoundsSurround, after the GPU dispatch and its readback), so a GPU
// frame must paint exactly the pixels a CPU frame paints, and nothing else.
// Checked for MandelbrotCalculator and EscapeTimeCalculator (Julia) on the
// Vulkan kernel; skipped without a Vulkan device.

using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S623GpuSurroundTests
{
    private const int W = 64, H = 48;
    private const uint Oob = 0xFF3366CCu;

    private static IColorMap Theme(bool surround) => DataDrivenColorThemes.Create(new ColorThemeData
    {
        Name = "OobGpuProbe",
        Category = "User",
        Kind = ColorThemeKind.Gradient,
        Stops =
        {
            new ColorStopData { Position = 0f, R = 0,   G = 0,   B = 0,   A = 255 },
            new ColorStopData { Position = 1f, R = 255, G = 255, B = 255, A = 255 },
        },
        OutOfBoundsColor = surround ? new InSetColorData(0x33, 0x66, 0xCC) { A = 0xFF } : null,
    })!;

    public enum Kind { Mandelbrot, Julia }

    private static (uint[] Px, bool Gpu) Render(Kind kind, IGpuKernelLike gpu, bool surround)
    {
        if (kind == Kind.Mandelbrot)
        {
            var c = new MandelbrotCalculator(W, H)
            {
                CenterX = 0, CenterY = 0, Zoom = 0.0015, MaxIterations = 150, ColorMap = Theme(surround),
                GpuKernel = gpu.Kernel, UseGpuCompute = gpu.Kernel != null,
            };
            c.Calculate(default);
            return ((uint[])c.ColorBuffer.Clone(), c.LastFrameUsedGpuCompute);
        }
        var e = new EscapeTimeCalculator(W, H)
        {
            FractalType = FractalType.Julia, CenterX = 0, CenterY = 0, Zoom = 0.0015, MaxIterations = 150,
            ColorMap = Theme(surround), GpuKernel = gpu.Kernel, UseGpuCompute = gpu.Kernel != null,
        };
        e.Calculate(default);
        return ((uint[])e.ColorBuffer.Clone(), e.LastGpuRoute.State == FracturingFog.Render.GpuRouteState.Gpu);
    }

    private readonly record struct IGpuKernelLike(FracturingFog.Rendering.IGpuKernel? Kernel);

    [Theory]
    [InlineData(Kind.Mandelbrot)]
    [InlineData(Kind.Julia)]
    public void A_Gpu_Frame_Paints_The_Same_Surround_As_A_Cpu_Frame(Kind kind)
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        using (k)
        {
            var gpu = new IGpuKernelLike(k);
            var (gpuOn, ran) = Render(kind, gpu, surround: true);
            Assert.True(ran, $"{kind}: the frame did not run on the GPU");
            var (gpuOff, _) = Render(kind, gpu, surround: false);
            var (cpuOn, cpuRan) = Render(kind, new IGpuKernelLike(null), surround: true);
            Assert.False(cpuRan);

            bool[] Surround(uint[] px) => px.Select(p => p == Oob).ToArray();
            var gpuMask = Surround(gpuOn);
            Assert.Equal(Surround(cpuOn), gpuMask);                 // same pixels as the CPU
            Assert.Contains(true, gpuMask);
            Assert.Contains(false, gpuMask);
            for (int i = 0; i < gpuOn.Length; i++)
                if (!gpuMask[i]) Assert.Equal(gpuOff[i], gpuOn[i]);   // nothing else touched
        }
    }
}
