// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1481 — the shallow GPU escape-time kernels (HlslEntry: base, colour, orbit colour)
// run in TDR row bands, as the deep kernels do (#1334). They dispatched the whole
// frame at once, and a 1080p orbit-coloured minibrot frame at 4000 iterations ran past
// the ~2 s OS watchdog on a GT 710 (device removed). Checked:
//   • band sizing (MandelbrotKernelSource.ShallowBands), against the coverage rule a
//     band's dispatch follows: every row of the frame is in exactly one band, in order;
//     every band but the last is a whole number of thread-group rows; no band is over
//     ShallowDispatchIterBudget unless it is the one group the kernel can't go below,
//     and none is needlessly short; a frame within the budget stays one dispatch, and
//     the frame that reset the GT 710 does not; a masked frame never has more bands;
//   • on whatever Vulkan device the host has (skipped without one): the base, colour and
//     orbit colour kernels, one thread per pixel and wavefront, with per-row caps and
//     with a pixel mask, give the same bytes in bands as in one band. Every buffer is
//     dirtied with another view first, so a band that wrote the wrong rows (or none)
//     can't pass on the last frame's leftovers;
//   • the band count is what ShallowBands gives, and the bands don't wait for each
//     other: a banded frame waits for the queue as often as a one-band frame, and
//     FF_VULKAN_SERIAL_BANDS waits after every band.
// The D3D11 kernel is covered by --gpubandprobe shallow.

using System;
using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes ShallowBandRowsOverride / GpuWavefront.Mode
public sealed class S1481ShallowGpuBandTests : IDisposable
{
    private readonly int _savedRows = MandelbrotKernelSource.ShallowBandRowsOverride;

    public S1481ShallowGpuBandTests() => MandelbrotKernelSource.ShallowBandRowsOverride = 0;

    public void Dispose()
    {
        MandelbrotKernelSource.ShallowBandRowsOverride = _savedRows;
        VulkanComputeKernel.ForceSerialBands = false;
        GpuWavefront.Mode = GpuWavefront.Default;
    }

    // ── band sizing ──────────────────────────────────────────────────────────

    private const long Budget = MandelbrotKernelSource.ShallowDispatchIterBudget;

    public static TheoryData<int, int, int, int> Frames() => new()
    {
        { 64, 1, 100, 8 }, { 333, 100, 5000, 8 }, { 960, 540, 4000, 8 }, { 1920, 1080, 4000, 8 },
        { 1920, 1080, 4000, 16 }, { 1920, 1080, 250, 8 }, { 3840, 2160, 2000, 4 }, { 3840, 2160, 50000, 8 },
        { 1000, 9, 1_000_000, 8 }, { 7680, 4320, 1000, 32 },
    };

    [Theory]
    [MemberData(nameof(Frames))]
    public void Bands_Cover_Every_Row_Once_Within_The_Budget(int w, int h, int maxIter, int groupRows)
    {
        var bands = MandelbrotKernelSource.ShallowBands(w, h, maxIter, groupRows);
        int next = 0;
        long perRow = (long)w * maxIter;
        for (int b = 0; b < bands.Length; b++)
        {
            var (rowBase, rows) = bands[b];
            bool last = b == bands.Length - 1;
            Assert.Equal(next, rowBase);
            Assert.True(rows > 0, $"band {b}: {rows} rows");
            if (!last) Assert.Equal(0, rows % groupRows);
            // Within the budget, unless one group of rows is already over it.
            if (rows > groupRows) Assert.True(rows * perRow <= Budget, $"band {b}: {rows} rows = {rows * perRow} iter-pixels > {Budget}");
            // Not needlessly short: another group would have gone over the budget.
            if (!last) Assert.True((rows + groupRows) * perRow > Budget, $"band {b}: {rows} rows, room for {groupRows} more");
            next += rows;
        }
        Assert.Equal(h, next);
    }

    [Fact]
    public void A_Frame_Within_The_Budget_Is_One_Dispatch_And_The_Gt710_Frame_Is_Not()
    {
        // 1080p at 250 iterations: 518 M iter-pixels, one dispatch as before #1481.
        Assert.True(1920L * 1080 * 250 <= Budget, "precondition");
        var one = Assert.Single(MandelbrotKernelSource.ShallowBands(1920, 1080, 250));
        Assert.Equal((0, 1080), (one.RowBase, one.Rows));
        // The frame that reset the GT 710: 1080p at 4000 iterations, 8.3 G iter-pixels.
        var bands = MandelbrotKernelSource.ShallowBands(1920, 1080, 4000);
        Assert.True(bands.Length >= 9, $"{bands.Length} bands");
        Assert.All(bands, b => Assert.True((long)b.Rows * 1920 * 4000 <= Budget));
    }

