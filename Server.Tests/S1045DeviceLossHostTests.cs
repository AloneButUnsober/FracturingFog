// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1045 — a renderer that rebuilt its GPU device after a loss moves its
// DeviceGeneration on; the host must drop the kernels built on the dead device
// and rebuild what is in use on the new one, and show the renderer's device
// status once. (The D3D11 presenter's own detect-and-rebuild needs a window and
// a device: the root exe's --d3ddevicelossprobe covers it.)

using System;
using System.Collections.Generic;
using System.Reflection;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1045DeviceLossHostTests
{
    private sealed class FakeRenderer : IFractalRenderer
    {
        public int Generation;
        public string? Status;
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "fake";
        public bool VSync { get; set; }
        public int DeviceGeneration => Generation;
        public string? DeviceStatus => Status;
        public void Dispose() { }
    }

    private sealed class Kernel : IGpuKernel
    {
        public bool Disposed;
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public string BackendLabel => "Stub";
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f, uint[]? colorDst = null,
            float[]? trapDst = null, GpuDomainWarp warp = default)
            => Array.Fill(iterDst, maxIter, 0, width * height);
        public void Dispose() => Disposed = true;
    }

    private sealed class Relief : IReliefRaymarchKernel
    {
        public bool Disposed;
        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
            float[]? aovNormalXyz = null, float[]? aovDepth = null) { }
        public void Dispose() => Disposed = true;
    }

    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;

    private static (FractalRenderHost Host, FakeRenderer R, List<Kernel> Kernels, List<Relief> Reliefs, List<string> Status) Make()
    {
        var r = new FakeRenderer();
        var host = new FractalRenderHost(r, new FractalViewState(), 32, 24, ColorPalette.BuiltIns[0]);
        var kernels = new List<Kernel>();
        var reliefs = new List<Relief>();
        var status = new List<string>();
        host.GpuKernelFactory = (_, _) => { var k = new Kernel(); kernels.Add(k); return k; };
        host.ReliefKernelFactory = (_, _) => { var k = new Relief(); reliefs.Add(k); return k; };
        host.StatusRequested += (_, s) => status.Add(s);
        return (host, r, kernels, reliefs, status);
    }

    private static IGpuKernel? MainKernel(FractalRenderHost h) =>
        ((MandelbrotCalculator)typeof(FractalRenderHost).GetField("_calculator", F)!.GetValue(h)!).GpuKernel;

    private static IGpuKernel? BuddhaKernel(FractalRenderHost h) =>
        ((BuddhaFamilyCalculator)typeof(FractalRenderHost).GetField("_buddhabrotCalculator", F)!.GetValue(h)!).GpuKernel;

    private static IReliefRaymarchKernel? EnsureRelief(FractalRenderHost h) =>
        (IReliefRaymarchKernel?)typeof(FractalRenderHost).GetMethod("EnsureReliefKernel", F)!.Invoke(h, null);

    [Fact]
    public void A_Rebuilt_Device_Replaces_Every_Kernel_Built_On_The_Old_One()
    {
        var (host, r, kernels, reliefs, status) = Make();
        using var _ = host;
        host.UseGpuCompute = true;
        var relief1 = EnsureRelief(host);
        Assert.Single(kernels);
        Assert.Same(kernels[0], MainKernel(host));

        host.SyncGpuKernelsWithDevice();   // same generation: nothing happens
        Assert.Single(kernels);
        Assert.False(kernels[0].Disposed);

        r.Generation = 1;
        r.Status = "GPU reset: display restored";
        host.SyncGpuKernelsWithDevice();

        Assert.True(kernels[0].Disposed);
        Assert.Equal(2, kernels.Count);
        Assert.Same(kernels[1], MainKernel(host));
        Assert.Same(kernels[1], BuddhaKernel(host));
        Assert.True(host.UseGpuCompute);
        Assert.True(reliefs[0].Disposed);
        var relief2 = EnsureRelief(host);   // rebuilt on its next use
        Assert.NotSame(relief1, relief2);
        Assert.Same(reliefs[1], relief2);
        Assert.Equal(new[] { "GPU reset: display restored" }, status);

        host.SyncGpuKernelsWithDevice();    // status shown once, kernels kept
        Assert.Single(status);
        Assert.Equal(2, kernels.Count);
    }

    [Fact]
    public void With_Gpu_Compute_Off_The_Kernel_Is_Dropped_And_Built_On_Next_Use()
    {
        var (host, r, kernels, _, _) = Make();
        using var _h = host;
        host.UseGpuCompute = true;
        host.UseGpuCompute = false;
        r.Generation = 1;
        host.SyncGpuKernelsWithDevice();
        Assert.True(kernels[0].Disposed);
        Assert.Single(kernels);
        Assert.Null(MainKernel(host));

        host.UseGpuCompute = true;
        Assert.Equal(2, kernels.Count);
        Assert.Same(kernels[1], MainKernel(host));
    }

    // End to end: the live host's frame loop syncs before it calculates, so the
    // first frame after a rebuild already runs on a fresh kernel.
    [Fact]
    public void The_Next_Live_Frame_Runs_On_A_Fresh_Kernel()
    {
        var (host, r, kernels, _, status) = Make();
        using var _h = host;
        host.UseGpuCompute = true;
        using var done = new System.Threading.ManualResetEventSlim();
        host.FrameCompleted += (_, _) => done.Set();

        r.Generation = 1;
        r.Status = "GPU reset: display restored";
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame completed");

        Assert.True(kernels[0].Disposed);
        Assert.Equal(2, kernels.Count);
        Assert.Same(kernels[1], MainKernel(host));
        Assert.Contains("GPU reset: display restored", status);
    }

    [Fact]
    public void A_Give_Up_Status_Is_Shown_Without_A_New_Generation()
    {
        var (host, r, kernels, _, status) = Make();
        using var _h = host;
        host.UseGpuCompute = true;
        r.Status = "GPU reset: the display could not be restored - restart the app";
        host.SyncGpuKernelsWithDevice();
        Assert.Equal(new[] { r.Status }, status);
        Assert.False(kernels[0].Disposed);
    }
}
