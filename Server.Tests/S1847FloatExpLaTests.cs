// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1847 (epic #1428, C5c) — the floatexp LA stage table and evaluator. Checked:
//   • Where double holds every number, LaStageTableFx builds the double LaStageTable's
//     stages: the same stage count, step lengths, next indices and references, and A, B,
//     Thr, ThrC within 1e-12 — for several reference orbits, both period tests.
//   • Past double (c = i, 4096 steps) the double table is not finite and the floatexp one
//     is, holding A and B past 1e308.
//   • In frames past double (1e400, 1e1000 at c = i) the FX rows run LA by default (Auto),
//     its work (LA steps + plain steps) is at most a third of the plain loop's, and every
//     pixel's count stays the plain loop's — S1842's direct-BigFloat count.
//   • On S1842's minibrot frame (where double holds), the floatexp evaluator forced on and
//     the double LA evaluator take the same steps: LA steps within 0.5 %, plain steps
//     within 0.5 %, counts equal on ≥ 99 % of pixels.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1847FloatExpLaTests
{
    private static (double[] Zr, double[] Zi, int Len) Orbit(double cr, double ci, int n)
    {
        var zr = new double[n + 2]; var zi = new double[n + 2];
        double a = 0, b = 0; int len = n;
        for (int k = 0; k <= n; k++)
        {
            zr[k] = a; zi[k] = b;
            if (a * a + b * b > 4) { len = k; break; }
            double t = a * a - b * b + cr; b = 2 * a * b + ci; a = t;
        }
        return (zr, zi, len);
    }

    private static void Rel(double want, FloatExp got, double tol, string what)
    {
        double g = got.ToDouble();
        Assert.True(Math.Abs(g - want) <= tol * Math.Max(Math.Abs(want), 1e-300), $"{what}: {g:R} vs {want:R}");
    }

    [Theory]
    [InlineData(0.0, 1.0, 256, LaPeriodTest.ThresholdDip)]
    [InlineData(0.0, 1.0, 256, LaPeriodTest.MinMagnitude)]
    [InlineData(-0.1225611668766536, 0.7448617666197446, 3000, LaPeriodTest.ThresholdDip)]   // near the period-3 nucleus
    [InlineData(-1.7548776662466927, 0.0, 2000, LaPeriodTest.ThresholdDip)]                  // near the airplane nucleus
    [InlineData(-0.7436438870371587, 0.1318259042053119, 3000, LaPeriodTest.MinMagnitude)]   // seahorse valley
    public void Where_Double_Holds_Everything_The_Floatexp_Stages_Are_The_Double_Stages(double cr, double ci, int n, LaPeriodTest test)
    {
        var (zr, zi, len) = Orbit(cr, ci, n);
        var d = LaStageTable.Build(zr, zi, len, LaStageTable.DefaultTolerance, test)!;
        Assert.True(d.Finite, "double table overflowed: pick a shorter orbit");
        Assert.True(d.Stages >= 2, $"{d.Stages} stage(s)");   // a real stage structure to compare
        var f = LaStageTableFx.Build(zr, zi, len, LaStageTable.DefaultTolerance, test)!;
        Assert.True(f.Finite);
        Assert.Equal(d.Stages, f.Stages);
        Assert.Equal(d.StageStart, f.StageStart);
        Assert.Equal(d.StageCount, f.StageCount);
        for (int i = 0; i < d.Data.Length; i++)
        {
            ref readonly var a = ref d.Data[i];
            ref readonly var b = ref f.Data[i];
            Assert.Equal(a.Len, b.Len);
            Assert.Equal(a.Next, b.Next);
            Assert.Equal(a.RefRe, b.RefRe);
            Assert.Equal(a.RefIm, b.RefIm);
            Rel(a.ARe, b.A.Re, 1e-12, $"A.re {i}"); Rel(a.AIm, b.A.Im, 1e-12, $"A.im {i}");
            Rel(a.BRe, b.B.Re, 1e-12, $"B.re {i}"); Rel(a.BIm, b.B.Im, 1e-12, $"B.im {i}");
            if (a.Thr > 1e-280) Rel(a.Thr, b.Thr, 1e-12, $"Thr {i}");
            if (a.ThrC > 1e-280) Rel(a.ThrC, b.ThrC, 1e-12, $"ThrC {i}");
        }
        Assert.Equal(d.Up, f.Up);
    }

    [Fact]
    public void Past_Double_The_Floatexp_Table_Holds_What_The_Double_One_Cannot()
    {
        var (zr, zi, len) = Orbit(0, 1, 4096);
        var d = LaStageTable.Build(zr, zi, len)!;
        var f = LaStageTableFx.Build(zr, zi, len)!;
        Assert.False(d.Finite);
        Assert.True(f.Finite);
        bool big = false;
        foreach (var s in f.Data)
            if (s.B.Cheb().Log2Abs() > 1024 || s.A.Cheb().Log2Abs() > 1024) big = true;
        Assert.True(big);
    }

    private static MandelbrotCalculator RenderAtI(int zl, DeepLaMode la, bool accel)
    {
        var calc = new MandelbrotCalculator(32, 32) { CenterX = 0, CenterY = 1, MaxIterations = 600 + 2 * zl, ColorMap = new HsvPalette() };
        calc.SetZoomLog10(zl);
        calc.ApReferenceZoomLog10 = 100;
        calc.DisableAcceleration = !accel;
        calc.LaMode = la;
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    [Theory]
    [InlineData(400)]
    [InlineData(1000)]
    public void Past_Double_La_Runs_By_Default_Cuts_The_Work_And_Keeps_The_Counts(int zl)
    {
        var plain = RenderAtI(zl, DeepLaMode.Off, accel: false);
        var la = RenderAtI(zl, DeepLaMode.Auto, accel: true);   // the default
        Assert.EndsWith(" LA FX", la.LastPrecisionLabel);
        Assert.NotNull(la.LastLaTableFx);
        var (px, laSteps, plainSteps, _) = la.LastLaWork;
        Assert.Equal(32 * 32, px);
        Assert.True(laSteps > 0);
        var (_, _, plainOnly) = plain.LastPtWork;
        Assert.True((laSteps + plainSteps) * 3 <= plainOnly, $"{laSteps}+{plainSteps} vs {plainOnly}");
        for (int i = 0; i < plain.IterationBuffer.Length; i++)
            Assert.Equal(plain.IterationBuffer[i], la.IterationBuffer[i]);
    }

    // Escape count by direct BigFloat iteration (as S1842's oracle, bailout 512).
    private static int Direct(ExactCentre centre, double ox, double oy, BigFloat pixel, int prec, int maxIter)
    {
        var cr = BigFloat.Add(centre.Re, BigFloat.Mul(pixel, BigFloat.FromDouble(ox, prec), prec), prec);
        var ci = BigFloat.Add(centre.Im, BigFloat.Mul(pixel, BigFloat.FromDouble(oy, prec), prec), prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 512.0 * 512.0) return n;
            var zr2 = BigFloat.Mul(zr, zr, prec); var zi2 = BigFloat.Mul(zi, zi, prec); var zri = BigFloat.Mul(zr, zi, prec);
            zi = BigFloat.Add(zri.ScaleB(1), ci, prec);
            zr = BigFloat.Add(BigFloat.Sub(zr2, zi2, prec), cr, prec);
        }
        return maxIter;
    }

    // S1842's minibrot frame (period 998 off-centre, the reference outside it). Where double holds, the floatexp evaluator
    // (forced) and the double one (LaMode On, no AT / SIMD, which it doesn't have) run the
    // same stages from the same tables (to 1e-12), so they take the same steps.
    [Fact]
    public void With_A_Minibrot_In_View_The_Floatexp_Evaluator_Is_The_Double_One()
    {
        const int W = 32, H = 32;
        var seed = DeepComplex.FromLimbs(-0.743643887037158704752191506114774, 0, 0, 0, 0, 0, 0, 0,
                                          0.131825904205311970493132056385139, 0, 0, 0, 0, 0, 0, 0);
        var found = FracturingFog.Abstractions.Explore.NucleusFinder.FindMinibrot(seed, 1e-13);
        Assert.True(found.Found);
        double size = found.Size.Magnitude;
        int maxIt = Math.Min(20 * found.Period, 20000);
        double zoom = 3.5 / (5 * size);
        var nuc = found.Nucleus;
        var d = nuc.Translate(1.5 * size, 0);
        MandelbrotCalculator Run(bool fx)
        {
            var c = new MandelbrotCalculator(W, H)
            {
                MaxIterations = maxIt, ColorMap = new HsvPalette(), Zoom = zoom,
                CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
                CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
                CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
                CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
            };
            c.FloatExpZoomLog10 = fx ? 0 : 1000;
            c.LaMode = DeepLaMode.On;
            c.UseLaAt = false;
            c.LaSimd = false;
            c.Calculate(CancellationToken.None);
            return c;
        }
        var fxc = Run(true);
        var dbl = Run(false);
        Assert.EndsWith(" LA FX", fxc.LastPrecisionLabel);
        Assert.EndsWith(" LA", dbl.LastPrecisionLabel);
        int diff = 0;
        for (int i = 0; i < W * H; i++) if (fxc.IterationBuffer[i] != dbl.IterationBuffer[i]) diff++;
        var (_, fl, fp, _) = fxc.LastLaWork;
        var (_, dl, dp, _) = dbl.LastLaWork;
        // Measured: 5 / 1024 counts differ (borderline decisions at 1e-12), LA steps equal,
        // plain steps within 0.01 %.
        Assert.True(diff <= W * H / 100, $"{diff}/{W * H} counts differ");
        Assert.True(fl > 0 && Math.Abs(fl - dl) <= dl / 200, $"LA steps {fl} vs {dl}");
        Assert.True(Math.Abs(fp - dp) <= dp / 200, $"plain steps {fp} vs {dp}");
    }
}
