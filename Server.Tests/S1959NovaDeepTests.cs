// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1959 (epic #1253, D14) — Nova past the double loop on the perturbation engine: R2Plan.Nova, a
// parameter-plane z₀ = 1 (no rebasing), the engine's step stop |z_{n+1} − z_n|² < 1e-12, and the double
// loop's basin (root sector) and smooth count. Checks, each against something outside the engine:
//   • NewtonCalculator's basins and smooth counts against direct BigFloat iteration of Nova written out by
//     hand (z − R·(z^d − 1)/(d·z^(d−1)) + c from z₀ = 1, the same stop), at Misiurewicz views (the
//     critical orbit from z₀ = 1 landing on a repelling 2-cycle) 1e20 / 1e40 / 1e100 / 1e320 (the
//     last through SetZoomLog10 and the scaled-δ prelude), and in Julia mode on its repelling 2-cycle;
//     precondition: the view is above the chaotic floor (counts unchanged under a 1e-12-pixel shift);
//   • at 1e11 (the switch until #1964 moved it to 1e7, S1964NovaSwitchTests) the engine matches the
//     oracle (the double loop is measured there); below DeepZoomThreshold the frame is byte-identical;
//   • the poster path hands over the full centre and the log zoom; FamilyDepth has no limit for Nova.
//   • in a basin interior (1e40, 1e320) every pixel stops as direct iteration does — at 1e320 inside the
//     scaled-δ prelude.
// Mutants (by hand):
//   • caught: the step stop in the double loop; in the prelude and the BLA step guard (the basin-interior
//     view); the reference ignoring z₀; the smooth count without the step before; the wrong root sector;
//     Julia mode's c dropped; the deep threshold ignored;
//   • survives, with reason: the step stop in direct BigFloat iteration — no view here sends a pixel
//     there (8 failed references first); it has the caught stops' shape.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;
using static FracturingFog.Server.Tests.S1921DeepFxTests;

namespace FracturingFog.Server.Tests;

public sealed class S1959NovaDeepTests(ITestOutputHelper output)
{
    internal const int D = 3, MaxIt = 1500;
    private const double Relax = 1.0, Eps2 = 1e-12;

    // z − R·(z^d − 1)/(d·z^(d−1)) + c
    internal static (BigFloat, BigFloat) NovaStep(BigFloat zr, BigFloat zi, BigFloat cr, BigFloat ci, int p)
    {
        BigFloat pr = zr, pi = zi;   // z^(d−1)
        for (int k = 1; k < D - 1; k++)
            (pr, pi) = (BigFloat.Sub(BigFloat.Mul(pr, zr, p), BigFloat.Mul(pi, zi, p), p), BigFloat.Add(BigFloat.Mul(pr, zi, p), BigFloat.Mul(pi, zr, p), p));
        var nr = BigFloat.Sub(BigFloat.Sub(BigFloat.Mul(pr, zr, p), BigFloat.Mul(pi, zi, p), p), BigFloat.FromDouble(1, p), p);   // z^d − 1
        var ni = BigFloat.Add(BigFloat.Mul(pr, zi, p), BigFloat.Mul(pi, zr, p), p);
        var dr = BigFloat.Mul(BigFloat.FromDouble(D, p), pr, p); var di = BigFloat.Mul(BigFloat.FromDouble(D, p), pi, p);
        var den = BigFloat.Add(Sq(dr, p), Sq(di, p), p);
        var qr = BigFloat.Div(BigFloat.Add(BigFloat.Mul(nr, dr, p), BigFloat.Mul(ni, di, p), p), den, p);
        var qi = BigFloat.Div(BigFloat.Sub(BigFloat.Mul(ni, dr, p), BigFloat.Mul(nr, di, p), p), den, p);
        var rr = BigFloat.FromDouble(Relax, p);
        return (BigFloat.Add(BigFloat.Sub(zr, BigFloat.Mul(rr, qr, p), p), cr, p), BigFloat.Add(BigFloat.Sub(zi, BigFloat.Mul(rr, qi, p), p), ci, p));
    }

