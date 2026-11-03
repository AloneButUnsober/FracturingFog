// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1620 — a headless render (--batch) raymarches the relief, and composites the
// froxel volume, on its own GPU device (HeadlessGpu.ReliefKernel / FroxelKernel),
// as the live host does through FractalRenderHost.EnsureReliefKernel. Before, the
// poster path passed no kernel, so every batch relief traced on the CPU while the
// live view (Relief2DGpuRaymarch, on by default) used the GPU.
//   • Routing (stub kernels): a relief frame runs the headless relief kernel, built
//     once for many frames; Relief2DGpuRaymarch off, a flat render, or no headless
//     device (the live shell) never builds it. The froxel kernel is built and run
//     only alongside the GPU relief with froxel volumetrics on.
//   • --relief-cpu (the live Ctrl+Shift+G) keeps the trace on the CPU, and the
//     Command builder emits it for a live view with GPU relief off (round trip).
//   • Parity (real Vulkan device, skips without one): the GPU frame is
//     deterministic, differs from the CPU trace (so the kernel really ran), and
//     tracks it — the GPU kernel is gated against its CPU twin
//     (--reliefgpuraymarch), not the full CPU trace, so the two were never
//     byte-identical; live and batch now agree instead.
// Mutant: passing null for the kernel in PosterRenderer.ApplyReliefIfEnabled again
// fails the routing and parity tests.

using System;
using System.Linq;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes HeadlessGpu statics
public sealed class S1620BatchGpuReliefTests(ITestOutputHelper output)
{
    private const int W = 96, H = 64;

    // Escape-time stub: a varied field so the relief has terrain to trace.
    private sealed class StubKernel : IGpuKernel
    {
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public string BackendLabel => "Stub";
        public void SetPalette(IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f, uint[]? colorDst = null,
            float[]? trapDst = null, GpuDomainWarp warp = default)
        {
            for (int i = 0; i < width * height; i++) { iterDst[i] = 1 + i % 50; smoothDst[i] = 1 + i % 50; }
        }
        public void Dispose() { }
    }

