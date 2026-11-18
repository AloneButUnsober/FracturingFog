// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1889 (epic #1428, C5h) — the float-exp LA evaluator's plain tail in double where that is
// exact (MandelbrotCalculator.TailInDouble). Checked:
//   • byte-identical frames with it on and off (iteration, smooth, colour, distance and
//     normal buffers) on: S1886's antenna minibrots of periods 250 (~1e299) and 415
//     (~1e498) at their home framing and p250 zoomed out 10^13 (forced onto the float-exp
//     rows), S1842's c = i at 1e400 and 1e1000 and its OD-tail centre at 1e600, a pixel
//     jitter (#1851), a sub-rect tile (D-6b), and the dense M(4,1) centre (S1880) at 1e320 and
//     1e600, whose long plain tails rebase and climb back; and two of them with climbing off
//     (LaClimbOnRebase), where the tail rebases in place and runs long enough to
//     renormalise the derivative;
//   • it carries the tail: on the minibrots nearly all plain steps run in double, and with
//     it off none do; on c = i at 1e1000 (dc far below double) too.
// Speed is measured and reported (the rows' time, best of 2), not asserted.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes the static FxTailInDouble
public sealed class S1889FxTailInDoubleTests(ITestOutputHelper output)
{
    private const int W = 64, H = 36;

    private static MandelbrotCalculator Make(ExactCentre e, double zl, int maxIter, double fxAt = 290,
        (double, double) jitter = default, int subW = 0, bool climb = true)
    {
        var d = e.ToDeep();
        var c = new MandelbrotCalculator(subW > 0 ? subW : W, subW > 0 ? H / 2 : H)
        {
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
            MaxIterations = maxIter, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Unbounded,
            FloatExpZoomLog10 = fxAt, LaClimbOnRebase = climb,
        };
        if (subW > 0) { c.ImageWidth = W; c.ImageHeight = H; c.SubRectOffsetX = 9; c.SubRectOffsetY = 7; }
        c.SetZoomLog10(zl);
        c.SetDepth(e, zl);
        c.PixelJitter = jitter;
        return c;
    }

    // S1842's views: c = i, or a centre near it whose offsets OD can't hold.
    private static ExactCentre NearI(int zl, bool tail)
    {
        int prec = BigFloat.PrecisionForZoom(zl);
        var re = tail ? BigFloat.Parse("3.1415926535e-" + (zl - 40), prec) : BigFloat.Zero(prec);
        var im = tail ? BigFloat.Add(BigFloat.FromDouble(1, prec), BigFloat.Parse("2.7182818284e-" + (zl - 45), prec), prec)
                      : BigFloat.FromDouble(1, prec);
        return new ExactCentre(re, im);
    }

    private const string Mx = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";
    private const string My = "0.9562865108091415007710960577299774358098333365105291700343143215005246590657167325269784107873398072043444724926469284366752406567465722656200815719741551313831228054884443810571677870203738395055477279309548311953190385170024877917670113105649462054553485859930615004427397447762045980085973915977603864276670744042253335354323566859404712";

    private static ExactCentre M41()
    {
        Assert.True(ExactCentre.TryParse(Mx, My, out var c));
        return c;
    }

    public static TheoryData<string> Scenes => new() { "p250", "p415", "p250-out", "i400", "i1000", "tail600", "jitter", "subrect", "m41-320", "m41-600",
        "p250-noclimb", "m41-320-noclimb" };

