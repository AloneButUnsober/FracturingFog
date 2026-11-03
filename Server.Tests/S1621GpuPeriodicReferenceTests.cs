// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1621 — the GPU deep perturbation kernels take #1614's periodic (truncated)
// reference: refLen = p + 1, and a pixel rebases when the reference runs out, as
// on the CPU. What broke it was the SA prelude: its skip was allowed up to
// k = refLen, but the GPU reference buffer holds Z_0..Z_{refLen-1} (the CPU array
// also has Z_refLen), so m = refLen read past the end. A full-length orbit almost
// never lets k reach refLen; a period-128 one (refLen 129) often does. The skip now
// stops at refLen - 1. Checked on the Vulkan fp64 kernel (the same HLSL the D3D11
// backend compiles) on the bench-kit p128 minibrot (fractadyne bench-kit, WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from rith's FF
// 0.7.1 benchmark report; see the bibliography):
//   • the periodic reference is really used on the GPU (period found, refLen p + 1,
//     the frame ran on the GPU kernel);
//   • sampled pixels match exact iteration (fixed-point BigInteger, no perturbation)
//     at least as well as with the full-length orbit;
//   • the periodic and full-orbit GPU frames agree on iteration counts to the same
//     3 % S1614 allows the CPU, and on smooth values where those agree;
//   • a view whose reference is not periodic renders byte-identically with
//     detection on or off.
// No QD-band (1e25..1e50) periodic view: the QD centre (~1e-64) lands within a
// pixel of a nucleus only for minibrots wider than ~1e-32, and the kit has none
// there (S1614's p953 nucleus at 1e45 is not detected). The kernels get the same
// Hi-limb doubles at every tier.
// Mutant: dropping the clamp (k <= refLen again) fails the agreement test.

using System;
using System.Linq;
using System.Numerics;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes MandelbrotCalculator / kernel statics
public sealed class S1621GpuPeriodicReferenceTests(ITestOutputHelper output)
{
    private const int W = 96, H = 54, Bits = 700;

    private sealed record Scene(string Name, double[] Cx, double[] Cy, double Zoom, int MaxIter, int Period);

    // 40-mini-p128-1e13.kfr: KF zoom 1.25603e13, converted for W x H (#1615).
    private static readonly Scene P128 = Kfr("p128",
        "-7.145519235131990488163335631423597550599502471617637408317e-1",
        "3.540207347648327221719984333721934753758406620526068439482e-1", 1.256030e13, 3840, 128);

    // The period-448 minibrot at 5.2e33: its reference is not detected as periodic (S1614).
    private static readonly Scene P448 = new("p448",
        [
            -0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50,
            -1.0894092006534645e-66, -5.481175082318642e-83, -1.5340609125118276e-99, 1.4285946495395532e-116,
        ],
        [
            -0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50,
            -4.820603700098655e-67, -3.1189999875481587e-83, 1.2339668272603997e-99, -9.480108552256602e-117,
        ],
        5.176068e33, 13440, 0);

