// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1340 — the wavefront schedule of the deep-zoom perturbation kernels (CSPerturb,
// CSPerturbSA with SA and BLA) and the calculator's Auto choice. The bands of a deep
// frame each pull their pixels from their own counter (gWork[band]). On the Vulkan
// device (skipped without fp64):
//   • the wavefront frame is bit-identical to one thread per pixel — iter, smooth and
//     the final z / dz/dc of every pixel — for the plain and the SA / SA + BLA kernels,
//     with one band and with many 8-row bands (a counter each), at odd frame sizes.
//     Each wavefront frame follows a frame of another view, so a skipped pixel would
//     keep that view's value (checked: the views differ on most pixels);
//   • the calculator, under Auto, runs one thread per pixel on its first frame and the
//     wavefront schedule on the next exactly when the first frame's warp waste reached
//     the threshold (a boundary-heavy preset and a balanced one), with unchanged
//     output, and reports it in the GPU route;
//   • WarpWaste against hand-computed frames.
// The D3D11 kernels are covered by --gpuwavefrontprobe deep.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes PerturbBudgetMs / PerturbBandRowsOverride / UseGpuPerturbation
public sealed class S1340GpuWavefrontDeepTests(ITestOutputHelper output) : IDisposable
{
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly int _savedRows = MandelbrotKernelSource.PerturbBandRowsOverride;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation;

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotKernelSource.PerturbBandRowsOverride = _savedRows;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        GpuWavefront.Reset();
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationSA) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        // Correctness tests: no first-band abort to the CPU under the suite's load.
        MandelbrotKernelSource.PerturbBudgetMs = 0;
        return k;
    }

    private const double EscapeR2 = 512.0 * 512.0;

    private sealed record Deep(string Name, double Cx, double Cy, double Zoom, int MaxIter);

    // Seahorse-valley spiral arms (deep enough for BLA at 1e12) and a filament view.
    private static readonly Deep[] Views =
    {
        new("seahorse 1e6", -0.743643887037151, 0.13182590420533, 1e6, 6000),
        new("seahorse 1e12", -0.743643887037151, 0.13182590420533, 1e12, 6000),
    };

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

    private sealed class Frame
    {
        public readonly int[] It; public readonly float[] Sm, Zr, Zi, Dr, Di;
        public bool Wave;
        public Frame(int n) { It = new int[n]; Sm = new float[n]; Zr = new float[n]; Zi = new float[n]; Dr = new float[n]; Di = new float[n]; }
    }

    private enum Kernel { Plain, Sa, SaBla }

    /// <summary>One deep frame of <paramref name="v"/> (offset by <paramref name="shift"/>
    /// view widths for the dirty frame) under the given schedule.</summary>
    private static Frame Render(VulkanComputeKernel k, Deep v, int w, int h, Kernel kernel, bool wave, double shift = 0)
    {
        var r = Orbit(v.Cx, v.Cy, v.MaxIter);
        double scale = 3.5 / (Math.Max(w, h) * v.Zoom);
        double off0x = -0.5 * w + shift * w, off0y = -0.5 * h - shift * h * 0.6;
        var f = new Frame(w * h);
        GpuWavefront.PerturbMode = wave ? GpuWavefront.On : GpuWavefront.Off;
        try
        {
            if (kernel == Kernel.Plain)
                k.RunPerturb(w, h, scale, v.MaxIter, EscapeR2, off0x, off0y, r.Zr, r.Zi, r.Len, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);
            else
            {
                var sa = new SeriesApproximation(r.Zr, r.Zi, r.Len);
                BlaTable? bla = kernel == Kernel.SaBla
                    ? new BlaTable(r.Zr, r.Zi, r.Len, Math.Sqrt(2.0) * 0.5 * Math.Max(w, h) * scale) : null;
                k.RunPerturbSA(w, h, scale, v.MaxIter, EscapeR2, off0x, off0y, r.Zr, r.Zi, r.Len, 1e-3, sa.SafeMax,
                    sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di,
                    bla?.GpuCoefficients, bla?.Levels ?? 0);
            }
        }
        finally { GpuWavefront.PerturbMode = GpuWavefront.PerturbDefault; }
        f.Wave = k.LastFrameWavefront;
        return f;
    }

    private static int FirstBitDiff(float[] a, float[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return i;
        return -1;
    }

    private static void AssertSame(string label, Frame plain, Frame wave)
    {
        for (int i = 0; i < plain.It.Length; i++)
            Assert.True(plain.It[i] == wave.It[i], $"{label}: iteration differs at pixel {i} ({plain.It[i]} vs {wave.It[i]})");
        foreach (var (a, b, n) in new[] { (plain.Sm, wave.Sm, "smooth"), (plain.Zr, wave.Zr, "zr"), (plain.Zi, wave.Zi, "zi"),
                                          (plain.Dr, wave.Dr, "dr"), (plain.Di, wave.Di, "di") })
            Assert.True(FirstBitDiff(a, b) < 0, $"{label}: {n} differs at pixel {FirstBitDiff(a, b)}");
    }

    [Theory]
    [InlineData(0, 96, 64, 0)]     // plain kernel, one band
    [InlineData(1, 96, 64, 0)]     // SA
    [InlineData(2, 96, 64, 0)]     // SA + BLA
    [InlineData(0, 97, 61, 8)]     // odd sizes, 8 bands of 8 rows (a counter each)
    [InlineData(2, 97, 61, 8)]
    [InlineData(2, 160, 90, 16)]
    public void Wavefront_Is_Bit_Identical_To_One_Thread_Per_Pixel(int kernelIndex, int w, int h, int bandRows)
    {
        var kernel = (Kernel)kernelIndex;
        using var k = Device();
        MandelbrotKernelSource.PerturbBandRowsOverride = bandRows;
        foreach (var v in Views)
        {
            string label = $"{v.Name} {kernel} {w}x{h} bands {bandRows}";
            var plain = Render(k, v, w, h, kernel, wave: false);
            var dirty = Render(k, v, w, h, kernel, wave: false, shift: 0.43);
            var wave = Render(k, v, w, h, kernel, wave: true);
            Assert.False(plain.Wave, $"{label}: the one-thread-per-pixel frame ran the wavefront schedule");
            Assert.True(wave.Wave, $"{label}: the wavefront frame ran one thread per pixel");
            int n = w * h, differ = Enumerable.Range(0, n).Count(i => dirty.It[i] != plain.It[i]
                || BitConverter.SingleToInt32Bits(dirty.Zr[i]) != BitConverter.SingleToInt32Bits(plain.Zr[i]));
            Assert.True(differ > n / 2, $"{label}: precondition — the dirty view differs on only {differ} of {n} pixels");
            Assert.True(plain.It.Distinct().Count() > 30, $"{label}: precondition — a flat frame");
            AssertSame(label, plain, wave);
        }
    }

    // Shipped deep presets (Resources/regions.json): "Marina" is boundary-heavy (warp
    // waste ~1.9 at 960x540), "Spider Star" balanced (~1.0).
    private static MandelbrotCalculator PresetCalc(VulkanComputeKernel k, bool marina) => marina
        ? new MandelbrotCalculator(192, 108)
        {
            CenterX = -0.8166959533897611, CenterY = 0.17829091833221097, Zoom = 37039746442841656, MaxIterations = 20000,
            ColorMap = new HsvPalette(), GpuKernel = k,
        }
        : new MandelbrotCalculator(192, 108)
        {
            CenterX = -1.9998000000003664, CenterXLo = -4.920546579744369e-17, CenterY = -2.56187704972203e-24,
            Zoom = 1.2103202390345426e+21, MaxIterations = 20000, ColorMap = new HsvPalette(), GpuKernel = k,
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Auto_Picks_The_Next_Frame_From_The_Warp_Waste(bool marina)
    {
        using var k = Device();
        MandelbrotCalculator.UseGpuPerturbation = true;
        GpuWavefront.Reset();
        Assert.Equal(GpuWavefront.Auto, GpuWavefront.PerturbMode);

        // Reference: one thread per pixel throughout.
        var refCalc = PresetCalc(k, marina);
        GpuWavefront.PerturbMode = GpuWavefront.Off;
        refCalc.Calculate(CancellationToken.None);
        GpuWavefront.Reset();
        Assert.True(refCalc.LastFrameUsedGpuPerturbation, refCalc.LastGpuRoute.Detail);

        var c = PresetCalc(k, marina);
        c.Calculate(CancellationToken.None);
        Assert.True(c.LastFrameUsedGpuPerturbation, c.LastGpuRoute.Detail);
        Assert.False(c.LastFrameUsedGpuWavefront, "the first frame has no warp waste to go on");
        double waste = c.LastGpuWarpWaste;
        Assert.Equal(GpuWavefront.WarpWaste(c.IterationBuffer, c.Width, c.Height), waste);
        output.WriteLine($"{(marina ? "Marina" : "Spider Star")}: warp waste {waste:F2}, route {c.LastGpuRoute.Detail}");
        // Preconditions: the two presets fall on either side of the threshold.
        if (marina) Assert.True(waste > GpuWavefront.PerturbWasteThreshold + 0.1, $"Marina waste {waste:F2}");
        else Assert.True(waste < GpuWavefront.PerturbWasteThreshold - 0.2, $"Spider Star waste {waste:F2}");

        c.Calculate(CancellationToken.None);
        Assert.Equal(marina, c.LastFrameUsedGpuWavefront);
        Assert.Equal(marina, k.LastFrameWavefront);
        Assert.Equal(marina, c.LastGpuRoute.Detail!.Contains("wavefront"));
        Assert.Equal(refCalc.IterationBuffer, c.IterationBuffer);
        Assert.True(FirstBitDiff(refCalc.SmoothBuffer, c.SmoothBuffer) < 0, "smooth differs from one thread per pixel");
        Assert.False(GpuWavefront.PerturbHint, "the calculator left its hint set");

        // Forced off: never, whatever the waste.
        GpuWavefront.Mode = GpuWavefront.Off;
        c.Calculate(CancellationToken.None);
        Assert.False(c.LastFrameUsedGpuWavefront);
        Assert.False(k.LastFrameWavefront);
    }

    [Fact]
    public void Warp_Waste_Matches_Hand_Computed_Frames()
    {
        // Uniform: every group is balanced.
        Assert.Equal(1.0, GpuWavefront.WarpWaste(Enumerable.Repeat(7, 16 * 16).ToArray(), 16, 16));
        // One slow pixel per 8x8 tile, the rest 0: each tile is busy 64 x its max.
        var hot = new int[16 * 16];
        for (int t = 0; t < 4; t++) hot[(t / 2) * 8 * 16 + (t % 2) * 8] = 100;
        Assert.Equal(64.0, GpuWavefront.WarpWaste(hot, 16, 16));
        // A 10x9 frame (partial tiles): tile (0,0) 8x8 of 1 plus one 3; the partial
        // tiles 2x8, 8x1 and 2x1 all 1 → busy = 64*3 + 16 + 8 + 2 = 218; need = 63 + 3 + 26 = 92.
        var part = Enumerable.Repeat(1, 10 * 9).ToArray();
        part[3 * 10 + 4] = 3;
        Assert.Equal(218.0 / 92.0, GpuWavefront.WarpWaste(part, 10, 9), 12);
        // Degenerate input.
        Assert.Equal(1.0, GpuWavefront.WarpWaste(new int[4], 2, 2));
        Assert.Equal(1.0, GpuWavefront.WarpWaste(new int[3], 2, 2));
    }

    [Fact]
    public void Knobs_Resolve_The_Deep_Schedule()
    {
        GpuWavefront.Reset();
        Assert.Equal(GpuWavefront.PerturbDefault, GpuWavefront.PerturbMode);
        GpuWavefront.PerturbMode = GpuWavefront.Auto;
        Assert.False(GpuWavefront.UsePerturb);
        GpuWavefront.PerturbHint = true;
        Assert.True(GpuWavefront.UsePerturb);
        GpuWavefront.Mode = GpuWavefront.Off;   // the master switch
        Assert.False(GpuWavefront.UsePerturb);
        GpuWavefront.Mode = GpuWavefront.On;
        GpuWavefront.PerturbMode = GpuWavefront.Off;
        Assert.False(GpuWavefront.UsePerturb);
        GpuWavefront.PerturbMode = GpuWavefront.On;
        GpuWavefront.PerturbHint = false;
        Assert.True(GpuWavefront.UsePerturb);
        GpuWavefront.Reset();
        Assert.False(GpuWavefront.PerturbHint);
        // Deep groups: a quarter of the band's pixels as threads, at least one group.
        Assert.Equal(15u, GpuWavefront.PerturbGroups(960 * 4, 64));
        Assert.Equal(1u, GpuWavefront.PerturbGroups(3, 64));
    }
}
