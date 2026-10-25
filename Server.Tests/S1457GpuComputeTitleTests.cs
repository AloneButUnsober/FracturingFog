// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1457 — the window title names the GPU that computes on Windows too: the
// D3D11 kernel the host attached reports its DXGI adapter (IGpuKernel.DeviceName),
// and GpuComputeSelection.FromAttachedKernel turns it into the same line Linux
// shows. No kernel (e.g. the DirectX 12 renderer) says the GPU is off. The
// Vulkan kernel reports the device its context picked.

using System.Linq;
using FracturingFog.Hosting;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1457GpuComputeTitleTests
{
    private sealed class Kernel(string label, string? name, bool overrideName = true) : IGpuKernel
    {
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public string BackendLabel => label;
        string? IGpuKernel.DeviceName => overrideName ? name : null;
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f, uint[]? colorDst = null,
            float[]? trapDst = null, GpuDomainWarp warp = default) { }
        public void Dispose() { }
    }

    /// <summary>A kernel that doesn't override DeviceName: the interface default.</summary>
    private sealed class PlainKernel : IGpuKernel
    {
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public string BackendLabel => "Stub";
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f, uint[]? colorDst = null,
            float[]? trapDst = null, GpuDomainWarp warp = default) { }
        public void Dispose() { }
    }

    [Fact]
    public void An_Attached_D3d11_Kernel_Names_Its_Adapter()
    {
        var d = GpuComputeSelection.FromAttachedKernel(new Kernel("D3D11", "NVIDIA GeForce GT 710"));
        Assert.True(d.UseGpu);
        Assert.Equal("NVIDIA GeForce GT 710", d.Device);
        Assert.Equal("GPU compute: D3D11 (NVIDIA GeForce GT 710)", d.Status);
    }

    [Fact]
    public void No_Kernel_Means_The_Cpu_Computes_And_Says_So()
    {
        var d = GpuComputeSelection.FromAttachedKernel(null);
        Assert.False(d.UseGpu);
        Assert.Null(d.Device);
        Assert.Equal("GPU compute off: this renderer has no GPU compute kernel", d.Status);
    }

    [Fact]
    public void A_Kernel_That_Cannot_Name_Its_Device_Still_Says_It_Computes()
    {
        Assert.Null(((IGpuKernel)new PlainKernel()).DeviceName);   // the interface default
        var d = GpuComputeSelection.FromAttachedKernel(new PlainKernel());
        Assert.True(d.UseGpu);
        Assert.Equal("GPU compute: Stub (unknown device)", d.Status);
    }

    [Fact]
    public void The_Vulkan_Kernel_Reports_The_Device_Its_Context_Picked()
    {
        using var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k == null) return;   // no Vulkan device on this machine
        var devices = GpuComputeSelection.ProbeDevices()!;
        var best = devices.Where(x => x.HasCompute).OrderByDescending(x => VulkanContext.RankType(x.Type)).First();
        Assert.Equal(best.Name, ((IGpuKernel)k).DeviceName);
        Assert.EndsWith(": " + best.Name, VulkanComputeKernel.ProbeDeviceName());
    }
}
