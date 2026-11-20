// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1960 — the alt calculators' AA (Supersampling.AccumulateAlt) jittered only the double CenterX /
// CenterY: past ~1e13-1e14 the sub-pixel offset rounded to a whole ulp or to 0, so the samples were
// off by pixels or all the same image (no AA — the "grainy" deep Multibrot). For an IDeepCentre
// calculator it now moves the full centre (limbs and exact centre). Checks:
//   • a 4×4 supersampled EscapeTimeCalculator Multibrot frame at 1e16 equals an independent oracle: one
//     render at 4× the resolution (the same zoom: its pixel is a quarter), its centre moved −0.375 px
//     exactly, box-averaged per 4×4 cell, with the cell AccumulateAlt skips taken from the plain frame
//     (its base sample, as GridOffsets leaves out the middle cell). Precondition: the frame differs from
//     the plain one (AA did something);
//   • the jitter moves the full centre by exactly j pixels (limbs and exact centre) and restores it.
// Mutants (by hand): the old jitter (double CenterX / CenterY only) — caught by both: the frame equals the
// plain one (0 pixels differ: every offset rounded to 0, the reported loss of AA) and 87 of 1536 pixels
// miss the 4× render; the moved centre is not j pixels away.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;

namespace FracturingFog.Server.Tests;

public sealed class S1960AltAaJitterTests(ITestOutputHelper output)
{
    private const int Zl = 16, It = 2000;

    private static Step Cube => (a, b, cr, ci, p) =>
    {
        var a2 = Sq(a, p); var b2 = Sq(b, p);
        var re = BigFloat.Sub(BigFloat.Mul(a2, a, p), BigFloat.Mul(BigFloat.FromDouble(3, p), BigFloat.Mul(a, b2, p), p), p);
        var im = BigFloat.Sub(BigFloat.Mul(BigFloat.FromDouble(3, p), BigFloat.Mul(a2, b, p), p), BigFloat.Mul(b2, b, p), p);
        return (BigFloat.Add(re, cr, p), BigFloat.Add(im, ci, p));
    };

    private static EscapeTimeCalculator Calc(int w, int h, ExactCentre c)
    {
        var calc = new EscapeTimeCalculator(w, h)
        {
            FractalType = FractalType.Multibrot, Quality = QualityPreset.Ultra, MaxIterations = It, Zoom = Math.Pow(10, Zl),
            ColorMap = ColorPalette.BuiltIns[0], FractalParameters = new FractalParameters { MultibrotExponent = 3, AdaptiveAa = false },
        };
        calc.SetDeepCentre(c.ToDeep(), c);
        return calc;
    }

    private static int Ch(uint c, int k) => (int)((c >> (8 * k)) & 0xFF);

    [Fact]
    public void A_Supersampled_Deep_Frame_Equals_A_Box_Averaged_Four_Times_Larger_Render()
    {
        // z³ + c's Misiurewicz point -0.2303 + 1.3276i (S1921), a third / a fifth of a pixel off. |Im| ≈ 1.33:
        // ulp 2.2e-16 against a pixel of 7.3e-18 at 1e16 — the old jitter rounded every offset to 0.
        double s = 3.5 / W / Math.Pow(10, Zl);
        var c = Off(Misiurewicz(Cube, -0.23026540906830995, 1.3276480964335278, Zl), s, Zl);

        var plain = Calc(W, H, c);
        plain.Calculate(CancellationToken.None);
        var basePx = plain.ColorBuffer.Take(W * H).ToArray();

        var aa = Calc(W, H, c);
        aa.Calculate(CancellationToken.None);
        Supersampling.AccumulateAlt(aa, 16, CancellationToken.None);
        var got = aa.ColorBuffer.Take(W * H).ToArray();
        Assert.True(aa.CenterExact is { } back && back.Equals(c), "the centre is restored");

        int pl = BigFloat.PrecisionForZoom(Zl + 2);
        var shifted = c.TranslatePixels(-0.375, -0.375, s, Math.Log10(s), pl);
        var big = Calc(4 * W, 4 * H, shifted);
        big.Calculate(CancellationToken.None);
        var hi = big.ColorBuffer;

        int off = 0, differs = 0, n = W * H;
        string ex = "";
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var sum = new int[4];
                for (int l = 0; l < 4; l++)
                    for (int k = 0; k < 4; k++)
                    {
                        uint px = k == 2 && l == 2 ? basePx[y * W + x] : hi[(4 * y + l) * 4 * W + 4 * x + k];
                        for (int ch = 0; ch < 4; ch++) sum[ch] += Ch(px, ch);
                    }
                uint g = got[y * W + x];
                bool bad = false;
                for (int ch = 0; ch < 4; ch++) bad |= Math.Abs((sum[ch] + 8) / 16 - Ch(g, ch)) > 1;
                if (bad) { off++; if (ex.Length < 200) ex += $"({x},{y}) "; }
                if (g != basePx[y * W + x]) differs++;
            }
        output.WriteLine($"1e{Zl}: {n - off}/{n} pixels match the 4x render; {differs} differ from the plain frame");
        Assert.True(differs > n / 10, $"precondition: AA changed the frame ({differs} pixels)");
        Assert.True(off <= n / 100, $"{off} pixels differ from the box-averaged 4x render: {ex}");
    }

    [Fact]
    public void The_Jitter_Moves_The_Full_Centre_By_Exactly_J_Pixels()
    {
        double s = 3.5 / W / Math.Pow(10, Zl);
        var c = new ExactCentre(BigFloat.FromDouble(-0.2302654090683099, 200).ScaleB(0), BigFloat.FromDouble(1.3276480964335278, 200));
        var calc = Calc(W, H, c);
        var j = new Supersampling.AltJitter(calc);
        j.Apply(0.375, -0.125);
        int p = 300;
        var moved = ExactCentre.FromDeep(calc.GetDeepCentre(), p);
        double dx = BigFloat.Sub(moved.Re, c.Re.WithPrecision(p), p).ToDouble() / s;
        double dy = BigFloat.Sub(moved.Im, c.Im.WithPrecision(p), p).ToDouble() / s;
        Assert.Equal(0.375, dx, 9);
        Assert.Equal(-0.125, dy, 9);
        var e = Assert.NotNull(calc.CenterExact);
        Assert.Equal(0.375, BigFloat.Sub(e.Re, c.Re.WithPrecision(p), p).ToDouble() / s, 9);
        j.Restore();
        Assert.True(ExactCentre.SameLimbs(c.ToDeep(), calc.GetDeepCentre()));
        Assert.Equal(c, calc.CenterExact);
    }
}
