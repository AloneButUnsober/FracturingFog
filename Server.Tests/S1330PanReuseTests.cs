// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1330 — pan pixel reuse on the anchored lattice (MandelbrotCalculator
// .LatticeAligned / CalculatePanned): a whole-pixel pan shifts the buffers and
// computes only the exposed strips. The independent reference is a FRESH
// calculator rendering the whole frame at the new offset:
//   • in the DD, QD and OD tiers, over a sequence of pans that are not multiples
//     of the SIMD lane width (so the lattice batching matters) on a frame whose
//     width isn't either, every output buffer is byte-identical to the fresh
//     render, and the panned frame computed exactly the exposed pixels;
//   • the scalar perturbation path too (ForceScalarPtPath);
//   • a pan the lattice can't reuse (off the frame, past LatticeMaxOffset, a
//     changed palette, lattice off) falls back to a full render — still equal;
//   • ShiftBuffer against a naive copy;
//   • a small pan is several times faster than the full frame.

using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // ForceScalarPtPath is static
public sealed class S1330PanReuseTests(ITestOutputHelper output)
{
    private sealed record View(string Name, double[] Cx, double[] Cy, double Zoom, int MaxIter);

    private static readonly View Dd = new("DD saprobe 1e15",
        new[] { -1.1726999042772253, 8.9529605787776783E-17, 0, 0, 0, 0, 0, 0 },
        new[] { -0.2968356710071185, -2.3536240906562374E-18, 0, 0, 0, 0, 0, 0 }, 1e15, 4096);

    private static readonly View Qd = new("QD Deeper and Deeper 4.5e46",
        new[] { -1.9918151296901943, -7.8219818188678307E-17, 3.2454272033149852E-33, -2.6986232918289806E-49, 0, 0, 0, 0 },
        new[] { -5.5240415753972429E-06, -2.8404793590633191E-22, 1.5048294824547351E-38, -6.0649764033320806E-55, 0, 0, 0, 0 },
        4.49845E+46, 20000);

    private static View Od()
    {
        // The S1319 minibrot (period 1808, OD tier past 1e68).
        var nucleus = new DeepComplex(
            new OD(-1.9918151296901943, -7.82198368112659E-17, -5.6237669196087764E-33, 2.6876672348091084E-49,
                   8.344815422720153E-68, 5.62021278812849E-84, 3.758605147229201E-100, 3.74170214755604E-117),
            new OD(-5.524041575397243E-06, -2.9567164202839686E-22, -2.2585741366702907E-38, 6.439806134954378E-55,
                   1.8890258519870084E-71, 1.8268317099722925E-87, -9.199248922388955E-104, 5.069913980636591E-120));
        var found = new NucleusResult(NucleusStatus.Found, nucleus, 1808,
            new Complex(1.2484338945256195E-69, 1.3350422957373403E-69), 0);
        var (fc, zoom) = NucleusFinder.Frame(found);
        return new View("OD minibrot 1e68",
            new[] { fc.Re.X0, fc.Re.X1, fc.Re.X2, fc.Re.X3, fc.Re.X4, fc.Re.X5, fc.Re.X6, fc.Re.X7 },
            new[] { fc.Im.X0, fc.Im.X1, fc.Im.X2, fc.Im.X3, fc.Im.X4, fc.Im.X5, fc.Im.X6, fc.Im.X7 },
            zoom, NucleusFinder.SuggestedIterations(1808));
    }

    // Not a multiple of 4 or 8: the lattice batches straddle both frame edges.
    private const int W = 70, H = 46;