    [Fact]
    public void A_Masked_Frame_Never_Has_More_Bands_And_Still_Covers_Every_Row()
    {
        const int w = 1920, h = 1080, maxIter = 4000;
        var rng = new Random(1481);
        foreach (double density in new[] { 0.0, 0.03, 0.5, 1.0 })
        {
            var mark = new byte[w * h];
            for (int i = 0; i < mark.Length; i++) mark[i] = rng.NextDouble() < density ? (byte)1 : (byte)0;
            var mask = GpuPixelMask.FromBytes(mark, w, h);
            var masked = MandelbrotKernelSource.ShallowBands(w, h, maxIter, 8, mask.Words);
            var plain = MandelbrotKernelSource.ShallowBands(w, h, maxIter, 8);
            Assert.True(masked.Length <= plain.Length, $"density {density}: {masked.Length} > {plain.Length} bands");
            Assert.Equal(h, masked.Sum(b => b.Rows));
            for (int b = 1; b < masked.Length; b++) Assert.Equal(masked[b - 1].RowBase + masked[b - 1].Rows, masked[b].RowBase);
        }
    }

    [Fact]
    public void The_Override_Sets_The_Band_Height_And_The_Deep_One_Does_Not()
    {
        int savedDeep = MandelbrotKernelSource.PerturbBandRowsOverride;
        try
        {
            MandelbrotKernelSource.ShallowBandRowsOverride = 64;
            Assert.Equal(3, MandelbrotKernelSource.ShallowBands(256, 192, 100).Length);
            MandelbrotKernelSource.ShallowBandRowsOverride = 0;
            MandelbrotKernelSource.PerturbBandRowsOverride = 8;   // the deep kernels' hook
            Assert.Single(MandelbrotKernelSource.ShallowBands(256, 192, 100));
            Assert.Equal(24, MandelbrotKernelSource.PerturbBands(256, 192, 100).Length);
        }
        finally { MandelbrotKernelSource.PerturbBandRowsOverride = savedDeep; }
    }

    // ── Vulkan: banded == one band ─────────────────────────────────────────────

    private const int W = 256, H = 192, N = W * H;

    public enum Variant { Base, Colour, Orbit }

    private sealed record View(double Cx, double Cy, double Width, int MaxIter);

    private static readonly View Minibrot = new(-1.7548776662466927, 0.0, 0.04, 4000);
    private static readonly View Other = new(-0.7453, 0.1127, 0.01, 3000);

    private sealed class Frame
    {
        public readonly int[] It = new int[N];
        public readonly float[] Sm = new float[N], Zr = new float[N], Zi = new float[N], Dr = new float[N], Di = new float[N], Trap = new float[N];
        public readonly uint[] Col = new uint[N];
        public int Bands, Waits;
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    private static IGpuHlslPalette Palette(Variant v)
    {
        if (v == Variant.Colour) return ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First();
        bool prev = InterpretedOrbitColorMap.GpuEnabled;
        InterpretedOrbitColorMap.GpuEnabled = true;
        try
        {
            var m = InterpretedColorMap.TryCreate(
                "return hsv(saturate(trapMin + stripeAvg * 0.3 + tiaAvg * 0.2), saturate(0.4 + trapRing), saturate(0.3 + curvature * 0.1));",
                null, out string? err);
            Assert.Null(err);
            return Assert.IsAssignableFrom<IGpuHlslPalette>(m);
        }
        finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
    }

    private static Frame Render(VulkanComputeKernel k, View v, Variant variant, int schedule, int bandRows,
                                int[]? perRow = null, GpuPixelMask? mask = null)
    {
        var f = new Frame();
        k.SetPalette(variant == Variant.Base ? null : Palette(variant));
        if (variant != Variant.Base) Assert.True(k.HasGpuPalette, $"{variant} palette did not compile");
        k.PixelMask = mask;
        GpuWavefront.Mode = schedule;
        MandelbrotKernelSource.ShallowBandRowsOverride = bandRows;
        try
        {
            k.Run(W, H, v.Cx, v.Cy, v.Width / W, v.MaxIter, 4.0, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di, perRow,
                  FractalKind.Mandelbrot, 0f, 0f,
                  colorDst: variant == Variant.Base ? null : f.Col,
                  trapDst: variant == Variant.Orbit ? f.Trap : null);
        }
        finally
        {
            MandelbrotKernelSource.ShallowBandRowsOverride = 0;
            GpuWavefront.Mode = GpuWavefront.Default;
            k.PixelMask = null;
        }
        f.Bands = k.LastShallowBandCount;
        f.Waits = k.LastFrameQueueWaits;
        return f;
    }

