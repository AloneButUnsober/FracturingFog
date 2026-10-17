// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1340 — the wavefront (persistent-thread) schedule of the shallow GPU
// escape-time kernel. One thread per pixel, a warp runs until its slowest pixel
// finishes; under the wavefront schedule a fixed number of threads pull pixels
// from a global counter and iterate them 32 steps at a time, refilling a lane as
// soon as its pixel finishes. Both schedules are one compiled shader (gWave in the
// cbuffer), so a pixel runs the same instructions either way. These tests drive the
// Vulkan kernel directly (skipped without a device):
//   • every output (iter, smooth, colour, the final z / dz/dc of every pixel) is
//     bit-identical to one thread per pixel, on Mandelbrot and every other kind,
//     with the periodicity check off / on / diagnostic, the colour kernel, per-row
//     caps and the domain warp. The wavefront frame always follows a frame of
//     another view, so a pixel it skipped would keep that view's value (checked:
//     the views differ on most pixels);
//   • every frame size: odd, smaller than a thread group, and many pixels per
//     thread (few threads, one group);
//   • the orbit colour kernel (no wavefront form) runs one thread per pixel;
//   • the time win on views mixing fast and slow pixels, and no loss on the rest.
// The D3D11 kernel is covered by --gpuwavefrontprobe.

using System;
using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection("MandelbrotGpuStatics")]
public sealed class S1340GpuWavefrontTests(ITestOutputHelper output)
{
    public sealed record View(string Name, FractalKind Kind, double Cx, double Cy, double Width,
                              int MaxIter, float P0 = 0f, float P1 = 0f)
    {
        public override string ToString() => Name;
    }

    private static readonly View[] Views =
    {
        new("Whole set", FractalKind.Mandelbrot, -0.6, 0.0, 3.2, 2000),
        new("Seahorse valley", FractalKind.Mandelbrot, -0.7453, 0.1127, 0.01, 3000),
        new("Elephant valley", FractalKind.Mandelbrot, 0.2925, 0.0149, 0.035, 20000),
        new("Period-3 minibrot", FractalKind.Mandelbrot, -1.7548776662466927, 0.0, 0.04, 4000),
        new("Rabbit Julia", FractalKind.Julia, 0.0, 0.0, 3.2, 2000, -0.12256117f, 0.74486177f),
        new("Burning Ship", FractalKind.BurningShip, -1.762, -0.028, 0.05, 2000),
        new("Tricorn", FractalKind.Tricorn, -0.3, 0.0, 4.0, 2000),
        new("Multibrot d3", FractalKind.Multibrot, 0.0, 0.0, 3.0, 2000, 3f),
        new("Multibrot d7", FractalKind.Multibrot, 0.0, 0.0, 3.0, 1000, 7f),
        new("Phoenix", FractalKind.Phoenix, 0.0, 0.0, 3.0, 2000, -0.5f),
    };

    public static TheoryData<View> AllViews()
    {
        var d = new TheoryData<View>();
        foreach (var v in Views) d.Add(v);
        return d;
    }

    private sealed class Out
    {
        public readonly int W, H;
        public int[] It; public float[] Sm, Zr, Zi, Dr, Di; public uint[] Col;
        public bool Wave;
        public double DispatchMs;
        public Out(int w, int h)
        {
            W = w; H = h; int n = w * h;
            It = new int[n]; Sm = new float[n]; Zr = new float[n]; Zi = new float[n]; Dr = new float[n]; Di = new float[n];
            Col = new uint[n];
        }
    }

    private enum Variant { Base, Colour, Orbit }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    private static IGpuHlslPalette Palette(Variant v)
    {
        if (v == Variant.Colour)
            return ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First();
        bool prev = InterpretedOrbitColorMap.GpuEnabled;
        InterpretedOrbitColorMap.GpuEnabled = true;
        try
        {
            var m = InterpretedColorMap.TryCreate("return hsv(saturate(trapMin + stripeAvg * 0.3), 0.9, 1.0);", null, out string? err);
            Assert.Null(err);
            return Assert.IsAssignableFrom<IGpuHlslPalette>(m);
        }
        finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
    }

