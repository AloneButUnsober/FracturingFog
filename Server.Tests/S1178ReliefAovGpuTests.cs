// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1178 — the Relief 3D GPU raymarch encodes the DebugAov views in the shader
// (EncodeAov, a twin of ShadingPipeline.EncodeAov), so a view no longer forces the
// CPU trace. HeightfieldRaymarch2D hands the kernel the view through
// ReliefUniforms.AovMode; the twin (ReliefRaymarchGpu) and the HLSL return the
// chosen component from ShadeFlat at the point the CPU Shade does (before ambient,
// IBL, reflections, glass and fog). The Step-count view now carries the real march
// step on every path (the CPU trace used to pass 0: a solid blue view).
// Checked, for every view, with shadows, AO, specular and fog on:
//   • the GPU kernel's CPU twin — plugged in at the kernel seam, so the frame
//     really takes the GPU branch — against the CPU relief trace, an independent
//     implementation (HeightfieldRaymarch2D → ShadingPipeline.Shade);
//   • the real Vulkan relief kernel (float HLSL) against the twin (skipped without
//     a Vulkan device);
//   • non-vacuity: every view differs from the beauty and from the others, and the
//     Step-count view is no longer one colour.
// The D3D11 kernel (FXC) is covered by the --reliefgpuraymarch probe's AOV pass.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1178ReliefAovGpuTests(ITestOutputHelper output)
{
    private const int W = 160, H = 120;

    private sealed class TwinKernel : IReliefRaymarchKernel
    {
        public int Calls;
        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
            float[]? aovNormalXyz = null, float[]? aovDepth = null)
        {
            Calls++;
            ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, keep, albedo, dst, out _, out _, aovNormalXyz, aovDepth);
        }
        public void Dispose() { }
    }

    // Two bumps (a tall one casting a shadow onto a lower plateau) over a floor.
    private static (float[] height, uint[] albedo) Field()
    {
        var hbuf = new float[W * H];
        var albedo = new uint[W * H];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double u = (x + 0.5) / W - 0.5, v = (y + 0.5) / H - 0.5;
            double r1 = Math.Sqrt((u + 0.15) * (u + 0.15) + v * v) / 0.22;
            double r2 = Math.Sqrt((u - 0.2) * (u - 0.2) + (v - 0.1) * (v - 0.1)) / 0.3;
            float a = r1 >= 1 ? 0f : (float)(0.5 * (1 + Math.Cos(Math.PI * r1)));
            float b = r2 >= 1 ? 0f : (float)(0.25 * (1 + Math.Cos(Math.PI * r2)));
            hbuf[y * W + x] = Math.Max(a, b);
            albedo[y * W + x] = 0xFF000000u | ((uint)(80 + 120 * u + 60) << 16) | ((uint)(90 + 100 * v + 50) << 8) | 0x60u;
        }
        return (hbuf, albedo);
    }

    private static FractalParameters Params(AovView view)
    {
        var fx = LightingFxData.CreateDefault();
        var l = fx.Light1; l.Phi = 1.2; l.Theta = 1.0; l.Intensity = 1.4; fx.Light1 = l;
        fx.ShadowSteps = 32; fx.ShadowLightMask = 0x1; fx.ShadowSoftK = 8.0;
        fx.AoSamples = 5; fx.AoStrength = 0.8;
        fx.SpecularStrength = 1.0; fx.Roughness = 0.35;
        // Fog on: a view skips the hit fog (the CPU Shade returns before it), the
        // sky-miss in-scatter still runs.
        fx.FogDensity = 0.6; fx.VolumeSteps = 16;
        fx.DebugAov = view;
        return new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = true,
            Relief2DHeightScale = 1.4, Relief2DCameraAzimuthDeg = 25, Relief2DCameraElevationDeg = 35,
            Relief2DCameraFovDeg = 55, Relief2DGroundPlane = true, Relief2DAutoShade = false,
            Lighting = fx,
        };
    }

    private static uint[] Render(AovView view, IReliefRaymarchKernel? kernel)
    {
        var (hb, al) = Field();
        var dst = new uint[W * H];
        HeightfieldRaymarch2D.Render(al, hb, W, H, W, H, Params(view), dst, out _, kernel);
        return dst;
    }

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long s = 0;
        for (int i = 0; i < a.Length; i++)
            for (int k = 0; k < 24; k += 8)
                s += Math.Abs((int)((a[i] >> k) & 0xFF) - (int)((b[i] >> k) & 0xFF));
        return s / (a.Length * 3.0);
    }

    public static IEnumerable<object[]> Views() =>
        Enum.GetValues<AovView>().Where(v => v != AovView.Beauty).Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(Views))]
    public void The_Gpu_Twin_Encodes_Each_View_Like_The_Cpu_Trace(AovView view)
    {
        var twin = new TwinKernel();
        var gpu = Render(view, twin);
        Assert.True(twin.Calls > 0, $"{view}: the view still forced the CPU trace");
        var cpu = Render(view, null);
        double drift = MeanDrift(gpu, cpu);
        output.WriteLine($"{view}: twin vs CPU trace {drift:F2}");
        // The twin's march, shading and encode run in double like the CPU; what
        // differs is the CPU's supersample-free path details (bilinear albedo, the
        // shading probe), a fraction of a level on the beauty.
        Assert.True(drift < 1.5, $"{view}: twin vs CPU trace mean drift {drift:F2}");
    }

    [Fact]
    public void Every_View_Is_Distinct_And_The_Step_View_Carries_Steps()
    {
        var beauty = Render(AovView.Beauty, new TwinKernel());
        var views = Enum.GetValues<AovView>().Where(v => v != AovView.Beauty)
            .ToDictionary(v => v, v => Render(v, new TwinKernel()));
        foreach (var (v, img) in views)
            Assert.True(MeanDrift(img, beauty) > 2, $"{v} looks like the beauty");
        var list = views.ToList();
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
                Assert.True(!list[i].Value.SequenceEqual(list[j].Value), $"{list[i].Key} == {list[j].Key}");

        // The Step-count view used to be solid blue on the CPU trace (hit step 0).
        int distinct = Render(AovView.StepCount, null).Distinct().Count();
        Assert.True(distinct > 8, $"the CPU Step-count view has only {distinct} colours");
        int distinctGpu = views[AovView.StepCount].Distinct().Count();
        Assert.True(distinctGpu > 8, $"the GPU-twin Step-count view has only {distinctGpu} colours");
    }

    [Fact]
    public void The_Vulkan_Kernel_Encodes_Each_View_Like_Its_Twin()
    {
        using var vk = ReliefRaymarchVulkanKernel.TryCreateWithOwnContext();
        if (vk is null) Assert.Skip("no Vulkan device on this host");
        foreach (var view in Enum.GetValues<AovView>())
        {
            var twin = Render(view, new TwinKernel());
            var gpu = Render(view, vk);
            double drift = MeanDrift(gpu, twin);
            int far = 0;
            for (int i = 0; i < gpu.Length; i++)
                for (int k = 0; k < 24; k += 8)
                    if (Math.Abs((int)((gpu[i] >> k) & 0xFF) - (int)((twin[i] >> k) & 0xFF)) > 24) { far++; break; }
            int diff = gpu.Zip(twin).Count(q => q.First != q.Second);
            output.WriteLine($"{view}: Vulkan vs twin mean {drift:F3}, pixels differing {diff}, > 24 levels off {far}");
            // float shader vs double twin: silhouette pixels may flip hit / miss.
            Assert.True(drift < 1.5, $"{view}: Vulkan vs twin mean drift {drift:F2}");
            Assert.True(far < gpu.Length / 50, $"{view}: {far} pixels far off the twin");
        }
    }
}
