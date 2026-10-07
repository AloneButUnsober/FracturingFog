// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-J / GPU parity plan G4.3 â€” the GPU orbit path fills TrapBuffer. An orbit
// ColorGen theme (one that reads trapMin) colours on the GPU orbit kernel, but that
// kernel wrote colour only: MandelbrotCalculator.TrapBuffer kept whatever the last
// CPU frame left there, so the orbit-trap relief height source (Trap / Blend) read a
// stale or empty field, and the hi-res relief twin had to be forced to the CPU. The
// orbit kernel now writes the per-pixel trap minimum to a fourth UAV (u4 / binding
// 204), read back into TrapBuffer. These tests render on the Vulkan device (skipped
// without one) against the CPU orbit path:
//   â€¢ the trap field matches the CPU's, and every pixel is written (the buffer is
//     NaN-filled first, so a kernel that skips the write is caught);
//   â€¢ in-set pixels carry 0, as the CPU writes for an exterior theme;
//   â€¢ a theme that does not read trapMin gets an all-zero trap, as on the CPU;
//   â€¢ the relief height built from the GPU trap matches the CPU-built one.

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Rendering.Vulkan;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1173JGpuOrbitTrapTests
{
    private const int W = 320, H = 240, MaxIter = 256;

    private const string TrapTheme = "return hsv(saturate(trapMin), 0.9, 1.0);";
    private const string StripeTheme = "return hsv(saturate(stripeAvg), 0.9, 1.0);";

    private static InterpretedOrbitColorMap Theme(string src)
    {
        bool prev = InterpretedOrbitColorMap.GpuEnabled;
        InterpretedOrbitColorMap.GpuEnabled = true;
        try
        {
            var m = InterpretedColorMap.TryCreate(src, null, out string? err);
            Assert.Null(err);
            return Assert.IsType<InterpretedOrbitColorMap>(m);
        }
        finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    private sealed record Frame(float[] Trap, float[] Smooth, int[] Iter, bool Gpu);

    private static Frame Render(InterpretedOrbitColorMap map, VulkanComputeKernel? gpu)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = MaxIter, ColorMap = map,
            UseGpuCompute = gpu != null, GpuKernel = gpu,
        };
        Array.Fill(c.TrapBuffer, float.NaN);   // a pixel the frame does not write stays NaN
        c.Calculate(CancellationToken.None);
        return new Frame((float[])c.TrapBuffer.Clone(), (float[])c.SmoothBuffer.Clone(),
                         (int[])c.IterationBuffer.Clone(), c.LastFrameUsedGpuCompute);
    }

    [Fact]
    public void Gpu_Orbit_Frame_Fills_TrapBuffer_Like_The_Cpu()
    {
        using var k = Device();
        var theme = Theme(TrapTheme);
        var g = Render(theme, k);
        var c = Render(theme, null);

        Assert.True(g.Gpu, "the orbit theme did not run on the GPU orbit kernel");
        Assert.False(c.Gpu);
        Assert.DoesNotContain(g.Trap, float.IsNaN);   // every pixel written

        // Independent invariant: the trap is min |z_n| over n >= 1 and z_1 = c, so an
        // escaped pixel's trap lies in [0, |c|]; in the set (exterior theme) it is 0.
        double scale = 3.5 / Math.Max(W, H);
        int escaped = 0;
        for (int i = 0; i < g.Trap.Length; i++)
        {
            if (g.Iter[i] >= MaxIter) { Assert.Equal(0f, g.Trap[i]); continue; }
            escaped++;
            double cr = -0.5 + (i % W - 0.5 * W) * scale, ci = (i / W - 0.5 * H) * scale;
            double absC = Math.Sqrt(cr * cr + ci * ci);
            Assert.InRange(g.Trap[i], 0f, (float)(absC * (1 + 1e-5) + 1e-6));
        }
        Assert.True(escaped > g.Trap.Length / 4, $"only {escaped} escaped pixels");
        Assert.True(g.Trap.Count(v => v > 0f) > escaped / 2, "trap field is mostly zero");

        // Parity with the CPU orbit path where both took the same escape iteration
        // (fp32 vs fp64 orbit: a few boundary pixels diverge).
        var d = Enumerable.Range(0, g.Trap.Length)
            .Where(i => g.Iter[i] == c.Iter[i] && g.Iter[i] < MaxIter)
            .Select(i => (double)Math.Abs(g.Trap[i] - c.Trap[i])).OrderBy(v => v).ToArray();
        Assert.True(d.Length > escaped / 2, $"only {d.Length} pixels share their escape iteration");
        double median = d[d.Length / 2], p95 = d[(int)(d.Length * 0.95)];
        Assert.True(median < 1e-4, $"median |trap GPU âˆ’ CPU| {median:E2}");
        Assert.True(p95 < 1e-2, $"p95 |trap GPU âˆ’ CPU| {p95:E2}");

        // Contrast: the CPU trap is far from a zeroed (never-written) field, so the
        // agreement above is not trivially met.
        double meanCpu = c.Trap.Where((v, i) => c.Iter[i] < MaxIter).Average(v => (double)v);
        Assert.True(meanCpu > 100 * Math.Max(median, 1e-6), $"CPU mean trap {meanCpu:E2} vs median diff {median:E2}");
    }

    [Fact]
    public void Theme_Without_TrapMin_Writes_A_Zero_Trap_Like_The_Cpu()
    {
        using var k = Device();
        var theme = Theme(StripeTheme);
        var g = Render(theme, k);
        var c = Render(theme, null);
        Assert.True(g.Gpu, "the orbit theme did not run on the GPU orbit kernel");
        Assert.All(c.Trap, v => Assert.Equal(0f, v));
        Assert.All(g.Trap, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Trap_Relief_Height_From_The_Gpu_Matches_The_Cpu()
    {
        using var k = Device();
        var theme = Theme(TrapTheme);
        var g = Render(theme, k);
        var c = Render(theme, null);
        int n = W * H;
        float[] HeightOf(Frame f) => ReliefHeightField.Build(f.Smooth, f.Trap, n, ReliefHeightSource.Trap, 0.5);
        var hg = HeightOf(g);
        var hc = HeightOf(c);
        // A stale / zero trap makes Build fall back to the smooth field â€” far from the trap height.
        var hs = ReliefHeightField.Build(c.Smooth, new float[n], n, ReliefHeightSource.Trap, 0.5);

        double drift = 0, contrast = 0; int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (g.Iter[i] != c.Iter[i]) continue;
            drift += Math.Abs(hg[i] - hc[i]); contrast += Math.Abs(hs[i] - hc[i]); m++;
        }
        drift /= m; contrast /= m;
        Assert.True(contrast > Math.Max(0.05, 8 * drift), $"height drift {drift:E2} vs no-trap contrast {contrast:E2}");
    }

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    [Fact]
    public void Host_HiRes_Trap_Twin_Runs_On_The_Gpu_And_Matches_The_Cpu()
    {
        using var k = Device();
        var theme = Theme(TrapTheme);
        const int dw = 160, dh = 120;   // below the 480 field floor -> a 640x480 twin
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), dw, dh, ColorPalette.BuiltIns[0]);
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        var main = (MandelbrotCalculator)typeof(FractalRenderHost).GetField("_calculator", F)!.GetValue(host)!;
        main.CenterX = -0.5; main.CenterY = 0; main.Zoom = 1.0; main.MaxIterations = MaxIter;
        main.ColorMap = theme; main.UseGpuCompute = true; main.GpuKernel = k;
        var fp = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DHeightSource = ReliefHeightSource.Trap,
        };
        var capture = typeof(FractalRenderHost).GetMethod("TryCaptureHiResReliefField", F)!;
        bool Capture() => (bool)capture.Invoke(host, new object[] { FractalType.Mandelbrot, fp, dw, dh, CancellationToken.None })!;

        Assert.True(Capture(), "hi-res relief capture refused");
        var twin = (MandelbrotCalculator)typeof(FractalRenderHost).GetField("_reliefFieldCalc", F)!.GetValue(host)!;
        Array.Fill(twin.TrapBuffer, float.NaN);
        Assert.True(Capture(), "hi-res relief capture refused");
        Assert.True(twin.Width > dw && twin.Height > dh, $"twin {twin.Width}x{twin.Height} is not hi-res");
        Assert.True(twin.LastFrameUsedGpuCompute, "the trap twin ran on the CPU");
        Assert.DoesNotContain(twin.TrapBuffer, float.IsNaN);
        var gTrap = (float[])twin.TrapBuffer.Clone();
        var gIter = (int[])twin.IterationBuffer.Clone();

        // The same twin on the CPU orbit path.
        twin.UseGpuCompute = false; twin.GpuKernel = null;
        twin.Calculate(CancellationToken.None);
        Assert.False(twin.LastFrameUsedGpuCompute);
        var d = Enumerable.Range(0, gTrap.Length)
            .Where(i => gIter[i] == twin.IterationBuffer[i] && gIter[i] < MaxIter)
            .Select(i => (double)Math.Abs(gTrap[i] - twin.TrapBuffer[i])).OrderBy(v => v).ToArray();
        Assert.True(d.Length > gTrap.Length / 4, $"only {d.Length} shared escaped pixels");
        Assert.True(d[d.Length / 2] < 1e-4, $"median |trap GPU − CPU| {d[d.Length / 2]:E2}");
    }
}