    private static (ExactCentre E, double Zl, int MaxIt, double FxAt, (double, double) J, int SubW) Scene(string s) => s.Replace("-noclimb", "") switch
    {
        "p250" => From(S1886LaAtFloatExpTests.Minibrot(250), 25000),
        "p415" => From(S1886LaAtFloatExpTests.Minibrot(415), 41500),
        "p250-out" => From(S1886LaAtFloatExpTests.Minibrot(250, -0.5, -13), 25000, 200),
        "i400" => (NearI(400, false), 400, 600 + 4 * 400, 290, default, 0),
        "i1000" => (NearI(1000, false), 1000, 600 + 4 * 1000, 290, default, 0),
        "tail600" => (NearI(600, true), 600, 600 + 4 * 600, 290, default, 0),
        "jitter" => From(S1886LaAtFloatExpTests.Minibrot(250), 25000, 290, (0.37, -0.29)),
        // The dense M(4,1) centre (S1880): long plain tails that rebase and climb.
        "m41-320" => (M41(), 320, 1000 + 12 * 320, 290, default, 0),
        "m41-600" => (M41(), 600, 1000 + 12 * 600, 290, default, 0),
        _ => From(S1886LaAtFloatExpTests.Minibrot(250), 25000, 290, default, 40),
    };

    private static (ExactCentre, double, int, double, (double, double), int) From(
        (ExactCentre C, double Z, int P) m, int maxIt, double fxAt = 290, (double, double) j = default, int subW = 0)
        => (m.C, m.Z, maxIt, fxAt, j, subW);

    [Theory]
    [MemberData(nameof(Scenes))]
    public void The_Double_Tail_Renders_The_Same_Frame(string name)
    {
        var (e, zl, maxIt, fxAt, j, subW) = Scene(name);
        bool climb = !name.EndsWith("-noclimb");   // without climbing back the tail rebases in place: long tails
        bool saved = MandelbrotCalculator.FxTailInDouble;
        try
        {
            MandelbrotCalculator Frame(bool dbl)
            {
                MandelbrotCalculator.FxTailInDouble = dbl;
                var c = Make(e, zl, maxIt, fxAt, j, subW, climb);
                c.Calculate(CancellationToken.None);
                return c;
            }
            var off = Frame(false);
            var on = Frame(true);
            Assert.Contains("LA FX", on.LastPrecisionLabel);
            int n = on.Width * on.Height;
            Assert.Equal(off.IterationBuffer.Take(n), on.IterationBuffer.Take(n));
            Assert.Equal(off.SmoothBuffer.Take(n), on.SmoothBuffer.Take(n));
            Assert.Equal(off.ColorBuffer.Take(n), on.ColorBuffer.Take(n));
            Assert.Equal(off.DistanceBuffer.Take(n), on.DistanceBuffer.Take(n));
            Assert.Equal(off.NormalXBuffer.Take(n), on.NormalXBuffer.Take(n));
            Assert.Equal(off.NormalYBuffer.Take(n), on.NormalYBuffer.Take(n));
            Assert.Contains(on.IterationBuffer.Take(n), v => v < maxIt);

            long plain = on.LastLaWork.PlainSteps, dbl = on.LastFxDoubleSteps;
            Assert.Equal(0, off.LastFxDoubleSteps);
            double Rows(bool d) { MandelbrotCalculator.FxTailInDouble = d; var c = Make(e, zl, maxIt, fxAt, j, subW, climb); c.Calculate(CancellationToken.None);
                double best = 1e9; for (int r = 0; r < 2; r++) { c.Calculate(CancellationToken.None); best = Math.Min(best, c.LastHpStageMs.Pixels); } return best; }
            double tOff = Rows(false), tOn = Rows(true);
            var ev = on.LastFxDoubleTailEvents;
            output.WriteLine($"{name} 1e{zl:F1} [{on.LastPrecisionLabel}]: plain {plain:N0}, in double {dbl:N0} ({100.0 * dbl / Math.Max(plain, 1):F1} %), climbs {ev.Climbs}, renorms {ev.Renorms}; " +
                             $"rows {tOn:F1} ms vs {tOff:F1} ms (x{tOff / tOn:F2})");
            if (plain > 0) Assert.True(dbl * 10 >= plain * 9, $"{name}: {dbl} of {plain} plain steps in double");
        }
        finally { MandelbrotCalculator.FxTailInDouble = saved; }
    }
}