    // The direct orbit: (basin, smooth count) by the double loop's rule.
    internal static (int Basin, float Smooth) Orbit(BigFloat z0r, BigFloat z0i, BigFloat cr, BigFloat ci, int p)
    {
        BigFloat zr = z0r, zi = z0i;
        double prev2 = double.MaxValue;
        for (int n = 0; n < MaxIt; n++)
        {
            var (ar, ai) = NovaStep(zr, zi, cr, ci, p);
            double sr = ar.ToDouble() - zr.ToDouble(), si = ai.ToDouble() - zi.ToDouble(), step2 = sr * sr + si * si;
            zr = ar; zi = ai;
            if (step2 < Eps2)
                return (NewtonCalculator.RootSector(zr.ToDouble(), zi.ToDouble(), D), NewtonCalculator.NovaSmooth(n, prev2, step2, Eps2));
            prev2 = step2;
        }
        return (-1, MaxIt);
    }

    // Preperiod 3, period 2 (multipliers ~7 and ~11). A period-1 landing is no view for Nova: its step stop
    // fires at a repelling fixed point too (the step is ~0 there), so every pixel stops at once, one colour.
    internal static readonly (double Re, double Im)[] ParamViews = [(-0.2848472433159221, 0.510804036200044), (-0.6718142473539443, 0.10584589661496834)];

