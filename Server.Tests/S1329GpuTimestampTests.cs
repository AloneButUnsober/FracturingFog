// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1329 — GPU timestamp queries give each kernel frame per-stage GPU time
// (IGpuKernel.LastGpuStageTimes) on the device's own clock. Checked:
//   • GpuStageTimes.FromStamps: segments close at each stamp, a Begin opens a
//     packet (the idle gap before it is not counted), the counter wraps by the
//     valid-bit mask, bad input gives null;
//   • on the Vulkan device (skipped without one):
//     – a frame's stages are > 0 and sum to no more than the host's wall time
//       around the same frame (LastDispatchMs + LastReadbackMs);
//     – on an all-in-set view (every pixel runs maxIter) the dispatch time grows
//       roughly linearly with maxIter — the stamp really brackets the shader;
//     – a banded perturbation frame reports one packet per band;
//     – on a discrete card, FF_VULKAN_MEMORY=device-outputs adds a copy-out stage
//       that the host layout doesn't have (#1228), and the staged orbit an
//       upload stage.
// The D3D11 kernel is covered by the --gputimestampprobe CLI check.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes PerturbBudgetMs
public sealed class S1329GpuTimestampTests(ITestOutputHelper output) : IDisposable
{
    private readonly double _savedBudget = SetBudget();
    private static double SetBudget() { double b = MandelbrotKernelSource.PerturbBudgetMs; MandelbrotKernelSource.PerturbBudgetMs = 0; return b; }
    public void Dispose() => MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;

    private const double EscapeR2 = 512.0 * 512.0;

    // ── Stamp arithmetic ───────────────────────────────────────────────────────

    [Fact]
    public void Stamps_Turn_Into_Stage_Times_And_Skip_Idle_Gaps()
    {
        var stages = new List<GpuStage>
        {
            GpuStage.Begin, GpuStage.Upload, GpuStage.Dispatch,     // packet 1: 10 + 100 ticks
            GpuStage.Begin, GpuStage.Dispatch,                      // 5000-tick idle gap, then 50
            GpuStage.Begin, GpuStage.CopyOut,                       // 7
        };
        ulong[] ticks = { 1000, 1010, 1110, 6110, 6160, 6200, 6207 };
        var t = GpuStageTimes.FromStamps(stages, ticks, nsPerTick: 1e6)!.Value;   // 1 tick = 1 ms
        Assert.Equal(10, t.UploadMs, 9);
        Assert.Equal(150, t.DispatchMs, 9);
        Assert.Equal(7, t.CopyOutMs, 9);
        Assert.Equal(3, t.Packets);
        Assert.Equal(167, t.TotalMs, 9);

        // A 36-bit counter that wrapped between two stamps.
        ulong mask = (1UL << 36) - 1;
        var w = GpuStageTimes.FromStamps(new[] { GpuStage.Begin, GpuStage.Dispatch }, new[] { mask - 4, 5UL }, 1e6, mask)!.Value;
        Assert.Equal(10, w.DispatchMs, 9);

        Assert.Null(GpuStageTimes.FromStamps(new List<GpuStage>(), Array.Empty<ulong>(), 1));
        Assert.Null(GpuStageTimes.FromStamps(new[] { GpuStage.Dispatch }, new[] { 1UL }, 1));   // no Begin
        Assert.Null(GpuStageTimes.FromStamps(new[] { GpuStage.Begin, GpuStage.Dispatch }, new[] { 1UL, 2UL }, 0));
    }

    // ── Vulkan ─────────────────────────────────────────────────────────────────