    private static MandelbrotCalculator Calc(View v, int offX, int offY, IColorMap? map = null) => new(W, H)
    {
        Quality = QualityPreset.Extreme,
        CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
        CenterX4 = v.Cx[4], CenterX5 = v.Cx[5], CenterX6 = v.Cx[6], CenterX7 = v.Cx[7],
        CenterY = v.Cy[0], CenterYLo = v.Cy[1], CenterY2 = v.Cy[2], CenterY3 = v.Cy[3],
        CenterY4 = v.Cy[4], CenterY5 = v.Cy[5], CenterY6 = v.Cy[6], CenterY7 = v.Cy[7],
        Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = map ?? Hsv,
        ImageWidth = W, ImageHeight = H, SubRectOffsetX = offX, SubRectOffsetY = offY,
        LatticeAligned = true,
    };

    private static readonly IColorMap Hsv = new HsvPalette();

    private static string? FirstDifference(MandelbrotCalculator a, MandelbrotCalculator b)
    {
        string? D<T>(string name, T[] x, T[] y) where T : struct
        {
            for (int i = 0; i < W * H; i++)
                if (!x[i].Equals(y[i])) return $"{name}[{i % W},{i / W}]: {x[i]} vs {y[i]}";
            return null;
        }
        return D("iter", a.IterationBuffer, b.IterationBuffer) ?? D("smooth", a.SmoothBuffer, b.SmoothBuffer)
            ?? D("distance", a.DistanceBuffer, b.DistanceBuffer) ?? D("normalX", a.NormalXBuffer, b.NormalXBuffer)
            ?? D("normalY", a.NormalYBuffer, b.NormalYBuffer) ?? D("colour", a.ColorBuffer, b.ColorBuffer)
            ?? D("finalZr", a.FinalZrBuffer, b.FinalZrBuffer) ?? D("finalZi", a.FinalZiBuffer, b.FinalZiBuffer)
            ?? D("finalDr", a.FinalDrBuffer, b.FinalDrBuffer) ?? D("finalDi", a.FinalDiBuffer, b.FinalDiBuffer);
    }

    private static readonly (int Dx, int Dy)[] Pans = { (5, 0), (0, -7), (-13, 9), (1, 1), (-3, -2), (11, -6), (0, 0) };

    private void PanSequenceMatchesFreshRenders(View v, bool scalar)
    {
        bool saved = MandelbrotCalculator.ForceScalarPtPath;
        MandelbrotCalculator.ForceScalarPtPath = scalar;   // static: the class is in the statics collection
        try { PanSequence(v, scalar); }
        finally { MandelbrotCalculator.ForceScalarPtPath = saved; }
    }

    private void PanSequence(View v, bool scalar)
    {
        var a = Calc(v, 3, -5);
        a.Calculate(CancellationToken.None);
        Assert.Equal((long)W * H, a.LastFrameComputedPixels);
        int ox = 3, oy = -5;
        foreach (var (dx, dy) in Pans)
        {
            Assert.True(a.CalculatePanned(dx, dy, CancellationToken.None), $"{v.Name}: pan ({dx},{dy}) wasn't reused");
            ox += dx; oy += dy;
            Assert.Equal(ox, a.SubRectOffsetX);
            Assert.Equal(oy, a.SubRectOffsetY);
            long exposed = (long)W * H - (long)(W - Math.Abs(dx)) * (H - Math.Abs(dy));
            Assert.Equal(exposed, a.LastFrameComputedPixels);

            var fresh = Calc(v, ox, oy);
            fresh.Calculate(CancellationToken.None);
            int distinct = fresh.IterationBuffer.Take(W * H).Distinct().Count();
            int inSet = fresh.IterationBuffer.Take(W * H).Count(i => i >= v.MaxIter);
            Assert.True(distinct > 20 && inSet < W * H, $"{v.Name}: degenerate frame ({distinct} values, {inSet} in set)");
            string? diff = FirstDifference(a, fresh);
            Assert.True(diff == null, $"{v.Name} after pan ({dx},{dy}) to offset ({ox},{oy}): {diff}");
        }
        output.WriteLine($"{v.Name}{(scalar ? " (scalar)" : "")}: {Pans.Length} pans byte-identical, lane path {(System.Runtime.Intrinsics.X86.Avx512F.IsSupported ? "AVX-512" : "AVX2")}");
    }