    private static string? FirstDifference(Frame a, Frame b, bool[]? only = null)
    {
        for (int i = 0; i < N; i++)
        {
            if (only != null && !only[i]) continue;
            if (a.It[i] != b.It[i]) return $"iter at ({i % W}, {i / W})";
            if (a.Col[i] != b.Col[i]) return $"colour at ({i % W}, {i / W})";
            foreach (var (x, y, n) in new[] { (a.Sm, b.Sm, "smooth"), (a.Zr, b.Zr, "zr"), (a.Zi, b.Zi, "zi"),
                                              (a.Dr, b.Dr, "dr"), (a.Di, b.Di, "di"), (a.Trap, b.Trap, "trap") })
                if (BitConverter.SingleToInt32Bits(x[i]) != BitConverter.SingleToInt32Bits(y[i])) return $"{n} at ({i % W}, {i / W})";
        }
        return null;
    }

    public static TheoryData<Variant, int> Kernels() => new()
    {
        { Variant.Base, GpuWavefront.Off }, { Variant.Base, GpuWavefront.On },
        { Variant.Colour, GpuWavefront.Off }, { Variant.Colour, GpuWavefront.On },
        { Variant.Orbit, GpuWavefront.Off }, { Variant.Orbit, GpuWavefront.On },
    };

    [Theory]
    [MemberData(nameof(Kernels))]
    public void Banded_Equals_One_Band(Variant variant, int schedule)
    {
        using var k = Device();
        Frame whole = Render(k, Minibrot, variant, schedule, H);
        Assert.Equal(1, whole.Bands);
        Assert.True(whole.It.Distinct().Count() > 50, "precondition: a flat frame");
        foreach (int rows in new[] { 8, 24, 64 })
        {
            Frame dirty = Render(k, Other, variant, schedule, H);
            int differ = Enumerable.Range(0, N).Count(i => dirty.It[i] != whole.It[i]);
            Assert.True(differ > N / 2, $"precondition: the dirty view differs on only {differ} of {N} pixels");
            Frame banded = Render(k, Minibrot, variant, schedule, rows);
            Assert.True(banded.Bands >= 3, $"{rows} rows: {banded.Bands} bands");
            if (rows == 64) Assert.Equal(3, banded.Bands);   // any tuned group height up to 64 divides it
            Assert.Null(FirstDifference(whole, banded));
            Assert.Equal(whole.Waits, banded.Waits);   // the bands queue back to back
        }
    }

    [Fact]
    public void Banded_Equals_One_Band_With_Per_Row_Caps_And_A_Mask()
    {
        using var k = Device();
        var perRow = Enumerable.Range(0, H).Select(y => 200 + y * 20).ToArray();
        var mark = new byte[N];
        var only = new bool[N];
        for (int i = 0; i < N; i++) { bool m = (i % W + i / W * 3) % 5 < 2; mark[i] = m ? (byte)1 : (byte)0; only[i] = m; }
        foreach (int schedule in new[] { GpuWavefront.Off, GpuWavefront.On })
        {
            Frame whole = Render(k, Minibrot, Variant.Colour, schedule, H, perRow);
            Render(k, Other, Variant.Colour, schedule, H);
            Frame banded = Render(k, Minibrot, Variant.Colour, schedule, 8, perRow);
            Assert.Null(FirstDifference(whole, banded));

            var mask = GpuPixelMask.FromBytes(mark, W, H);
            Frame wholeMasked = Render(k, Minibrot, Variant.Colour, schedule, H, mask: mask);
            Render(k, Other, Variant.Colour, schedule, H);
            Frame bandedMasked = Render(k, Minibrot, Variant.Colour, schedule, 8, mask: mask);
            Assert.True(bandedMasked.Bands > 1, "precondition: several bands");
            Assert.Null(FirstDifference(wholeMasked, bandedMasked, only));
            // The marked pixels are the unmasked frame's.
            Assert.Null(FirstDifference(Render(k, Minibrot, Variant.Colour, schedule, H), bandedMasked, only));
        }
    }

    [Fact]
    public void The_Band_Count_Follows_The_Budget_And_Serial_Bands_Wait_After_Each()
    {
        using var k = Device();
        // No override: a 4000-iteration frame of 256 x 192 is 197 M iter-pixels, one band.
        Frame auto = Render(k, Minibrot, Variant.Base, GpuWavefront.Off, 0);
        Assert.Equal(MandelbrotKernelSource.ShallowBands(W, H, Minibrot.MaxIter, 64).Length, auto.Bands);
        Assert.Equal(1, auto.Bands);

        Frame banded = Render(k, Minibrot, Variant.Base, GpuWavefront.Off, 64);
        VulkanComputeKernel.ForceSerialBands = true;
        Frame serial;
        try { serial = Render(k, Minibrot, Variant.Base, GpuWavefront.Off, 64); }
        finally { VulkanComputeKernel.ForceSerialBands = false; }
        Assert.Equal(3, serial.Bands);
        Assert.Equal(banded.Waits + serial.Bands - 1, serial.Waits);
        Assert.Null(FirstDifference(banded, serial));
    }
}
