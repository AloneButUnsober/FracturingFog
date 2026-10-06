// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-F / GPU parity plan G3.3 — QuatMandel dual-orbit surface colour (#909) in
// the GPU kernel. Before this, QMandelDualOrbitColor forced the CPU. The kernel now
// runs QuatMandelbrotCalculator.DualOrbitSurfaceScalar's twin at each hit and feeds
// it to the colour-map LUT as the smooth value (GpuKernelUtils.SurfaceAlbedoAt),
// with the LUT baked out to smooth 255.
//
// The CPU frame is the reference: the GPU dual-orbit frame (ILGPU CPU accelerator)
// must match the CPU dual-orbit frame, track the seed the way the CPU does, and be
// far from the CPU's standard colouring — so a kernel that ignored the flag (or the
// seed) fails.

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

public sealed class S1173FQuatMandelDualOrbitGpuTests
{
    private const int W = 64, H = 48;

    private static (uint[] color, GpuRoute route) Render(bool gpu, bool dual, double seedX, int theme, bool features = false)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        if (features)
        {
            fx.ShadowSteps = 24; fx.ShadowLightMask = 0x1;
            fx.AoSamples = 4; fx.AoStrength = 0.6;
            fx.ReflectionStrength = 0.3;
        }
        var c = new QuatMandelbrotCalculator(W, H)
        {
            ColorMap = ColorPalette.BuiltIns[theme],
            Zoom = 1.0,
            FractalParameters = new FractalParameters
            {
                Lighting = fx,
                QMandelDualOrbitColor = dual,
                QMandelDualSeedX = seedX,
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
    [InlineData(0, false)]    // PhongStone — normal-dependent theme (9x9 LUT grid)
    [InlineData(40, false)]   // MonoBandPhong3D
    [InlineData(0, true)]     // + shadows / AO / reflections
    public void Gpu_Dual_Orbit_Colour_Matches_The_Cpu(int theme, bool features)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            string label = $"theme {theme}{(features ? " + features" : "")}";
            var g = Render(gpu: true, dual: true, 0.4, theme, features);
            Assert.Equal(GpuRouteState.Gpu, g.route.State);
            uint[] cpu = Render(gpu: false, dual: true, 0.4, theme, features).color;
            double same = MeanDrift(g.color, cpu);
            Assert.True(same < 1.5, $"{label}: GPU vs CPU dual-orbit drift {same:F2} (bound 1.5)");

            // The flag reached the kernel: the CPU's standard colouring is far away.
            // (PhongStone's colour leans on the normal more than the smooth value, so
            // its gap is small in absolute terms — 0.9-1.3, still >8x the drift.)
            double vsStandard = MeanDrift(g.color, Render(gpu: false, dual: false, 0.4, theme, features).color);
            Assert.True(vsStandard > Math.Max(0.5, 8 * same),
                $"{label}: GPU dual frame is as close to the standard colouring ({vsStandard:F2}) as to dual ({same:F2})");

            // The seed reached the kernel: another seed moves the GPU frame the way it moves the CPU frame.
            var g2 = Render(gpu: true, dual: true, -0.6, theme, features).color;
            double seedSame = MeanDrift(g2, Render(gpu: false, dual: true, -0.6, theme, features).color);
            double seedMove = MeanDrift(g2, g.color);
            Assert.True(seedSame < 1.5, $"{label}: GPU vs CPU at seed -0.6 drift {seedSame:F2}");
            Assert.True(seedMove > Math.Max(0.5, 8 * seedSame), $"{label}: moving the seed barely changed the GPU frame ({seedMove:F2})");
        }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    [Fact]
    public void Lut_Spans_The_Dual_Orbit_Range_Only_When_Asked()
    {
        var sp = new GpuShadingParams();
        GpuAlbedoLut.Bake(ColorPalette.BuiltIns[40], 192.0, 0.5, 10, ref sp);
        double plain = sp.AlbedoSMax;
        Assert.True(plain < 255, $"the standard LUT range ({plain:F1}) is below the dual-orbit range — the test premise");
        GpuAlbedoLut.Bake(ColorPalette.BuiltIns[40], 192.0, 0.5, 10, ref sp, 256.0);
        Assert.Equal(256.0, sp.AlbedoSMax);
        GpuAlbedoLut.Bake(ColorPalette.BuiltIns[40], 192.0, 0.5, 10, ref sp);
        Assert.Equal(plain, sp.AlbedoSMax);   // off again → the standard range, unchanged
    }
}