    // A Misiurewicz point: from z₀ = 1, z₃ lies on a 2-cycle (f(f(z₃)) = z₃), by Newton rescaled past double.
    internal static ExactCentre Misiurewicz(double gr, double gi, int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl) + 64;
        var (r, i) = Newton((cr, ci) =>
        {
            BigFloat zr = BigFloat.FromDouble(1, p), zi = BigFloat.Zero(p);
            for (int k = 0; k < 3; k++) (zr, zi) = NovaStep(zr, zi, cr, ci, p);
            var (mr, mi) = NovaStep(zr, zi, cr, ci, p);
            var (nr, ni) = NovaStep(mr, mi, cr, ci, p);
            return (BigFloat.Sub(nr, zr, p), BigFloat.Sub(ni, zi, p));
        }, gr, gi, p, (long)(zl * 3.33) + 40);
        return new ExactCentre(r, i);
    }

    internal static NewtonCalculator Nova(int w, int h, bool julia = false) => new(w, h)
    {
        FractalType = FractalType.Nova, MaxIterations = MaxIt, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Ultra,
        FractalParameters = new FractalParameters { NewtonExponent = D, NewtonRelaxation = Relax, NovaJuliaMode = julia },
    };

    private void CheckFrame(string name, NewtonCalculator calc, View v, bool julia)
    {
        var (eq, total, seen, stable, ex) = Agreement(calc, v, julia);
        output.WriteLine($"{name}: {eq}/{total} basin + smooth equal, {seen} distinct, {stable} stable; [{calc.LastPrecisionLabel}] {calc.LastPerturbationStats}");
        Assert.True(seen >= 5, $"{name}: precondition, structure ({seen})");
        Assert.True(stable >= total * 0.95, $"{name}: precondition, above the chaotic floor ({stable}/{total})");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total}. {ex}");
    }

    // #1964 — the frame's basin and smooth count against direct BigFloat iteration on every third pixel:
    // the pixels equal, the distinct (basin, floor(smooth)) classes, and the pixels whose class a 1e-12
    // pixel shift keeps (the chaotic floor).
    internal static (int Eq, int Total, int Seen, int Stable, string Ex) Agreement(NewtonCalculator calc, View v, bool julia)
    {
        int pf = BigFloat.PrecisionForZoom(v.Zl) + 96;
        var jc = (BigFloat.FromDouble(-0.5, pf), BigFloat.Zero(pf));   // NovaJuliaC's default
        var shift = BigFloat.FromDouble(1e-12 * v.Sm, pf).ScaleB(v.S0);
        (int, float) Px(BigFloat pr, BigFloat pi) => julia
            ? Orbit(pr, pi, jc.Item1, jc.Item2, pf)
            : Orbit(BigFloat.FromDouble(1, pf), BigFloat.Zero(pf), pr, pi, pf);
        int total = 0, eq = 0, stable = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        var gate = new object();
        string ex = "";
        var samples = (from y in Enumerable.Range(0, H) where y % 3 == 1 from x in Enumerable.Range(0, W) where x % 3 == 1 select (x, y)).ToArray();
        Parallel.ForEach(samples, xy =>
        {
            var (x, y) = xy;
            var pr = v.Off(x, W, v.Centre.Re, pf);
            var pi = v.Off(y, H, v.Centre.Im, pf);
            var (b, s) = Px(pr, pi);
            var (b2, s2) = Px(BigFloat.Add(pr, shift, pf), pi);
            int idx = y * W + x;
            bool ok = calc.BasinBuffer[idx] == b && Math.Abs(calc.SmoothBuffer[idx] - s) < 1e-3;
            lock (gate)
            {
                total++; seen.Add((int)Math.Floor(s) * 16 + b + 1);
                if (b == b2 && Math.Floor(s) == Math.Floor(s2)) stable++;
                if (ok) eq++;
                else if (ex.Length < 200) ex += $"({x},{y}) b{calc.BasinBuffer[idx]} s{calc.SmoothBuffer[idx]:F3} vs b{b} s{s:F3}; ";
            }
        });
        return (eq, total, seen.Count, stable, ex);
    }

    private void Param(int view, int zl)
    {
        var v = ViewAt(Misiurewicz(ParamViews[view].Re, ParamViews[view].Im, zl), zl);
        var calc = Nova(W, H);
        calc.SetDeepCentre(v.Centre.ToDeep(), v.Centre);
        calc.SetZoomLog10(zl);
        calc.Calculate(CancellationToken.None);
        Assert.Equal("PT", calc.LastPrecisionLabel);
        if (zl > 290) Assert.True(calc.LastPerturbationStats.ScaledSteps > 0, "precondition: the scaled-δ prelude ran");
        CheckFrame($"Nova view {view} 1e{zl}", calc, v, julia: false);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(0, 40)]
    [InlineData(1, 40)]
    [InlineData(0, 100)]
    [InlineData(1, 320)]
    public void Nova_Matches_Direct_Iteration(int view, int zl) => Param(view, zl);

    [Theory]
    [InlineData(40)]
    [InlineData(320)]
    public void Nova_Julia_Matches_Direct_Iteration(int zl)
    {
        // Julia c = −0.5 (NovaJuliaC's default): a point of its repelling 2-cycle (multiplier ~10.7), by Newton.
        int p = BigFloat.PrecisionForZoom(zl) + 64;
        var (zr, zi) = Newton((a, b) =>
        {
            var (gr, gi) = NovaStep(a, b, BigFloat.FromDouble(-0.5, p), BigFloat.Zero(p), p);
            var (fr, fi) = NovaStep(gr, gi, BigFloat.FromDouble(-0.5, p), BigFloat.Zero(p), p);
            return (BigFloat.Sub(fr, a, p), BigFloat.Sub(fi, b, p));
        }, -0.4730955739635228, 0.12126155044701115, p, (long)(zl * 3.33) + 40);
        var v = ViewAt(new ExactCentre(zr, zi), zl);
        var calc = Nova(W, H, julia: true);
        calc.SetDeepCentre(v.Centre.ToDeep(), v.Centre);
        calc.SetZoomLog10(zl);
        calc.Calculate(CancellationToken.None);
        Assert.Equal("PT", calc.LastPrecisionLabel);
        CheckFrame($"Nova Julia 1e{zl}", calc, v, julia: true);
    }

    [Fact]
    public void At_1e11_The_Engine_Matches_Direct_Iteration_And_The_Double_Loop_Is_Measured()
    {
        // At 1e11 (the switch before #1964) the engine must match the oracle. The double loop is measured, not required: near a
        // repelling 2-cycle (multiplier ~11) its polar-form rounding grows as fast as the pixels separate.
        var v = ViewAt(Misiurewicz(ParamViews[0].Re, ParamViews[0].Im, 11), 11);
        NewtonCalculator Run(bool engine)
        {
            var c = Nova(W, H);
            c.AllowPerturbation = engine;
            c.SetDeepCentre(v.Centre.ToDeep(), v.Centre);
            c.SetZoomLog10(11);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var e = Run(true);
        Assert.Equal("PT", e.LastPrecisionLabel);
        CheckFrame("Nova engine 1e11", e, v, julia: false);
        var dl = Run(false);
        Assert.Equal("SP", dl.LastPrecisionLabel);
        int n = W * H, eq = Enumerable.Range(0, n).Count(i => e.BasinBuffer[i] == dl.BasinBuffer[i] && Math.Abs(e.SmoothBuffer[i] - dl.SmoothBuffer[i]) < 1e-2);
        output.WriteLine($"1e11: the double loop agrees with the engine on {eq}/{n}");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(320)]
    public void In_A_Basin_Interior_Every_Pixel_Stops_As_Direct_Iteration_Does(int zl)
    {
        // c = 0.1 + 0.1i: the orbit from z₀ = 1 converges in a few dozen steps, every pixel with it, while
        // the pixels' δ is still tiny — at 1e320 still in the scaled-δ prelude, which must stop them too.
        int p = BigFloat.PrecisionForZoom(zl) + 64;
        var v = ViewAt(new ExactCentre(BigFloat.FromDouble(0.1, p), BigFloat.FromDouble(0.1, p)), zl);
        var calc = Nova(W, H);
        calc.SetDeepCentre(v.Centre.ToDeep(), v.Centre);
        calc.SetZoomLog10(zl);
        calc.Calculate(CancellationToken.None);
        int pf = BigFloat.PrecisionForZoom(zl) + 96, ok = 0, total = 0;
        string ex = "";
        for (int y = 1; y < H; y += 7)
            for (int x = 1; x < W; x += 7)
            {
                var (b0, s0) = Orbit(BigFloat.FromDouble(1, pf), BigFloat.Zero(pf), v.Off(x, W, v.Centre.Re, pf), v.Off(y, H, v.Centre.Im, pf), pf);
                int idx = y * W + x;
                total++;
                if (b0 >= 0 && calc.BasinBuffer[idx] == b0 && Math.Abs(calc.SmoothBuffer[idx] - s0) < 1e-3) ok++;
                else if (ex.Length < 160) ex += $"({x},{y}) b{calc.BasinBuffer[idx]} s{calc.SmoothBuffer[idx]:F3} vs b{b0} s{s0:F3}; ";
            }
        output.WriteLine($"basin interior 1e{zl}: {ok}/{total}; {calc.LastPerturbationStats}");
        Assert.Equal(total, ok);
    }

    [Fact]
    public void Below_The_Threshold_The_Frame_Is_Byte_Identical()
    {
        var m = Misiurewicz(ParamViews[0].Re, ParamViews[0].Im, 13);
        NewtonCalculator Run(bool engine)
        {
            var c = Nova(W, H);
            c.AllowPerturbation = engine;
            c.SetDeepCentre(m.ToDeep(), m);
            c.Zoom = 1e6;   // #1964: below the switch (1e7)
            c.Calculate(CancellationToken.None);
            return c;
        }
        var on = Run(true); var off = Run(false);
        Assert.Equal("SP", on.LastPrecisionLabel);
        Assert.True(on.ColorBuffer.Distinct().Count() >= 3, "precondition: structure");
        Assert.Equal(off.ColorBuffer, on.ColorBuffer);
    }

    [Fact]
    public void The_Poster_Path_Hands_Over_The_Centre_And_The_Log_Zoom()
    {
        double zl = 400.5;
        var exact = new ExactCentre(BigFloat.FromDouble(-0.3, 1400), BigFloat.FromDouble(0.5, 1400).ScaleB(0));
        var req = new PosterRequest
        {
            FractalType = FractalType.Nova, Width = 8, Height = 8, Zoom = FracturingFog.ViewState.ZoomLog.FromLog10(zl), ZoomLog10 = zl,
            MaxIterations = 64, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard, FractalParameters = new FractalParameters(),
            CenterX = -0.3, CenterY = 0.5, CenterExact = exact,
        };
        var calc = PosterRenderer.BuildCaptureCalculator(req);
        Assert.Equal(zl, Assert.IsAssignableFrom<IDeepZoom>(calc).ZoomLog10, 9);
        Assert.Equal(exact, Assert.IsAssignableFrom<IDeepCentre>(calc).CenterExact);
    }

    [Fact]
    public void Nova_Has_No_Limit()
    {
        Assert.Equal(QualityPreset.Deepest.ZoomMax, FamilyDepth.MaxZoomFor(FractalType.Nova));
        Assert.Equal(AutoExplorer.DoubleMaxZoom, FamilyDepth.MaxZoomFor(FractalType.Newton));   // plain Newton stays double
    }
}
