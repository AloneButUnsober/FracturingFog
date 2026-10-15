// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1228 — VulkanComputeKernel keeps the buffers the shader reads many times (the
// reference orbit, the SA / BLA tables) in VRAM behind host-visible staging;
// FF_VULKAN_MEMORY=device-outputs also stages the frame outputs and the Buddhabrot
// histogram, and =host is the pre-#1228 all-host layout. Checked on whatever
// Vulkan device the host has (skipped without one):
//   • every kernel path gives byte-identical output in all three layouts, over a
//     sequence that changes the orbit, resizes, and re-runs a Buddhabrot batch (a
//     stale staging copy, a missed upload or readback, or an uncleared histogram
//     shows up as a difference against the host layout, which maps the buffers
//     directly as before #1228). The sequence is not vacuous: the two orbits give
//     different frames and every output carries data;
//   • on a discrete card (host-visible memory off the device-local heap) each
//     layout puts what it says in VRAM: nothing for host, the orbit (and not the
//     frame) for auto, the frame as well for device-outputs;
//   • FF_VULKAN_MEMORY parses to the layouts.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes PerturbBudgetMs
public sealed class S1228VulkanBufferPlacementTests : IDisposable
{
    // Correctness, not speed: no first-band abort on a weak-fp64 card under load.
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    public S1228VulkanBufferPlacementTests() => MandelbrotKernelSource.PerturbBudgetMs = 0;
    public void Dispose() => MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;

    private const double EscapeR2 = 512.0 * 512.0;

    private static VulkanComputeKernel Kernel(VulkanBufferPlacement placement)
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext(placement);
        if (k is null) Assert.Skip("no Vulkan device on this host");
        Assert.Equal(placement, k.Placement);
        return k;
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

    private static InterpretedOrbitColorMap TrapTheme()
    {
        bool prev = InterpretedOrbitColorMap.GpuEnabled;
        InterpretedOrbitColorMap.GpuEnabled = true;
        try
        {
            var m = InterpretedColorMap.TryCreate("return hsv(saturate(trapMin), 0.9, 1.0);", null, out string? err);
            Assert.Null(err);
            return Assert.IsType<InterpretedOrbitColorMap>(m);
        }
        finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
    }

    private sealed class Frame
    {
        public readonly int W, H, N;
        public readonly int[] It; public readonly float[] Sm, Zr, Zi, Dr, Di;
        public Frame(int w, int h)
        {
            W = w; H = h; N = w * h;
            It = new int[N]; Sm = new float[N]; Zr = new float[N]; Zi = new float[N]; Dr = new float[N]; Di = new float[N];
        }
        public IEnumerable<(string, Array)> Outputs(string tag) =>
            [($"{tag}.iter", It), ($"{tag}.smooth", Sm), ($"{tag}.zr", Zr), ($"{tag}.zi", Zi), ($"{tag}.dr", Dr), ($"{tag}.di", Di)];
    }

