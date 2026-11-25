// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2049 — physics P1: the GPU ODE template (Rendering.D3D/OdeKernelSource) on Hénon-Heiles, and
// solid blocks for its flat exit colours. Independent checks:
//   • Vulkan (double) gives every interior pixel the CPU's exit (at most 1 in 10,000 may differ,
//     the #1972 standard; interior = all 8 CPU neighbours share the exit), and its escape times
//     agree with the CPU's on interior pixels; single precision only moves edge pixels
//     (bounded). D3D11 is checked by --odegpuprobe (the test project cannot load the
//     Windows-only backend);
//   • the calculator routes to the GPU backend and colours its outcomes with the CPU's code;
//   • the outcome words decode exactly (double and single);
//   • solid blocks with flat exit colours give the full render pixel for pixel on every
//     Hénon-Heiles preset, fill a real share of the frame, and stay off when exits are shaded;
//   • the settings survive the command line.

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
public sealed class S2049OdeGpuTests
{
    private readonly ITestOutputHelper _out;
    public S2049OdeGpuTests(ITestOutputHelper o) => _out = o;

    private static FractalRegion Preset(string name) => FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);

    private static (HenonHeilesScene Scene, List<int> Items, int[] Cpu, double[] CpuTime, Func<int, (double, double)> View) Reference(FractalRegion region, int w, int h)
    {
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        var scene = HenonHeilesScene.Build(p);
        (double, double) View(int i) => HenonHeilesCalculator.PlaneAt(i % w + 0.5, i / w + 0.5, w, h, region.CenterX, region.CenterY, region.Zoom);
        var items = new List<int>();
        var cpu = new int[w * h];
        var time = new double[w * h];
        Span<double> s = stackalloc double[4];
        for (int i = 0; i < w * h; i++)
        {
            var (a, b) = View(i);
            if (scene.LaunchState(a, b, s) && s[0] * s[0] + s[1] * s[1] <= 4.0) items.Add(i);
            else cpu[i] = -2;
        }
        System.Threading.Tasks.Parallel.ForEach(items, i =>
        {
            Span<double> st = stackalloc double[4];
            var (a, b) = View(i);
            var o = scene.Trace(a, b, st);
            cpu[i] = o.Exit; time[i] = o.Time;
        });
        return (scene, items, cpu, time, View);
    }

    private static bool Interior(int[] cpu, int w, int h, int i)
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

    [Fact]
    public void Vulkan_Matches_The_Cpu_Exits_And_Times_When_Present()
    {
        using var k = FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext();
        if (k == null) return;   // no Vulkan device
        foreach (var name in new[] { "Henon-Heiles - Exit Basins", "Henon-Heiles - Poincare Section" })
        {
            const int w = 120, h = 80;
            var (scene, items, cpu, time, view) = Reference(Preset(name), w, h);
            foreach (bool single in new[] { false, true })
            {
                if (!single && !k.SupportsDouble) continue;
                var o = HenonHeilesGpu.Trace(k, scene, items, view, single, TestContext.Current.CancellationToken);
                Assert.NotNull(o);
                int interior = 0, inDiff = 0, edgeDiff = 0;
                double worstT = 0;
                for (int q = 0; q < items.Count; q++)
                {
                    int i = items[q];
                    bool inn = Interior(cpu, w, h, i);
                    if (inn) interior++;
                    if (o![q].Exit != cpu[i]) { if (inn) inDiff++; else edgeDiff++; continue; }
                    if (inn && !single) worstT = Math.Max(worstT, Math.Abs(o[q].Time - time[i]) / Math.Max(1.0, time[i]));
                }
                _out.WriteLine($"{name} {(single ? "fp32" : "fp64")}: {interior} interior, {inDiff} interior / {edgeDiff} edge differ, worst rel time {worstT:E2}");
                Assert.True(interior > 500);
                if (!single)
                {
                    Assert.True(inDiff <= Math.Max(0, interior / 10_000), $"{inDiff} interior pixels differ");
                    Assert.True(worstT < 1e-6, $"escape time off by {worstT:E2}");
                }
                else Assert.True(inDiff <= interior / 200, $"fp32: {inDiff} interior pixels differ");
            }
        }
    }

    [Fact]
    public void The_Calculator_Routes_To_The_Gpu_And_Colours_As_The_Cpu()
    {
        using var probe = FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext();
        if (probe == null) return;
        var beforeFactory = OdeTraceBackends.VulkanFactory;
        try
        {
            OdeTraceBackends.ResetForTests();
            OdeTraceBackends.VulkanFactory = () => FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext();
            OdeTraceBackends.Selection = OdeTraceBackends.Choice.Vulkan;
            var region = Preset("Henon-Heiles - Exit Basins");
            HenonHeilesCalculator Render(OdeGpuRoute.Mode mode)
            {
                HenonHeilesCalculator.GpuSelection = mode;
                var p = new FractalParameters();
                region.Params!.ApplyTo(p);
                var fx = p.Lighting; fx.UseGpuRender = true; p.Lighting = fx;
                if (!probe.SupportsDouble) p.HenonHeilesGpuPrecision = BlackHoleGpuPrecision.Single;
                var c = new HenonHeilesCalculator(96, 60) { CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, ColorMap = new HsvPalette(), FractalParameters = p };
                c.Calculate(TestContext.Current.CancellationToken);
                return c;
            }
            var gpu = Render(OdeGpuRoute.Mode.Gpu);
            Assert.StartsWith("Vulkan", gpu.LastGpuBackend);
            var cpu = Render(OdeGpuRoute.Mode.Cpu);
            Assert.Null(cpu.LastGpuBackend);
            Assert.Equal("CPU forced", cpu.LastGpuRoute.Reason);
            // Same colour wherever the exit agrees (the colour pass is shared code).
            int same = 0, differ = 0;
            for (int i = 0; i < gpu.ColorBuffer.Length; i++)
            {
                if (gpu.ExitBuffer[i] != cpu.ExitBuffer[i]) { differ++; continue; }
                same++;
                if (gpu.ColorBuffer[i] != cpu.ColorBuffer[i])
                {
                    // Escape-time shading may round a unit apart on the time's last bits.
                    for (int s = 0; s < 24; s += 8)
                        Assert.InRange(Math.Abs((int)((gpu.ColorBuffer[i] >> s) & 0xFF) - (int)((cpu.ColorBuffer[i] >> s) & 0xFF)), 0, 1);
                }
            }
            _out.WriteLine($"{same} same exit, {differ} differ");
            Assert.True(differ < gpu.ColorBuffer.Length / 100);
        }
        finally
        {
            HenonHeilesCalculator.GpuSelection = OdeGpuRoute.Mode.Auto;
            OdeTraceBackends.Selection = OdeTraceBackends.Choice.Auto;
            OdeTraceBackends.ResetForTests();
            OdeTraceBackends.VulkanFactory = beforeFactory;
        }
    }

    [Fact]
    public void Outcome_Words_Decode_Exactly()
    {
        var fam = new OdeFamily("T", 2, 1, "");
        foreach (bool single in new[] { false, true })
        {
            int w = single ? 1 : 2, stride = 2 + (1 + 2 + 1) * w;
            var words = new uint[2 * stride];
            void Put(int o, double v)
            {
                if (single) words[o] = unchecked((uint)BitConverter.SingleToInt32Bits((float)v));
                else { ulong b = unchecked((ulong)BitConverter.DoubleToInt64Bits(v)); words[o] = (uint)b; words[o + 1] = (uint)(b >> 32); }
            }
            for (int item = 0; item < 2; item++)
            {
                int o = item * stride;
                words[o] = (uint)(item == 0 ? 2 : 1) | ((uint)(item == 0 ? 0 : 3) << 4);   // escaped / captured by body 2
                words[o + 1] = (uint)(100 + item);
                Put(o + 2, 1.25 + item);
                Put(o + 2 + w, -0.5 * item); Put(o + 2 + 2 * w, 3.0);
                Put(o + 2 + 3 * w, 7.0 + item);
            }
            var r = new OdeResult(2, 2, 1);
            OdeTraceBackends.Decode(words, 0, 2, fam, single, r);
            Assert.Equal(new[] { 2, 1 }, r.Fate);
            Assert.Equal(new[] { -1, 2 }, r.Body);
            Assert.Equal(new[] { 100, 101 }, r.Steps);
            Assert.Equal(new[] { 1.25, 2.25 }, r.T);
            Assert.Equal(new[] { 0.0, 3.0, -0.5, 3.0 }, r.Final);
            Assert.Equal(new[] { 7.0, 8.0 }, r.Channels);
        }
    }

    [Fact]
    public void Solid_Blocks_Equal_The_Full_Render_With_Flat_Colours()
    {
        long totalFilled = 0, totalPixels = 0;
        foreach (var region in FractalRegionLibrary.Instance.BuiltIns.Where(r => r.FractalType == FractalType.HenonHeilesBasins))
        {
            var p = new FractalParameters();
            region.Params!.ApplyTo(p);
            p.HenonHeilesChannel = HenonHeilesChannel.Exit;
            p.HenonHeilesShadeExits = false;
            HenonHeilesCalculator Render(bool blocks)
            {
                p.HenonHeilesSolidBlocks = blocks;
                var c = new HenonHeilesCalculator(160, 100) { CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, ColorMap = new HsvPalette(), FractalParameters = p };
                c.Calculate(TestContext.Current.CancellationToken);
                return c;
            }
            var full = Render(false);
            var blocks = Render(true);
            Assert.False(blocks.MaskExact);
            int diff = 0;
            for (int i = 0; i < full.ColorBuffer.Length; i++) if (full.ColorBuffer[i] != blocks.ColorBuffer[i]) diff++;
            _out.WriteLine($"{region.Name}: filled {blocks.LastSolidFilled}, differ {diff}");
            Assert.Equal(0, diff);
            Assert.Equal(full.ExitBuffer, blocks.ExitBuffer);
            totalFilled += blocks.LastSolidFilled; totalPixels += full.ColorBuffer.Length;
        }
        Assert.True(totalFilled > totalPixels / 5, $"only {totalFilled} of {totalPixels} filled");

        // Shaded exits: the option stays off (a fill would flatten the shading).
        var q = new FractalParameters();
        Preset("Henon-Heiles - Exit Basins").Params!.ApplyTo(q);
        q.HenonHeilesSolidBlocks = true;
        var c2 = new HenonHeilesCalculator(64, 40) { ColorMap = new HsvPalette(), FractalParameters = q };
        c2.Calculate(TestContext.Current.CancellationToken);
        Assert.Equal(0, c2.LastSolidFilled);
        Assert.True(c2.MaskExact);
    }

    [Fact]
    public void Settings_Survive_The_Command_Line_And_The_Env_Choices_Parse()
    {
        var p = new FractalParameters
        {
            HenonHeilesGpuPrecision = BlackHoleGpuPrecision.Single, HenonHeilesShadeExits = false, HenonHeilesSolidBlocks = true,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.HenonHeilesBasins, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.HenonHeilesBasins, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.HenonHeilesBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        Assert.Equal(BlackHoleGpuPrecision.Single, q.HenonHeilesGpuPrecision);
        Assert.False(q.HenonHeilesShadeExits);
        Assert.True(q.HenonHeilesSolidBlocks);

        Assert.Equal(OdeGpuRoute.Mode.Cpu, OdeGpuRoute.Parse("0"));
        Assert.Equal(OdeGpuRoute.Mode.Gpu, OdeGpuRoute.Parse("gpu"));
        Assert.Equal(OdeGpuRoute.Mode.Auto, OdeGpuRoute.Parse(null));
        Assert.Equal(OdeTraceBackends.Choice.Vulkan, OdeTraceBackends.Parse("VK"));
        Assert.Contains(EnvVarCatalog.All, e => e.Name == "FF_GPU_HENON_HEILES");
        Assert.Contains(EnvVarCatalog.All, e => e.Name == "FF_GPU_ODE_BACKEND");
        Assert.True((FracturingFog.Rendering.OdeKernelSource.MaxBandPixels + 63) / FracturingFog.Rendering.OdeKernelSource.GroupSize <= 65535);
    }
}

/// <summary>#2049 — the ODE GPU test classes share OdeTraceBackends' static kernels; one test's
/// ResetForTests disposed a Vulkan device another class's frame was using (a native use-after-free that
/// crashed the test process). They run serially in this collection.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OdeGpuBackendsCollection
{
    public const string Name = "OdeGpuBackends";
}
