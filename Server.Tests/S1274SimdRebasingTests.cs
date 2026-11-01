// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1274 — the rebased perturbation fallback runs 4 pixels at a time
// (MandelbrotCalculator.ComputePixelsPTRebased4) instead of one by one. Past
// ~1e30 almost every pixel of the PT4 / PT8 rows glitches into that fallback, so
// it is the deep-zoom hot loop. The scalar ComputePixelPTRebased is the
// specification: every buffer the frame writes (iteration, smooth, final z,
// derivative, colour) must be byte-identical with UseSimdRebasing on and off.
//   • the --rebaseprobe deep regions (3e47, 1e47, 4.5e46, 7.6e26), with and
//     without SA, each with the precondition that the rebased fallback resolved
//     most pixels (so the vector loop actually ran);
//   • a mixed view where lanes finish at very different iterations (refill),
//     plus maxIter 1 / 2 edge cases;
//   • the SIMD path is not slower than the scalar one on a deep frame (loose).

using System;
using System.Diagnostics;
using System.Linq;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes MandelbrotCalculator statics
public sealed class S1274SimdRebasingTests(ITestOutputHelper output)
{
    private sealed record View(string Name, double[] Cx, double[] Cy, double Zoom);

    private static readonly View[] Deep =
    [
        new("3E47 Test",
            [-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51],
            [-5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55], 3E+47),
        new("E45Test04",
            [0.40679612541749072, 1.0460588279145483E-17, -4.3674629952735269E-35, 5.0770999219861446E-50],
            [-0.56778808906247447, -4.0266051197805093E-17, 1.5194922328871422E-33, 5.0770999219861446E-50], 1.07808E+47),
        new("Deeper and Deeper",
            [-1.9918151296901943, -7.8219818188678307E-17, 3.2454272033149852E-33, -2.6986232918289806E-49],
            [-5.5240415753972429E-06, -2.8404793590633191E-22, 1.5048294824547351E-38, -6.0649764033320806E-55], 4.49845E+46),
        new("Deep Lightning in Space",
            [-1.4181949444785762, -7.4415882477902279E-17, 0.0, 0.0],
            [-0.12700786443815276, -2.3429499355532375E-18, 0.0, 0.0], 7.58348E+26),
    ];

    private sealed record Frame(int[] Iter, float[] Smooth, float[] Zr, float[] Zi, float[] Dr, float[] Di, uint[] Color,
                                long Rebased, double Ms, int Pixels, int RefLen);

    private static Frame Render(View v, bool simd, int w, int h, int maxIter, bool saOff = false)
    {
        bool prevSimd = MandelbrotCalculator.UseSimdRebasing, prevAllow = MandelbrotCalculator.AllowPtRebasing;
        bool prevResume = MandelbrotCalculator.ResumeRebased;
        MandelbrotCalculator.UseSimdRebasing = simd;
        MandelbrotCalculator.AllowPtRebasing = true;
        // #1613 — glitched lanes restart through the queue / scalar loop under test.
        MandelbrotCalculator.ResumeRebased = false;
        try
        {
            var c = new MandelbrotCalculator(w, h)
            {
                CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
                CenterY = v.Cy[0], CenterYLo = v.Cy[1], CenterY2 = v.Cy[2], CenterY3 = v.Cy[3],
                Zoom = v.Zoom, MaxIterations = maxIter,
                Quality = QualityPreset.Extreme,
                ColorMap = new HsvPalette(),
                DisableSeriesApproximation = saOff,
            };
            var sw = Stopwatch.StartNew();
            c.Calculate();
            sw.Stop();
            return new Frame((int[])c.IterationBuffer.Clone(), (float[])c.SmoothBuffer.Clone(),
                (float[])c.FinalZrBuffer.Clone(), (float[])c.FinalZiBuffer.Clone(),
                (float[])c.FinalDrBuffer.Clone(), (float[])c.FinalDiBuffer.Clone(),
                (uint[])c.ColorBuffer.Clone(), c.PtRebasedPixels, sw.Elapsed.TotalMilliseconds, w * h, c.ReferenceOrbitLength);
        }
        finally
        {
            MandelbrotCalculator.UseSimdRebasing = prevSimd;
            MandelbrotCalculator.AllowPtRebasing = prevAllow;
            MandelbrotCalculator.ResumeRebased = prevResume;
        }
    }

    private static void AssertIdentical(Frame scalar, Frame simd, string what)
    {
        Assert.Equal(scalar.Rebased, simd.Rebased);
        Assert.True(scalar.Iter.AsSpan().SequenceEqual(simd.Iter), $"{what}: iteration counts differ");
        Assert.True(scalar.Smooth.AsSpan().SequenceEqual(simd.Smooth), $"{what}: smooth differs");
        Assert.True(scalar.Zr.AsSpan().SequenceEqual(simd.Zr) && scalar.Zi.AsSpan().SequenceEqual(simd.Zi), $"{what}: final z differs");
        Assert.True(scalar.Dr.AsSpan().SequenceEqual(simd.Dr) && scalar.Di.AsSpan().SequenceEqual(simd.Di), $"{what}: derivative differs");
        Assert.True(scalar.Color.AsSpan().SequenceEqual(simd.Color), $"{what}: colour differs");
    }