    private static VulkanComputeKernel Kernel(VulkanBufferPlacement? placement = null)
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext(placement);
        if (k is null) Assert.Skip("no Vulkan device on this host");
        // Skip only when the queue has no timestamps; after that a null result is a bug.
        if (!k.SupportsGpuTimestamps) { var d = k.Description; k.Dispose(); Assert.Skip($"{d}: no GPU timestamps on this queue"); }
        return k;
    }

    private sealed class Frame(int w, int h)
    {
        public readonly int W = w, H = h;
        public readonly int[] It = new int[w * h];
        public readonly float[] Sm = new float[w * h], Zr = new float[w * h], Zi = new float[w * h],
            Dr = new float[w * h], Di = new float[w * h];
    }

    // Inside the period-3 bulb: every pixel runs to maxIter (the shader's cardioid /
    // period-2 bulb skip would make the main cardioid free). #1342: on the plain
    // kernel — the periodicity early-out would stop these pixels after a few hundred
    // steps, whatever the cap.
    private static void InSetFrame(VulkanComputeKernel k, Frame f, int maxIter)
    {
        GpuPeriodicity.Mode = GpuPeriodicity.Off;
        try { k.Run(f.W, f.H, -0.1225611668766536, 0.7448617666197442, 0.02 / f.W, maxIter, 4.0, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di); }
        finally { GpuPeriodicity.Reset(); }
    }

    [Fact]
    public void A_Frame_Has_Positive_Stages_Within_The_Host_Wall_Time()
    {
        using var k = Kernel();
        var f = new Frame(256, 256);
        InSetFrame(k, f, 500);   // warm (pipeline creation)
        for (int r = 0; r < 3; r++)
        {
            InSetFrame(k, f, 1000);
            Assert.All(f.It, it => Assert.Equal(1000, it));   // precondition: all in set
            var t = k.LastGpuStageTimes;
            Assert.NotNull(t);
            double wall = k.LastDispatchMs + k.LastReadbackMs;
            output.WriteLine($"{t}  host wall {wall:F2} ms");
            Assert.True(t.Value.DispatchMs > 0, $"dispatch {t.Value.DispatchMs}");
            Assert.True(t.Value.TotalMs <= wall, $"GPU stages {t.Value.TotalMs:F3} ms > host wall {wall:F3} ms");
            Assert.Equal(1, t.Value.Packets);
        }
    }

    [Fact]
    public void Dispatch_Time_Grows_Linearly_With_MaxIter_On_An_All_In_Set_View()
    {
        using var k = Kernel();
        var f = new Frame(512, 512);
        InSetFrame(k, f, 500);
        Assert.All(f.It, it => Assert.Equal(500, it));   // precondition: all in set
        Assert.NotNull(k.LastGpuStageTimes);
        double Best(int maxIter)
        {
            double best = double.MaxValue;
            for (int r = 0; r < 3; r++) { InSetFrame(k, f, maxIter); best = Math.Min(best, k.LastGpuStageTimes!.Value.DispatchMs); }
            return best;
        }
        double t1 = Best(1000), t2 = Best(2000), t4 = Best(4000);
        output.WriteLine($"dispatch: 1000 → {t1:F2} ms, 2000 → {t2:F2} ms, 4000 → {t4:F2} ms");
        Assert.InRange(t2 / t1, 1.6, 2.4);
        Assert.InRange(t4 / t1, 3.2, 4.8);
    }

    private static (double[] Zr, double[] Zi, int Len) Orbit(double cx, double cy, int maxIter)
    {
        var zr = new double[maxIter + 1]; var zi = new double[maxIter + 1];
        double x = 0, y = 0; int len = maxIter;
        for (int n = 0; n < maxIter; n++)
        {
            zr[n] = x; zi[n] = y;
            if (x * x + y * y >= EscapeR2) { len = n; break; }
            double nx = x * x - y * y + cx; y = 2 * x * y + cy; x = nx;
        }
        return (zr, zi, len);
    }

    private static GpuStageTimes Perturb(VulkanComputeKernel k, int w, int h, int maxIter)
    {
        if (!k.SupportsPerturbation) Assert.Skip("Vulkan device has no shaderFloat64");
        var o = Orbit(-0.1225611668766536, 0.7448617666197442, maxIter);
        var f = new Frame(w, h);
        k.RunPerturb(w, h, 3.5 / (w * 1e4), maxIter, EscapeR2, -w / 2, -h / 2, o.Zr, o.Zi, o.Len,
            f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);
        var t = k.LastGpuStageTimes;
        Assert.NotNull(t);
        Assert.True(t.Value.TotalMs <= k.LastDispatchMs + k.LastReadbackMs);
        return t.Value;
    }

    [Fact]
    public void A_Banded_Perturbation_Frame_Reports_A_Packet_Per_Band()
    {
        using var k = Kernel(VulkanBufferPlacement.HostOnly);   // no staging submits
        const int W = 128, H = 384, MaxIter = 4000;
        int bandRows = MandelbrotKernelSource.PerturbBandRows(W, H, MaxIter);
        int bands = (H + bandRows - 1) / bandRows;
        var t = Perturb(k, W, H, MaxIter);
        output.WriteLine($"{t}; {bands} band(s) of {bandRows} rows");
        Assert.Equal(bands, t.Packets);
        Assert.True(t.DispatchMs > 0);
    }

    [Fact]
    public void Device_Outputs_Adds_A_Copy_Out_Stage_On_A_Discrete_Card()
    {
        using (var ctx = VulkanContextOrSkip())
        {
            uint hostType = VulkanHostMemory.FindType(ctx.Vk, ctx.PhysicalDevice, uint.MaxValue, readback: false);
            if (VulkanHostMemory.IsOnDeviceLocalHeap(ctx.Vk, ctx.PhysicalDevice, hostType))
                Assert.Skip($"{ctx.PickedName}: unified memory, nothing is staged");
        }
        GpuStageTimes Run(VulkanBufferPlacement p) { using var k = Kernel(p); Perturb(k, 128, 96, 2000); return Perturb(k, 128, 96, 2000); }
        var host = Run(VulkanBufferPlacement.HostOnly);
        var auto = Run(VulkanBufferPlacement.Auto);
        var dev = Run(VulkanBufferPlacement.DeviceOutputs);
        output.WriteLine($"host: {host}\nauto: {auto}\ndevice-outputs: {dev}");
        Assert.Equal(0, host.UploadMs);
        Assert.Equal(0, host.CopyOutMs);
        Assert.Equal(0, auto.CopyOutMs);     // outputs stay host-cached in auto
        Assert.True(auto.UploadMs > 0, "auto stages the orbit: an upload stage");
        Assert.True(dev.CopyOutMs > 0, "device-outputs stages the frame: a copy-out stage");
    }

    private static VulkanContext VulkanContextOrSkip()
    {
        VulkanContext? ctx = null;
        try
        {
            ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); ctx = null; }
            else ctx.CreateComputeDevice();
        }
        catch { ctx?.Dispose(); ctx = null; }
        if (ctx is null) Assert.Skip("no Vulkan device on this host");
        return ctx;
    }}
