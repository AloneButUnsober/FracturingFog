// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-D / GPU parity plan G3.2 — the KIFS Octahedron, Dodecahedron and
// MandelboxRot folds render on the GPU (KifsFoldGpuCalculator) instead of always
// falling back to the CPU. The kernel DEs are ports of the shipped CPU DEs, so the
// CPU frame is the reference: for every fold the GPU frame (ILGPU CPU accelerator)
// must stay within a small drift of the CPU frame, plain and with shadows, AO,
// reflections and volumetric fog on. The fold must actually reach the kernel: each
// fold's GPU frame is far from the CPU frame of every OTHER fold, so a kernel that
// ran the wrong DE (or Menger's) fails.

using System;
using System.Collections.Generic;
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

public sealed class S1173DKifsFoldGpuTests
{
    private const int W = 64, H = 48;

    private static readonly KifsFoldKind[] s_allFolds =
    {
        KifsFoldKind.Menger, KifsFoldKind.Sierpinski,
        KifsFoldKind.Octahedron, KifsFoldKind.Dodecahedron, KifsFoldKind.MandelboxRot,
    };

    public static TheoryData<KifsFoldKind, bool> Cases()
    {
        var d = new TheoryData<KifsFoldKind, bool>();
        foreach (var f in new[] { KifsFoldKind.Octahedron, KifsFoldKind.Dodecahedron, KifsFoldKind.MandelboxRot })
        { d.Add(f, false); d.Add(f, true); }
        return d;
    }

    private static LightingFxData Fx(bool gpu, bool features)
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
        return fx;
    }

    private static (uint[] color, GpuRoute route) Render(KifsFoldKind fold, LightingFxData fx)
    {
        var c = new KifsCalculator(W, H)
        {
            ColorMap = ColorPalette.BuiltIns[0],
            Zoom = 1.0,
            FractalParameters = new FractalParameters { Lighting = fx, KifsFold = fold },
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
    [MemberData(nameof(Cases))]
    public void Gpu_Fold_Matches_The_Cpu_Fold(KifsFoldKind fold, bool features)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            string label = $"{fold}{(features ? " + shadows/AO/reflections/fog" : "")}";
            var g = Render(fold, Fx(gpu: true, features));
            Assert.Equal(GpuRouteState.Gpu, g.route.State);

            double same = MeanDrift(g.color, Render(fold, Fx(gpu: false, features)).color);
            Assert.True(same < 1.5, $"{label}: GPU vs CPU mean drift {same:F2} (bound 1.5)");

            // The right DE ran: every other fold's CPU frame is far away.
            var others = new List<string>();
            foreach (var other in s_allFolds)
            {
                if (other == fold) continue;
                double d = MeanDrift(g.color, Render(other, Fx(gpu: false, features)).color);
                if (!(d > Math.Max(2.0, 8 * same))) others.Add($"{other} {d:F2}");
            }
            Assert.True(others.Count == 0,
                $"{label}: the GPU frame ({same:F2} from its own fold) is as close to {string.Join(", ", others)}");
        }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    [Fact]
    public void Kernel_Constants_Are_The_Cpu_Ones()
    {
        // The kernel takes its constants from the host; they must be exactly what the
        // CPU DE uses (same values -> same bits). #1197.
        var m = KifsFoldGpuParams.For(KifsGpuFold.MandelboxRot, 2, 1, 1, 1, 8, 10);
        Assert.Equal(Math.Cos(KifsCalculator.MandelboxRotAngle), m.RotA);
        Assert.Equal(Math.Sin(KifsCalculator.MandelboxRotAngle), m.RotB);
        Assert.Equal(Math.PI / 48.0, KifsCalculator.MandelboxRotAngle);

        var d = KifsFoldGpuParams.For(KifsGpuFold.Dodecahedron, 2, 1, 1, 1, 8, 10);
        var n = KifsCalculator.DodecaNormals;
        Assert.Equal(n[0], (d.N1X, d.N1Y, d.N1Z));
        Assert.Equal(n[1], (d.N2X, d.N2Y, d.N2Z));
        Assert.Equal(n[2], (d.N3X, d.N3Y, d.N3Z));
        foreach (var v in n) Assert.Equal(1.0, v.X * v.X + v.Y * v.Y + v.Z * v.Z, 12);
    }
}