    public static TheoryData<int, bool> DeepCases()
    {
        var d = new TheoryData<int, bool>();
        for (int i = 0; i < Deep.Length; i++) { d.Add(i, false); d.Add(i, true); }
        return d;
    }

    [Theory]
    [MemberData(nameof(DeepCases))]
    public void Deep_Frames_Are_Byte_Identical_To_The_Scalar_Rebased_Loop(int view, bool saOff)
    {
        var v = Deep[view];
        var scalar = Render(v, simd: false, 96, 72, 6000, saOff);
        var simd = Render(v, simd: true, 96, 72, 6000, saOff);
        // Precondition: the rebased fallback ran (past ~1e40 it renders most of the frame;
        // at 7.6e26 with SA on only the SA-glitched lanes reach it).
        Assert.True(scalar.Rebased > (v.Zoom > 1e40 ? scalar.Pixels / 2 : 0), $"{v.Name}: only {scalar.Rebased}/{scalar.Pixels} pixels rebased");
        AssertIdentical(scalar, simd, v.Name);
        int lo = scalar.Iter.Min(), hi = scalar.Iter.Max(), inSet = scalar.Iter.Count(i => i >= 6000);
        output.WriteLine($"{v.Name} saOff={saOff}: rebased {scalar.Rebased}/{scalar.Pixels}, iter {lo}..{hi}, in-set {inSet}, scalar {scalar.Ms:F0} ms, simd {simd.Ms:F0} ms");
    }

    [Fact]
    public void Lanes_Finishing_At_Different_Iterations_Are_Refilled_Correctly()
    {
        // The 3e47 centre at 1e35: escapes spread over ~1200..1600 iterations here, and a
        // cap of 1450 puts part of the frame in the set. Odd width, so the row's
        // scalar-tail pixels queue too. Lanes finish (escape or cap) and refill
        // constantly.
        var v = Deep[0] with { Zoom = 1e35 };
        const int Cap = 1450;
        var scalar = Render(v, simd: false, 133, 41, Cap);
        var simd = Render(v, simd: true, 133, 41, Cap);
        Assert.True(scalar.Rebased > scalar.Pixels / 2, $"only {scalar.Rebased}/{scalar.Pixels} pixels rebased");
        int lo = int.MaxValue, hi = 0, inSet = 0;
        foreach (int it in scalar.Iter) { lo = Math.Min(lo, it); hi = Math.Max(hi, it); if (it >= Cap) inSet++; }
        Assert.True(hi - lo > 200, $"iteration spread {lo}..{hi}: lanes would finish together");
        Assert.True(inSet > 0 && inSet < scalar.Pixels, $"in-set {inSet}/{scalar.Pixels}: need both kinds");
        AssertIdentical(scalar, simd, "mixed");
    }

    [Fact]
    public void An_Exhausted_Reference_Rebases_Like_The_Scalar_Loop()
    {
        // The 7.6e26 centre at 1e30, moved 20 px right: the reference escapes before
        // part of the frame does, so those pixels rebase because the reference runs
        // out (m + 1 >= refLen), not because |z| < |δ|.
        var v0 = Deep[3];
        double px = 4.0 / (1e30 * 96);
        var v = v0 with { Zoom = 1e30, Cx = [v0.Cx[0], v0.Cx[1] + 20 * px, v0.Cx[2], v0.Cx[3]] };
        var scalar = Render(v, simd: false, 96, 72, 6000, saOff: true);
        var simd = Render(v, simd: true, 96, 72, 6000, saOff: true);
        Assert.True(scalar.Rebased > scalar.Pixels / 2, $"only {scalar.Rebased}/{scalar.Pixels} pixels rebased");
        int outlive = scalar.Iter.Count(i => i >= scalar.RefLen);
        Assert.True(outlive > 0, $"no pixel outlives the {scalar.RefLen}-step reference (max {scalar.Iter.Max()})");
        AssertIdentical(scalar, simd, "exhausted reference");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void Tiny_Iteration_Caps_Match(int maxIter)
    {
        var v = Deep[3];
        AssertIdentical(Render(v, false, 37, 9, maxIter), Render(v, true, 37, 9, maxIter), $"maxIter {maxIter}");
    }

    [Fact]
    public void Simd_Rebasing_Is_Not_Slower_Than_Scalar()
    {
        var v = Deep[0];
        Render(v, true, 64, 48, 2000);   // warm up both paths (JIT)
        Render(v, false, 64, 48, 2000);
        double bestScalar = double.MaxValue, bestSimd = double.MaxValue;
        for (int rep = 0; rep < 3; rep++)
        {
            bestScalar = Math.Min(bestScalar, Render(v, false, 192, 144, 20000).Ms);
            bestSimd = Math.Min(bestSimd, Render(v, true, 192, 144, 20000).Ms);
        }
        output.WriteLine($"192x144 @ 3e47, 20000 iter: scalar {bestScalar:F0} ms, simd {bestSimd:F0} ms ({bestScalar / bestSimd:F2}x)");
        // Loose: the full suite runs other work in parallel. The speedup is reported, not pinned.
        Assert.True(bestSimd < bestScalar * 1.1, $"SIMD rebasing {bestSimd:F0} ms vs scalar {bestScalar:F0} ms");
    }
}