    private static Out Run(VulkanComputeKernel k, View v, int w, int h, int schedule,
                           int periodicity = GpuPeriodicity.On, Variant variant = Variant.Base,
                           int[]? perRow = null, GpuDomainWarp warp = default, int? maxIter = null)
    {
        var o = new Out(w, h);
        k.SetPalette(variant == Variant.Base ? null : Palette(variant));
        if (variant != Variant.Base) Assert.True(k.HasGpuPalette, $"{variant} palette did not compile");
        GpuWavefront.Mode = schedule;
        GpuPeriodicity.Mode = periodicity;
        try
        {
            k.Run(w, h, v.Cx, v.Cy, v.Width / w, maxIter ?? v.MaxIter, 4.0,
                  o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di, perRow, v.Kind, v.P0, v.P1,
                  colorDst: variant == Variant.Base ? null : o.Col,
                  trapDst: variant == Variant.Orbit ? new float[w * h] : null,
                  warp: warp);
        }
        finally { GpuWavefront.Mode = GpuWavefront.Default; GpuPeriodicity.Reset(); }
        o.Wave = k.LastFrameWavefront;
        o.DispatchMs = k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs;
        return o;
    }

    /// <summary>Another view of the same kind: what a pixel the next frame skips keeps.</summary>
    private static View Dirty(View v) => v with { Name = v.Name + " (dirty)", Cx = v.Cx + v.Width * 0.37, Cy = v.Cy - v.Width * 0.21, Width = v.Width * 0.6 };

    private static int FirstDiff<T>(T[] a, T[] b) where T : IEquatable<T>
    {
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return i;
        return -1;
    }

