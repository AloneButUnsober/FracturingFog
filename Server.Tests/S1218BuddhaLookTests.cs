// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1218 — the default Buddhabrot rendered near-black and Nebulabrot over a red disc.
// Every kept orbit starts at z0 = 0, so the origin pixel took one hit per orbit
// (~437K at the defaults, the next hottest ~1.8K) and the log normalisation divided
// the image by it; the fast escapers washed the |c| <= 2 disc. Now no sampler draws
// z0, and escaping orbits shorter than BuddhaMinIter are not drawn. Checked on
// every sampler — the shared CPU one, the GPU (Vulkan, skipped without a device),
// the classic double-precision one, Metropolis — by invariants that do not depend
// on the random draw:
//   • the origin pixel is no hotter than its neighbourhood;
//   • with BuddhaMinIter at the low band edge the low band is empty, exactly
//     (every drawn orbit escapes at or past it), and with 0 it is not.

using System;
using System.Linq;
using FracturingFog.Models;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1218BuddhaLookTests
{
    private const int W = 160, H = 120;

    public enum Sampler { SharedCpu, Gpu, ClassicDouble, Metropolis }

    private static (BuddhabrotCalculator C, IDisposable? Gpu) Render(Sampler s, int minIter, int low = 20)
    {
        VulkanComputeKernel? k = null;
        if (s == Sampler.Gpu)
        {
            BuddhaFamilyCalculator.UseGpuBuddha = true;   // opt-in, per thread
            k = VulkanComputeKernel.TryCreateWithOwnContext();
            if (k is null) Assert.Skip("no Vulkan device on this host");
        }
        var c = new BuddhabrotCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0, Zoom = 1, MaxIterations = 2000,
            GpuKernel = k, UseGpuCompute = k != null,
            FractalParameters = new FractalParameters
            {
                BuddhaSamples = 200_000, BuddhaIterLow = low, BuddhaIterMid = 200, BuddhaIterHigh = 2000,
                BuddhaMinIter = minIter, BuddhaMetropolis = s == Sampler.Metropolis, BuddhaZoomCompensation = false,
            },
        };
        bool saved = BuddhaFamilyCalculator.UseSharedUniformSampler;
        BuddhaFamilyCalculator.UseSharedUniformSampler = s != Sampler.ClassicDouble;
        try { c.Calculate(); }
        finally { BuddhaFamilyCalculator.UseSharedUniformSampler = saved; }
        Assert.Equal(s == Sampler.Gpu, c.LastCalculateUsedGpu);
        return (c, k);
    }

    [Theory]
    [InlineData(Sampler.SharedCpu)]
    [InlineData(Sampler.Gpu)]
    [InlineData(Sampler.ClassicDouble)]
    [InlineData(Sampler.Metropolis)]
    public void The_Origin_Pixel_Carries_No_One_Hit_Per_Orbit_Spike(Sampler s)
    {
        var (c, gpu) = Render(s, minIter: 12);
        using var _ = gpu;
        // The pixel z = 0 projects to (centre offset 0.5 / scale + W/2, H/2).
        double scale = 3.5 / W;
        int ox = (int)(0.5 / scale + W * 0.5), oy = H / 2;
        uint At(int x, int y) => c.HitsR[y * W + x] + c.HitsG[y * W + x] + c.HitsB[y * W + x];
        var ring = (from dy in Enumerable.Range(-2, 5) from dx in Enumerable.Range(-2, 5)
                    where dx != 0 || dy != 0 select (double)At(ox + dx, oy + dy)).OrderBy(v => v).ToArray();
        double median = ring[ring.Length / 2];
        uint origin = At(ox, oy);
        Assert.True(origin <= 10 * median + 10, $"{s}: origin pixel {origin} hits, neighbourhood median {median}");
    }

    [Theory]
    [InlineData(Sampler.SharedCpu)]
    [InlineData(Sampler.Gpu)]
    [InlineData(Sampler.ClassicDouble)]
    [InlineData(Sampler.Metropolis)]
    public void BuddhaMinIter_Drops_Every_Orbit_That_Escapes_Sooner(Sampler s)
    {
        // Low band = class (escape) iteration < 20; with BuddhaMinIter 20 nothing
        // drawn can land there.
        var (cut, g1) = Render(s, minIter: 20, low: 20);
        using (g1) Assert.All(cut.HitsR, v => Assert.Equal(0u, v));
        Assert.True(cut.HitsG.Sum(v => (long)v) > 0, $"{s}: nothing drawn at all");
        var (all, g2) = Render(s, minIter: 0, low: 20);
        using (g2) Assert.True(all.HitsR.Sum(v => (long)v) > 0, $"{s}: the low band is empty even without the cut");
    }
}
