// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-G / GPU parity plan G3.5 — the Coquaternion (split-quaternion) Mandelbrot
// gets a GPU kernel (CoquaternionGpuCalculator). Before this it had none and
// always rendered on the CPU ("no GPU kernel").
//
// The CPU CoquaternionMandelbrotCalculator is the reference. The GPU frame (ILGPU
// CPU accelerator) must match the CPU frame, plain and with shadows / AO /
// reflections / fog, at two slice constants — and the slice must reach the
// kernel: the GPU frame at one sliceW is far from the CPU frame at the other.
// (S1172Gpu3DAlbedoTests / S1173BGpu3DHdriTests / Froxel3DGpuTests cover the colour
// map, the post stack, the HDRI and the froxel hybrid for this family too.)

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1173GCoquaternionGpuTests
{
    private const int W = 64, H = 48;

    private static (uint[] color, GpuRoute route) Render(bool gpu, double sliceW, bool features)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        if (features)
        {
            fx.ShadowSteps = 24; fx.ShadowLightMask = 0x1;
            fx.AoSamples = 4; fx.AoStrength = 0.6;
            fx.ReflectionStrength = 0.4; fx.Metallic = 0.3;
            fx.FogDensity = 0.08; fx.VolumeSteps = 24;
        }
        var c = new CoquaternionMandelbrotCalculator(W, H)
        {
            ColorMap = ColorPalette.BuiltIns[39],   // HsvPhong3D
            Zoom = 1.0,
            FractalParameters = new FractalParameters { Lighting = fx, CoquaternionSliceW = sliceW },
        };
        c.Calculate(CancellationToken.None);
        return ((uint[])c.ColorBuffer.Clone(), c.LastGpuRoute);
    }

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            for (int s = 0; s < 24; s += 8)
                sum += Math.Abs((int)((a[i] >> s) & 0xFF) - (int)((b[i] >> s) & 0xFF));
        return sum / (a.Length * 3.0);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(0.35, false)]
    [InlineData(0.0, true)]
    [InlineData(0.35, true)]
    public void Gpu_Coquaternion_Matches_The_Cpu(double sliceW, bool features)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            string label = $"sliceW {sliceW}{(features ? " + shadows/AO/reflections/fog" : "")}";
            var g = Render(gpu: true, sliceW, features);
            Assert.Equal(GpuRouteState.Gpu, g.route.State);
            double same = MeanDrift(g.color, Render(gpu: false, sliceW, features).color);
            Assert.True(same < 1.0, $"{label}: GPU vs CPU mean drift {same:F2} (bound 1.0)");

            // The slice reached the kernel: the other slice's CPU frame is far away.
            double other = sliceW == 0.0 ? 0.35 : 0.0;
            double vsOther = MeanDrift(g.color, Render(gpu: false, other, features).color);
            Assert.True(vsOther > Math.Max(1.0, 8 * same),
                $"{label}: the GPU frame is as close to sliceW {other} ({vsOther:F2}) as to its own slice ({same:F2})");
        }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }
}
