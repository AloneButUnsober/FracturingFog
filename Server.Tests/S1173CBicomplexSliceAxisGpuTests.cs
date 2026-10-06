// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-C / GPU parity plan G3.1 — the Bicomplex GPU kernel takes the slice axis
// (BicomplexGpuParams.SliceAxis) instead of hardcoding K, so non-K axes no longer
// fall back to the CPU. The CPU BicomplexDE packing is the reference: for every
// axis the GPU frame (ILGPU CPU accelerator) must match the CPU frame of the SAME
// axis, and must NOT match the CPU K frame — a kernel that ignored the axis would
// pass the first check only by accident of a symmetric scene, never the second.
// The axis also rides the region snapshot, so --param BicomplexSliceAxis=N and
// the Command builder's live seed carry it.

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

public sealed class S1173CBicomplexSliceAxisGpuTests
{
    private const int W = 64, H = 48;
    // Off-zero so the four packings give different sets (sliceW = 0 makes some coincide).
    private const double SliceW = 0.4;

    private static (uint[] color, GpuRoute route) Render(BicomplexSliceAxis axis, bool gpu)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        var c = new BicomplexMandelbrotCalculator(W, H)
        {
            ColorMap = ColorPalette.BuiltIns[0],
            Zoom = 1.0,
            FractalParameters = new FractalParameters
            {
                Lighting = fx, BicomplexSliceW = SliceW, BicomplexSliceAxis = axis,
            },
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
    [InlineData(BicomplexSliceAxis.K)]
    [InlineData(BicomplexSliceAxis.J)]
    [InlineData(BicomplexSliceAxis.I)]
    [InlineData(BicomplexSliceAxis.R)]
    public void Gpu_Kernel_Renders_Every_Slice_Axis_Like_The_Cpu(BicomplexSliceAxis axis)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            var g = Render(axis, gpu: true);
            Assert.Equal(GpuRouteState.Gpu, g.route.State);

            double same = MeanDrift(g.color, Render(axis, gpu: false).color);
            Assert.True(same < 3.0, $"{axis}: GPU vs CPU mean drift {same:F2} (bound 3)");

            if (axis != BicomplexSliceAxis.K)
            {
                double vsK = MeanDrift(g.color, Render(BicomplexSliceAxis.K, gpu: false).color);
                // Measured: own axis 0.04-0.07, vs K 3.5-4.0 (the K kernel on a J/I/R scene).
                Assert.True(vsK > Math.Max(1.0, 10 * same),
                    $"{axis}: the GPU frame is as close to the CPU K frame ({vsK:F2}) as to its own axis ({same:F2}) — the kernel ignores the axis");
            }
        }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    [Theory]
    [InlineData(BicomplexSliceAxis.J)]
    [InlineData(BicomplexSliceAxis.I)]
    [InlineData(BicomplexSliceAxis.R)]
    public void Region_Snapshot_Carries_The_Slice_Axis(BicomplexSliceAxis axis)
    {
        var live = new FractalParameters { BicomplexSliceAxis = axis };
        var snap = RegionFractalParams.Snapshot(FractalType.BicomplexMandelbrot, live)!;
        Assert.Equal((int)axis, snap.BicomplexSliceAxis);

        var recalled = new FractalParameters();
        System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(
            System.Text.Json.JsonSerializer.Serialize(snap))!.ApplyTo(recalled);
        Assert.Equal(axis, recalled.BicomplexSliceAxis);

        Assert.Null(RegionFractalParams.Snapshot(FractalType.BicomplexMandelbrot, new FractalParameters())!
            .BicomplexSliceAxis);   // K default is omitted

        // --param BicomplexSliceAxis=N goes through the same property.
        var overlay = RegionFractalParams.FromKeyValues(
            new[] { new System.Collections.Generic.KeyValuePair<string, string>("BicomplexSliceAxis", ((int)axis).ToString()) },
            FractalType.BicomplexMandelbrot, out var err);
        Assert.True(overlay != null, err);
        var viaParam = new FractalParameters();
        overlay!.ApplyTo(viaParam);
        Assert.Equal(axis, viaParam.BicomplexSliceAxis);
    }

    [Fact]
    public void Lone_Family_Param_Lands_Without_Camera_Keys()
    {
        // Family-unique 3D keys used to apply only inside the Cam3D camera block, so a
        // --param without the camera keys was silently dropped (batch parity, #997).
        var overlay = RegionFractalParams.FromKeyValues(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("KleinianDeFactor", "0.5"),
            new System.Collections.Generic.KeyValuePair<string, string>("QMandelDualSeedX", "0.25"),
        }, FractalType.Kleinian, out var err);
        Assert.True(overlay != null, err);
        var p = new FractalParameters();
        overlay!.ApplyTo(p);
        Assert.Equal(0.5, p.KleinianDeFactor);
        Assert.Equal(0.25, p.QMandelDualSeedX);
    }
}
