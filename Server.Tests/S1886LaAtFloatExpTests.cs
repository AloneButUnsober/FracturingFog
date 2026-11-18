// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1886 (epic #1428, C5g) — the LA approximation transform past double (FFMath.LaAtFx over
// LaStageTableFx; method from Imagina's HInfLAEvaluator, GitHub user 5E-324, AGPL-3.0, see
// the bibliography #imagina; as S1627 for the double AT). Checked:
//   • where double holds every number, LaAtFx is LaAt: the same stage selected, the same
//     Len, and its bounds and coefficients within 1e-12 (the tables themselves agree to
//     1e-12, S1847);
//   • on the antenna minibrots of periods 250 (~1e299) and 415 (~1e498) (S1864's Newton
//     nucleus from −2, framed through the atom size; float-exp pixels), the AT is used,
//     every in / out is the AT-off frame's, and on sampled pixels the counts match direct
//     BigFloat iteration as often as the AT-off frame's do (S1627's bar);
//   • the float-exp LA kernel's cbuffer gets the same AT words from LaAtFx (GpuLaAt.FromFx)
//     as from LaAt where double holds it, and its float Esc² is finite (it ran to 2^256 and
//     became +∞: the kernel's AT then never escaped); (A GPU frame test waits on #1887: the kernel
//     renders these minibrot views wrong past ~1e77, AT or not.)
//   • the AT's steps replace stage steps: no more steps than without it (the minibrot's top
//     stage already steps by the period; the gain is each AT step in double, measured and
//     reported, not asserted: timing).

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1886LaAtFloatExpTests(ITestOutputHelper output)
{
    private const int W = 48, H = 36, K = 100;

    // S1864: the period-n nucleus nearest −2 (Newton from c = −2) and its atom size.
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

    // The minibrot framed as the main set at its home view (centre −0.5, zoom 1).
    internal static (ExactCentre Centre, double ZoomLog10, int Prec) Minibrot(int n) => Minibrot(n, -0.5, 0);

    // c = nucleus + Size·c′ (c′ real), at zoom 10^dz / Size.
    internal static (ExactCentre Centre, double ZoomLog10, int Prec) Minibrot(int n, double cPrime, double dz)
    {
        int prec = BigFloat.PrecisionForZoom(1.25 * n + 10 + Math.Max(dz, 0)) + 64;
        var c = Nucleus(n, prec);
        var size = Size(c, n, prec);
        double zl = -size.ToFloatExp().Log2Abs() / Math.Log2(10) + dz;
        var re = BigFloat.Add(c, BigFloat.Mul(size, BigFloat.FromDouble(cPrime, prec), prec), prec);
        return (new ExactCentre(re, BigFloat.Zero(prec)), zl, prec);
    }

    internal static MandelbrotCalculator Render(ExactCentre e, double zl, int maxIter, bool at, IGpuKernel? k = null)
    {
        var d = e.ToDeep();
        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
            MaxIterations = maxIter, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Unbounded,
            UseLaAt = at, GpuKernel = k,
        };
        calc.SetZoomLog10(zl);
        calc.SetDepth(e, zl);
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    // Escape count of c = centre + (ox, oy)·pixel, iterated directly in BigFloat (S1842's oracle).
    private static int Direct(ExactCentre centre, double ox, double oy, FloatExp pixel, int prec, int maxIter)
    {
        var px = BigFloat.FromFloatExp(pixel, prec);
        var cr = BigFloat.Add(centre.Re.WithPrecision(prec), BigFloat.Mul(px, BigFloat.FromDouble(ox, prec), prec), prec);
        var ci = BigFloat.Add(centre.Im.WithPrecision(prec), BigFloat.Mul(px, BigFloat.FromDouble(oy, prec), prec), prec);
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

    [Theory]
    [InlineData(250, -0.5, 0.0, true)]    // the minibrot at the main set's home framing
    [InlineData(415, -0.5, 0.0, true)]
    [InlineData(250, 0.25, 6.0, false)]   // its cusp ×10^6: the view is small next to c''s offset
    public void Past_Double_The_At_Runs_And_Keeps_The_Counts(int n, double cPrime, double dz, bool allPixels)
    {
        var (e, zl, prec) = Minibrot(n, cPrime, dz);
        Assert.True(zl > 290, "precondition: float-exp pixels");
        int maxIter = n * K;
        var off = Render(e, zl, maxIter, at: false);
        var on = Render(e, zl, maxIter, at: true);
        // The rows' time (the orbit excluded), best of 3 re-renders of each (cached orbit).
        double Rows(MandelbrotCalculator c)
        {
            double best = double.MaxValue;
            for (int r = 0; r < 3; r++) { c.Calculate(CancellationToken.None); best = Math.Min(best, c.LastHpStageMs.Pixels); }
            return best;
        }
        double tOff = Rows(off), tOn = Rows(on);
        Assert.Contains("FX", on.LastPrecisionLabel);
        Assert.True(on.LastFrameUsedLa && off.LastFrameUsedLa, "precondition: the float-exp LA evaluator");
        Assert.Null(off.LastLaAtFx);
        if (dz > 0)
        {
            output.WriteLine($"p{n} c'={cPrime} 1e{zl:F1}: AT stage {on.LastLaAtFx?.Stage.ToString() ?? "none"} of {on.LastLaTableFx!.Stages}; differ {Enumerable.Range(0, W * H).Count(i => on.IterationBuffer[i] != off.IterationBuffer[i])}");
            // The view is too small next to c''s offset for any stage's AT (FF's margin,
            // S1627: c' = RefC + C·δc rounded in double would be coarser than δc): none.
            Assert.Null(on.LastLaAtFx);
            Assert.Equal(off.IterationBuffer, on.IterationBuffer);
            return;
        }
        Assert.NotNull(on.LastLaAtFx);
        var (atPx, atSteps) = on.LastLaAtWork;
        var wOn = on.LastLaWork; var wOff = off.LastLaWork;
        long workOn = atSteps + wOn.LaSteps + wOn.PlainSteps, workOff = wOff.LaSteps + wOff.PlainSteps;
        int inOut = Enumerable.Range(0, W * H).Count(i => (on.IterationBuffer[i] >= maxIter) != (off.IterationBuffer[i] >= maxIter));
        int differ = Enumerable.Range(0, W * H).Count(i => on.IterationBuffer[i] != off.IterationBuffer[i]);
        int inSet = off.IterationBuffer.Take(W * H).Count(v => v >= maxIter);

        // The truth on sampled pixels (escaping and in-set alike).
        var pixel = FloatExp.FromLog10(Math.Log10(3.5 / Math.Max(W, H)) - zl);
        var rng = new Random(1886 + n);
        var idx = Enumerable.Range(0, W * H).OrderBy(_ => rng.Next()).Take(16).ToArray();
        var want = new int[idx.Length];
        Parallel.For(0, idx.Length, q => want[q] = Direct(e, idx[q] % W - W * 0.5, idx[q] / W - H * 0.5, pixel, prec, maxIter));
        int okOn = 0, okOff = 0;
        for (int q = 0; q < idx.Length; q++)
        {
            if (on.IterationBuffer[idx[q]] == want[q]) okOn++;
            if (off.IterationBuffer[idx[q]] == want[q]) okOff++;
        }
        output.WriteLine($"p{n} 1e{zl:F1}: AT stage {on.LastLaAtFx!.Value.Stage} len {on.LastLaAtFx.Value.Len}, {atPx}/{W * H} px; " +
                         $"steps {workOn:N0} (AT {atSteps:N0}) vs {workOff:N0}; rows {tOn:F1} ms vs {tOff:F1} ms (x{tOff / tOn:F2}); " +
                         $"in set {inSet}, in/out {inOut}, counts differ {differ}; truth {okOn} vs {okOff} of {idx.Length}");
        // Every pixel (the stage's δc bound is far wider than the minibrot: even zoomed out
        // 10^4 every pixel is inside it, so no view here exercises that bound; LaAtFx's
        // ThrC equals LaAt's, below).
        if (allPixels) Assert.Equal(W * H, atPx);
        Assert.True(inSet > 0 && inSet < W * H, "precondition: in-set and escaping pixels");
        Assert.Equal(0, inOut);
        Assert.True(okOn >= okOff, $"truth: AT {okOn} vs no AT {okOff}");
        // The AT's steps replace stage steps (in double, where the stages step in FloatExp).
        Assert.True(atSteps > 0 && workOn <= workOff, $"steps {workOn} (AT {atSteps}) vs {workOff}");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    public void Where_Double_Holds_LaAtFx_Is_LaAt(int n)
    {
        // A shallower antenna minibrot: the double tables hold every number.
        var (e, zl, _) = Minibrot(n);
        var calc = new MandelbrotCalculator(W, H) { MaxIterations = n * K, Quality = QualityPreset.Unbounded, ColorMap = ColorPalette.BuiltIns[0] };
        var d = e.ToDeep();
        calc.CenterX = d.Re.X0; calc.CenterXLo = d.Re.X1; calc.CenterX2 = d.Re.X2; calc.CenterX3 = d.Re.X3;
        calc.CenterX4 = d.Re.X4; calc.CenterX5 = d.Re.X5; calc.CenterX6 = d.Re.X6; calc.CenterX7 = d.Re.X7;
        calc.CenterY = d.Im.X0; calc.CenterYLo = d.Im.X1; calc.CenterY2 = d.Im.X2; calc.CenterY3 = d.Im.X3;
        calc.CenterY4 = d.Im.X4; calc.CenterY5 = d.Im.X5; calc.CenterY6 = d.Im.X6; calc.CenterY7 = d.Im.X7;
        calc.SetZoomLog10(zl);
        calc.SetDepth(e, zl);
        calc.LaMode = DeepLaMode.On;
        calc.Calculate(CancellationToken.None);
        var t = calc.LaTable!;
        Assert.True(t.Finite, "precondition: the double table holds");
        // The same orbit (its double heads, as both tables read it).
        var zr = new double[t.RefLen + 1]; var zi = new double[t.RefLen + 1];
        for (int k = 0; k <= t.RefLen; k++) { var (re, im) = calc.ReferenceOrbitPoint(k); zr[k] = re.X0; zi[k] = im.X0; }
        var tfx = LaStageTableFx.Build(zr, zi, t.RefLen, t.Tolerance, t.PeriodTest, t.StageBits)!;
        double radius = 3.5 / Math.Max(W, H) * Math.Pow(10, -zl) * Math.Sqrt(W * W + H * H) * 0.5;
        var at = LaAt.Select(t, radius);
        var afx = LaAtFx.Select(tfx, FloatExp.FromDouble(radius));
        Assert.NotNull(at);
        Assert.NotNull(afx);
        var a = at!.Value; var b = afx!.Value;
        output.WriteLine($"p{n} 1e{zl:F1}: AT stage {a.Stage} len {a.Len}");
        Assert.Equal(a.Stage, b.Stage);
        Assert.Equal(a.Len, b.Len);
        static void Close(double want, double got, string what)
            => Assert.True(Math.Abs(got - want) <= 1e-12 * Math.Max(Math.Abs(want), 1e-300), $"{what}: {got} vs {want}");
        Close(a.ThrC, b.ThrC.ToDouble(), "ThrC");
        Close(a.Esc2, b.Esc2, "Esc2");
        Close(a.RefCRe, b.RefCRe, "RefC re"); Close(a.RefCIm, b.RefCIm, "RefC im");
        Close(a.CRe, b.C.Re.ToDouble(), "C re"); Close(a.CIm, b.C.Im.ToDouble(), "C im");
        Close(a.InvZRe, b.InvZ.Re.ToDouble(), "1/A re"); Close(a.InvZIm, b.InvZ.Im.ToDouble(), "1/A im");

        // The kernel's cbuffer: the same AT words (to the float the kernel keeps) either way.
        var pd = FracturingFog.Rendering.GpuLaFxParams.Make(W, H, 1.5, n * K, 512 * 512, -W / 2, -H / 2, t.RefLen, t.Stages,
            new FracturingFog.Rendering.GpuLaAt(a.Len, a.ThrC, a.Esc2, a.RefCRe, a.RefCIm, a.CRe, a.CIm, a.InvZRe, a.InvZIm), -200);
        var pf = FracturingFog.Rendering.GpuLaFxParams.Make(W, H, 1.5, n * K, 512 * 512, -W / 2, -H / 2, t.RefLen, t.Stages,
            FracturingFog.Rendering.GpuLaAt.FromFx(b), -200);
        Assert.Equal(pd.AtLen, pf.AtLen);
        Assert.Equal((pd.AtThrCE, pd.AtCE, pd.AtInvZE), (pf.AtThrCE, pf.AtCE, pf.AtInvZE));
        static void F(float want, float got, string what) => Assert.True(got == want || Math.Abs(got - want) <= 2.4e-7f * Math.Max(Math.Abs(want), 1e-30f), $"{what}: {got} vs {want}");
        F(pd.AtThrCM, pf.AtThrCM, "ThrC"); F(pd.AtCR, pf.AtCR, "C re"); F(pd.AtCI, pf.AtCI, "C im");
        F(pd.AtInvZR, pf.AtInvZR, "1/A re"); F(pd.AtInvZI, pf.AtInvZI, "1/A im");
        F(pd.AtEsc2, pf.AtEsc2, "Esc2"); F(pd.AtRefCR, pf.AtRefCR, "RefC re"); F(pd.AtRefCI, pf.AtRefCI, "RefC im");
        // The kernel's float Esc² stays finite (Esc² runs to 2^256; +∞ never lets the AT escape).
        Assert.True(float.IsFinite(pf.AtEsc2) && pf.AtEsc2 <= float.MaxValue && pf.AtEsc2 > 4f, $"AtEsc2 {pf.AtEsc2} (Esc2 {b.Esc2:E2})");
    }
}
