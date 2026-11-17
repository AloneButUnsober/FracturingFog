// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1864 (epic #1428, C11) — end-to-end validation past 1e100 against theory, not against
// FF's own pipeline: a minibrot is a small copy of the whole set. Near its nucleus the
// atom-size map c = nucleus + Size·c′ carries the main set M (c′) onto the minibrot, and
// p iterations at c act like one at c′ (p = the period). So the deep render's in-set
// mask, framed through that map, must match M's own mask at the home framing.
//
// The minibrots are the real-axis "antenna" ones next to c = −2 (period n, the root of
// z_n(c) = 0 nearest −2; Size > 0, so no rotation). Their sizes fall about 16× per
// period: n = 125 / 250 / 415 put them at ~1e-149 / ~1e-299 / ~1e-498, i.e. the AP
// reference orbit (past 1e110), floatexp pixels (past 1e290) and a zoom past double.
// The nucleus is found by Newton in BigFloat and the size by the atom-domain estimate
// (NucleusFinder.AtomSize's recurrence, Claude Heiland-Allen — see the bibliography),
// both here in BigFloat, independent of the renderer. Checked:
//   • The deep mask matches M's (intersection over union ≥ 0.9; the masks differ only on
//     boundary pixels, where the iteration budgets and the copy's slight distortion differ).
//   • Past OD (n = 250, 415) the exact centre is load-bearing: with the centre rounded to
//     OD the minibrot is far outside the frame (IoU ≈ 0).

using System;
using System.Threading;
using FracturingFog.Calculators;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1864DeepMinibrotMaskTests(ITestOutputHelper output)
{
    private const int W = 48, H = 36, K = 100;   // K: the main set's iteration budget

    // The period-n nucleus nearest −2: Newton on z_n(c) = 0 from c = −2 (real).
    private static BigFloat Nucleus(int n, int prec)
    {
        var c = BigFloat.FromDouble(-2, prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int step = 0; step < 40; step++)
        {
            var z = BigFloat.Zero(prec); var dz = BigFloat.Zero(prec);
            for (int k = 0; k < n; k++)
            {
                dz = BigFloat.Add(BigFloat.Mul(z, dz, prec).ScaleB(1), one, prec);
                z = BigFloat.Add(BigFloat.Mul(z, z, prec), c, prec);
            }
            c = BigFloat.Sub(c, BigFloat.Div(z, dz, prec), prec);
        }
        return c;
    }

    // Atom size: z = 0, l = 1, b = 1; for q = 1..n−1 { z = z² + c; l = 2z·l; b += 1/l }; 1/(b·l²).
    private static BigFloat Size(BigFloat c, int n, int prec)
    {
        var z = BigFloat.Zero(prec); var l = BigFloat.FromDouble(1, prec); var b = BigFloat.FromDouble(1, prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int q = 1; q < n; q++)
        {
            z = BigFloat.Add(BigFloat.Mul(z, z, prec), c, prec);
            l = BigFloat.Mul(z, l, prec).ScaleB(1);
            b = BigFloat.Add(b, BigFloat.Div(one, l, prec), prec);
        }
        return BigFloat.Div(one, BigFloat.Mul(b, BigFloat.Mul(l, l, prec), prec), prec);
    }

    private static bool[] Mask(MandelbrotCalculator calc, int maxIter)
    {
        calc.Calculate(CancellationToken.None);
        var m = new bool[W * H];
        for (int i = 0; i < m.Length; i++) m[i] = calc.IterationBuffer[i] >= maxIter;
        return m;
    }

    private static double IoU(bool[] a, bool[] b)
    {
        int inter = 0, uni = 0;
        for (int i = 0; i < a.Length; i++) { if (a[i] && b[i]) inter++; if (a[i] || b[i]) uni++; }
        return uni == 0 ? 0 : (double)inter / uni;
    }

    private static int Count(bool[] a) { int n = 0; foreach (bool v in a) if (v) n++; return n; }

    private static MandelbrotCalculator Calc(DeepComplex centre, int maxIter) => new(W, H)
    {
        CenterX = centre.Re.X0, CenterXLo = centre.Re.X1, CenterX2 = centre.Re.X2, CenterX3 = centre.Re.X3,
        CenterX4 = centre.Re.X4, CenterX5 = centre.Re.X5, CenterX6 = centre.Re.X6, CenterX7 = centre.Re.X7,
        CenterY = centre.Im.X0, CenterYLo = centre.Im.X1, CenterY2 = centre.Im.X2, CenterY3 = centre.Im.X3,
        CenterY4 = centre.Im.X4, CenterY5 = centre.Im.X5, CenterY6 = centre.Im.X6, CenterY7 = centre.Im.X7,
        MaxIterations = maxIter, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Unbounded,
    };

    [Theory]
    [InlineData(125)]   // ~1e149: AP reference orbit, double pixels
    [InlineData(250)]   // ~1e299: exact centre past OD, floatexp pixels
    [InlineData(415)]   // ~1e498: and the zoom past double
    public void A_Deep_Antenna_Minibrot_Has_The_Main_Sets_Shape(int n)
    {
        // The main set at the home framing (centre −0.5, zoom 1).
        var home = Calc(new DeepComplex(-0.5, 0), K);
        home.Zoom = 1;
        var m = Mask(home, K);
        Assert.InRange(Count(m), W * H / 10, W * H / 2);

        // The minibrot, framed through c = nucleus + Size·c′ (precision for ~1e500 + guard).
        int prec = BigFloat.PrecisionForZoom(1.25 * n + 10) + 64;
        var c = Nucleus(n, prec);
        var size = Size(c, n, prec);
        Assert.True(size.Sign > 0, "an antenna minibrot near −2 is not rotated");
        double zl = -size.ToFloatExp().Log2Abs() / Math.Log2(10);   // zoom 1/Size
        Assert.InRange(zl, 1.17 * n, 1.21 * n);   // the depth this n was picked for
        var centreRe = BigFloat.Sub(c, size.ScaleB(-1), prec);      // nucleus + Size·(−0.5)
        var exact = new ExactCentre(centreRe, BigFloat.Zero(prec));

        int deepIter = n * K;
        var deep = Calc(exact.ToDeep(), deepIter);
        deep.Zoom = ZoomLog.FromLog10(zl);   // as the live host: Zoom (saturated past double), then SetDepth
        deep.SetDepth(exact, zl);
        var dm = Mask(deep, deepIter);
        double iou = IoU(m, dm);
        output.WriteLine($"n={n} 1e{zl:F1}: IoU {iou:F3}, in-set {Count(dm)} vs M {Count(m)}");
        Assert.True(iou >= 0.9, $"n={n} (1e{zl:F1}): IoU {iou:F3}, in-set {Count(dm)} vs M {Count(m)}");

        // Past OD the exact centre carries it: rounded to OD, the minibrot leaves the frame.
        if (n >= 250)
        {
            var rounded = Calc(exact.ToDeep(), deepIter);
            rounded.Zoom = ZoomLog.FromLog10(zl);
            rounded.SetDepth(null, zl);
            var rm = Mask(rounded, deepIter);
            output.WriteLine($"  OD-rounded centre: IoU {IoU(m, rm):F3}, in-set {Count(rm)}");
            Assert.True(IoU(m, rm) < 0.2, $"n={n}: the OD-rounded centre still frames the minibrot (IoU {IoU(m, rm):F3})");
        }
    }
}
