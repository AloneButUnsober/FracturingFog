// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2049 part 2 — more ODE families on the GPU template, through the shared OdeGpuFrame.
// Independent checks, per family:
//   • Vulkan (double) gives every interior pixel the CPU's outcome, within the floor the CPU meets
//     against itself (tolerance moved by one part in 10^12, measured here): at most
//     max(1 in 10,000, twice that floor) interior pixels differ (the rule accepted in #2054);
//   • the calculator routes the ODE view to the GPU, colours the outcomes with its CPU code, and
//     keeps a non-ODE view (three-body's Lagrange basins, a Newton map) on the CPU;
//   • single precision floors the tolerances (a float step cannot meet a target below float
//     epsilon);
//   • the precision setting survives the command line.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Scattering;
using Xunit;

namespace FracturingFog.Server.Tests;

// Shares the static OdeTraceBackends kernels (ResetForTests disposes them): one serial collection.
[Collection(OdeGpuBackendsCollection.Name)]
public sealed class S2049OdeGpuFamiliesTests
{
    private readonly ITestOutputHelper _out;
    public S2049OdeGpuFamiliesTests(ITestOutputHelper o) => _out = o;

    private static FractalRegion Preset(string name) => FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);

    [Fact]
    public void ThreeBody_Vulkan_Matches_The_Cpu_Fates_Within_The_Floor_When_Present()
    {
        using var k = FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext();
        if (k == null || !k.SupportsDouble) return;   // no Vulkan fp64 device
        const int w = 96, h = 60;
        var region = Preset("Three-Body - Copenhagen Fates");
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        var scene = ThreeBodyScene.Build(p);
        var p2 = new FractalParameters();
        region.Params!.ApplyTo(p2);
        p2.ThreeBodyTolerance *= 1.0 + 1e-12;
        var scene2 = ThreeBodyScene.Build(p2);
        (double, double) View(int i) => ThreeBodyCalculator.PlaneAt(i % w + 0.5, i / w + 0.5, w, h, region.CenterX, region.CenterY, region.Zoom);
        double e2 = scene.EscapeRadius * scene.EscapeRadius;
        var items = new List<int>();
        var cpu = new int[w * h];
        var cpu2 = new int[w * h];
        var s = new double[4];
        for (int i = 0; i < w * h; i++)
        {
            var (a, b) = View(i);
            if (scene.LaunchState(a, b, s) && a * a + b * b <= e2) items.Add(i); else cpu[i] = cpu2[i] = -2;
        }
        System.Threading.Tasks.Parallel.ForEach(items, i =>
        {
            Span<double> st = stackalloc double[4];
            var (a, b) = View(i);
            cpu[i] = scene.Trace(a, b, st).Gate;
            cpu2[i] = scene2.Trace(a, b, st).Gate;
        });
        bool Interior(int i)
        {
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h || cpu[yy * w + xx] != cpu[i]) return false;
                }
            return true;
        }
        var t = ThreeBodyGpu.Job(scene, single: false);
        var r = k.Trace(new OdeJob
        {
            Family = t.Family, FamilyParams = t.FamilyParams, RelTol = t.RelTol, AbsTol = t.AbsTol,
            InitialStep = t.InitialStep, MaxSteps = t.MaxSteps, Count = items.Count,
            Launches = (first, n, dst) =>
            {
                var st = new double[4];
                for (int i = 0; i < n; i++) { var (a, b) = View(items[first + i]); scene.LaunchState(a, b, st); Array.Copy(st, 0, dst, i * 4, 4); }
            },
        }, TestContext.Current.CancellationToken);
        Assert.NotNull(r);
        int interior = 0, gpuDiff = 0, floor = 0;
        for (int q = 0; q < items.Count; q++)
        {
            int i = items[q];
            if (!Interior(i)) continue;
            interior++;
            if (ThreeBodyGpu.OutcomeOf(r!, q).Gate != cpu[i]) gpuDiff++;
            if (cpu2[i] != cpu[i]) floor++;
        }
        int allowed = Math.Max(interior / 10_000, 2 * floor);
        _out.WriteLine($"{interior} interior, GPU differs {gpuDiff}, CPU floor {floor}, allowed {allowed}");
        Assert.True(interior > 500);
        Assert.True(gpuDiff <= allowed, $"{gpuDiff} interior pixels differ (allowed {allowed})");
    }

    [Fact]
    public void ThreeBody_Routes_Fates_To_The_Gpu_And_Keeps_Lagrange_On_The_Cpu()
    {
        using var probe = FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext();
        if (probe == null) return;
        var beforeFactory = OdeTraceBackends.VulkanFactory;
        try
        {
            OdeTraceBackends.ResetForTests();
            OdeTraceBackends.VulkanFactory = () => FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext();
            OdeTraceBackends.Selection = OdeTraceBackends.Choice.Vulkan;
            ThreeBodyCalculator Render(string name, OdeGpuRoute.Mode mode)
            {
                OdeGpuRoute.Shared = mode;
                var region = Preset(name);
                var p = new FractalParameters();
                region.Params!.ApplyTo(p);
                var fx = p.Lighting; fx.UseGpuRender = true; p.Lighting = fx;
                if (!probe.SupportsDouble) p.ThreeBodyGpuPrecision = BlackHoleGpuPrecision.Single;
                var c = new ThreeBodyCalculator(64, 40) { CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, ColorMap = new HsvPalette(), FractalParameters = p };
                c.Calculate(TestContext.Current.CancellationToken);
                return c;
            }
            var gpu = Render("Three-Body - Copenhagen Fates", OdeGpuRoute.Mode.Gpu);
            Assert.StartsWith("Vulkan", gpu.LastGpuBackend);
            var cpu = Render("Three-Body - Copenhagen Fates", OdeGpuRoute.Mode.Cpu);
            Assert.Null(cpu.LastGpuBackend);
            int differ = 0;
            for (int i = 0; i < gpu.ColorBuffer.Length; i++)
            {
                if (gpu.GateBuffer[i] != cpu.GateBuffer[i]) { differ++; continue; }
                for (int s = 0; s < 24; s += 8)
                    Assert.InRange(Math.Abs((int)((gpu.ColorBuffer[i] >> s) & 0xFF) - (int)((cpu.ColorBuffer[i] >> s) & 0xFF)), 0, 1);
            }
            Assert.True(differ < gpu.ColorBuffer.Length / 100, $"{differ} gates differ");

            var lag = Render("Three-Body - Lagrange Basins", OdeGpuRoute.Mode.Gpu);
            Assert.Null(lag.LastGpuBackend);
            Assert.Equal("Lagrange view", lag.LastGpuRoute.Reason);
        }
        finally
        {
            OdeGpuRoute.Shared = OdeGpuRoute.Mode.Auto;
            OdeTraceBackends.Selection = OdeTraceBackends.Choice.Auto;
            OdeTraceBackends.ResetForTests();
            OdeTraceBackends.VulkanFactory = beforeFactory;
        }
    }

    [Fact]
    public void Single_Precision_Floors_The_Tolerances()
    {
        var job = new OdeJob
        {
            Family = ThreeBodyGpu.Family, FamilyParams = new[] { 0.5 }, RelTol = 1e-9, AbsTol = 1e-12, InitialStep = 0.01,
            Count = 0, Launches = (_, _, _) => { }, Single = true,
        };
        var p = OdeTraceBackends.Params(job);
        Assert.Equal(OdeTraceBackends.SingleMinRelTol, p[0]);
        Assert.Equal(OdeTraceBackends.SingleMinAbsTol, p[1]);
        var d = OdeTraceBackends.Params(new OdeJob
        {
            Family = ThreeBodyGpu.Family, FamilyParams = new[] { 0.5 }, RelTol = 1e-9, AbsTol = 1e-12, InitialStep = 0.01,
            Count = 0, Launches = (_, _, _) => { },
        });
        Assert.Equal(1e-9, d[0]);
        Assert.Equal(1e-12, d[1]);
        Assert.True(float.Epsilon < OdeTraceBackends.SingleMinRelTol && OdeTraceBackends.SingleMinRelTol > 1.2e-7);   // above float epsilon
    }

    [Fact]
    public void ThreeBody_Precision_Survives_The_Command_Line()
    {
        var p = new FractalParameters { ThreeBodyGpuPrecision = BlackHoleGpuPrecision.Single };
        var snap = RegionFractalParams.Snapshot(FractalType.ThreeBodyBasins, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.ThreeBodyBasins, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.ThreeBodyBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        Assert.Equal(BlackHoleGpuPrecision.Single, q.ThreeBodyGpuPrecision);
        Assert.Contains(EnvVarCatalog.All, e => e.Name == "FF_GPU_ODE");
    }
    [Fact]
    public void SpinningBinary_Vulkan_Matches_The_Cpu_Within_The_Floor_When_Present()
    {
        using var k = FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext();
        if (k == null || !k.SupportsDouble) return;   // no Vulkan fp64 device
        const int w = 24, h = 16;
        foreach (var name in new[] { "Spinning Binary - Levin Conservative", "Spinning Binary - Merger Side (Dissipative)" })
        {
            var region = Preset(name);
            var p = new FractalParameters();
            region.Params!.ApplyTo(p);
            var scene = SpinningBinaryScene.Build(p);
            var p2 = new FractalParameters();
            region.Params!.ApplyTo(p2);
            p2.SpinBinTolerance *= 1.0 + 1e-12;
            var scene2 = SpinningBinaryScene.Build(p2);
            (double, double) View(int i) => SpinningBinaryCalculator.PlaneAt(i % w + 0.5, i / w + 0.5, w, h, region.CenterX, region.CenterY, region.Zoom);
            var cpu = new int[w * h];
            var cpu2 = new int[w * h];
            System.Threading.Tasks.Parallel.For(0, w * h, i =>
            {
                Span<double> st = stackalloc double[SpinningBinarySystem.StateSize];
                var (a, b) = View(i);
                cpu[i] = scene.Trace(a, b, st).Gate;
                cpu2[i] = scene2.Trace(a, b, st).Gate;
            });
            var t = SpinningBinaryGpu.Job(scene, single: false);
            var r = k.Trace(new OdeJob
            {
                Family = t.Family, FamilyParams = t.FamilyParams, RelTol = t.RelTol, AbsTol = t.AbsTol,
                InitialStep = t.InitialStep, MaxSteps = t.MaxSteps, Count = w * h,
                Launches = (first, n, dst) =>
                {
                    var st = new double[SpinningBinarySystem.StateSize];
                    for (int i = 0; i < n; i++) { var (a, b) = View(first + i); scene.LaunchState(a, b, st); Array.Copy(st, 0, dst, i * st.Length, st.Length); }
                },
            }, TestContext.Current.CancellationToken);
            Assert.NotNull(r);
            int differ = 0, floor = 0;
            for (int i = 0; i < w * h; i++)
            {
                if (SpinningBinaryGpu.OutcomeOf(scene, r!, i).Gate != cpu[i]) differ++;
                if (cpu2[i] != cpu[i]) floor++;
            }
            _out.WriteLine($"{name}: GPU differs {differ} of {w * h}, CPU floor {floor}");
            Assert.True(differ <= Math.Max(1, 2 * floor), $"{name}: {differ} pixels differ (CPU floor {floor})");
        }
    }

    [Fact]
    public void SpinningBinary_Routes_To_The_Gpu_And_Its_Precision_Survives_The_Command_Line()
    {
        using (var probe = FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext())
        {
            if (probe != null)
            {
                var beforeFactory = OdeTraceBackends.VulkanFactory;
                try
                {
                    OdeTraceBackends.ResetForTests();
                    OdeTraceBackends.VulkanFactory = () => FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext();
                    OdeTraceBackends.Selection = OdeTraceBackends.Choice.Vulkan;
                    OdeGpuRoute.Shared = OdeGpuRoute.Mode.Gpu;
                    var region = Preset("Spinning Binary - Levin Conservative");
                    var p = new FractalParameters();
                    region.Params!.ApplyTo(p);
                    var fx = p.Lighting; fx.UseGpuRender = true; p.Lighting = fx;
                    p.SpinBinGpuPrecision = BlackHoleGpuPrecision.Single;   // quick on any device
                    var c = new SpinningBinaryCalculator(16, 10) { CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, ColorMap = new HsvPalette(), FractalParameters = p };
                    c.Calculate(TestContext.Current.CancellationToken);
                    Assert.StartsWith("Vulkan", c.LastGpuBackend);
                    Assert.Contains(c.GateBuffer, g => g >= 0);
                }
                finally
                {
                    OdeGpuRoute.Shared = OdeGpuRoute.Mode.Auto;
                    OdeTraceBackends.Selection = OdeTraceBackends.Choice.Auto;
                    OdeTraceBackends.ResetForTests();
                    OdeTraceBackends.VulkanFactory = beforeFactory;
                }
            }
        }
        var q0 = new FractalParameters { SpinBinGpuPrecision = BlackHoleGpuPrecision.Single };
        var snap = RegionFractalParams.Snapshot(FractalType.SpinningBinaryBasins, q0)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.SpinningBinaryBasins, Parameters = q0, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.SpinningBinaryBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        Assert.Equal(BlackHoleGpuPrecision.Single, q.SpinBinGpuPrecision);
    }

}
