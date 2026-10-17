// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1342 — periodicity early-out in the shallow GPU escape-time kernels. The CPU
// loops stop an in-set pixel once z repeats a snapshot (Brent schedule, 16 … 1024);
// the GPU SP kernel only skipped the cardioid and the period-2 bulb, so every other
// in-set pixel ran to the cap. The kernel now has the check, bit-exact on the whole
// iteration state, every 32 steps (GpuPeriodicity; a compiled variant,
// FF_PERIODICITY). These tests drive the Vulkan kernel directly (skipped without a
// device):
//   • on vs off, every output that is read is bit-identical — iter, smooth,
//     colour, trap on every pixel, and the final z / dz/dc of every escaping
//     pixel — on Mandelbrot views past the bulbs and on each other kind (Julia,
//     Burning Ship, Tricorn, Multibrot, Phoenix), for the base, colour and orbit
//     kernels and with per-row caps. Precondition (diagnostic mode): the check
//     really fired on a large share of each view's in-set pixels, and only on
//     pixels the frame marks in set;
//   • false positives against an independent oracle: no pixel the check marks
//     in set escapes in a 200 000-step CPU double iteration of its c;
//   • the time win on interior-heavy views at caps 10k / 100k.
// The D3D11 kernel is covered by --gpuperiodicityprobe.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection("MandelbrotGpuStatics")]
public sealed class S1342GpuPeriodicityTests(ITestOutputHelper output)
{
    private const int W = 256, H = 192;

    public sealed record View(string Name, FractalKind Kind, double Cx, double Cy, double Width,
                              int MaxIter, float P0 = 0f, float P1 = 0f)
    {
        public double Scale => Width / W;
        public override string ToString() => Name;
    }