    private sealed class StubRelief : IReliefRaymarchKernel
    {
        public int Runs;
        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
            float[]? aovNormalXyz = null, float[]? aovDepth = null)
        {
            Runs++;
            Array.Fill(dst, 0xFF123456u, 0, u.W * u.H);
            if (aovDepth != null) Array.Fill(aovDepth, 1f, 0, u.W * u.H);
        }
        public void Dispose() { }
    }

    private sealed class StubFroxel : IFroxelVolumeKernel
    {
        public int Composites;
        public void Composite(in FroxelGpuUniforms u, uint[] beauty, float[] worldDepth, int w, int h, uint[] dst)
            => Composites++;
        public void Dispose() { }
    }

    private static FractalParameters Relief(bool gpu = true, bool froxel = false)
    {
        var fp = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = gpu,
            Relief2DHeightScale = 1.4, Relief2DCameraAzimuthDeg = 25, Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55, Relief2DGroundPlane = false,
            Relief2DFroxelVolumetrics = froxel,
        };
        if (froxel)
        {
            var fx = fp.Lighting;
            fx.FogDensity = 0.5;
            fp.Lighting = fx;
        }
        return fp;
    }

    private static PosterRequest Request(FractalParameters fp) => new()
    {
        FractalType = FractalType.Mandelbrot,
        CenterX = -0.7436447860, CenterY = 0.1318252536, Zoom = 200, MaxIterations = 400,
        Width = W, Height = H, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard,
        FractalParameters = fp, Path = "unused.png", Format = ImageFileFormat.Png,
    };

    private static uint[] Render(FractalParameters fp) =>
        PosterRenderer.RenderToPixels(Request(fp), default, out _, out _);

    private sealed class Rig
    {
        public int ReliefBuilds, FroxelBuilds;
        public readonly StubRelief Relief = new();
        public readonly StubFroxel Froxel = new();
    }

    private static void WithHeadless(IGpuKernel? kernel, Action<Rig> body)
    {
        var savedKernel = HeadlessGpu.Kernel;
        var savedRelief = HeadlessGpu.ReliefKernelFactory;
        var savedFroxel = HeadlessGpu.FroxelKernelFactory;
        var rig = new Rig();
        try
        {
            HeadlessGpu.Kernel = kernel;
            HeadlessGpu.ReliefKernelFactory = _ => { rig.ReliefBuilds++; return rig.Relief; };
            HeadlessGpu.FroxelKernelFactory = _ => { rig.FroxelBuilds++; return rig.Froxel; };
            body(rig);
        }
        finally
        {
            HeadlessGpu.Kernel = savedKernel;
            HeadlessGpu.ReliefKernelFactory = savedRelief;
            HeadlessGpu.FroxelKernelFactory = savedFroxel;
        }
    }

    [Fact]
    public void A_Relief_Frame_Runs_The_Headless_Relief_Kernel_Built_Once()
    {
        WithHeadless(new StubKernel(), rig =>
        {
            var a = Render(Relief());
            Render(Relief());
            Assert.Equal(1, rig.ReliefBuilds);
            Assert.Equal(2, rig.Relief.Runs);
            Assert.Equal(0, rig.FroxelBuilds);
            Assert.Equal(0xFF123456u, a[a.Length / 2]);   // the kernel's frame came back
        });
    }

    [Fact]
    public void Gpu_Raymarch_Off_A_Flat_Render_Or_No_Headless_Device_Never_Builds_It()
    {
        WithHeadless(new StubKernel(), rig =>
        {
            Render(Relief(gpu: false));
            Render(new FractalParameters());
            Assert.Equal(0, rig.ReliefBuilds);
        });
        // The live shell: no headless kernel, so its exports keep the CPU trace.
        WithHeadless(null, rig =>
        {
            Render(Relief());
            Assert.Equal(0, rig.ReliefBuilds);
            Assert.Equal(0, rig.Relief.Runs);
        });
    }

    [Fact]
    public void The_Froxel_Kernel_Composites_Only_Alongside_A_Gpu_Relief_With_Froxel_On()
    {
        WithHeadless(new StubKernel(), rig =>
        {
            Render(Relief(froxel: true));
            Assert.Equal(1, rig.FroxelBuilds);
            Assert.Equal(1, rig.Froxel.Composites);
            Assert.Equal(1, rig.Relief.Runs);

            Render(Relief(gpu: false, froxel: true));   // CPU relief → CPU froxel post-pass
            Assert.Equal(1, rig.Froxel.Composites);
            Assert.Equal(1, rig.Relief.Runs);
        });
    }

    [Fact]
    public void On_A_Vulkan_Device_The_Gpu_Relief_Tracks_The_Cpu_Trace()
    {
        var probe = VulkanComputeKernel.TryCreateWithOwnContext();
        if (probe is null) Assert.Skip("no Vulkan device on this host");
        using var relief = ReliefRaymarchVulkanKernel.TryCreateWithOwnContext();
        if (relief is null) { probe.Dispose(); Assert.Skip("no Vulkan relief kernel on this host"); }

        var savedKernel = HeadlessGpu.Kernel;
        var savedRelief = HeadlessGpu.ReliefKernelFactory;
        try
        {
            // Both frames run the same escape-time pass on the device; only the relief
            // route differs (Relief2DGpuRaymarch off = the CPU trace).
            HeadlessGpu.Kernel = probe;
            HeadlessGpu.ReliefKernelFactory = _ => relief;
            var cpu = Render(Relief(gpu: false));
            var gpu1 = Render(Relief());
            var gpu2 = Render(Relief());

            Assert.True(gpu1.AsSpan().SequenceEqual(gpu2), "the GPU relief frame is deterministic");
            int differ = 0, big = 0; long sum = 0;
            for (int i = 0; i < cpu.Length; i++)
            {
                int m = 0;
                for (int s = 0; s < 24; s += 8)
                    m = Math.Max(m, Math.Abs((int)((cpu[i] >> s) & 0xFF) - (int)((gpu1[i] >> s) & 0xFF)));
                if (m > 0) differ++;
                if (m > 8) big++;
                sum += m;
            }
            double mean = (double)sum / cpu.Length;
            output.WriteLine($"GPU vs CPU relief: {differ}/{cpu.Length} differ, {big} by > 8 levels, mean |diff| {mean:0.00}");
            Assert.True(differ > 0, "precondition: the GPU kernel ran (its frame is not the CPU trace)");
            Assert.True(mean < 4.0, $"mean |diff| {mean:0.00} levels");
            Assert.True(big <= cpu.Length / 10, $"{big}/{cpu.Length} pixels differ by > 8 levels");
        }
        finally
        {
            HeadlessGpu.Kernel = savedKernel;
            HeadlessGpu.ReliefKernelFactory = savedRelief;
            probe.Dispose();
        }
    }

    [Fact]
    public void Relief_Cpu_Round_Trips_Through_The_Command_Builder()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true, ReliefGpu = false,
        };
        var report = BatchCommandBuilder.BuildWithReport(snap);
        Assert.Contains(BatchFlags.ReliefCpu, report.Args);
        var back = new[] { "FracturingFog", "--batch" }.Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "o.png" : a)).ToArray();
        Assert.True(BatchOptions.TryParse(back, 2, out var o, out var e), e);
        Assert.True(o.ReliefCpu);

        // GPU relief on (the default), or --gpu off (already CPU): no flag.
        Assert.DoesNotContain(BatchFlags.ReliefCpu, BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            { Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true }).Args);
        Assert.DoesNotContain(BatchFlags.ReliefCpu, BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            { Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true, ReliefGpu = false, GpuCompute = false }).Args);
    }
}
