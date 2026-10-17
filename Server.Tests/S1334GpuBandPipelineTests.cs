// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1334 — deep-zoom perturbation frames run in TDR row bands (both backends size
// them with MandelbrotKernelSource.PerturbBandRows; the D3D11 side is checked by
// --gpubandprobe). Checked:
//   • band sizing, against the shader's own coverage rule: a band dispatches
//     ⌈rows / 8⌉ thread groups of 8 rows from its gRowBase, and every pixel row of
//     the frame is run exactly once, for every frame shape and depth (before #1334
//     a band that was not a whole number of groups also ran the next band's first
//     rows, up to 8 times over in deep frames); the band stays within the TDR
//     budget unless it is the single group the kernel can't go below, and is never
//     needlessly short;
//   • on whatever Vulkan device the host has (skipped without one or without fp64):
//     every banded program (plain, orbit means, SA + BLA, float-exp, scaled fp32), in
//     the host, auto and device-outputs layouts, gives the same bytes in 8-row bands
//     as dispatched in one band. Every buffer is dirtied with another view (another
//     reference orbit) first, so a band that wrote the wrong rows (or none), or ran
//     before the uploads landed, can't pass on the last frame's leftovers;
//   • the bands don't wait for each other: a banded frame waits for the queue twice
//     (after band 0 for the too-slow abort, and at the end) or once with the abort
//     off; FF_VULKAN_SERIAL_BANDS waits after every band;
//   • the too-slow abort still fires after band 0, and the kernel renders correctly
//     afterwards.

using System;
using System.Linq;
using FracturingFog.FFMath;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes PerturbBudgetMs / PerturbBandRowsOverride
public sealed class S1334GpuBandPipelineTests : IDisposable
{
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly int _savedRows = MandelbrotKernelSource.PerturbBandRowsOverride;

