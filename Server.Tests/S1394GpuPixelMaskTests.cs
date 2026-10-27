// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1394 part 1 — the GPU pixel mask on the Vulkan kernels (skipped without a device;
// the deep cases without shaderFloat64 run the fp32 kernel only). The D3D11 kernels are
// covered by --gpupixelmaskprobe.
//   • A masked dispatch into arrays holding a sentinel gives every marked pixel the
//     full dispatch's iter / smooth / final z / dz (and colour) bit for bit, and leaves
//     every unmarked one at the sentinel: the shallow kernel (base and colour;
//     Mandelbrot, Julia, Multibrot) and the deep ones (double, SA, float-exp, scaled
//     fp32), in both schedules, for a scattered mask and the solid-region grid lines.
//   • A mask of another size is ignored; RunDeferred under a mask defers nothing.
//   • The colour kernel writes the CPU writeback's final z / dz for an in-set pixel
//     (zeros; the cardioid / bulb skip's (0, 0, 1, 0)), not the orbit's last z, which
//     differs from pixel to pixel and kept every border of a GPU-coloured frame mixed.
//   • The calculators on the Vulkan kernel: a forced solid-region frame equals the full
//     GPU frame wherever the skip didn't fill and on every filled in-set pixel, fills,
//     and leaves the kernel's mask cleared — Mandelbrot shallow (GPU and CPU colouring),
//     Julia, and deep.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes PerturbBudgetMs / UseGpuPerturbation
public sealed class S1394GpuPixelMaskTests(ITestOutputHelper output) : IDisposable
{
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation;

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        GpuWavefront.Reset();
        GpuPeriodicity.Reset();
        VulkanComputeKernel.ForceFloatExpEntry = false;
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // no first-band abort under the suite's load
        return k;
    }

    private const uint Sentinel = 0xDEADBEEF;
    private static readonly float SentinelF = BitConverter.Int32BitsToSingle(unchecked((int)Sentinel));

    private sealed class Out
    {
        public readonly int[] It; public readonly float[] Sm, Zr, Zi, Dr, Di; public readonly uint[] Col;
        public Out(int n, bool sentinel)
        {
            It = new int[n]; Sm = new float[n]; Zr = new float[n]; Zi = new float[n]; Dr = new float[n]; Di = new float[n]; Col = new uint[n];
            if (!sentinel) return;
            Array.Fill(It, unchecked((int)Sentinel)); Array.Fill(Col, Sentinel);
            foreach (var a in new[] { Sm, Zr, Zi, Dr, Di }) Array.Fill(a, SentinelF);
        }
    }

    private static int B(float f) => BitConverter.SingleToInt32Bits(f);

    private static void AssertMasked(string label, Out full, Out masked, byte[] mark, bool colour)
    {
        for (int i = 0; i < mark.Length; i++)
        {
            if (mark[i] != 0)
            {
                Assert.True(full.It[i] == masked.It[i] && B(full.Sm[i]) == B(masked.Sm[i]) && B(full.Zr[i]) == B(masked.Zr[i])
                            && B(full.Zi[i]) == B(masked.Zi[i]) && B(full.Dr[i]) == B(masked.Dr[i]) && B(full.Di[i]) == B(masked.Di[i])
                            && (!colour || full.Col[i] == masked.Col[i]),
                    $"{label}: marked pixel {i}: iter {masked.It[i]} vs {full.It[i]}, colour {masked.Col[i]:X8} vs {full.Col[i]:X8}");
            }
            else
            {
                Assert.True(masked.It[i] == unchecked((int)Sentinel) && (uint)B(masked.Sm[i]) == Sentinel
                            && (uint)B(masked.Zr[i]) == Sentinel && (uint)B(masked.Zi[i]) == Sentinel
                            && (uint)B(masked.Dr[i]) == Sentinel && (uint)B(masked.Di[i]) == Sentinel
                            && masked.Col[i] == Sentinel,
                    $"{label}: unmarked pixel {i} was written (iter {masked.It[i]}, colour {masked.Col[i]:X8})");
            }
        }
    }

    private static byte[] Scatter(int n)
    {
        var m = new byte[n];
        var rng = new Random(1394);
        for (int i = 0; i < n; i++) m[i] = (byte)(rng.NextDouble() < 0.3 ? 1 : 0);
        return m;
    }

    private static byte[] Grid(int w, int h)
    {
        var m = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (y % MandelbrotCalculator.SolidTileSize == 0 || x % MandelbrotCalculator.SolidTileSize == 0 || y == h - 1 || x == w - 1)
                    m[y * w + x] = 1;
        return m;
    }

    [Theory]
    [InlineData(FractalKind.Mandelbrot, -1.7548776662466927, 0.0, 0.05, 2000, 0f, 0f)]
    [InlineData(FractalKind.Julia, 0.0, 0.0, 3.2, 1000, -0.123f, 0.745f)]
    [InlineData(FractalKind.Multibrot, 0.0, 0.0, 3.2, 1000, 4f, 0f)]
    public void A_Masked_Shallow_Dispatch_Writes_Exactly_The_Marked_Pixels(FractalKind kind, double cx, double cy, double span, int iter, float p0, float p1)
    {
        using var k = Device();
        const int w = 203, h = 131;   // odd: partial groups and a partial last mask word
        int n = w * h;
        var palette = ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First();
        foreach (bool colour in new[] { false, true })
            foreach (int wave in new[] { GpuWavefront.Off, GpuWavefront.On })
            {
                Out Frame(byte[]? mark)
                {
                    var o = new Out(n, mark != null);
                    k.SetPalette(colour ? palette : null);
                    k.PixelMask = mark == null ? null : GpuPixelMask.FromBytes(mark, w, h);
                    GpuWavefront.Mode = wave;
                    try
                    {
                        k.Run(w, h, cx, cy, span / w, iter, 4.0, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di,
                              null, kind, p0, p1, colorDst: colour ? o.Col : null);
                    }
                    finally { k.PixelMask = null; GpuWavefront.Reset(); }
                    if (!colour) Array.Fill(o.Col, mark == null ? 0u : Sentinel);
                    Assert.Equal(wave == GpuWavefront.On, k.LastFrameWavefront);
                    return o;
                }
                var full = Frame(null);
                foreach (var (name, mark) in new[] { ("scatter", Scatter(n)), ("grid", Grid(w, h)) })
                    AssertMasked($"{kind} {(colour ? "colour" : "base")} wave {wave} {name}", full, Frame(mark), mark, colour);
            }
    }

    private const double EscapeR2 = 512.0 * 512.0;

    // A reference orbit at the period-3 nucleus (it never escapes), the frame around it.
    private static (double[] Zr, double[] Zi) NucleusOrbit(int maxIter)
    {
        const double c = -1.7548776662466927;
        var zr = new double[maxIter]; var zi = new double[maxIter];
        double x = 0, y = 0;
        for (int i = 0; i < maxIter; i++) { zr[i] = x; zi[i] = y; (x, y) = (x * x - y * y + c, 2 * x * y); }
        return (zr, zi);
    }

    public enum DeepKernel { Double, Sa, FloatExp, ScaledF32 }

    [Theory]
    [InlineData(DeepKernel.Double)]
    [InlineData(DeepKernel.Sa)]
    [InlineData(DeepKernel.FloatExp)]
    [InlineData(DeepKernel.ScaledF32)]
    public void A_Masked_Deep_Dispatch_Writes_Exactly_The_Marked_Pixels(DeepKernel kernel)
    {
        using var k = Device();
        if (kernel is DeepKernel.Double or DeepKernel.Sa && !k.SupportsPerturbationSA) Assert.Skip("no shaderFloat64");
        const int w = 161, h = 97, maxIt = 600;
        int n = w * h;
        var (rz, ri) = NucleusOrbit(maxIt);
        // Wide enough that the frame escapes around the minibrot (both kinds of pixel).
        double scale = 3.5 / w / 40;
        var zero = new double[maxIt + 1];
        foreach (int wave in new[] { GpuWavefront.Off, GpuWavefront.On })
        {
            Out Frame(byte[]? mark)
            {
                var o = new Out(n, mark != null);
                Array.Fill(o.Col, mark == null ? 0u : Sentinel);
                k.PixelMask = mark == null ? null : GpuPixelMask.FromBytes(mark, w, h);
                GpuWavefront.Mode = GpuWavefront.On;
                GpuWavefront.PerturbMode = wave;
                VulkanComputeKernel.ForceFloatExpEntry = kernel == DeepKernel.FloatExp;
                try
                {
                    switch (kernel)
                    {
                        case DeepKernel.Double:
                            k.RunPerturb(w, h, scale, maxIt, EscapeR2, -w / 2.0 + 0.25, -h / 2.0 + 0.25, rz, ri, maxIt,
                                o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di);
                            break;
                        case DeepKernel.Sa:
                            k.RunPerturbSA(w, h, scale, maxIt, EscapeR2, -w / 2.0 + 0.25, -h / 2.0 + 0.25, rz, ri, maxIt,
                                1e-6, 0, zero, zero, zero, zero, zero, zero, zero, zero, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di);
                            break;
                        default:
                            k.RunPerturbFloatExp(w, h, scale, maxIt, EscapeR2, -w / 2.0 + 0.25, -h / 2.0 + 0.25, rz, ri, maxIt,
                                o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di);
                            Assert.Equal(kernel == DeepKernel.ScaledF32, k.LastPerturbFloatExpWasScaledF32);
                            break;
                    }
                }
                finally { k.PixelMask = null; GpuWavefront.Reset(); VulkanComputeKernel.ForceFloatExpEntry = false; }
                return o;
            }
            var full = Frame(null);
            Assert.Contains(full.It, it => it >= maxIt);   // precondition: in-set pixels
            Assert.Contains(full.It, it => it < maxIt);    // and escaping ones
            foreach (var (name, mark) in new[] { ("scatter", Scatter(n)), ("grid", Grid(w, h)) })
                AssertMasked($"{kernel} wave {wave} {name}", full, Frame(mark), mark, colour: false);
        }
    }

    [Fact]
    public void A_Masked_Deep_Dispatch_Skips_The_Work_Of_The_Unmarked_Pixels()
    {
        // The outputs can't show it (the read-back copies only marked pixels): the time
        // does. All in set at 2000 iterations, every pixel costs the cap; the grid lines
        // are ~4 % of the frame (GT 710: 9-37 % of the full frame's time, by schedule).
        using var k = Device();
        if (!k.SupportsPerturbationSA) Assert.Skip("no shaderFloat64");
        const int w = 320, h = 192, maxIt = 2000;
        var (rz, ri) = NucleusOrbit(maxIt);
        double scale = 3.5 / w / 3e4;
        var mask = GpuPixelMask.FromBytes(Grid(w, h), w, h);
        double Time(GpuPixelMask? m)
        {
            var o = new Out(w * h, false);
            double best = double.MaxValue;
            for (int r = 0; r < 3; r++)
            {
                k.PixelMask = m;
                try
                {
                    k.RunPerturb(w, h, scale, maxIt, EscapeR2, -w / 2.0, -h / 2.0, rz, ri, maxIt, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di);
                }
                finally { k.PixelMask = null; }
                best = Math.Min(best, k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs);
            }
            if (m == null) Assert.All(o.It, it => Assert.Equal(maxIt, it));   // precondition: all in set
            return best;
        }
        foreach (int wave in new[] { GpuWavefront.Off, GpuWavefront.On })
        {
            GpuWavefront.Mode = GpuWavefront.On;
            GpuWavefront.PerturbMode = wave;
            try
            {
                double full = Time(null), masked = Time(mask);
                output.WriteLine($"wave {wave}: full {full:F2} ms, grid lines ({mask.Count} px) {masked:F2} ms");
                Assert.True(masked < 0.6 * full, $"wave {wave}: masked {masked:F2} ms vs full {full:F2} ms");
            }
            finally { GpuWavefront.Reset(); }
        }
    }

    [Fact]
    public void A_Mask_Of_Another_Size_Is_Ignored_And_A_Masked_Deferred_Dispatch_Defers_Nothing()
    {
        using var k = Device();
        const int w = 160, h = 100;
        int n = w * h;
        var palette = ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First();
        k.SetPalette(palette);
        Out Run(GpuPixelMask? mask, bool deferred, out GpuAuxOutputs pending)
        {
            var o = new Out(n, true);
            k.PixelMask = mask;
            pending = GpuAuxOutputs.None;
            try
            {
                if (deferred)
                    pending = k.RunDeferred(w, h, -1.7548776662466927, 0, 0.05 / w, 1000, 4.0, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di,
                        null, FractalKind.Mandelbrot, 0, 0, colorDst: o.Col);
                else
                    k.Run(w, h, -1.7548776662466927, 0, 0.05 / w, 1000, 4.0, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di,
                        null, FractalKind.Mandelbrot, 0, 0, colorDst: o.Col);
            }
            finally { k.PixelMask = null; }
            return o;
        }
        var full = Run(null, false, out _);
        var other = Run(GpuPixelMask.FromBytes(Grid(w + 1, h), w + 1, h), false, out _);
        var all = new byte[n]; Array.Fill(all, (byte)1);
        AssertMasked("a mask of another size", full, other, all, colour: true);

        var mark = Grid(w, h);
        var masked = Run(GpuPixelMask.FromBytes(mark, w, h), deferred: true, out var pending);
        Assert.Equal(GpuAuxOutputs.None, pending);
        AssertMasked("a masked RunDeferred", full, masked, mark, colour: true);
    }

    [Theory]
    [InlineData(GpuPeriodicity.Off)]
    [InlineData(GpuPeriodicity.On)]
    public void The_Colour_Kernel_Writes_The_Cpu_Final_Z_Of_An_In_Set_Pixel(int periodicity)
    {
        using var k = Device();
        const int w = 160, h = 100;
        int n = w * h;
        k.SetPalette(ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First());
        GpuPeriodicity.Mode = periodicity;
        var o = new Out(n, false);
        // The whole set: the cardioid / bulb skip, other in-set pixels, escaping ones.
        k.Run(w, h, -0.75, 0, 3.5 / w, 1000, 4.0, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di, null, FractalKind.Mandelbrot, 0, 0, colorDst: o.Col);
        double scale = 3.5 / w;
        int skipped = 0, iterated = 0;
        for (int i = 0; i < n; i++)
        {
            if (o.It[i] < 1000) continue;
            float cx = (float)(-0.75 + (i % w - 0.5 * w) * scale), cy = (float)((i / w - 0.5 * h) * scale);
            float xm = cx - 0.25f, q = xm * xm + cy * cy;
            bool skip = q * (q + xm) <= 0.25f * cy * cy || (cx + 1) * (cx + 1) + cy * cy <= 0.0625f;
            var z = (o.Zr[i], o.Zi[i], o.Dr[i], o.Di[i]);
            if (skip) { skipped++; Assert.Equal((0f, 0f, 1f, 0f), z); }
            else if (z == (0f, 0f, 1f, 0f)) skipped++;   // on the fp32 predicate's edge: the kernel skipped it
            else { iterated++; Assert.Equal((0f, 0f, 0f, 0f), z); }
        }
        output.WriteLine($"in set: {skipped} by the cardioid / bulb skip, {iterated} iterated to the cap");
        Assert.True(iterated > 50, "precondition: in-set pixels outside the cardioid and bulb");
    }

    private const int CW = 192, CH = 136;   // the calculator frames (the skip needs 2 tiles each way)

    private static MandelbrotCalculator Mandel(IGpuKernel k, IColorMap map, double x, double y, double zoom, int iter, bool skip) => new(CW, CH)
    {
        CenterX = x, CenterY = y, Zoom = zoom, MaxIterations = iter, ColorMap = map,
        UseGpuCompute = true, GpuKernel = k, SolidRegionSkip = skip, SolidRegionAuto = false, CaptureFilledMask = true,
    };

    private static (string, Array)[] Buffers(MandelbrotCalculator c) => new (string, Array)[]
    {
        ("Color", c.ColorBuffer), ("Iteration", c.IterationBuffer), ("Smooth", c.SmoothBuffer), ("Distance", c.DistanceBuffer),
        ("NormalX", c.NormalXBuffer), ("NormalY", c.NormalYBuffer), ("FinalZr", c.FinalZrBuffer), ("FinalZi", c.FinalZiBuffer),
        ("FinalDr", c.FinalDrBuffer), ("FinalDi", c.FinalDiBuffer),
    };

    private static (string, Array)[] Buffers(EscapeTimeCalculator c) => new (string, Array)[]
    {
        ("Color", c.ColorBuffer), ("Iteration", c.IterationBuffer), ("Smooth", c.SmoothBuffer), ("Distance", c.DistanceBuffer),
        ("NormalX", c.NormalXBuffer), ("NormalY", c.NormalYBuffer), ("FinalZr", c.FinalZrBuffer), ("FinalZi", c.FinalZiBuffer),
        ("FinalDr", c.FinalDrBuffer), ("FinalDi", c.FinalDiBuffer),
    };

    // The S1343 invariants: exact where not filled, exact on filled in-set pixels. A
    // GPU-coloured frame writes no normals (they hold whatever the uninitialised buffers
    // held), so they are left out there.
    private static (long Filled, long Wrong) Compare((string, Array)[] off, (string, Array)[] on, int[] offIter, byte[] fm, int maxIt,
        bool gpuColour = false)
    {
        if (gpuColour)
        {
            off = off.Where(b => !b.Item1.StartsWith("Normal", StringComparison.Ordinal)).ToArray();
            on = on.Where(b => !b.Item1.StartsWith("Normal", StringComparison.Ordinal)).ToArray();
        }
        long filled = 0, wrong = 0;
        for (int i = 0; i < fm.Length; i++)
        {
            bool check = fm[i] == 0 || offIter[i] >= maxIt;
            if (fm[i] != 0) { filled++; if (offIter[i] < maxIt) wrong++; }
            if (!check) continue;
            foreach (var ((name, a), (_, b)) in off.Zip(on))
                Assert.True(a.Length <= i || Equals(a.GetValue(i), b.GetValue(i)),
                    $"pixel {i} ({(fm[i] == 0 ? "computed" : "filled, in set")}): {name} {b.GetValue(i)} vs {a.GetValue(i)}");
        }
        return (filled, wrong);
    }

    [Theory]
    [InlineData("GPU colour, minibrot interior", true, -1.7548776662466927, 0.0, 3e3, 2000)]
    [InlineData("GPU colour, minibrot", true, -1.7548776662466927, 0.0, 30.0, 1000)]
    [InlineData("CPU colour, minibrot", false, -1.7548776662466927, 0.0, 30.0, 1000)]
    public void A_Shallow_Mandelbrot_Frame_On_The_Gpu_Equals_The_Full_Gpu_Frame(string name, bool gpuColour, double x, double y, double zoom, int iter)
    {
        using var k = Device();
        IColorMap map = gpuColour
            ? (IColorMap)ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First()
            : ColorPalette.BuiltIns.First(m => m is not IGpuHlslPalette && m is not IOrbitAwareColorMap && m is not IInteriorAwareColorMap);
        Assert.Equal(gpuColour, map is IGpuHlslPalette);   // precondition: the colour path under test
        var off = Mandel(k, map, x, y, zoom, iter, false);
        off.Calculate(CancellationToken.None);
        var on = Mandel(k, map, x, y, zoom, iter, true);
        on.Calculate(CancellationToken.None);
        Assert.True(off.LastFrameUsedGpuCompute && on.LastFrameUsedGpuCompute, "precondition: both frames on the GPU");
        var (filled, wrong) = Compare(Buffers(off), Buffers(on), off.IterationBuffer, on.LastFilledMask!, iter, gpuColour);
        output.WriteLine($"{name}: {on.LastFrameSolidPasses} passes, filled {filled}, wrong {wrong}");
        if (gpuColour)   // the skip's own normals are defined: zero, filled pixels too
            Assert.True(on.NormalXBuffer.All(v => v == 0) && on.NormalYBuffer.All(v => v == 0), "normals not zeroed");
        Assert.Equal(filled, on.LastFrameFilledPixels);
        Assert.True(filled > 0, "the GPU took the mask: the frame filled");
        Assert.True(wrong <= CW * CH / 2000);
        Assert.Null(k.PixelMask);
    }

    [Fact]
    public void A_Julia_Frame_On_The_Gpu_Equals_The_Full_Gpu_Frame()
    {
        using var k = Device();
        EscapeTimeCalculator Make(bool skip) => new(CW, CH)
        {
            CenterX = -0.0884, CenterY = 0.4, Zoom = 20, MaxIterations = 2000,
            ColorMap = (IColorMap)ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First(),
            UseGpuCompute = true, GpuKernel = k, FractalType = FractalType.Julia, CaptureFilledMask = true,
            FractalParameters = new FractalParameters { JuliaC = new(-0.123, 0.745), SolidRegionSkip = skip, SolidRegionAuto = false },
        };
        var off = Make(false); off.Calculate(CancellationToken.None);
        var on = Make(true); on.Calculate(CancellationToken.None);
        Assert.Equal(FracturingFog.Render.GpuRouteState.Gpu, on.LastGpuRoute.State);
        var (filled, wrong) = Compare(Buffers(off), Buffers(on), off.IterationBuffer, on.LastFilledMask!, 2000, gpuColour: true);
        output.WriteLine($"Julia: {on.LastFrameSolidPasses} passes, filled {filled}, wrong {wrong}");
        Assert.Equal(filled, on.LastFrameFilledPixels);
        Assert.True(filled > 0, "the GPU took the mask: the frame filled");
        Assert.Null(k.PixelMask);
    }

    // Counts the escaped pixels the calculator colours (every Map overload ends here).
    private sealed class CountingMap : IColorMap
    {
        private readonly HsvPalette _inner = new();
        public long Calls;
        public ColorPaletteType Type => _inner.Type;
        public int MaxIterations { get => _inner.MaxIterations; set => _inner.MaxIterations = value; }
        public int Map(float smooth, float distance, int iterations)
        {
            Interlocked.Increment(ref Calls);
            return _inner.Map(smooth, distance, iterations);
        }
    }

    [Fact]
    public void A_Deep_Frame_On_The_Gpu_Equals_The_Full_Gpu_Frame()
    {
        using var k = Device();
        if (!k.SupportsPerturbation && !k.SupportsPerturbationFloatExp) Assert.Skip("no deep kernel");
        MandelbrotCalculator.UseGpuPerturbation = true;
        // The shipped Deep Mini preset framed closer: half its pixels in the minibrot.
        var counting = new CountingMap();
        MandelbrotCalculator Make(bool skip) => new(CW, CH)
        {
            CenterX = -1.9998000000003664, CenterXLo = -4.9205478607770514e-17, CenterY = 4.404058120451339e-27,
            Zoom = 8.8e24, MaxIterations = 20000, ColorMap = skip ? counting : new HsvPalette(),
            UseGpuCompute = true, GpuKernel = k, SolidRegionSkip = skip, SolidRegionAuto = false, CaptureFilledMask = true,
        };
        var off = Make(false); off.Calculate(CancellationToken.None);
        var on = Make(true); on.Calculate(CancellationToken.None);
        Assert.True(off.LastFrameUsedGpuPerturbation && on.LastFrameUsedGpuPerturbation, $"precondition: deep on the GPU ({on.LastGpuRoute.Detail})");
        var (filled, wrong) = Compare(Buffers(off), Buffers(on), off.IterationBuffer, on.LastFilledMask!, 20000);
        output.WriteLine($"deep: {on.LastFrameSolidPasses} passes, filled {filled}, wrong {wrong}, coloured {counting.Calls}");
        // Each pass coloured only the pixels it computed: every computed escaping pixel once.
        long escapedComputed = 0;
        for (int i = 0; i < CW * CH; i++)
            if (on.LastFilledMask![i] == 0 && on.IterationBuffer[i] < 20000) escapedComputed++;
        Assert.Equal(escapedComputed, counting.Calls);
        Assert.Equal(filled, on.LastFrameFilledPixels);
        Assert.True(filled > 0, "the GPU took the mask: the frame filled");
        Assert.Null(k.PixelMask);
    }
}