    [Fact] public void Dd_Pans_Are_Byte_Identical_To_Fresh_Renders() => PanSequenceMatchesFreshRenders(Dd, scalar: false);
    [Fact] public void Qd_Pans_Are_Byte_Identical_To_Fresh_Renders() => PanSequenceMatchesFreshRenders(Qd, scalar: false);
    [Fact] public void Od_Pans_Are_Byte_Identical_To_Fresh_Renders() => PanSequenceMatchesFreshRenders(Od(), scalar: false);
    [Fact] public void Scalar_Path_Pans_Are_Byte_Identical_To_Fresh_Renders() => PanSequenceMatchesFreshRenders(Dd, scalar: true);

    [Fact]
    public void Pans_The_Lattice_Cannot_Reuse_Fall_Back_To_A_Full_Render()
    {
        void Check(MandelbrotCalculator a, int dx, int dy, string why)
        {
            Assert.False(a.CalculatePanned(dx, dy, CancellationToken.None), why);
            Assert.Equal((long)W * H, a.LastFrameComputedPixels);
            var fresh = Calc(Dd, a.SubRectOffsetX, a.SubRectOffsetY, map: a.ColorMap);
            fresh.LatticeMaxOffset = a.LatticeMaxOffset;
            fresh.Calculate(CancellationToken.None);
            Assert.True(FirstDifference(a, fresh) == null, why);
        }
        var a = Calc(Dd, 0, 0);
        a.Calculate(CancellationToken.None);
        Check(a, W, 0, "a pan off the frame");
        Check(a, 0, -H - 3, "a pan off the frame (vertical)");
        a.LatticeMaxOffset = 20;
        a.Calculate(CancellationToken.None);
        Check(a, 30, 0, "past LatticeMaxOffset");
        a.LatticeMaxOffset = 0;
        a.SubRectOffsetX = a.SubRectOffsetY = 0;   // back inside the bound, so only the palette differs
        a.Calculate(CancellationToken.None);
        a.ColorMap = new HsvPalette();   // same kind, another instance: only the frame-state check refuses it
        Check(a, 4, 4, "a changed palette");
        var plain = Calc(Dd, 0, 0);
        plain.LatticeAligned = false;
        plain.Calculate(CancellationToken.None);
        Assert.False(plain.CalculatePanned(4, 4, CancellationToken.None), "lattice off");
    }