    public S1334GpuBandPipelineTests() => MandelbrotKernelSource.PerturbBudgetMs = 0;

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotKernelSource.PerturbBandRowsOverride = _savedRows;
        VulkanComputeKernel.ForceSerialBands = false;
        VulkanComputeKernel.ForceFloatExpEntry = false;
    }

    // ── band sizing ──────────────────────────────────────────────────────────

    private const int GroupRows = 8;   // [numthreads(8, 8, 1)] in every perturbation kernel

    /// <summary>How many times each row is run: band b dispatches ⌈rows_b / 8⌉ groups
    /// of 8 rows from its row base, and the kernel skips rows past the frame.</summary>
    private static int[] RowRuns(int height, int bandRows)
    {
        var runs = new int[height];
        for (int rowBase = 0; rowBase < height; rowBase += bandRows)
        {
            int rows = Math.Min(bandRows, height - rowBase);
            int dispatched = (rows + GroupRows - 1) / GroupRows * GroupRows;
            for (int y = rowBase; y < Math.Min(height, rowBase + dispatched); y++) runs[y]++;
        }
        return runs;
    }

    public static TheoryData<int, int> Shapes() => new()
    {
        { 64, 1 }, { 64, 7 }, { 333, 8 }, { 333, 100 }, { 960, 540 }, { 1920, 1080 }, { 3840, 2160 }, { 1000, 9 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_Row_Runs_Exactly_Once_Within_The_Budget(int width, int height)
    {
        MandelbrotKernelSource.PerturbBandRowsOverride = 0;
        foreach (int maxIter in new[] { 1, 100, 1000, 2600, 5000, 20000, 1_000_000, int.MaxValue })
        {
            int rows = MandelbrotKernelSource.PerturbBandRows(width, height, maxIter);
            string at = $"{width}x{height} maxIter {maxIter}: {rows} rows";
            Assert.InRange(rows, 1, height);
            Assert.True(rows % GroupRows == 0 || rows == height, $"{at}: not a whole number of groups");

            var runs = RowRuns(height, rows);
            int twice = Array.FindIndex(runs, r => r != 1);
            Assert.True(twice < 0, $"{at}: row {twice} runs {(twice < 0 ? 0 : runs[twice])} times");

            // Within the TDR budget unless it is one group (the kernel can't dispatch less) ...
            long budgetRows = MandelbrotKernelSource.PerturbDispatchIterBudget / ((long)width * maxIter);
            if (rows > GroupRows) Assert.True(rows <= budgetRows, $"{at}: over the {budgetRows}-row budget");
            // ... and never needlessly short: one more group would break the budget (or the frame).
            if (rows < height) Assert.True(rows + GroupRows > budgetRows, $"{at}: {budgetRows} rows would fit");
        }
    }

    [Theory]
    [InlineData(1, 100, 8)]
    [InlineData(8, 100, 8)]
    [InlineData(13, 100, 8)]
    [InlineData(16, 100, 16)]
    [InlineData(500, 100, 100)]
    [InlineData(3, 5, 5)]
    public void The_Override_Is_Rounded_To_Whole_Groups(int forced, int height, int expected)
    {
        MandelbrotKernelSource.PerturbBandRowsOverride = forced;
        int rows = MandelbrotKernelSource.PerturbBandRows(333, height, 1000);
        Assert.Equal(expected, rows);
        Assert.All(RowRuns(height, rows), r => Assert.Equal(1, r));
    }

    // ── Vulkan banded programs ───────────────────────────────────────────────

    private const double EscapeR2 = 512.0 * 512.0;
    private const int W = 96, H = 200, N = W * H, MaxIter = 3000;

    private static VulkanComputeKernel Kernel(VulkanBufferPlacement placement)
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext(placement);
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbation) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        return k;
    }

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

    private static readonly (double[] Zr, double[] Zi, int Len) Seahorse = Orbit(-0.743643887037151, 0.13182590420533, MaxIter);
    // The dirtying view: another reference orbit (and so other SA / BLA tables), so a
    // band that missed an upload can't pass on the staged copy of the right one.
    private static readonly (double[] Zr, double[] Zi, int Len) Feigenbaum = Orbit(-1.4011551890920506, 0.0, MaxIter);

    private sealed class Frame
    {
        public readonly int[] It = new int[N];
        public readonly float[] Sm = new float[N], Zr = new float[N], Zi = new float[N], Dr = new float[N], Di = new float[N];
        public float[] Extra = Array.Empty<float>();

        public Array[] All => [It, Sm, Zr, Zi, Dr, Di, Extra];
    }

    private enum Path { Plain, Orbit, Sa, FloatExp, ScaledF32 }

    /// <summary>One frame of <paramref name="path"/> at <paramref name="zoom"/>, around
    /// the seahorse reference (or the Feigenbaum one, <paramref name="dirty"/>).</summary>
    private static Frame Render(VulkanComputeKernel k, Path path, double zoom, bool dirty)
    {
        var o = dirty ? Feigenbaum : Seahorse;
        var f = new Frame();
        double scale = 3.5 / (W * zoom);
        switch (path)
        {
            case Path.Plain:
                k.RunPerturb(W, H, scale, MaxIter, EscapeR2, -W / 2, -H / 2, o.Zr, o.Zi, o.Len, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);
                break;
            case Path.Orbit:
                int mask = MandelbrotKernelSource.OrbAll, stride = MandelbrotKernelSource.PerturbOrbitStride(mask);
                f.Extra = new float[N * stride];
                k.RunPerturbOrbit(W, H, scale, MaxIter, EscapeR2, -W / 2, -H / 2, o.Zr, o.Zi, o.Len,
                    mask, -0.743643887037151, 0.13182590420533, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di, f.Extra);
                break;
            case Path.Sa:
                var sa = new SeriesApproximation(o.Zr, o.Zi, o.Len);
                var bla = new BlaTable(o.Zr, o.Zi, o.Len, Math.Sqrt(2.0) * 0.5 * Math.Max(W, H) * scale);
                k.RunPerturbSA(W, H, scale, MaxIter, EscapeR2, -W / 2, -H / 2, o.Zr, o.Zi, o.Len, 1e-3, sa.SafeMax,
                    sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI,
                    f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di, bla.GpuCoefficients, bla.Levels);
                break;
            case Path.FloatExp:
            case Path.ScaledF32:
                VulkanComputeKernel.ForceFloatExpEntry = path == Path.FloatExp;
                try
                {
                    k.RunPerturbFloatExp(W, H, scale, MaxIter, EscapeR2, -W / 2, -H / 2, o.Zr, o.Zi, o.Len,
                        f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);
                    Assert.Equal(path == Path.ScaledF32, k.LastPerturbFloatExpWasScaledF32);
                }
                finally { VulkanComputeKernel.ForceFloatExpEntry = false; }
                break;
        }
        return f;
    }

    private static Frame RenderInBands(VulkanComputeKernel k, Path path, double zoom, int bandRows, bool dirty = false)
    {
        MandelbrotKernelSource.PerturbBandRowsOverride = bandRows;
        try { return Render(k, path, zoom, dirty); }
        finally { MandelbrotKernelSource.PerturbBandRowsOverride = 0; }
    }

    private static string? FirstDifference(Frame a, Frame b)
    {
        Array[] x = a.All, y = b.All;
        string[] names = ["iter", "smooth", "zr", "zi", "dr", "di", "extra"];
        for (int o = 0; o < x.Length; o++)
        {
            if (x[o].Length != y[o].Length) return $"{names[o]} length";
            for (int i = 0; i < x[o].Length; i++)
                if (!Equals(x[o].GetValue(i), y[o].GetValue(i)))
                    return $"{names[o]} at pixel ({i % W}, {i / W % H})";
        }
        return null;
    }

    public static TheoryData<VulkanBufferPlacement> Layouts() =>
        [VulkanBufferPlacement.HostOnly, VulkanBufferPlacement.Auto, VulkanBufferPlacement.DeviceOutputs];

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Every_Banded_Program_Equals_One_Band(VulkanBufferPlacement placement)
    {
        using var k = Kernel(placement);
        Assert.True(H / GroupRows > 2, "precondition: several bands");
        foreach (Path path in Enum.GetValues<Path>())
        {
            Frame whole = RenderInBands(k, path, 1e6, H);
            Frame other = RenderInBands(k, path, 1e3, H, dirty: true);   // dirty every buffer with another view
            Assert.NotNull(FirstDifference(whole, other));
            Assert.True(whole.It.Distinct().Count() > 20, $"{path}: precondition: a flat frame");

            Frame banded = RenderInBands(k, path, 1e6, GroupRows);
            Assert.Equal((H + GroupRows - 1) / GroupRows, k.LastGpuStageTimes?.Packets ?? (H + GroupRows - 1) / GroupRows);
            string? diff = FirstDifference(whole, banded);
            Assert.True(diff is null, $"{placement} {path}: 8-row bands differ from one band at {diff}");
        }
    }

    [Fact]
    public void The_Bands_Do_Not_Wait_For_Each_Other()
    {
        using var k = Kernel(VulkanBufferPlacement.Auto);
        int bands = (H + GroupRows - 1) / GroupRows;
        Frame reference = RenderInBands(k, Path.Plain, 1e6, H);
        Assert.Equal(1, k.LastFrameQueueWaits);

        MandelbrotKernelSource.PerturbBudgetMs = 0;          // no abort: one wait, at the end
        RenderInBands(k, Path.Plain, 1e6, GroupRows);
        Assert.Equal(1, k.LastFrameQueueWaits);

        MandelbrotKernelSource.PerturbBudgetMs = 1e9;        // abort armed: band 0, then the end
        Frame armed = RenderInBands(k, Path.Plain, 1e6, GroupRows);
        Assert.Equal(2, k.LastFrameQueueWaits);
        Assert.Null(FirstDifference(reference, armed));

        VulkanComputeKernel.ForceSerialBands = true;         // the pre-#1334 shape: every band
        Frame serial = RenderInBands(k, Path.Plain, 1e6, GroupRows);
        Assert.Equal(bands, k.LastFrameQueueWaits);
        Assert.Null(FirstDifference(reference, serial));
    }

    [Fact]
    public void The_Too_Slow_Abort_Fires_After_Band_Zero_And_The_Kernel_Recovers()
    {
        using var k = Kernel(VulkanBufferPlacement.Auto);
        Frame reference = RenderInBands(k, Path.Plain, 1e6, H);

        MandelbrotKernelSource.PerturbBudgetMs = 1e-6;       // any band 0 extrapolates past it
        var ex = Assert.Throws<TimeoutException>(() => RenderInBands(k, Path.Plain, 1e6, GroupRows));
        Assert.Contains(MandelbrotKernelSource.PerturbTooSlowMarker, ex.Message);
        Assert.Equal(1, k.LastFrameQueueWaits);              // only band 0 ran

        MandelbrotKernelSource.PerturbBudgetMs = 0;
        RenderInBands(k, Path.Plain, 1e3, H, dirty: true);
        Frame after = RenderInBands(k, Path.Plain, 1e6, GroupRows);
        Assert.Null(FirstDifference(reference, after));
    }
}