    /// <summary>One pass over every kernel path; returns each output by name.</summary>
    private static List<(string Name, Array Data)> Sequence(VulkanComputeKernel k)
    {
        var outs = new List<(string, Array)>();

        // SP escape time, no GPU palette.
        var sp = new Frame(320, 240);
        k.Run(sp.W, sp.H, -0.5, 0.0, 3.5 / sp.W, 256, 4.0, sp.It, sp.Sm, sp.Zr, sp.Zi, sp.Dr, sp.Di);
        outs.AddRange(sp.Outputs("sp"));

        if (k.SupportsPerturbation)
        {
            // Two different reference orbits at the same size: the second upload must
            // replace the first (seahorse valley, then the Feigenbaum point, longer orbit).
            var a = Orbit(-0.743643887037151, 0.13182590420533, 3000);
            var pa = new Frame(96, 96);
            k.RunPerturb(96, 96, 3.5 / (96 * 1e6), 3000, EscapeR2, -48, -48, a.Zr, a.Zi, a.Len,
                pa.It, pa.Sm, pa.Zr, pa.Zi, pa.Dr, pa.Di);
            outs.AddRange(pa.Outputs("ptA"));

            var b = Orbit(-1.4011551890920506, 0.0, 5000);   // Feigenbaum point: in the set, boundary all round
            var pb = new Frame(96, 96);
            k.RunPerturb(96, 96, 3.5 / (96 * 1e4), 5000, EscapeR2, -48, -48, b.Zr, b.Zi, b.Len,
                pb.It, pb.Sm, pb.Zr, pb.Zi, pb.Dr, pb.Di);
            outs.AddRange(pb.Outputs("ptB"));

            // SA + BLA (the orbit, 8 coefficient tables and the BLA table all staged).
            var sa = new SeriesApproximation(a.Zr, a.Zi, a.Len);
            double scale = 3.5 / (96 * 1e12);
            var bla = new BlaTable(a.Zr, a.Zi, a.Len, Math.Sqrt(2.0) * 0.5 * 96 * scale);
            var ps = new Frame(96, 96);
            k.RunPerturbSA(96, 96, scale, 3000, EscapeR2, -48, -48, a.Zr, a.Zi, a.Len, 1e-3, sa.SafeMax,
                sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI,
                ps.It, ps.Sm, ps.Zr, ps.Zi, ps.Dr, ps.Di, bla.GpuCoefficients, bla.Levels);
            outs.AddRange(ps.Outputs("sa"));

            // Orbit means, at a new size (every frame buffer is re-created).
            int mask = MandelbrotKernelSource.OrbAll, stride = MandelbrotKernelSource.PerturbOrbitStride(mask);
            var po = new Frame(64, 48);
            var orbit = new float[po.N * stride];
            k.RunPerturbOrbit(64, 48, 3.5 / (64 * 1e6), 3000, EscapeR2, -32, -24, a.Zr, a.Zi, a.Len,
                mask, -0.743643887037151, 0.13182590420533, po.It, po.Sm, po.Zr, po.Zi, po.Dr, po.Di, orbit);
            outs.AddRange(po.Outputs("orb"));
            outs.Add(("orb.means", orbit));
        }

        // SP with an orbit GPU palette: colour + trap outputs.
        k.SetPalette(TrapTheme());
        Assert.True(k.HasGpuPalette, "the trap theme did not compile for the GPU");
        var sc = new Frame(200, 150);
        var color = new uint[sc.N]; var trap = new float[sc.N];
        k.Run(sc.W, sc.H, -0.5, 0.0, 3.5 / sc.W, 256, 4.0, sc.It, sc.Sm, sc.Zr, sc.Zi, sc.Dr, sc.Di,
            colorDst: color, trapDst: trap);
        k.SetPalette(null);
        outs.AddRange(sc.Outputs("spc"));
        outs.Add(("spc.color", color));
        outs.Add(("spc.trap", trap));

        // Buddhabrot twice: the histogram is cleared between batches, so each adds
        // the same counts to its (fresh) arrays.
        for (int rep = 0; rep < 2; rep++)
        {
            int bw = 160, bh = 120, bn = bw * bh;
            var r = new uint[bn]; var g = new uint[bn]; var bl = new uint[bn];
            k.RunBuddhaBatch(new GpuBuddhaBatch(bw, bh, 3.0 / bw, -0.5, 0, 500, false, false, 20, 100, 7u, 1, 200_000), r, g, bl);
            outs.Add(($"buddha{rep}.r", r)); outs.Add(($"buddha{rep}.g", g)); outs.Add(($"buddha{rep}.b", bl));
        }
        return outs;
    }

    private static bool Same(Array x, Array y) => x.Length == y.Length
        && Enumerable.Range(0, x.Length).All(i => Equals(x.GetValue(i), y.GetValue(i)));

