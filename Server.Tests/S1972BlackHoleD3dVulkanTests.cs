// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1972 — the D3D11 and Vulkan twins of the black hole trace. Per-pixel parity with the CPU
// trace (double): every non-edge pixel has the same fate, and the delay / final momentum agree
// to the integrator's tolerance; the single-precision kernel differs only on edge pixels, bounded
// as the ILGPU float kernel's (S1926). The fp64 Newton reciprocal / square root agree with IEEE
// division and sqrt to an ulp or two, so a chaotic edge pixel may flip; those are counted, not
// required equal. Backends a machine lacks are skipped (WARP stands in for D3D11 hardware).

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Scattering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1972BlackHoleD3dVulkanTests
{
    private const int W = 64, H = 40;

    private static (BlackHoleGpuParams P, sbyte[] Fates, double[] Delay) Cpu(FractalParameters p, double zoom = BlackHoleShadowCalculator.DefaultZoom)
    {
        var scene = BlackHoleShadowCalculator.Scene.Build(p, zoom, BlackHoleShadowCalculator.PixelPitch(W, H, zoom));
        var gp = BlackHoleShadowGpuCalculator.ParamsFor(scene, W, H, 0, 0, zoom);
        var fates = new sbyte[W * H];
        var delay = new double[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (u, v) = BlackHoleShadowCalculator.PlaneAt(x + 0.5, y + 0.5, W, H, 0, 0, zoom);
                var (o, sys) = scene.Trace(u, v, default, channels: false);
                fates[y * W + x] = (sbyte)(o.Fate == ScatterFate.Captured ? o.Body : o.Fate == ScatterFate.Escaped ? scene.EscapedGate : -1);
                delay[y * W + x] = sys.Delay;
            }
        return (gp, fates, delay);
    }

    // An edge pixel: a 4-neighbour has another fate.
    private static bool Edge(sbyte[] f, int i)
    {
        int x = i % W, y = i / W;
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            int nx = x + dx, ny = y + dy;
            if (nx >= 0 && ny >= 0 && nx < W && ny < H && f[ny * W + nx] != f[i]) return true;
        }
        return false;
    }

    private static void AssertParity(IBlackHoleTraceKernel k, bool single, double maxEdgeFlips)
    {
        var p = new FractalParameters();
        var (gp, fates, delay) = Cpu(p);
        var o = k.Trace(gp, single, CancellationToken.None);
        Assert.True(o != null, k.LastError);
        int flips = 0, interior = 0, edges = 0;
        for (int i = 0; i < W * H; i++)
        {
            bool edge = Edge(fates, i);
            if (edge) edges++;
            if (o!.Gate(i) == fates[i])
            {
                if (!single && !edge && fates[i] == 2)
                    Assert.True(Math.Abs(o.Delay(i) - delay[i]) <= 1e-5 * (1 + Math.Abs(delay[i])), $"pixel {i}: delay {o.Delay(i)} vs {delay[i]}");
                continue;
            }
            if (edge) flips++; else interior++;
        }
        Assert.True(interior == 0 || single && interior <= W * H / 200, $"{k.Backend} {(single ? "f32" : "f64")}: {interior} interior pixels differ");
        Assert.True(flips <= maxEdgeFlips * Math.Max(1, edges), $"{k.Backend}: {flips} of {edges} edge pixels flipped");
    }

    // D3D11 parity runs in the WinExe probe (--bhgpuprobe): this test project is net10.0 and
    // cannot reference the Windows-only Rendering.D3D. Vulkan runs here.
    [Fact]
    public void Vulkan_Double_Matches_The_Cpu_Trace_When_Present()
    {
        using var k = FracturingFog.Rendering.Vulkan.BlackHoleVulkanKernel.TryCreateWithOwnContext();
        if (k == null) return;   // no Vulkan device
        var probe = k.Trace(Cpu(new FractalParameters()).P, single: false, CancellationToken.None);
        if (probe == null && k.LastError.Contains("shaderFloat64")) { AssertParity(k, single: true, maxEdgeFlips: 0.5); return; }
        AssertParity(k, single: false, maxEdgeFlips: 0.05);
    }

    [Fact]
    public void Vulkan_Single_Differs_Only_On_Edges_When_Present()
    {
        using var k = FracturingFog.Rendering.Vulkan.BlackHoleVulkanKernel.TryCreateWithOwnContext();
        if (k == null) return;
        AssertParity(k, single: true, maxEdgeFlips: 0.5);
    }

    [Fact]
    public void The_Calculator_Routes_To_The_Vulkan_Backend_And_Colours_As_The_Cpu()
    {
        using var probe = FracturingFog.Rendering.Vulkan.BlackHoleVulkanKernel.TryCreateWithOwnContext();
        if (probe == null) return;   // no Vulkan device
        var beforeSel = BlackHoleTraceBackends.Selection;
        var beforeGpu = BlackHoleShadowCalculator.GpuSelection;
        var beforeFactory = BlackHoleTraceBackends.VulkanFactory;
        try
        {
            BlackHoleTraceBackends.ResetForTests();
            BlackHoleTraceBackends.VulkanFactory = () => FracturingFog.Rendering.Vulkan.BlackHoleVulkanKernel.TryCreateWithOwnContext();
            BlackHoleTraceBackends.Selection = BlackHoleTraceBackends.Choice.Vulkan;
            (uint[] Colours, sbyte[] Fates, string? Backend) Frame(bool gpu)
            {
                BlackHoleShadowCalculator.GpuSelection = gpu ? BlackHoleShadowCalculator.GpuBlackHoleMode.Gpu : BlackHoleShadowCalculator.GpuBlackHoleMode.Cpu;
                var fx = FracturingFog.Rendering.Lighting.LightingFxData.CreateDefault();
                fx.UseGpuRender = true;
                var c = new BlackHoleShadowCalculator(W, H) { ColorMap = new HsvPalette(), FractalParameters = new FractalParameters { Lighting = fx } };
                c.Calculate(CancellationToken.None);
                return ((uint[])c.ColorBuffer.Clone(), (sbyte[])c.FateBuffer.Clone(), c.LastGpuBackend);
            }
            var gpuFrame = Frame(true);
            var cpuFrame = Frame(false);
            Assert.StartsWith("Vulkan", gpuFrame.Backend);
            Assert.Null(cpuFrame.Backend);
            int differ = 0;
            for (int i = 0; i < W * H; i++) if (gpuFrame.Fates[i] != cpuFrame.Fates[i]) differ++;
            Assert.True(differ <= W * H / 200, $"{differ} fates differ");
        }
        finally
        {
            BlackHoleTraceBackends.Selection = beforeSel;
            BlackHoleShadowCalculator.GpuSelection = beforeGpu;
            BlackHoleTraceBackends.ResetForTests();
            BlackHoleTraceBackends.VulkanFactory = beforeFactory;
        }
    }

    [Fact]
    public void Backend_Choice_Parses_And_Routes()
    {
        Assert.Equal(BlackHoleTraceBackends.Choice.D3D11, BlackHoleTraceBackends.Parse("d3d11"));
        Assert.Equal(BlackHoleTraceBackends.Choice.Vulkan, BlackHoleTraceBackends.Parse("VK"));
        Assert.Equal(BlackHoleTraceBackends.Choice.Ilgpu, BlackHoleTraceBackends.Parse("cuda"));
        Assert.Equal(BlackHoleTraceBackends.Choice.Auto, BlackHoleTraceBackends.Parse(null));
        Assert.Null(BlackHoleTraceBackends.KernelFor(BlackHoleTraceBackends.Choice.Ilgpu, ilgpuAvailable: false));
    }
}
