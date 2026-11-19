// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1425 — "deep-zoom renders occasionally fuzzy at OD depth (Julia morph / Descend)". The
// issue's second suspect: every MSAA / TAA sample jitters the centre (CentreJitter, #1270)
// and since #1427 reuses the reference orbit built at the unjittered centre; at a morph
// view the minibrot is far below a pixel, so a wrong jitter or a wrong recycle offset
// would smear the samples into a blur. Checked against an independent truth: the same
// view rendered at 4× the resolution without anti-aliasing and box-filtered down 4×4.
// The view is three Julia-morph steps from the p448 minibrot (JuliaMorph.Plan at α = ½,
// each target the highest-count pixel off-centre, as a click would pick), at 8e70: OD
// with LA. The Extreme tier's anti-aliasing (4×4 MSAA, then 32 TAA samples, as the live
// view settles; Supersampling.AccumulateQuality) must
//   • land much closer to the truth than the single sample does, and
//   • keep the truth's sharpness: its mean neighbour difference within 10 % of the truth's
//     (a blur lowers it, a no-op AA leaves the single sample's much higher one).
// Measured (240×135): from the truth the single sample is 72.3 and AA 37.5; sharpness
// truth 51.8, single 96.7, AA 49.4. The same with the orbit reuse off (bit-identical).
// Mutants (by hand), all caught:
//   • the jitter doubled (CentreJitter.Apply, 2·j): sharpness 31.0, a blur. The distance
//     alone (34.3) would miss it, hence the sharpness bar;
//   • the jitter dropped (AA a no-op): 72.3 / 96.7, as the single sample;
//   • the recycle offset zeroed (TryRecycleReferenceOrbit): fails the reuse case only, as
//     the single sample; the fresh-orbit case passes.
// So the AA pass does not blur a deep morph view. A morph view's exterior counts change
// faster than a pixel, so with a fast-cycling palette (HSV here) the AA mean of the
// sub-pixel colours is a soft grey haze around the layers: the truth looks the same.

using System;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes UseGpuPerturbation / AaReuseReferenceOrbit
public sealed class S1425DeepMorphAaTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 240, H = 135, Up = 4;
    private readonly bool _gp = MandelbrotCalculator.UseGpuPerturbation, _reuse = MandelbrotCalculator.AaReuseReferenceOrbit;

    public void Dispose()
    {
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.AaReuseReferenceOrbit = _reuse;
    }

    // p448 minibrot → three morph steps (periods 1392, 1840, 3680).
    private static readonly double[] Re = [-0.7145519151951202, -2.0790192891565584E-17, 7.523290020591929E-34, -1.2095994997567973E-50,
        -5.348459495909179E-67, 1.151967365619671E-84, 4.3961339160163316E-101, 1.0189990753068562E-117];
    private static readonly double[] Im = [-0.35402073332318235, 2.5477179573090153E-17, 1.0745601090002337E-33, 3.1395945722365525E-50,
        -1.6360342728039118E-66, -1.040521114636281E-83, 6.757240529090947E-101, -3.91736143307257E-117];
    private const double Zoom = 7.979179450904242E+70;
    private const int MaxIter = 368000;

    private static MandelbrotCalculator Calc(int w, int h)
    {
        var c = new MandelbrotCalculator(w, h)
        {
            Quality = QualityPreset.Extreme, Zoom = Zoom, MaxIterations = MaxIter, ColorMap = new HsvPalette(),
            CenterX = Re[0], CenterXLo = Re[1], CenterX2 = Re[2], CenterX3 = Re[3],
            CenterX4 = Re[4], CenterX5 = Re[5], CenterX6 = Re[6], CenterX7 = Re[7],
            CenterY = Im[0], CenterYLo = Im[1], CenterY2 = Im[2], CenterY3 = Im[3],
            CenterY4 = Im[4], CenterY5 = Im[5], CenterY6 = Im[6], CenterY7 = Im[7],
        };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static int Ch(uint p, int sh) => (int)((p >> sh) & 255);

    // Mean absolute channel difference.
    private static double Mad(uint[] a, uint[] b)
    {
        long s = 0;
        for (int i = 0; i < W * H; i++)
            for (int sh = 0; sh <= 16; sh += 8) s += Math.Abs(Ch(a[i], sh) - Ch(b[i], sh));
        return s / (3.0 * W * H);
    }

    // Sharpness: mean absolute difference to the left and upper neighbours.
    private static double Sharpness(uint[] p)
    {
        double e = 0;
        for (int y = 1; y < H; y++)
            for (int x = 1; x < W; x++)
                for (int sh = 0; sh <= 16; sh += 8)
                {
                    int c = Ch(p[y * W + x], sh);
                    e += Math.Abs(c - Ch(p[y * W + x - 1], sh)) + Math.Abs(c - Ch(p[(y - 1) * W + x], sh));
                }
        return e / (6.0 * (W - 1) * (H - 1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Anti_Aliasing_At_A_Deep_Morph_View_Matches_A_Supersampled_Truth(bool reuseOrbit)
    {
        MandelbrotCalculator.UseGpuPerturbation = false;
        MandelbrotCalculator.AaReuseReferenceOrbit = reuseOrbit;

        var hi = Calc(Up * W, Up * H);
        Assert.Contains("OD", hi.LastPrecisionLabel);   // precondition: the OD tier
        var truth = new uint[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int sy = 0; sy < Up; sy++)
                    for (int sx = 0; sx < Up; sx++)
                    {
                        uint p = hi.ColorBuffer[(Up * y + sy) * Up * W + Up * x + sx];
                        r += Ch(p, 16); g += Ch(p, 8); b += Ch(p, 0);
                    }
                truth[y * W + x] = 0xFF000000u | (uint)(r / (Up * Up)) << 16 | (uint)(g / (Up * Up)) << 8 | (uint)(b / (Up * Up));
            }

        var single = Calc(W, H);
        var aa = Calc(W, H);
        var st = Supersampling.AccumulateQuality(aa, QualityPreset.Extreme, AdaptiveAa.Off, 0, temporal: true, CancellationToken.None);
        Assert.True(st.TemporalSamples > 1, $"precondition: the TAA samples ran ({st.TemporalSamples})");

        double madSingle = Mad(single.ColorBuffer, truth), madAa = Mad(aa.ColorBuffer, truth);
        double sTruth = Sharpness(truth), sSingle = Sharpness(single.ColorBuffer), sAa = Sharpness(aa.ColorBuffer);
        output.WriteLine($"{aa.LastPrecisionLabel}, reuse {reuseOrbit}: distance to the truth single {madSingle:F1}, AA {madAa:F1}; "
                       + $"sharpness truth {sTruth:F1}, single {sSingle:F1}, AA {sAa:F1}");
        Assert.True(madAa <= 0.7 * madSingle, $"AA {madAa:F1} from the truth, the single sample {madSingle:F1}");
        Assert.True(Math.Abs(sAa - sTruth) <= 0.1 * sTruth, $"AA sharpness {sAa:F1}, the truth's {sTruth:F1}");
    }
}