    private static Scene Kfr(string name, string re, string im, double kfZoom, int iter, int period)
    {
        string text = $"Re: {re}\nIm: {im}\nZoom: {kfZoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\n";
        if (!FracturingFog.ViewState.KfrLocation.TryParse(text, out var loc, out string? err))
            throw new InvalidOperationException(err);
        var (x, y) = loc!.Centre();
        return new(name, x, y, loc.FfZoom(W, H), iter, period);
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationSA) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        return k;
    }

    private static MandelbrotCalculator Render(IGpuKernel k, Scene s, bool periodic)
    {
        bool savedPeriodic = MandelbrotCalculator.PeriodicReferenceOrbit, savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
        double savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
        MandelbrotCalculator.PeriodicReferenceOrbit = periodic;
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // no first-band abort to the CPU under load
        try
        {
            var c = new MandelbrotCalculator(W, H)
            {
                CenterX = s.Cx[0], CenterXLo = s.Cx[1], CenterX2 = s.Cx[2], CenterX3 = s.Cx[3],
                CenterX4 = s.Cx[4], CenterX5 = s.Cx[5], CenterX6 = s.Cx[6], CenterX7 = s.Cx[7],
                CenterY = s.Cy[0], CenterYLo = s.Cy[1], CenterY2 = s.Cy[2], CenterY3 = s.Cy[3],
                CenterY4 = s.Cy[4], CenterY5 = s.Cy[5], CenterY6 = s.Cy[6], CenterY7 = s.Cy[7],
                Zoom = s.Zoom, MaxIterations = s.MaxIter,
                Quality = QualityPreset.Extreme,
                ColorMap = new HsvPalette(), GpuKernel = k,
            };
            c.Calculate();
            Assert.True(c.LastFrameUsedGpuPerturbation, $"precondition: {s.Name} ran on the GPU deep kernel");
            return c;
        }
        finally
        {
            MandelbrotCalculator.PeriodicReferenceOrbit = savedPeriodic;
            MandelbrotCalculator.UseGpuPerturbation = savedPerturb;
            MandelbrotKernelSource.PerturbBudgetMs = savedBudget;
        }
    }

    private static BigInteger Fixed(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        int shift = exp - 1075 + Bits;
        BigInteger v = shift >= 0 ? new BigInteger(mant) << shift : new BigInteger(mant) >> -shift;
        return d < 0 ? -v : v;
    }

    private static int Exact(Scene s, double dcR, double dcI)
    {
        BigInteger cr = s.Cx.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l)) + Fixed(dcR);
        BigInteger ci = s.Cy.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l)) + Fixed(dcI);
        BigInteger er2 = new BigInteger(512 * 512) << Bits, zr = 0, zi = 0;
        int n = 0;
        for (; n < s.MaxIter; n++)
        {
            BigInteger zr2 = (zr * zr) >> Bits, zi2 = (zi * zi) >> Bits;
            if (zr2 + zi2 >= er2) break;
            zi = ((zr * zi) >> (Bits - 1)) + ci;
            zr = zr2 - zi2 + cr;
        }
        return n;
    }

    [Fact]
    public void The_Gpu_Takes_The_Periodic_Reference_And_Matches_Exact_Iteration()
    {
        var s = P128;
        using var k = Device();
        var on = Render(k, s, periodic: true);
        var off = Render(k, s, periodic: false);
        Assert.Equal(s.Period, on.ReferencePeriod);
        Assert.Equal(s.Period + 1, on.ReferenceOrbitLength);
        Assert.Equal(0, off.ReferencePeriod);

        double scale = (3.5 / Math.Max(W, H)) / s.Zoom;
        var rng = new Random(1621);
        int n = 40, onOk = 0, offOk = 0;
        for (int t = 0; t < n; t++)
        {
            int x = rng.Next(W), y = rng.Next(H), i = y * W + x;
            int exact = Exact(s, (x - W * 0.5) * scale, (y - H * 0.5) * scale);
            if (on.IterationBuffer[i] == exact) onOk++;
            if (off.IterationBuffer[i] == exact) offOk++;
        }
        output.WriteLine($"{s.Name}: exact matches on the GPU: periodic {onOk}/{n}, full orbit {offOk}/{n}");
        Assert.True(offOk >= n - 4, $"full-orbit GPU frame only {offOk}/{n} exact");
        Assert.True(onOk >= offOk - 1, $"periodic {onOk}/{n} vs full orbit {offOk}/{n}");
    }

    [Fact]
    public void Periodic_And_Full_Orbit_Gpu_Frames_Agree()
    {
        var s = P128;
        using var k = Device();
        var on = Render(k, s, periodic: true);
        var off = Render(k, s, periodic: false);
        Assert.Equal(s.Period, on.ReferencePeriod);   // precondition: the truncated orbit ran
        int iterDiff = 0, agree = 0, smoothDiff = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (on.IterationBuffer[i] != off.IterationBuffer[i]) { iterDiff++; continue; }
            if (on.IterationBuffer[i] >= s.MaxIter) continue;
            agree++;
            if (Math.Abs(on.SmoothBuffer[i] - off.SmoothBuffer[i]) > 0.1) smoothDiff++;
        }
        output.WriteLine($"{s.Name}: iteration diff {iterDiff}/{W * H}; smooth diff {smoothDiff}/{agree}; SA {on.LastFrameUsedGpuSeriesApproximation}, BLA {on.LastFrameUsedGpuBla}");
        Assert.True(agree > W * H / 4, $"precondition: {agree} escaped pixels agree");
        Assert.True(iterDiff <= W * H * 3 / 100, $"{iterDiff}/{W * H} iteration counts differ");
        Assert.True(smoothDiff <= agree / 100, $"{smoothDiff}/{agree} smooth values differ");
    }

    [Fact]
    public void A_Non_Periodic_Reference_Renders_Byte_Identically_On_The_Gpu()
    {
        using var k = Device();
        var on = Render(k, P448, periodic: true);
        var off = Render(k, P448, periodic: false);
        Assert.Equal(0, on.ReferencePeriod);
        Assert.Equal(off.ReferenceOrbitLength, on.ReferenceOrbitLength);
        Assert.True(on.IterationBuffer.AsSpan().SequenceEqual(off.IterationBuffer));
        Assert.True(on.SmoothBuffer.AsSpan().SequenceEqual(off.SmoothBuffer));
        Assert.True(on.ColorBuffer.AsSpan().SequenceEqual(off.ColorBuffer));
    }
}