    [Fact]
    public void Every_Path_Is_Byte_Identical_In_All_Three_Layouts()
    {
        List<(string Name, Array Data)> host, auto, devOut;
        using (var k = Kernel(VulkanBufferPlacement.HostOnly)) host = Sequence(k);
        using (var k = Kernel(VulkanBufferPlacement.Auto)) auto = Sequence(k);
        using (var k = Kernel(VulkanBufferPlacement.DeviceOutputs)) devOut = Sequence(k);

        // Not vacuous: every output carries data, and the two orbits differ.
        foreach (var (name, data) in host)
            Assert.True(data.Cast<object>().Any(v => Convert.ToDouble(v) != 0), $"{name} is all zero");
        var byName = host.ToDictionary(o => o.Name, o => o.Data);
        if (byName.ContainsKey("ptA.iter"))
            Assert.False(Same(byName["ptA.iter"], byName["ptB.iter"]), "the two reference orbits gave the same frame");
        Assert.True(Same(byName["buddha0.r"], byName["buddha1.r"]), "host layout: the second Buddhabrot batch differs");

        Assert.Equal(host.Select(o => o.Name), auto.Select(o => o.Name));
        Assert.Equal(host.Select(o => o.Name), devOut.Select(o => o.Name));
        for (int i = 0; i < host.Count; i++)
        {
            Assert.True(Same(host[i].Data, auto[i].Data), $"auto layout differs from host on {host[i].Name}");
            Assert.True(Same(host[i].Data, devOut[i].Data), $"device-outputs layout differs from host on {host[i].Name}");
        }
    }

    [Fact]
    public void Each_Layout_Puts_What_It_Says_In_Vram_On_A_Discrete_Card()
    {
        // Discrete = the host-visible memory the old layout used is off the device-local heap.
        using (var ctx = TryContext())
        {
            if (ctx is null) Assert.Skip("no Vulkan device on this host");
            uint all = uint.MaxValue;
            uint hostType = VulkanHostMemory.FindType(ctx.Vk, ctx.PhysicalDevice, all, readback: false);
            if (VulkanHostMemory.IsOnDeviceLocalHeap(ctx.Vk, ctx.PhysicalDevice, hostType))
                Assert.Skip($"{ctx.PickedName}: unified memory, nothing is staged");
            Assert.NotNull(VulkanHostMemory.FindDeviceLocalType(ctx.Vk, ctx.PhysicalDevice, all));
        }

        const int W = 128, H = 96, N = W * H, MaxIter = 2000;
        var o = Orbit(-0.1225611668766536, 0.7448617666197442, MaxIter);
        long orbitBytes = 2L * o.Len * sizeof(double), frameBytes = 32L * N;
        long Vram(VulkanBufferPlacement p)
        {
            using var k = Kernel(p);
            if (!k.SupportsPerturbation) Assert.Skip("Vulkan device has no shaderFloat64");
            var f = new Frame(W, H);
            k.RunPerturb(W, H, 3.5 / (W * 1e4), MaxIter, EscapeR2, -W / 2, -H / 2, o.Zr, o.Zi, o.Len,
                f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);
            Assert.True(k.ResidentBytes >= frameBytes + orbitBytes, $"{p}: resident {k.ResidentBytes}");
            return k.ResidentDeviceLocalBytes;
        }

        Assert.Equal(0, Vram(VulkanBufferPlacement.HostOnly));
        long auto = Vram(VulkanBufferPlacement.Auto);
        Assert.InRange(auto, orbitBytes, orbitBytes + frameBytes / 2);   // orbit yes, frame no
        Assert.True(Vram(VulkanBufferPlacement.DeviceOutputs) >= orbitBytes + frameBytes - 4L * N, "device-outputs: frame not in VRAM");
    }

    private static VulkanContext? TryContext()
    {
        try
        {
            var ctx = VulkanContext.CreateInstance();
            if (ctx.EnumerateDevices().Count == 0) { ctx.Dispose(); return null; }
            ctx.CreateComputeDevice();
            return ctx;
        }
        catch { return null; }
    }

    [Theory]
    [InlineData(null, VulkanBufferPlacement.Auto)]
    [InlineData("", VulkanBufferPlacement.Auto)]
    [InlineData("auto", VulkanBufferPlacement.Auto)]
    [InlineData("host", VulkanBufferPlacement.HostOnly)]
    [InlineData(" HOST ", VulkanBufferPlacement.HostOnly)]
    [InlineData("device-outputs", VulkanBufferPlacement.DeviceOutputs)]
    [InlineData("vram", VulkanBufferPlacement.Auto)]
    public void Environment_Value_Parses_To_A_Layout(string? value, VulkanBufferPlacement expected)
        => Assert.Equal(expected, VulkanHostMemory.ParsePlacement(value));
}
