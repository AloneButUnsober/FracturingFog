// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1386 part 1 — seeding the first deep frame's wavefront Auto choice from a
// lower-resolution frame of the same view (GpuWavefront.EstimateFullWaste): the
// host's drag previews (FractalRenderHost.SeedWavefrontFromPreview) and the opt-in
// ¼-resolution seed dispatch (GpuWavefront.Seed). Checked:
//   • the estimator against hand-computed frames (the geometric mean of the 8×8
//     and the footprint-tile waste), and the Seed knob;
//   • on the Vulkan device (skipped without fp64):
//       – the seed dispatch IS the full frame, subsampled: every seed pixel (x', y')
//         equals the one-thread-per-pixel frame's pixel (4x', 4y') bit for bit
//         (an independent check of the dispatch's scale and offsets);
//       – the seeded first frame runs the schedule the estimate picks, and the
//         estimate makes the right call on both sides of the threshold: the
//         boundary-heavy Marina preset goes wavefront, the balanced Spider Star
//         doesn't (each against its own full-frame warp waste);
//       – the output of a seeded frame is unchanged (iter and smooth bit-identical
//         to one thread per pixel);
//       – no seed for the same or a nearby view (a pan under one frame width, a
//         zoom within 2×); a seed again after a jump (two frame widths, 4× zoom,
//         another iteration cap);
//       – seed off (the default) never seeds; a calculator that may not seed (a
//         preview sidecar) doesn't; a frame's own measured waste counts as its
//         view's hint (no seed after it);
//       – a GPU deep preview seeds the full-resolution calculator with its estimate,
//         that calculator's first frame follows it without a seed dispatch, and a
//         CPU preview seeds nothing.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1386WavefrontEstimateTests : IDisposable
{
    public void Dispose() => GpuWavefront.Reset();

    [Fact]
    public void The_Estimate_Is_The_Mean_Of_The_Two_Bounds()
    {
        // An 8x8 frame of 1s with one 9: over 8x8 tiles busy 64·9 = 576, need 72 → 8;
        // over the 2x2 footprint of a ¼ frame busy 4·9 + 15·4 = 96 → 96/72.
        var f = Enumerable.Repeat(1, 64).ToArray();
        f[3 * 8 + 5] = 9;
        Assert.Equal(8.0, GpuWavefront.WarpWaste(f, 8, 8, 8), 12);
        Assert.Equal(96.0 / 72.0, GpuWavefront.WarpWaste(f, 8, 8, 2), 12);
        Assert.Equal(Math.Sqrt(8.0 * 96.0 / 72.0), GpuWavefront.EstimateFullWaste(f, 8, 8, 4.0), 12);
        // ½: 4x4 footprint, 4 tiles, one holds the 9 → busy 16·9 + 48 = 192.
        Assert.Equal(Math.Sqrt(8.0 * 192.0 / 72.0), GpuWavefront.EstimateFullWaste(f, 8, 8, 2.0), 12);
        // A frame of its own resolution is its own waste; a divisor past 8 has 1x1 tiles (waste 1).
        Assert.Equal(GpuWavefront.WarpWaste(f, 8, 8), GpuWavefront.EstimateFullWaste(f, 8, 8, 1.0));
        Assert.Equal(Math.Sqrt(8.0), GpuWavefront.EstimateFullWaste(f, 8, 8, 16.0), 12);
        Assert.Equal(1.0, GpuWavefront.WarpWaste(f, 8, 8, 1), 12);
        // The 8x8 overload is the 8-tile one.
        Assert.Equal(GpuWavefront.WarpWaste(f, 8, 8, 8), GpuWavefront.WarpWaste(f, 8, 8));
    }

    [Fact]
    public void The_Seed_Knob_Defaults_Off_And_Resets()
    {
        GpuWavefront.Reset();
        Assert.Equal(GpuWavefront.SeedDefault, GpuWavefront.Seed);
        Assert.False(GpuWavefront.SeedDefault, "FF_GPU_WAVEFRONT_SEED is set in the test environment");
        GpuWavefront.Seed = true;
        Assert.True(GpuWavefront.Seed);
        GpuWavefront.Reset();
        Assert.False(GpuWavefront.Seed);
    }
}

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes PerturbBudgetMs / UseGpuPerturbation
public sealed class S1386WavefrontSeedTests(ITestOutputHelper output) : IDisposable
{
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation;

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        GpuWavefront.Reset();
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationSA) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // no first-band abort under the suite's load
        MandelbrotCalculator.UseGpuPerturbation = true;
        return k;
    }

    private const int W = 384, H = 216;   // the seed frame: 96x54, exactly 1/4 per axis

    // Shipped deep presets (Resources/regions.json): Marina boundary-heavy, Spider Star balanced.
    private static MandelbrotCalculator Preset(VulkanComputeKernel k, bool marina, int w = W, int h = H) => marina
        ? new MandelbrotCalculator(w, h)
        {
            CenterX = -0.8166959533897611, CenterY = 0.17829091833221097, Zoom = 37039746442841656, MaxIterations = 20000,
            ColorMap = new HsvPalette(), GpuKernel = k,
        }
        : new MandelbrotCalculator(w, h)
        {
            CenterX = -1.9998000000003664, CenterXLo = -4.920546579744369e-17, CenterY = -2.56187704972203e-24,
            Zoom = 1.2103202390345426e+21, MaxIterations = 20000, ColorMap = new HsvPalette(), GpuKernel = k,
        };

    private static MandelbrotCalculator Reference(VulkanComputeKernel k, bool marina)
    {
        var r = Preset(k, marina);
        GpuWavefront.PerturbMode = GpuWavefront.Off;
        try { r.Calculate(CancellationToken.None); }
        finally { GpuWavefront.Reset(); }
        Assert.True(r.LastFrameUsedGpuPerturbation, r.LastGpuRoute.Detail);
        return r;
    }

    private static bool SameBits(float[] a, float[] b) =>
        a.Length == b.Length && !a.Where((v, i) => BitConverter.SingleToInt32Bits(v) != BitConverter.SingleToInt32Bits(b[i])).Any();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Seed_Dispatch_Is_The_Full_Frame_Subsampled_And_Picks_The_First_Frame(bool marina)
    {
        using var k = Device();
        var reference = Reference(k, marina);
        double fullWaste = GpuWavefront.WarpWaste(reference.IterationBuffer, W, H);

        GpuWavefront.Seed = true;
        var c = Preset(k, marina);
        c.Calculate(CancellationToken.None);
        Assert.True(c.LastFrameUsedGpuPerturbation, c.LastGpuRoute.Detail);
        Assert.True(c.LastFrameRanWavefrontSeed, "the first frame ran no seed dispatch");
        var seed = c.LastWavefrontSeedIterations!;
        Assert.Equal((W / 4, H / 4), c.LastWavefrontSeedSize);

        // The seed frame is the full frame at every 4th pixel (4x', 4y').
        int sw = W / 4, sh = H / 4;
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
                Assert.True(seed[y * sw + x] == reference.IterationBuffer[4 * y * W + 4 * x],
                    $"seed ({x}, {y}) = {seed[y * sw + x]}, full ({4 * x}, {4 * y}) = {reference.IterationBuffer[4 * y * W + 4 * x]}");

        double est = GpuWavefront.EstimateFullWaste(seed, sw, sh, 4.0);
        Assert.Equal(est, c.LastGpuWavefrontSeed);
        output.WriteLine($"{(marina ? "Marina" : "Spider Star")}: full waste {fullWaste:F2}, seed estimate {est:F2}, " +
                         $"raw 1/4 8x8 waste {GpuWavefront.WarpWaste(seed, sw, sh):F2}");
        // The right call on both sides: the frame's own waste decides as the estimate did.
        Assert.Equal(fullWaste >= GpuWavefront.PerturbWasteThreshold, est >= GpuWavefront.PerturbWasteThreshold);
        if (marina) Assert.True(fullWaste > GpuWavefront.PerturbWasteThreshold + 0.2, $"precondition: Marina waste {fullWaste:F2}");
        else Assert.True(fullWaste < GpuWavefront.PerturbWasteThreshold - 0.2, $"precondition: Spider Star waste {fullWaste:F2}");
        Assert.Equal(est >= GpuWavefront.PerturbWasteThreshold, c.LastFrameUsedGpuWavefront);
        Assert.Equal(c.LastFrameUsedGpuWavefront, k.LastFrameWavefront);

        // The output is the one-thread-per-pixel frame's.
        Assert.Equal(reference.IterationBuffer, c.IterationBuffer);
        Assert.True(SameBits(reference.SmoothBuffer, c.SmoothBuffer), "smooth differs from one thread per pixel");
        Assert.False(GpuWavefront.PerturbHint, "the calculator left its hint set");
    }

    [Fact]
    public void No_Seed_For_A_Nearby_View_And_A_Seed_After_A_Jump()
    {
        using var k = Device();
        GpuWavefront.Seed = true;
        var c = Preset(k, marina: false, 192, 108);
        double frame = 3.5 / c.Zoom, x0 = c.CenterXLo, zoom = c.Zoom;

        bool Seeds(Action change)
        {
            change();
            c.Calculate(CancellationToken.None);
            Assert.True(c.LastFrameUsedGpuPerturbation, c.LastGpuRoute.Detail);
            return c.LastFrameRanWavefrontSeed;
        }

        Assert.True(Seeds(() => { }), "the first frame");
        Assert.False(Seeds(() => { }), "the same view again");
        Assert.False(Seeds(() => c.CenterXLo = x0 + 0.5 * frame), "a pan of half a frame");
        Assert.False(Seeds(() => c.Zoom = zoom * 1.8), "a zoom of 1.8x");
        Assert.True(Seeds(() => { c.Zoom = zoom; c.CenterXLo = x0 + 3 * frame; }), "a jump of three frame widths");
        Assert.True(Seeds(() => c.Zoom = zoom * 4), "a zoom of 4x");
        Assert.True(Seeds(() => c.MaxIterations = 10000), "another iteration cap");
        Assert.False(Seeds(() => { }), "the same view again");
    }

    [Fact]
    public void Seed_Off_Or_Not_Allowed_Never_Seeds()
    {
        using var k = Device();
        var c = Preset(k, marina: true, 192, 108);
        c.Calculate(CancellationToken.None);   // the default: seed off
        Assert.True(c.LastFrameUsedGpuPerturbation, c.LastGpuRoute.Detail);
        Assert.False(c.LastFrameRanWavefrontSeed);
        Assert.Null(c.LastWavefrontSeedIterations);
        Assert.False(c.LastFrameUsedGpuWavefront, "an unseeded first frame runs one thread per pixel");

        GpuWavefront.Seed = true;
        var side = Preset(k, marina: true, 192, 108);
        side.WavefrontSeedAllowed = false;
        side.Calculate(CancellationToken.None);
        Assert.False(side.LastFrameRanWavefrontSeed, "a calculator that may not seed ran the seed dispatch");

        GpuWavefront.Mode = GpuWavefront.Off;   // the master switch: no wavefront, no seed
        var off = Preset(k, marina: true, 192, 108);
        off.Calculate(CancellationToken.None);
        Assert.False(off.LastFrameRanWavefrontSeed);
        GpuWavefront.Reset();

        // A frame's own measured waste is a hint for its view: seeding switched on
        // afterwards has nothing to add there.
        GpuWavefront.Seed = true;
        c.Calculate(CancellationToken.None);
        Assert.False(c.LastFrameRanWavefrontSeed, "the frame's own waste was not kept as this view's hint");
        Assert.True(c.LastFrameUsedGpuWavefront, "Marina's measured waste picks the wavefront schedule");
    }

    [Fact]
    public void A_Gpu_Preview_Seeds_The_Full_Resolution_Calculator()
    {
        using var k = Device();
        GpuWavefront.Seed = true;   // the preview's hint must make a seed dispatch unnecessary
        var full = Preset(k, marina: true);
        var preview = Preset(k, marina: true, W / 4, H / 4);
        preview.WavefrontSeedAllowed = false;
        preview.Calculate(CancellationToken.None);
        Assert.True(preview.LastFrameUsedGpuPerturbation, preview.LastGpuRoute.Detail);

        FractalRenderHost.SeedWavefrontFromPreview(full, preview);
        double est = GpuWavefront.EstimateFullWaste(preview.IterationBuffer, W / 4, H / 4, 4.0);
        Assert.Equal(est, full.LastGpuWavefrontSeed);
        output.WriteLine($"Marina preview estimate {est:F2}");
        Assert.True(est >= GpuWavefront.PerturbWasteThreshold, $"precondition: the Marina preview estimates {est:F2}");

        full.Calculate(CancellationToken.None);
        Assert.False(full.LastFrameRanWavefrontSeed, "the preview's hint is for this view: no seed dispatch");
        Assert.True(full.LastFrameUsedGpuWavefront, "the seeded first frame ran one thread per pixel");

        // A CPU preview (no GPU deep frame) seeds nothing.
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpuPreview = Preset(k, marina: false, 48, 27);
        cpuPreview.GpuKernel = null;
        cpuPreview.Calculate(CancellationToken.None);
        var other = Preset(k, marina: false);
        FractalRenderHost.SeedWavefrontFromPreview(other, cpuPreview);
        Assert.Equal(0.0, other.LastGpuWavefrontSeed);
    }
}
