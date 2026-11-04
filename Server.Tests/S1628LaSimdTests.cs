// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1628 (epic #1623, S5) — the 4-lane AVX2 LA evaluator (MandelbrotCalculator.La4.cs;
// method from Imagina's HInfLAEvaluator, GitHub user 5E-324, AGPL-3.0, see the
// bibliography #imagina). Its contract is the #1274 one: byte-identical to the scalar
// evaluator, every buffer the frame writes, on the bench-kit scenes (fractadyne
// bench-kit, WindySnowOwl, https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit,
// from rith's FF 0.7.1 benchmark report; see the bibliography), with the AT on and off,
// through the #1344 pixel mask and in a #1330 lattice-aligned sub-rect. The scalar
// evaluator's accuracy against exact iteration is S1626 / S1627's.
// Mutants (run by hand against this file, all caught): see the design doc §4.4.

using System;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1628LaSimdTests(ITestOutputHelper output)
{
    private const int W = 61, H = 37;   // odd sizes: rows that do not fill the lanes

    private sealed record Scene(string Name, string X, string Y, double KfZoom, int Iter);

    // Centres from the kit's .kfr files (as Probes/DeepCostProbe.cs).
    private static readonly Scene[] All =
    {
        new("p448",
            "-0.7145519151951202|-2.0790192891565988e-17|1.1753166641509792e-33|-8.102457136962753e-50|-1.0894092006534645e-66|-5.481175082318642e-83|-1.5340609125118276e-99|1.4285946495395532e-116",
            "-0.35402073332318235|2.5477179573089096e-17|-6.072997541261718e-34|-1.8911088656798612e-50|-4.820603700098655e-67|-3.1189999875481587e-83|1.2339668272603997e-99|-9.480108552256602e-117",
            5.176068e33, 13440),
        new("path",
            "-0.028041054305504548|1.3292199503012176e-18|-5.27591145479967e-35|2.798737894555097e-51|-1.4491927624390092e-67|-6.233008271562717e-84|4.7734689309385144e-101|2.9022031733306904e-117",
            "-0.6948927538996524|5.189489838305484e-17|5.567870094609155e-34|1.7818449158374648e-50|1.8079413574553354e-68|2.5201987988615068e-85|-7.246511953022119e-102|-6.412330137037785e-118",
            1e33, 2859),
        new("p953",
            "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.203746728154813e-51|-4.269596053996866e-68|1.9389949392385534e-84|6.25819023760954e-101|-2.6646795788290983e-117",
            "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3771597857226995e-50|-5.482812918750436e-67|4.692321383846679e-84|-2.313052339214723e-100|2.1139231852108862e-116",
            1.342765e53, 28590),
        new("p15248",
            "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.198613498328184e-51|-2.6338819725348693e-68|-1.2531186861084395e-84|8.265175494285413e-101|-2.8428099960154152e-117",
            "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3770883977626e-50|-1.6692666809801752e-67|-4.583534842942509e-84|4.2910480785722876e-100|-1.016687227891797e-116",
            2.142891e57, 457440),
        new("p121984",
            "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.2016807295614754e-51|1.7176952655143277e-67|1.0180672183123456e-83|-1.5782574900160777e-100|8.451632690787226e-117",
            "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3766027088999295e-50|9.932823572218873e-67|2.5207480153941874e-83|-8.913322115858611e-100|2.182658649920256e-116",
            1.682674e66, 3659520),
    };

    public static TheoryData<string> Names => new() { "p448", "path", "p953", "p15248", "p121984" };

    private static MandelbrotCalculator Calc(Scene s, bool simd, bool at = true, Action<MandelbrotCalculator>? cfg = null)
    {
        double[] X = s.X.Split('|').Select(double.Parse).ToArray(), Y = s.Y.Split('|').Select(double.Parse).ToArray();
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3], CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
            CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3], CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
            Zoom = s.KfZoom * 3.5 * H / (4.0 * Math.Max(W, H)), MaxIterations = s.Iter,
            ColorMap = new HsvPalette(), UseLaEvaluator = true, UseLaAt = at, LaAtLoosen = 1.0, LaAtOffsetBits = 0,
            LaClimbOnRebase = true, LaTailBla = false, LaPeriodTest = LaPeriodTest.ThresholdDip, LaSimd = simd,
        };
        cfg?.Invoke(c);
        c.Calculate();
        return c;
    }

    // Every buffer the frame writes, at every pixel — or, with a #1344 mask, at the marked
    // pixels only: unmarked pixels keep whatever their (uninitialised) buffers held.
    private static void AssertSameBytes(MandelbrotCalculator a, MandelbrotCalculator b, string what, byte[]? mask = null)
    {
        int n = a.IterationBuffer.Length, bad = 0, first = -1;
        for (int i = 0; i < n; i++)
        {
            if (mask != null && mask[i] == 0) continue;
            bool same = a.IterationBuffer[i] == b.IterationBuffer[i]
                && BitConverter.SingleToInt32Bits(a.SmoothBuffer[i]) == BitConverter.SingleToInt32Bits(b.SmoothBuffer[i])
                && BitConverter.SingleToInt32Bits(a.DistanceBuffer[i]) == BitConverter.SingleToInt32Bits(b.DistanceBuffer[i])
                && BitConverter.SingleToInt32Bits(a.NormalXBuffer[i]) == BitConverter.SingleToInt32Bits(b.NormalXBuffer[i])
                && BitConverter.SingleToInt32Bits(a.NormalYBuffer[i]) == BitConverter.SingleToInt32Bits(b.NormalYBuffer[i])
                && BitConverter.SingleToInt32Bits(a.FinalZrBuffer[i]) == BitConverter.SingleToInt32Bits(b.FinalZrBuffer[i])
                && BitConverter.SingleToInt32Bits(a.FinalZiBuffer[i]) == BitConverter.SingleToInt32Bits(b.FinalZiBuffer[i])
                && BitConverter.SingleToInt32Bits(a.FinalDrBuffer[i]) == BitConverter.SingleToInt32Bits(b.FinalDrBuffer[i])
                && BitConverter.SingleToInt32Bits(a.FinalDiBuffer[i]) == BitConverter.SingleToInt32Bits(b.FinalDiBuffer[i])
                && a.ColorBuffer[i] == b.ColorBuffer[i];
            if (!same) { bad++; if (first < 0) first = i; }
        }
        Assert.True(bad == 0, $"{what}: {bad} px differ (first at {first})");
        Assert.Equal(a.LastLaWork, b.LastLaWork);
        Assert.Equal(a.LastLaAtWork, b.LastLaAtWork);
        Assert.Equal(a.LastLaStageSteps, b.LastLaStageSteps);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Simd_Is_Byte_Identical_To_Scalar(string name)
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 not available");
        var s = All.Single(x => x.Name == name);
        foreach (bool at in new[] { true, false })
        {
            var v = Calc(s, simd: true, at: at);
            var sc = Calc(s, simd: false, at: at);
            Assert.True(v.LastFrameUsedLa && v.LastFrameUsedLaSimd, "precondition: the 4-lane path ran");
            Assert.True(sc.LastFrameUsedLa && !sc.LastFrameUsedLaSimd, "precondition: the scalar path ran");
            var w = v.LastLaWork;
            output.WriteLine($"{name} AT {at}: {w.Pixels} px, LA {w.LaSteps / (double)w.Pixels:F1} + plain {w.PlainSteps / (double)w.Pixels:F1} steps/px, AT px {v.LastLaAtWork.Pixels}");
            AssertSameBytes(v, sc, $"{name} AT {at}");
        }
    }

    [Fact]
    public void Simd_Is_Byte_Identical_Through_The_Pixel_Mask_And_A_Sub_Rect()
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 not available");
        var s = All.Single(x => x.Name == "p953");
        // #1344 — a sparse mask (every third pixel, offset per row).
        var mask = new byte[W * H];
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)((i + i / W) % 3 == 0 ? 1 : 0);
        void Masked(MandelbrotCalculator c) => c.PixelMask = mask;
        var v = Calc(s, simd: true, cfg: Masked);
        var sc = Calc(s, simd: false, cfg: Masked);
        Assert.True(v.LastFrameUsedLaSimd);
        AssertSameBytes(v, sc, "masked", mask);
        // D-6b / #1330 — a tile of a larger image (sub-rect geometry).
        void Tile(MandelbrotCalculator c) { c.ImageWidth = 3 * W; c.ImageHeight = 2 * H; c.SubRectOffsetX = W; c.SubRectOffsetY = H / 2; }
        var tv = Calc(s, simd: true, cfg: Tile);
        var ts = Calc(s, simd: false, cfg: Tile);
        Assert.True(tv.LastFrameUsedLaSimd);
        AssertSameBytes(tv, ts, "sub-rect");
    }

    [Fact]
    public void Off_Or_Unsupported_Options_Keep_The_Scalar_Evaluator()
    {
        var s = All.Single(x => x.Name == "p953");
        Assert.False(Calc(s, simd: false).LastFrameUsedLaSimd);
        Assert.False(Calc(s, simd: true, cfg: c => c.LaTailBla = true).LastFrameUsedLaSimd);
        Assert.False(Calc(s, simd: true, cfg: c => c.LaClimbOnRebase = false).LastFrameUsedLaSimd);
    }
}