    private static readonly View[] Views =
    {
        // Mandelbrot past the cardioid / period-2 bulb the closed form skips.
        new("Whole set", FractalKind.Mandelbrot, -0.6, 0.0, 3.2, 2000),
        new("Period-3 bulb", FractalKind.Mandelbrot, -0.1225611668766536, 0.7448617666197442, 0.2, 2000),
        new("Period-3 minibrot", FractalKind.Mandelbrot, -1.7548776662466927, 0.0, 0.04, 4000),
        new("Seahorse valley", FractalKind.Mandelbrot, -0.7453, 0.1127, 0.01, 3000),
        new("Rabbit Julia", FractalKind.Julia, 0.0, 0.0, 3.2, 2000, -0.12256117f, 0.74486177f),
        new("Burning Ship", FractalKind.BurningShip, -0.45, -0.5, 3.0, 2000),
        new("Tricorn", FractalKind.Tricorn, -0.3, 0.0, 4.0, 2000),
        new("Multibrot d3", FractalKind.Multibrot, 0.0, 0.0, 3.0, 2000, 3f),
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
        public int[] It = new int[W * H];
        public float[] Sm = new float[W * H], Zr = new float[W * H], Zi = new float[W * H],
                       Dr = new float[W * H], Di = new float[W * H], Trap = new float[W * H];
        public uint[] Col = new uint[W * H];
        public double DispatchMs;
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

    private static Out Run(VulkanComputeKernel k, View v, int mode, Variant variant = Variant.Base,
                           int[]? perRow = null, int? maxIter = null)
    {
        var o = new Out();
        k.SetPalette(variant == Variant.Base ? null : Palette(variant));
        if (variant != Variant.Base) Assert.True(k.HasGpuPalette, $"{variant} palette did not compile");
        GpuPeriodicity.Mode = mode;
        try
        {
            k.Run(W, H, v.Cx, v.Cy, v.Scale, maxIter ?? v.MaxIter, 4.0,
                  o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di, perRow, v.Kind, v.P0, v.P1,
                  colorDst: variant == Variant.Base ? null : o.Col,
                  trapDst: variant == Variant.Orbit ? o.Trap : null);
        }
        finally { GpuPeriodicity.Reset(); }
        o.DispatchMs = k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs;
        return o;
    }

    private static int FirstDiff<T>(T[] a, T[] b, Func<int, bool>? where = null) where T : IEquatable<T>
    {
        for (int i = 0; i < a.Length; i++)
            if ((where == null || where(i)) && !a[i].Equals(b[i])) return i;
        return -1;
    }

    // Bit equality for floats (NaN == NaN, -0 != +0).
    private static int FirstBitDiff(float[] a, float[] b, Func<int, bool>? where = null)
    {
        for (int i = 0; i < a.Length; i++)
            if ((where == null || where(i)) && BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return i;
        return -1;
    }

    private void AssertSame(string label, Out off, Out on, int maxIter)
    {
        Assert.True(FirstDiff(off.It, on.It) < 0, $"{label}: iteration differs at pixel {FirstDiff(off.It, on.It)}");
        Assert.True(FirstBitDiff(off.Sm, on.Sm) < 0, $"{label}: smooth differs at pixel {FirstBitDiff(off.Sm, on.Sm)}");
        Assert.True(FirstDiff(off.Col, on.Col) < 0, $"{label}: colour differs at pixel {FirstDiff(off.Col, on.Col)}");
        Assert.True(FirstBitDiff(off.Trap, on.Trap) < 0, $"{label}: trap differs at pixel {FirstBitDiff(off.Trap, on.Trap)}");
        // Escaping pixels: the final z / dz/dc too (the distance estimate and normals).
        bool Esc(int i) => off.It[i] < maxIter;
        foreach (var (a, b, n) in new[] { (off.Zr, on.Zr, "zr"), (off.Zi, on.Zi, "zi"), (off.Dr, on.Dr, "dr"), (off.Di, on.Di, "di") })
            Assert.True(FirstBitDiff(a, b, Esc) < 0, $"{label}: escaping final {n} differs at pixel {FirstBitDiff(a, b, Esc)}");
    }

    /// <summary>Mandelbrot pixels the kernel's closed-form cardioid / period-2 bulb
    /// skip takes before the loop (CPU double form of the same tests).</summary>
    private static bool BulbSkipped(View v, int i)
    {
        if (v.Kind != FractalKind.Mandelbrot) return false;
        double cx = v.Cx + (i % W - 0.5 * W) * v.Scale, cy = v.Cy + (i / W - 0.5 * H) * v.Scale;
        double bx = cx + 1.0;
        if (bx * bx + cy * cy <= 0.0625) return true;
        double xm = cx - 0.25, q = xm * xm + cy * cy;
        return q * (q + xm) <= 0.25 * cy * cy;
    }

    /// <summary>Diagnostic-mode pixels where the check fired (smooth = −steps).</summary>
    private static List<int> Detected(Out diag) =>
        Enumerable.Range(0, diag.Sm.Length).Where(i => diag.Sm[i] < 0f).ToList();

    [Theory]
    [MemberData(nameof(AllViews))]
    public void Check_Changes_No_Output_And_Fires_On_The_In_Set_Pixels(View v)
    {
        using var k = Device();
        var off = Run(k, v, GpuPeriodicity.Off);
        var on = Run(k, v, GpuPeriodicity.On);
        AssertSame(v.Name, off, on, v.MaxIter);

        // Precondition: the frame has in-set pixels and the check fired on most of
        // them (the ones the cardioid / bulb skip leaves, for Mandelbrot), only on
        // pixels the frame marks in set, and within the cap.
        var diag = Run(k, v, GpuPeriodicity.Diagnostic);
        Assert.True(FirstDiff(off.It, diag.It) < 0, $"{v.Name}: diagnostic mode changed an iteration");
        var det = Detected(diag);
        int inSet = Enumerable.Range(0, W * H).Count(i => off.It[i] >= v.MaxIter && !BulbSkipped(v, i));
        output.WriteLine($"{v.Name}: {inSet} in set past the bulb skip, {det.Count} by the check, median step {(det.Count > 0 ? det.Select(i => -diag.Sm[i]).OrderBy(s => s).ElementAt(det.Count / 2) : 0):F0}");
        Assert.True(inSet > 500, $"{v.Name}: precondition — only {inSet} in-set pixels past the bulb skip");
        Assert.True(det.Count > inSet / 3, $"{v.Name}: the check fired on {det.Count} of {inSet} in-set pixels");
        Assert.DoesNotContain(det, i => BulbSkipped(v, i));
        Assert.All(det, i => Assert.True(off.It[i] >= v.MaxIter && -diag.Sm[i] <= v.MaxIter,
            $"{v.Name}: pixel {i} detected at step {-diag.Sm[i]} but iter {off.It[i]}"));
        // Escaped and cap-run pixels keep the plain smooth (0 for in set).
        Assert.True(FirstBitDiff(off.Sm, diag.Sm, i => diag.Sm[i] >= 0f) < 0, $"{v.Name}: diagnostic smooth differs off the detected pixels");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Colour_And_Orbit_Kernels_Are_Unchanged(int variantIndex)
    {
        var variant = (Variant)variantIndex;
        using var k = Device();
        foreach (var v in Views.Where(v => v.Kind is FractalKind.Mandelbrot or FractalKind.Julia or FractalKind.Phoenix))
        {
            var off = Run(k, v, GpuPeriodicity.Off, variant);
            var on = Run(k, v, GpuPeriodicity.On, variant);
            AssertSame($"{v.Name} ({variant})", off, on, v.MaxIter);
            Assert.Contains(on.Col, c => c != on.Col[0]);   // a real colour frame
            if (variant == Variant.Orbit) Assert.Contains(on.Trap, t => t > 0f);
            var det = Detected(Run(k, v, GpuPeriodicity.Diagnostic, variant));
            Assert.True(det.Count > 100, $"{v.Name} ({variant}): the check fired on only {det.Count} pixels");
        }
    }

    [Fact]
    public void Per_Row_Caps_Are_Unchanged()
    {
        using var k = Device();
        var v = Views.First(x => x.Name == "Period-3 bulb");
        // Caps from 40 (below most detections) to the frame cap; 0 = use the frame cap.
        var perRow = Enumerable.Range(0, H).Select(y => y % 7 == 0 ? 0 : 40 + (y * 37) % v.MaxIter).ToArray();
        var off = Run(k, v, GpuPeriodicity.Off, perRow: perRow);
        var on = Run(k, v, GpuPeriodicity.On, perRow: perRow);
        AssertSame("per-row caps", off, on, v.MaxIter);
        Assert.True(Detected(Run(k, v, GpuPeriodicity.Diagnostic, perRow: perRow)).Count > 1000, "the check did not fire under the row caps");
    }

    [Fact]
    public void No_Detected_Pixel_Escapes_In_A_Long_Cpu_Iteration()
    {
        using var k = Device();
        const int Steps = 200_000;
        int checkedPx = 0, escaped = 0;
        foreach (var v in Views.Where(x => x.Kind == FractalKind.Mandelbrot))
        {
            var diag = Run(k, v, GpuPeriodicity.Diagnostic);
            var det = Detected(diag);
            // Every 7th detected pixel keeps the CPU time reasonable.
            foreach (int i in det.Where((_, n) => n % 7 == 0))
            {
                double cr = v.Cx + (i % W - 0.5 * W) * v.Scale, ci = v.Cy + (i / W - 0.5 * H) * v.Scale;
                double x = 0, y = 0;
                for (int s = 0; s < Steps; s++)
                {
                    double x2 = x * x, y2 = y * y;
                    if (x2 + y2 >= 4.0) { escaped++; output.WriteLine($"{v.Name}: pixel {i} c=({cr:R}, {ci:R}) escapes at {s}"); break; }
                    y = 2 * x * y + ci; x = x2 - y2 + cr;
                }
                checkedPx++;
            }
        }
        output.WriteLine($"{checkedPx} detected pixels iterated {Steps} steps on the CPU, {escaped} escaped");
        Assert.True(checkedPx > 2000, $"precondition: only {checkedPx} detected pixels checked");
        Assert.Equal(0, escaped);
    }

    [Fact]
    public void Interior_Heavy_Frames_Are_Faster()
    {
        using var k = Device();
        // Interior-heavy (asserted) and mixed (reported) views. At a mixed view the
        // escaping and the slowly converging boundary pixels set the frame time.
        var asserted = new[] { "Period-3 bulb", "Rabbit Julia" };
        foreach (var name in new[] { "Period-3 bulb", "Rabbit Julia", "Period-3 minibrot", "Seahorse valley", "Whole set" })
        {
            var v = Views.First(x => x.Name == name);
            foreach (int cap in new[] { 10_000, 100_000 })
            {
                Run(k, v, GpuPeriodicity.On, maxIter: cap);   // warm
                double Best(int mode) => Enumerable.Range(0, 2).Min(_ => Run(k, v, mode, maxIter: cap).DispatchMs);
                double tOff = Best(GpuPeriodicity.Off), tOn = Best(GpuPeriodicity.On);
                output.WriteLine($"{name} cap {cap}: off {tOff:F1} ms, on {tOn:F1} ms ({tOff / tOn:F1}x)");
                if (asserted.Contains(name))
                    Assert.True(tOn * 3 < tOff, $"{name} cap {cap}: on {tOn:F1} ms vs off {tOff:F1} ms");
            }
        }

        // The check's cost where it never fires: the Julia set of c just past the
        // cusp is dust, and every pixel escapes slowly through the parabolic gate
        // (~300 steps), so the frame is all compare-and-count.
        var ext = new View("Cusp Julia", FractalKind.Julia, 0.0, 0.0, 1.0, 10_000, 0.2501f, 0f);
        Run(k, ext, GpuPeriodicity.On);
        double eOff = Enumerable.Range(0, 3).Min(_ => Run(k, ext, GpuPeriodicity.Off).DispatchMs);
        var eOnRun = Run(k, ext, GpuPeriodicity.On);
        double eOn = Math.Min(eOnRun.DispatchMs, Enumerable.Range(0, 2).Min(_ => Run(k, ext, GpuPeriodicity.On).DispatchMs));
        // Precondition: (nearly) all exterior — a handful of fp32 orbits stall.
        Assert.True(eOnRun.It.Count(t => t >= ext.MaxIter) < W * H / 1000, "cusp Julia frame is not exterior");
        output.WriteLine($"Cusp Julia (all escape, mean {eOnRun.It.Average():F0} steps): off {eOff:F2} ms, on {eOn:F2} ms ({(eOn / eOff - 1) * 100:+0;-0}%)");
    }

    [Fact]
    public void Mode_Override_Is_Per_Thread_And_Resets()
    {
        GpuPeriodicity.Reset();
        int def = GpuPeriodicity.Mode;
        Assert.Equal(GpuPeriodicity.Default, def);
        GpuPeriodicity.Mode = GpuPeriodicity.Off;
        int other = -1;
        var t = new System.Threading.Thread(() => other = GpuPeriodicity.Mode);
        t.Start(); t.Join();
        Assert.Equal(GpuPeriodicity.Off, GpuPeriodicity.Mode);
        Assert.Equal(def, other);
        GpuPeriodicity.Reset();
        Assert.Equal(def, GpuPeriodicity.Mode);
    }
}
