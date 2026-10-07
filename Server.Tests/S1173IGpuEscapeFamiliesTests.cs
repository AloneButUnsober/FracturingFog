// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-I / GPU parity plan G4.4 — Multibrot, Phoenix and the domain warp on the
// GPU. They were the escape-time family's CPU-only paths: Multibrot needed z^d,
// Phoenix carries the previous z, and the warp displaces each pixel's c (#253).
// The shared SP kernel (D3D11 + Vulkan) now has FractalKind 4 (Multibrot, closed
// forms for d = 3..5, polar otherwise, as MultibrotKernel) and 5 (Phoenix, with
// dz/dc carried as PhoenixKernel.StepWithPrevDeriv), and applies the warp per
// pixel from three cbuffer scalars. These tests render on the Vulkan device
// (skipped without one) against the CPU frame:
//   • the frame ran on the GPU (route), the escape iteration agrees on nearly every
//     pixel and the smooth count where it agrees;
//   • the colour drift is far below the drift to a contrast frame that differs
//     only in the feature under test (another power, p, or the warp off), so the
//     parity is not met by a kernel that ignores the feature.
// d = 8 also pins the fp32 overflow fix: it escapes with |z| ~ 1e21, where
// sqrt(zr*zr + zi*zi) was +inf and every such pixel's smooth -inf (SafeMag).

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1173IGpuEscapeFamiliesTests
{
    private const int W = 320, H = 240, MaxIter = 256;

    private sealed record Frame(uint[] Color, int[] Iter, float[] Smooth, GpuRoute Route);

    private static FractalParameters Params(int d = 3, double pr = -0.5, double pi = 0.0,
                                            double warp = 0.0, double freq = 1.0, Complex? julia = null)
    {
        var p = new FractalParameters
        {
            MultibrotExponent = d,
            PhoenixP = new Complex(pr, pi),
            DomainWarpEnabled = warp != 0.0,
            DomainWarpStrength = warp,
            DomainWarpFrequency = freq,
        };
        if (julia is Complex jc) p.JuliaC = jc;
        return p;
    }

    private static Frame Render(FractalType type, FractalParameters p, VulkanComputeKernel? gpu)
    {
        var theme = ColorPalette.BuiltIns.First(m => m is FracturingFog.Interefaces.IGpuHlslPalette);
        var c = new EscapeTimeCalculator(W, H)
        {
            FractalType = type, FractalParameters = p, ColorMap = theme, MaxIterations = MaxIter,
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            UseGpuCompute = gpu != null, GpuKernel = gpu,
        };
        c.Calculate(CancellationToken.None);
        return new Frame((uint[])c.ColorBuffer.Clone(), (int[])c.IterationBuffer.Clone(),
                         (float[])c.SmoothBuffer.Clone(), c.LastGpuRoute);
    }

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            for (int s = 0; s < 24; s += 8)
                sum += Math.Abs((int)((a[i] >> s) & 0xFF) - (int)((b[i] >> s) & 0xFF));
        return sum / (a.Length * 3.0);
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    public static TheoryData<string, FractalType, FractalParameters, FractalParameters> Cases => new()
    {
        // label, type, params, contrast params (differs only in the feature under test)
        { "Multibrot d2", FractalType.Multibrot, Params(d: 2), Params(d: 3) },
        { "Multibrot d3",         FractalType.Multibrot, Params(d: 3), Params(d: 4) },
        { "Multibrot d4",         FractalType.Multibrot, Params(d: 4), Params(d: 5) },
        { "Multibrot d5",         FractalType.Multibrot, Params(d: 5), Params(d: 4) },
        { "Multibrot d8", FractalType.Multibrot, Params(d: 8), Params(d: 7) },
        { "Phoenix",              FractalType.Phoenix,   Params(pr: -0.5), Params(pr: -0.3) },
        { "Phoenix complex p",    FractalType.Phoenix,   Params(pr: -0.4, pi: 0.1), Params(pr: -0.4) },
        { "Julia + warp",         FractalType.Julia,     Params(warp: 0.15, julia: new Complex(-0.8, 0.156)),
                                                         Params(julia: new Complex(-0.8, 0.156)) },
        { "Multibrot d3 + warp",  FractalType.Multibrot, Params(d: 3, warp: 0.2, freq: 2.0), Params(d: 3) },
        { "Phoenix + warp",       FractalType.Phoenix,   Params(warp: 0.1, freq: 0.0), Params() },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gpu_Frame_Matches_The_Cpu_Frame(string label, FractalType type, FractalParameters p, FractalParameters contrast)
    {
        using var k = Device();
        var g = Render(type, p, k);
        var c = Render(type, p, null);
        var other = Render(type, contrast, null);

        Assert.Equal(GpuRouteState.Gpu, g.Route.State);
        Assert.True(string.IsNullOrEmpty(g.Route.Detail), $"{label}: expected GPU colouring, got '{g.Route.Detail}'");
        Assert.NotEqual(GpuRouteState.Gpu, c.Route.State);

        int n = g.Iter.Length;
        int same = Enumerable.Range(0, n).Count(i => g.Iter[i] == c.Iter[i]);
        Assert.True(same > 0.97 * n, $"{label}: escape iteration agrees on only {same * 100.0 / n:F1}% of pixels");

        var d = Enumerable.Range(0, n)
            .Where(i => g.Iter[i] == c.Iter[i] && g.Iter[i] < MaxIter)
            .Select(i => (double)Math.Abs(g.Smooth[i] - c.Smooth[i])).OrderBy(v => v).ToArray();
        Assert.True(d.Length > n / 10, $"{label}: only {d.Length} escaped pixels share their iteration");
        Assert.True(d[d.Length / 2] < 0.01, $"{label}: median |smooth GPU − CPU| {d[d.Length / 2]:F4}");

        double drift = MeanDrift(g.Color, c.Color);
        double contrastDrift = MeanDrift(other.Color, c.Color);
        Assert.True(contrastDrift > Math.Max(4.0, 8 * drift),
            $"{label}: GPU vs CPU drift {drift:F2}, contrast frame {contrastDrift:F2}");
    }
}
