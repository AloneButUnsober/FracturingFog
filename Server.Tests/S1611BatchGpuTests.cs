// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1611 — --batch computes on the GPU as the live view does.
//   • --gpu auto|on|off parses; anything else is an error;
//   • the Command builder emits --gpu off when the live view has GPU compute off,
//     and nothing otherwise, and the command parses back;
//   • HeadlessGpu.Attach hands the process kernel to the Mandelbrot, escape-time and
//     Buddhabrot calculators, and the headless capture factory (PosterRenderer)
//     attaches it to what it builds; no kernel leaves them on the CPU;
//   • the fp32 shallow kernel's pixel-size gate (Fp32GpuResolves) refuses the views
//     --gpuprecisionprobe measured as moved (8000x at 4K and 1080p) and accepts the
//     ones it measured as clean, and the calculator routes a refused view to the CPU
//     without dispatching;
//   • GpuDeferredReadback.ProcessSetting sits under the thread override.

using System;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes HeadlessGpu / MandelbrotCalculator statics
public sealed class S1611BatchGpuTests
{
    private sealed class StubKernel : IGpuKernel
    {
        public int Runs;
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
            Runs++;
            Array.Fill(iterDst, maxIter, 0, width * height);
        }
        public void Dispose() { }
    }

    private static readonly string[] Base = { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };

    private static BatchOptions Parse(params string[] extra)
    {
        var args = new string[Base.Length + extra.Length];
        Base.CopyTo(args, 0);
        extra.CopyTo(args, Base.Length);
        Assert.True(BatchOptions.TryParse(args, 0, out var o, out var err), err);
        return o;
    }

    [Fact]
    public void Gpu_Flag_Parses_And_Defaults_To_Auto()
    {
        Assert.Equal(BatchGpuMode.Auto, Parse().Gpu);
        Assert.Equal(BatchGpuMode.Auto, Parse("--gpu", "auto").Gpu);
        Assert.Equal(BatchGpuMode.On, Parse("--gpu", "on").Gpu);
        Assert.Equal(BatchGpuMode.Off, Parse("--gpu", "OFF").Gpu);
        Assert.False(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--gpu", "cuda" },
            0, out _, out var err));
        Assert.Contains("--gpu", err);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Command_Builder_Emits_Gpu_Off_Only_When_Live_Gpu_Is_Off_And_It_Round_Trips(bool live)
    {
        string cmd = BatchCommandBuilder.Build(new BatchCommandSnapshot { GpuCompute = live });
        Assert.Equal(!live, cmd.Contains("--gpu off", StringComparison.Ordinal));
        var argv = cmd.Replace("<OUTPUT.png>", "out.png").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(BatchOptions.TryParse(argv, 2, out var o, out var err), err);
        Assert.Equal(live ? BatchGpuMode.Auto : BatchGpuMode.Off, o.Gpu);
    }

    [Fact]
    public void Attach_Hands_The_Kernel_To_Gpu_Capable_Calculators_And_No_Kernel_Leaves_Them_Alone()
    {
        var saved = HeadlessGpu.Kernel;
        try
        {
            HeadlessGpu.Kernel = null;
            var m0 = new MandelbrotCalculator(16, 16);
            HeadlessGpu.Attach(m0);
            Assert.Null(m0.GpuKernel);
            Assert.False(m0.UseGpuCompute);

            var k = new StubKernel();
            HeadlessGpu.Kernel = k;
            var m = new MandelbrotCalculator(16, 16);
            var e = new EscapeTimeCalculator(16, 16);
            var b = new BuddhabrotCalculator(16, 16);
            HeadlessGpu.Attach(m); HeadlessGpu.Attach(e); HeadlessGpu.Attach(b);
            HeadlessGpu.Attach(null);
            Assert.Same(k, m.GpuKernel); Assert.True(m.UseGpuCompute);
            Assert.Same(k, e.GpuKernel); Assert.True(e.UseGpuCompute);
            Assert.Same(k, b.GpuKernel); Assert.True(b.UseGpuCompute);

            // The headless capture factory the batch image / video paths use.
            var req = new PosterRequest { FractalType = FractalType.Julia, Width = 16, Height = 16, Zoom = 1, MaxIterations = 64 };
            var c = Assert.IsType<EscapeTimeCalculator>(PosterRenderer.BuildCaptureCalculator(req));
            Assert.Same(k, c.GpuKernel);
            HeadlessGpu.Kernel = null;
            var c0 = Assert.IsType<EscapeTimeCalculator>(PosterRenderer.BuildCaptureCalculator(req));
            Assert.Null(c0.GpuKernel);
        }
        finally { HeadlessGpu.Kernel = saved; }
    }

    [Theory]
    // --gpuprecisionprobe (GT 710): moved views (K ≈ 1..2.6) are refused...
    [InlineData(-0.743643887037151, 0.131825904205330, 8000, 3840, 2160, false)]
    [InlineData(-0.743643887037151, 0.131825904205330, 8000, 1920, 1080, false)]
    [InlineData(-0.743643887037151, 0.131825904205330, 3000, 3840, 2160, false)]
    [InlineData(-1.7686, 0.0016, 1000, 3840, 2160, false)]
    // ...clean ones (K ≥ 8, p95 ≤ 0.07 px) accepted...
    [InlineData(-0.743643887037151, 0.131825904205330, 1000, 1920, 1080, true)]
    [InlineData(-1.7686, 0.0016, 1000, 1920, 1080, true)]
    [InlineData(-0.75, 0, 1, 7680, 4320, true)]
    // ...and the zoom ceiling still holds.
    [InlineData(0, 0, 2e4, 64, 64, false)]
    public void Fp32_Gate_Follows_The_Measured_Pixel_Size(double cx, double cy, double zoom, int w, int h, bool ok)
        => Assert.Equal(ok, MandelbrotCalculator.Fp32GpuResolves(cx, cy, zoom, w, h));

    [Fact]
    public void A_View_Below_Fp32_Renders_On_The_Cpu_Without_Dispatching()
    {
        var k = new StubKernel();
        var calc = new MandelbrotCalculator(3840, 2160)
        {
            CenterX = -0.743643887037151, CenterY = 0.131825904205330, Zoom = 8000, MaxIterations = 64,
            Quality = QualityPreset.Standard, ColorMap = new HsvPalette(), GpuKernel = k, UseGpuCompute = true,
        };
        calc.Calculate();
        Assert.Equal(0, k.Runs);
        Assert.Equal(GpuRouteState.CpuFallback, calc.LastGpuRoute.State);
        Assert.Equal("pixels below fp32", calc.LastGpuRoute.Reason);

        // Precondition: the same view at window size within the gate does dispatch.
        var near = new MandelbrotCalculator(320, 180)
        {
            CenterX = -0.743643887037151, CenterY = 0.131825904205330, Zoom = 60, MaxIterations = 64,
            Quality = QualityPreset.Standard, ColorMap = new HsvPalette(), GpuKernel = k, UseGpuCompute = true,
        };
        near.Calculate();
        Assert.Equal(1, k.Runs);
    }

    [Fact]
    public void Deferred_Readback_Process_Setting_Sits_Under_The_Thread_Override()
    {
        var saved = GpuDeferredReadback.ProcessSetting;
        try
        {
            GpuDeferredReadback.Reset();
            GpuDeferredReadback.ProcessSetting = false;
            Assert.False(GpuDeferredReadback.Enabled);
            GpuDeferredReadback.Enabled = true;   // a thread override wins
            Assert.True(GpuDeferredReadback.Enabled);
            GpuDeferredReadback.Reset();
            GpuDeferredReadback.ProcessSetting = null;
            Assert.Equal(GpuDeferredReadback.Default, GpuDeferredReadback.Enabled);
        }
        finally { GpuDeferredReadback.ProcessSetting = saved; GpuDeferredReadback.Reset(); }
    }
}
