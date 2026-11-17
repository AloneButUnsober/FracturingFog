// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1845 (epic #1428, C5b) — the floatexp BLA table and its use in the floatexp pixel loop.
// Checked:
//   • Where double holds every number, BlaTableFx is BlaTable: each entry's A, B and R²
//     within 1e-12 relative of the double table built from the same orbit and dcMax.
//   • Past double the table holds numbers double can't (B past 1e308, R below 1e-308),
//     with none NaN or infinite.
//   • In frames past double (1e400, 1e1000 at c = i), BLA cuts the loop's work (steps +
//     skips) at least 3×, and every pixel's count stays the plain loop's — which S1842
//     shows is the direct BigFloat count there.
//   (BLA is an approximation: on S1842's minibrot frame it is off like the double path's
//   BLA, #1346 — that test runs the plain loop.)

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1845FloatExpBlaTests
{
    private static (double[] Zr, double[] Zi, int Len) Orbit(double cr, double ci, int n)
    {
        var zr = new double[n + 1]; var zi = new double[n + 1];
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

    [Fact]
    public void Where_Double_Holds_Everything_The_Floatexp_Table_Is_The_Double_Table()
    {
        // c = i: a bounded (Misiurewicz) orbit, so every level has entries.
        var (zr, zi, len) = Orbit(0, 1, 256);   // |B| ≲ 5.7^256 ≈ 1e193: inside double
        Assert.Equal(256, len);
        double dcMax = 1e-9;
        var d = new BlaTable(zr, zi, len, dcMax);
        var f = new BlaTableFx(zr, zi, len, FloatExp.FromDouble(dcMax));
        Assert.Equal(d.Levels, f.Levels);
        Assert.Equal(d.Data.Length, f.Data.Length);
        int checkedR = 0;
        for (int i = 0; i < d.Data.Length; i++)
        {
            ref readonly var a = ref d.Data[i];
            ref readonly var b = ref f.Data[i];
            Assert.Equal(a.L, b.L);
            Rel(a.ARe, b.ARe, 1e-12, $"A.re {i}"); Rel(a.AIm, b.AIm, 1e-12, $"A.im {i}");
            Rel(a.BRe, b.BRe, 1e-12, $"B.re {i}"); Rel(a.BIm, b.BIm, 1e-12, $"B.im {i}");
            if (a.R2 > 1e-280) { Rel(a.R2, b.R2, 1e-12, $"R2 {i}"); checkedR++; }
            else Assert.True(b.R2.IsZero || b.R2.ToDouble() <= 1e-279, $"R2 {i}");
        }
        Assert.True(checkedR > 30, $"{checkedR}");
        // Same answers from the lookups.
        var r = new Random(1845);
        for (int t = 0; t < 2000; t++)
        {
            int n = r.Next(0, len);
            double dm2 = Math.Pow(10, -r.NextDouble() * 40);
            Assert.Equal(d.Lookup(n, dm2, len), f.Lookup(n, FloatExp.FromDouble(dm2), len));
        }
    }

    [Fact]
    public void Past_Double_The_Table_Holds_What_Double_Cannot()
    {
        var (zr, zi, len) = Orbit(0, 1, 4096);
        var f = new BlaTableFx(zr, zi, len, FloatExp.FromLog10(-450));
        bool bigB = false, tinyR = false;
        foreach (var e in f.Data)
        {
            foreach (var v in new[] { e.ARe, e.AIm, e.BRe, e.BIm, e.R2 })
                Assert.True(double.IsFinite(v.M), v.ToString());
            if (FloatExp.Norm2(e.BRe, e.BIm).Log2Abs() > 2 * 1024) bigB = true;
            if (!e.R2.IsZero && e.R2.Log2Abs() < -2 * 1022) tinyR = true;
        }
        Assert.True(bigB, "no B past double");
        Assert.True(tinyR, "no R past double");
    }

    private static MandelbrotCalculator RenderAtI(int zl, bool bla, int w = 32)
    {
        var calc = new MandelbrotCalculator(w, w) { CenterX = 0, CenterY = 1, MaxIterations = 600 + 2 * zl, ColorMap = new HsvPalette() };
        calc.SetZoomLog10(zl);
        calc.ApReferenceZoomLog10 = 100;
        calc.DisableAcceleration = !bla;
        calc.LaMode = DeepLaMode.Off;   // the BLA loop (LA, the Auto default past 1e30, is #1847's)
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    [Theory]
    [InlineData(400)]
    [InlineData(1000)]
    public void Past_Double_Bla_Cuts_The_Work_And_Keeps_The_Counts(int zl)
    {
        var plain = RenderAtI(zl, bla: false);
        var fast = RenderAtI(zl, bla: true);
        Assert.EndsWith(" FX", fast.LastPrecisionLabel);
        var (skips, saved, steps) = fast.LastPtWork;
        var (_, _, plainSteps) = plain.LastPtWork;
        Assert.True(skips > 0 && saved > 0, $"{skips} skips");
        Assert.True((steps + skips) * 3 <= plainSteps, $"{steps}+{skips} vs {plainSteps}");
        // The plain loop matched direct BigFloat iteration exactly here (S1842); BLA keeps it.
        for (int i = 0; i < plain.IterationBuffer.Length; i++)
            Assert.Equal(plain.IterationBuffer[i], fast.IterationBuffer[i]);
    }
}
