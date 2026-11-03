// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1640 — with --gpu auto the batch opens its GPU device on the first Attach of a
// calculator that can dispatch on it (HeadlessGpu.KernelFactory), not up front.
//   • Invariant: every view MandelbrotCalculator.CpuOnlyView skips is one the
//     calculator would not have dispatched anyway — with a kernel attached the old,
//     eager way, the stub kernel is never run for it. So skipping the open never
//     moves a frame off the GPU.
//   • A view the GPU draws opens the device (once, however many calculators attach)
//     and dispatches; escape-time calculators open it too; a calculator with no GPU
//     kernel, or null, never opens it.
// Mutant: CpuOnlyView returning a reason for every view fails the dispatch test;
// returning null for every view fails the "not opened" test.

using System;
using FracturingFog.Calculators;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes HeadlessGpu / MandelbrotCalculator statics
public sealed class S1640LazyGpuTests
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

    private static MandelbrotCalculator View(double cx, double cy, double zoom, int w, int h) => new(w, h)
    {
        CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = 200,
        Quality = QualityPreset.Standard, ColorMap = new HsvPalette(),
    };

    private static void WithStatics(Action body)
    {
        var savedKernel = HeadlessGpu.Kernel;
        bool savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
        try { body(); }
        finally
        {
            HeadlessGpu.Kernel = savedKernel;
            MandelbrotCalculator.UseGpuPerturbation = savedPerturb;
        }
    }

    // Views the GPU can't draw: past 1e4 below the high-precision band, deep without
    // FF_GPU_PERTURB, and a shallow view whose pixels are under fp32 precision (8000x at 4K).
    public static TheoryData<double, double, double, int, int> CpuViews => new()
    {
        { -0.743643887037151, 0.131825904205330, 640000, 64, 36 },
        { -0.743643887037151, 0.131825904205330, 1e20, 64, 36 },
        { -0.743643887037151, 0.131825904205330, 8000, 3840, 2160 },
    };

    [Theory]
    [MemberData(nameof(CpuViews))]
    public void A_Skipped_View_Opens_No_Device_And_Would_Not_Have_Dispatched(double cx, double cy, double zoom, int w, int h)
    {
        WithStatics(() =>
        {
            MandelbrotCalculator.UseGpuPerturbation = false;
            int opened = 0;
            HeadlessGpu.KernelFactory = () => { opened++; return new StubKernel(); };
            var m = View(cx, cy, zoom, w, h);
            Assert.NotNull(m.CpuOnlyView());
            HeadlessGpu.Attach(m);
            Assert.Equal(0, opened);
            Assert.Null(m.GpuKernel);
            Assert.Equal(m.CpuOnlyView(), HeadlessGpu.LastNotOpened);

            // The invariant: attached eagerly, the same view never dispatches.
            if ((long)w * h > 64 * 36) return;   // the 4K view is covered by S1611's gate test
            var stub = new StubKernel();
            HeadlessGpu.Kernel = stub;
            var eager = View(cx, cy, zoom, w, h);
            HeadlessGpu.Attach(eager);
            Assert.Same(stub, eager.GpuKernel);   // precondition: really attached
            eager.Calculate();
            Assert.Equal(0, stub.Runs);
        });
    }

    [Fact]
    public void A_Drawable_View_Opens_The_Device_Once_And_Dispatches()
    {
        WithStatics(() =>
        {
            var stub = new StubKernel();
            int opened = 0;
            HeadlessGpu.KernelFactory = () => { opened++; return stub; };
            var m = View(-0.5, 0, 1, 64, 36);
            Assert.Null(m.CpuOnlyView());
            HeadlessGpu.Attach(m);
            HeadlessGpu.Attach(View(-0.5, 0, 2, 64, 36));
            HeadlessGpu.Attach(new EscapeTimeCalculator(16, 16));
            Assert.Equal(1, opened);
            Assert.Null(HeadlessGpu.LastNotOpened);
            Assert.Same(stub, m.GpuKernel);
            m.Calculate();
            Assert.True(stub.Runs > 0, "a drawable view should dispatch");
        });
    }

    [Fact]
    public void Deep_With_Gpu_Perturbation_And_Escape_Time_Open_The_Device()
    {
        WithStatics(() =>
        {
            MandelbrotCalculator.UseGpuPerturbation = true;
            Assert.Null(View(-0.743643887037151, 0.131825904205330, 1e20, 64, 36).CpuOnlyView());

            int opened = 0;
            HeadlessGpu.KernelFactory = () => { opened++; return new StubKernel(); };
            var e = new EscapeTimeCalculator(16, 16);
            HeadlessGpu.Attach(e);
            Assert.Equal(1, opened);
            Assert.NotNull(e.GpuKernel);
        });
    }

    [Fact]
    public void A_Calculator_Without_A_Gpu_Kernel_Never_Opens_The_Device()
    {
        WithStatics(() =>
        {
            int opened = 0;
            HeadlessGpu.KernelFactory = () => { opened++; return new StubKernel(); };
            HeadlessGpu.Attach(new MandelbulbCalculator(16, 16));
            HeadlessGpu.Attach(null);
            Assert.Equal(0, opened);
            HeadlessGpu.Kernel = null;   // clears the pending factory too
            Assert.Null(HeadlessGpu.KernelFactory);
        });
    }
}