    // Bit equality for floats (NaN == NaN, -0 != +0).
    private static int FirstBitDiff(float[] a, float[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return i;
        return -1;
    }

    private static void AssertSame(string label, Out plain, Out wave)
    {
        Assert.True(FirstDiff(plain.It, wave.It) < 0, $"{label}: iteration differs at pixel {FirstDiff(plain.It, wave.It)}");
        Assert.True(FirstDiff(plain.Col, wave.Col) < 0, $"{label}: colour differs at pixel {FirstDiff(plain.Col, wave.Col)}");
        foreach (var (a, b, n) in new[] { (plain.Sm, wave.Sm, "smooth"), (plain.Zr, wave.Zr, "zr"), (plain.Zi, wave.Zi, "zi"),
                                          (plain.Dr, wave.Dr, "dr"), (plain.Di, wave.Di, "di") })
            Assert.True(FirstBitDiff(a, b) < 0, $"{label}: {n} differs at pixel {FirstBitDiff(a, b)}");
    }

    /// <summary>One thread per pixel, then a frame of another view, then the
    /// wavefront frame, compared bit for bit. Preconditions: the schedules ran as
    /// asked, and the dirty view differs from the real one on most pixels.</summary>
    private Out Compare(VulkanComputeKernel k, string label, View v, int w, int h,
                        int periodicity = GpuPeriodicity.On, Variant variant = Variant.Base,
                        int[]? perRow = null, GpuDomainWarp warp = default)
    {
        var plain = Run(k, v, w, h, GpuWavefront.Off, periodicity, variant, perRow, warp);
        var dirty = Run(k, Dirty(v), w, h, GpuWavefront.Off, periodicity, variant, perRow, warp);
        var wave = Run(k, v, w, h, GpuWavefront.On, periodicity, variant, perRow, warp);
        Assert.False(plain.Wave, $"{label}: the one-thread-per-pixel frame ran the wavefront schedule");
        Assert.True(wave.Wave || variant == Variant.Orbit, $"{label}: the wavefront frame ran one thread per pixel");
        int n = w * h, differ = Enumerable.Range(0, n).Count(i => dirty.It[i] != plain.It[i]
            || BitConverter.SingleToInt32Bits(dirty.Zr[i]) != BitConverter.SingleToInt32Bits(plain.Zr[i]));
        Assert.True(differ > n / 2, $"{label}: precondition — the dirty view differs on only {differ} of {n} pixels");
        AssertSame(label, plain, wave);
        return wave;
    }

    [Theory]
    [MemberData(nameof(AllViews))]
    public void Wavefront_Is_Bit_Identical_To_One_Thread_Per_Pixel(View v)
    {
        using var k = Device();
        foreach (int per in new[] { GpuPeriodicity.Off, GpuPeriodicity.On, GpuPeriodicity.Diagnostic })
        {
            var wave = Compare(k, $"{v.Name} periodicity {per}", v, 256, 192, per);
            // Precondition: the frame mixes fast and slow pixels (what the schedule reorders).
            Assert.True(wave.It.Distinct().Count() > 50, $"{v.Name}: only {wave.It.Distinct().Count()} distinct iterations");
        }
    }

    [Fact]
    public void Colour_Kernel_Is_Bit_Identical()
    {
        using var k = Device();
        foreach (var v in Views)
        {
            var wave = Compare(k, $"{v.Name} (colour)", v, 256, 192, variant: Variant.Colour);
            Assert.Contains(wave.Col, c => c != wave.Col[0]);   // a real colour frame
        }
    }

    [Fact]
    public void Orbit_Colour_Kernel_Runs_One_Thread_Per_Pixel()
    {
        using var k = Device();
        var v = Views.First(x => x.Name == "Seahorse valley");
        var wave = Compare(k, "orbit", v, 256, 192, variant: Variant.Orbit);
        Assert.False(wave.Wave, "the orbit kernel has no wavefront form");
        // The next plain-kernel frame takes the schedule again.
        Assert.True(Run(k, v, 256, 192, GpuWavefront.On).Wave);
    }

    [Fact]
    public void Per_Row_Caps_And_Domain_Warp_Are_Bit_Identical()
    {
        using var k = Device();
        var v = Views.First(x => x.Name == "Seahorse valley");
        var perRow = Enumerable.Range(0, 192).Select(y => y % 7 == 0 ? 0 : 40 + (y * 37) % v.MaxIter).ToArray();
        var flat = Run(k, v, 256, 192, GpuWavefront.On);
        var capped = Compare(k, "per-row caps", v, 256, 192, perRow: perRow);
        Assert.True(FirstDiff(flat.It, capped.It) >= 0, "precondition — the row caps changed nothing");
        var warp = new GpuDomainWarp(0.3f, 6f, (float)(v.Width * 0.5));
        var warped = Compare(k, "domain warp", v, 256, 192, warp: warp);
        Assert.True(FirstDiff(flat.It, warped.It) >= 0, "precondition — the warp changed nothing");
    }

    [Theory]
    [InlineData(257, 93, 0)]       // odd sizes: partial groups along both axes
    [InlineData(3, 2, 0)]          // fewer pixels than a thread group
    [InlineData(1, 1, 0)]
    [InlineData(640, 360, 4096)]   // ~56 pixels per thread
    [InlineData(640, 360, 64)]     // one group, 3600 pixels per thread
    public void Every_Frame_Size_And_Thread_Count_Is_Bit_Identical(int w, int h, int threads)
    {
        using var k = Device();
        GpuWavefront.Threads = threads;
        try
        {
            foreach (var v in Views.Where(x => x.Name is "Seahorse valley" or "Phoenix" or "Rabbit Julia"))
            {
                // Tiny frames: the dirty view may coincide; compare against plain only.
                if (w * h < 64)
                {
                    var plain = Run(k, v, w, h, GpuWavefront.Off);
                    Run(k, Dirty(v), w, h, GpuWavefront.Off);
                    var wave = Run(k, v, w, h, GpuWavefront.On);
                    Assert.True(wave.Wave);
                    AssertSame($"{v.Name} {w}x{h}", plain, wave);
                }
                else Compare(k, $"{v.Name} {w}x{h} threads {threads}", v, w, h);
            }
        }
        finally { GpuWavefront.Threads = 0; }
    }

    [Fact]
    public void Consecutive_Wavefront_Frames_Restart_The_Counter()
    {
        using var k = Device();
        var a = Views.First(x => x.Name == "Elephant valley");
        var b = Views.First(x => x.Name == "Whole set");
        var plainA = Run(k, a, 320, 240, GpuWavefront.Off);
        var plainB = Run(k, b, 320, 240, GpuWavefront.Off);
        for (int round = 0; round < 3; round++)
        {
            AssertSame($"A round {round}", plainA, Run(k, a, 320, 240, GpuWavefront.On));
            AssertSame($"B round {round}", plainB, Run(k, b, 320, 240, GpuWavefront.On));
        }
    }

    [Fact]
    public void Mixed_Views_Are_Faster_And_Uniform_Ones_Not_Slower()
    {
        using var k = Device();
        const int W = 1024, H = 576;
        double Best(View v, int schedule) => Enumerable.Range(0, 3).Min(_ => Run(k, v, W, H, schedule).DispatchMs);
        foreach (var name in new[] { "Elephant valley", "Seahorse valley", "Period-3 minibrot", "Phoenix", "Whole set", "Rabbit Julia" })
        {
            var v = Views.First(x => x.Name == name);
            Run(k, v, W, H, GpuWavefront.On);
            Run(k, v, W, H, GpuWavefront.Off);
            double tOff = Best(v, GpuWavefront.Off), tOn = Best(v, GpuWavefront.On);
            output.WriteLine($"{name} {W}x{H}: one thread per pixel {tOff:F1} ms, wavefront {tOn:F1} ms ({tOff / tOn:F2}x)");
            if (name == "Elephant valley")
                Assert.True(tOn * 1.25 < tOff, $"{name}: wavefront {tOn:F1} ms vs {tOff:F1} ms");
            else
                Assert.True(tOn < tOff * 1.1, $"{name}: wavefront {tOn:F1} ms vs {tOff:F1} ms");
        }
    }

    [Fact]
    public void Knobs_Are_Per_Thread_And_Reset()
    {
        GpuWavefront.Reset();
        int def = GpuWavefront.Mode;
        Assert.Equal(GpuWavefront.Default, def);
        Assert.Equal(GpuWavefront.DefaultThreads, GpuWavefront.Threads);
        GpuWavefront.Mode = GpuWavefront.Off;
        GpuWavefront.Threads = 99;
        int otherMode = -1, otherThreads = -1;
        var t = new System.Threading.Thread(() => { otherMode = GpuWavefront.Mode; otherThreads = GpuWavefront.Threads; });
        t.Start(); t.Join();
        Assert.Equal(GpuWavefront.Off, GpuWavefront.Mode);
        Assert.Equal(99, GpuWavefront.Threads);
        Assert.Equal(def, otherMode);
        Assert.Equal(GpuWavefront.DefaultThreads, otherThreads);
        GpuWavefront.Reset();
        Assert.Equal(def, GpuWavefront.Mode);
        Assert.Equal(GpuWavefront.DefaultThreads, GpuWavefront.Threads);
        // Groups: at most the pixels' worth of threads, at least one group, at most 65535.
        GpuWavefront.Threads = 1000;
        try
        {
            Assert.Equal(16u, GpuWavefront.Groups(1_000_000, 64));
            Assert.Equal(1u, GpuWavefront.Groups(5, 64));
            Assert.Equal(2u, GpuWavefront.Groups(65, 64));
            GpuWavefront.Threads = int.MaxValue;
            Assert.Equal(65535u, GpuWavefront.Groups(long.MaxValue / 2, 1));
        }
        finally { GpuWavefront.Reset(); }
    }
}