    [Fact]
    public void A_Frame_Cancelled_Mid_Way_Leaves_Nothing_To_Reuse()
    {
        // A complete frame, then one cancelled while its rows are being written:
        // the buffers are half new, so the next pan must not reuse them.
        const int w = 960, h = 540;
        var v = Dd;
        var c = new MandelbrotCalculator(w, h)
        {
            Quality = QualityPreset.Extreme, CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterY = v.Cy[0], CenterYLo = v.Cy[1],
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = Hsv, ImageWidth = w, ImageHeight = h, LatticeAligned = true,
        };
        c.Calculate(CancellationToken.None);
        bool cancelledMidFrame = false;
        for (int attempt = 0; attempt < 6 && !cancelledMidFrame; attempt++)
        {
            c.Calculate(CancellationToken.None);   // a reusable frame before each try
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5 + 5 * attempt));
            try { c.Calculate(cts.Token); }
            catch (OperationCanceledException) { cancelledMidFrame = true; }
        }
        Assert.True(cancelledMidFrame, "precondition: could not cancel a frame mid-way");
        Assert.False(c.CalculatePanned(4, 4, CancellationToken.None), "reused a cancelled frame");
        Assert.Equal((long)w * h, c.LastFrameComputedPixels);
    }

    [Fact]
    public void A_Lattice_Frame_Agrees_With_A_Normal_Render_At_Its_Centre()
    {
        // Independent of the lattice: a frame almost a full frame off its anchor
        // (so the BLA radius has to cover the offset) against an ordinary render
        // centred where it shows. Not byte-identical (the centre's last bits and
        // the SIMD batches differ), but the same picture.
        const int w = 200, h = 150, ox = 180, oy = -130;
        var v = Dd with { Zoom = 1e14 };
        var lat = new MandelbrotCalculator(w, h)
        {
            Quality = QualityPreset.Extreme, CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterY = v.Cy[0], CenterYLo = v.Cy[1],
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = Hsv,
            ImageWidth = w, ImageHeight = h, SubRectOffsetX = ox, SubRectOffsetY = oy, LatticeAligned = true,
        };
        lat.Calculate(CancellationToken.None);
        double scale = (3.5 / Math.Max(w, h)) / v.Zoom;
        var cx = OD.FromCenterOffset(new OD(v.Cx[0], v.Cx[1], 0, 0, 0, 0, 0, 0), ox, scale);
        var cy = OD.FromCenterOffset(new OD(v.Cy[0], v.Cy[1], 0, 0, 0, 0, 0, 0), oy, scale);
        var normal = new MandelbrotCalculator(w, h)
        {
            Quality = QualityPreset.Extreme, CenterX = cx.X0, CenterXLo = cx.X1, CenterY = cy.X0, CenterYLo = cy.X1,
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = Hsv,
        };
        normal.Calculate(CancellationToken.None);
        int n = w * h, disagree = 0;
        for (int i = 0; i < n; i++) if (lat.IterationBuffer[i] != normal.IterationBuffer[i]) disagree++;
        int distinct = normal.IterationBuffer.Take(n).Distinct().Count();
        output.WriteLine($"lattice offset ({ox},{oy}) vs normal render: {disagree}/{n} iteration counts differ, {distinct} distinct");
        Assert.True(distinct > 20, $"precondition: a busy frame ({distinct} distinct)");
        Assert.True(disagree <= n / 200, $"{disagree}/{n} pixels differ from the normal render");
    }

    [Fact]
    public void ShiftBuffer_Matches_A_Naive_Copy()
    {
        var rng = new Random(1330);
        const int w = 13, h = 9;
        for (int t = 0; t < 200; t++)
        {
            int dx = rng.Next(-w - 2, w + 3), dy = rng.Next(-h - 2, h + 3);
            var src = Enumerable.Range(0, w * h).ToArray();
            var buf = (int[])src.Clone();
            MandelbrotCalculator.ShiftBuffer(buf, w, h, dx, dy);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = x + dx, sy = y + dy;
                    if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;   // exposed: recomputed later
                    Assert.True(buf[y * w + x] == src[sy * w + sx], $"shift ({dx},{dy}) at ({x},{y})");
                }
        }
    }

    [Fact]
    public void A_Small_Pan_Is_Much_Faster_Than_The_Full_Frame()
    {
        const int w = 480, h = 270;
        var v = Dd;
        var a = new MandelbrotCalculator(w, h)
        {
            Quality = QualityPreset.Extreme, CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterY = v.Cy[0], CenterYLo = v.Cy[1],
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = Hsv,
            ImageWidth = w, ImageHeight = h, LatticeAligned = true,
        };
        a.Calculate(CancellationToken.None);   // warm: orbit, BLA, SA
        double full = double.MaxValue, pan = double.MaxValue;
        for (int r = 0; r < 3; r++)
        {
            var sw = Stopwatch.StartNew();
            a.Calculate(CancellationToken.None);
            full = Math.Min(full, sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            Assert.True(a.CalculatePanned(r % 2 == 0 ? 6 : -6, 4, CancellationToken.None));
            pan = Math.Min(pan, sw.Elapsed.TotalMilliseconds);
        }
        output.WriteLine($"{w}x{h}: full frame {full:F1} ms, 6x4 px pan {pan:F1} ms ({full / pan:F1}x)");
        Assert.True(pan * 4 < full, $"pan {pan:F1} ms vs full {full:F1} ms");
    }
}
