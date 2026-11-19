// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1912 (epic #1253, D7) — formulas with a second state past double: TwoStatePlan (a δ for z and one
// for w: Phoenix's z_{n−1}, Spider's evolving c, a User Equation's prev) on
// PerturbationEngine.RenderTwoState. Each check against an oracle outside the plan:
//   • one δ step of both states against the BigFloat difference, for δ from 1e-14 to 1e-2;
//   • Phoenix and Spider against direct BigFloat iteration written out here (both states), at 1e30
//     and 1e80. A two-state map has no Misiurewicz points to use as views: Phoenix's map is
//     invertible, so an orbit cannot land on a fixed state it did not start at. The views land on a
//     saddle's stable manifold instead (one complex condition on c): Newton on ℓ_u·(S_{K+1} − S_K) = 0,
//     ℓ_u the saddle's unstable left eigenvector, at a K where S_K is near the saddle, so the
//     linearisation error is below the view's scale. Orbits there approach the saddle, then leave
//     along its unstable direction: structure at every depth. Every view must be above the chaotic
//     floor (direct counts stable under a 1e-12-pixel shift);
//   • a User Equation that reads prev (Phoenix typed in) through the plan;
//   • the calculator: Phoenix and Spider switch to the engine ("PT") at 1e11 and agree with the SP
//     path (which the retired scalar Phoenix "PT" tier matched: it rebuilt z in double).
// Mutants (by hand):
//   • caught: the cross-state term (the prev leaf's δ reading δz), Spider's δw₀ = ε, the tangent's
//     start;
//   • survives: the glitch test, as in S1907–S1911.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;

namespace FracturingFog.Server.Tests;

public sealed class S1912TwoStateTests(ITestOutputHelper output)
{
    private const double PhoenixP = 0.56667, SpiderD = 0.5;   // FractalParameters' defaults

    private delegate void Step2(ref BigFloat zr, ref BigFloat zi, ref BigFloat wr, ref BigFloat wi, BigFloat cr, BigFloat ci, int p);

    private static (BigFloat, BigFloat) M(BigFloat a, BigFloat b, BigFloat c, BigFloat d, int p)
        => (BigFloat.Sub(BigFloat.Mul(a, c, p), BigFloat.Mul(b, d, p), p), BigFloat.Add(BigFloat.Mul(a, d, p), BigFloat.Mul(b, c, p), p));

    // Phoenix: z′ = z² + c + p·w, w′ = z
    private static void PhoenixStep(ref BigFloat zr, ref BigFloat zi, ref BigFloat wr, ref BigFloat wi, BigFloat cr, BigFloat ci, int p)
    {
        var (sr, si) = M(zr, zi, zr, zi, p);
        var k = BigFloat.FromDouble(PhoenixP, p);
        var nr = BigFloat.Add(BigFloat.Add(sr, cr, p), BigFloat.Mul(k, wr, p), p);
        var ni = BigFloat.Add(BigFloat.Add(si, ci, p), BigFloat.Mul(k, wi, p), p);
        wr = zr; wi = zi; zr = nr; zi = ni;
    }

    // Spider: z′ = z² + w, w′ = d·w + z′ (w₀ = c)
    private static void SpiderStep(ref BigFloat zr, ref BigFloat zi, ref BigFloat wr, ref BigFloat wi, BigFloat cr, BigFloat ci, int p)
    {
        var (sr, si) = M(zr, zi, zr, zi, p);
        var nr = BigFloat.Add(sr, wr, p); var ni = BigFloat.Add(si, wi, p);
        var d = BigFloat.FromDouble(SpiderD, p);
        wr = BigFloat.Add(BigFloat.Mul(d, wr, p), nr, p); wi = BigFloat.Add(BigFloat.Mul(d, wi, p), ni, p);
        zr = nr; zi = ni;
    }

    private static (BigFloat, BigFloat, BigFloat, BigFloat) Init(bool spider, BigFloat cr, BigFloat ci, int p)
        => spider ? (BigFloat.Zero(p), BigFloat.Zero(p), cr, ci) : (BigFloat.Zero(p), BigFloat.Zero(p), BigFloat.Zero(p), BigFloat.Zero(p));

    private static int Count(Step2 f, bool spider, BigFloat cr, BigFloat ci, int p, int maxIter)
        => Orbit(f, spider, cr, ci, p, maxIter).N;

    // The count, and z at that iteration (or at stopAt).
    private static (int N, BigFloat Zr, BigFloat Zi) Orbit(Step2 f, bool spider, BigFloat cr, BigFloat ci, int p, int maxIter, int stopAt = -1)
    {
        var (zr, zi, wr, wi) = Init(spider, cr, ci, p);
        for (int n = 0; n < maxIter; n++)
        {
            if (n == stopAt) return (n, zr, zi);
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (stopAt < 0 && a * a + b * b >= 512.0 * 512.0) return (n, zr, zi);
            f(ref zr, ref zi, ref wr, ref wi, cr, ci, p);
        }
        return (maxIter, zr, zi);
    }

    // A c whose orbit lands on the saddle's stable manifold (header), from a double guess. ℓ (the
    // unstable left eigenvector) is taken in double at the guess; its error multiplies the tiny
    // stable component only.
    // Continuation in K from k0 (where the double guess lands): a large K straight from the guess
    // overflows, its orbit leaving the saddle long before step K.
    private static ExactCentre StableLanding(Step2 f, bool spider, double gr, double gi, (Complex L0, Complex L1) l, int k0, int k, int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl) + 64 + 2 * k;
        int kk = k0;
        (BigFloat, BigFloat) U(BigFloat cr, BigFloat ci)
        {
            var (zr, zi, wr, wi) = Init(spider, cr, ci, p);
            for (int n = 0; n < kk; n++) f(ref zr, ref zi, ref wr, ref wi, cr, ci, p);
            BigFloat az = zr, bz = zi, aw = wr, bw = wi;
            f(ref zr, ref zi, ref wr, ref wi, cr, ci, p);
            var (dzr, dzi) = (BigFloat.Sub(zr, az, p), BigFloat.Sub(zi, bz, p));
            var (dwr, dwi) = (BigFloat.Sub(wr, aw, p), BigFloat.Sub(wi, bw, p));
            var (x1, y1) = M(BigFloat.FromDouble(l.L0.Real, p), BigFloat.FromDouble(l.L0.Imaginary, p), dzr, dzi, p);
            var (x2, y2) = M(BigFloat.FromDouble(l.L1.Real, p), BigFloat.FromDouble(l.L1.Imaginary, p), dwr, dwi, p);
            return (BigFloat.Add(x1, x2, p), BigFloat.Add(y1, y2, p));
        }
        BigFloat r = BigFloat.FromDouble(gr, p), i = BigFloat.FromDouble(gi, p);
        var h = BigFloat.FromDouble(1, p).ScaleB(-p / 2);
        double hd = Math.Pow(2, -p / 2);
        for (int it = 0; it < 400; it++)
        {
            var (u1, u2) = U(r, i);
            var (a1, a2) = U(BigFloat.Add(r, h, p), i);   // holomorphic in c: one direction gives U′
            var du = new Complex(BigFloat.Sub(a1, u1, p).ToDouble() / hd, BigFloat.Sub(a2, u2, p).ToDouble() / hd);
            var step = new Complex(u1.ToDouble(), u2.ToDouble()) / du;
            r = BigFloat.Sub(r, BigFloat.FromDouble(step.Real, p), p);
            i = BigFloat.Sub(i, BigFloat.FromDouble(step.Imaginary, p), p);
            bool settled = step.Magnitude < (kk < k ? 1e-14 * Math.Pow(0.5, kk - k0) : Math.Pow(10, -zl - 12));
            if (settled && kk >= k) break;
            if (settled || it % 6 == 5) kk = Math.Min(k, kk + 2);
        }
        return new ExactCentre(r, i);
    }

    // ℓ_u for Phoenix's saddle (z*, z*), z* the root of z² + (p − 1)z + c = 0 with the larger |λ_u|.
    private static (Complex, Complex) PhoenixL(Complex c)
    {
        var r = Complex.Sqrt((PhoenixP - 1) * (PhoenixP - 1) - 4 * c);
        (Complex, Complex) best = default; double bestU = 0;
        foreach (var z in new[] { ((1 - PhoenixP) + r) / 2, ((1 - PhoenixP) - r) / 2 })
        {
            // J = [[2z, p], [1, 0]]: λ² − 2zλ − p = 0; left eigenvector (1, p/λ)
            var s = Complex.Sqrt(z * z + PhoenixP);
            foreach (var lam in new[] { z + s, z - s })
                if (lam.Magnitude > bestU) { bestU = lam.Magnitude; best = (Complex.One, PhoenixP / lam); }
        }
        return best;
    }

    private void Check(string name, Result got, Step2 f, bool spider, ExactCentre centre, double scale, int maxIter)
    {
        int p = centre.Precision + 96, total = 0, eq = 0, stable = 0, dvBad = 0, dvChecked = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        string ex = "";
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re.WithPrecision(p), BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var ci = BigFloat.Add(centre.Im.WithPrecision(p), BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                var (n, zr, zi) = Orbit(f, spider, cr, ci, p, maxIter);
                int n2 = Count(f, spider, BigFloat.Add(cr, BigFloat.FromDouble(scale * 1e-12, p), p), ci, p, maxIter);
                if (n == n2) stable++;
                int idx = y * W + x;
                total++; seen.Add(n);
                if (got.Iter[idx] == n) eq++;
                else if (ex.Length < 160) ex += $"({x},{y}) {got.Iter[idx]} vs {n}; ";
                if (n < maxIter && got.Iter[idx] == n)
                {
                    // ∂z_n/∂Re(c): a forward difference of the direct orbit, h = 1e-9 pixel.
                    double h = scale * 1e-9;
                    var (_, zr2, zi2) = Orbit(f, spider, BigFloat.Add(cr, BigFloat.FromDouble(h, p), p), ci, p, maxIter, stopAt: n);
                    double fr = BigFloat.Sub(zr2, zr, p).ToDouble() / h, fi = BigFloat.Sub(zi2, zi, p).ToDouble() / h;
                    double err = Math.Sqrt(Math.Pow(got.Dr[idx] - fr, 2) + Math.Pow(got.Di[idx] - fi, 2));
                    dvChecked++;
                    if (!(err < 1e-2 * Math.Sqrt(fr * fr + fi * fi))) dvBad++;
                }
            }
        output.WriteLine($"{name}: {eq}/{total} counts equal, {seen.Count} distinct, {stable} stable, derivative off on {dvBad}/{dvChecked}; {got.Stats}");
        Assert.True(stable == total, $"{name}: precondition, the view is above the chaotic floor ({stable}/{total})");
        Assert.True(seen.Count >= 5, $"{name}: precondition, structure ({seen.Count})");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total}. {ex}");
        Assert.True(dvChecked >= total / 2, $"{name}: precondition, escaped pixels to check ({dvChecked})");
        Assert.True(dvBad <= total / 50, $"{name}: derivative off by > 1 % on {dvBad} of {dvChecked}");
    }

    private static Result Run2(TwoStatePlan plan, ExactCentre centre, double scale, int maxIter)
    {
        var it = new int[W * H]; var dr = new double[W * H]; var di = new double[W * H];
        var st = PerturbationEngine.RenderTwoState(plan, centre, scale, W, H, maxIter,
            (idx, n, zr, zi, a, b) => { it[idx] = n; dr[idx] = a; di[idx] = b; }, CancellationToken.None);
        return new Result(it, dr, di, st);
    }

    // ── one step against BigFloat ──

    [Theory]
    [InlineData("phoenix")]
    [InlineData("spider")]
    public void One_Delta_Step_Of_Both_States_Is_Exact(string fam)
    {
        bool spider = fam == "spider";
        var plan = spider ? TwoStatePlan.Spider(SpiderD) : TwoStatePlan.Phoenix(PhoenixP, 0);
        Step2 f = spider ? SpiderStep : PhoenixStep;
        var rng = new Random(1912);
        double worst = 0;
        const int p = 300;
        for (int k = 0; k < 400; k++)
        {
            double Zr = rng.NextDouble() * 2 - 1, Zi = rng.NextDouble() * 2 - 1, Wr = rng.NextDouble() - 0.5, Wi = rng.NextDouble() - 0.5;
            double Cr = -0.7, Ci = 0.3, mag = Math.Pow(10, -2 - 12 * rng.NextDouble());
            double dz0 = (rng.NextDouble() - 0.5) * mag, dz1 = (rng.NextDouble() - 0.5) * mag;
            double dw0 = (rng.NextDouble() - 0.5) * mag, dw1 = (rng.NextDouble() - 0.5) * mag;
            double e0 = (rng.NextDouble() - 0.5) * mag, e1 = (rng.NextDouble() - 0.5) * mag;
            double a = dz0, b = dz1, c = dw0, d = dw1;
            plan.DeltaStep(Zr, Zi, Wr, Wi, Cr, Ci, ref a, ref b, ref c, ref d, e0, e1);
            BigFloat B(double v) => BigFloat.FromDouble(v, p);
            BigFloat zr = B(Zr), zi = B(Zi), wr = B(Wr), wi = B(Wi);
            f(ref zr, ref zi, ref wr, ref wi, B(Cr), B(Ci), p);
            BigFloat zr2 = BigFloat.Add(B(Zr), B(dz0), p), zi2 = BigFloat.Add(B(Zi), B(dz1), p);
            BigFloat wr2 = BigFloat.Add(B(Wr), B(dw0), p), wi2 = BigFloat.Add(B(Wi), B(dw1), p);
            f(ref zr2, ref zi2, ref wr2, ref wi2, BigFloat.Add(B(Cr), B(e0), p), BigFloat.Add(B(Ci), B(e1), p), p);
            double[] want = [BigFloat.Sub(zr2, zr, p).ToDouble(), BigFloat.Sub(zi2, zi, p).ToDouble(),
                             BigFloat.Sub(wr2, wr, p).ToDouble(), BigFloat.Sub(wi2, wi, p).ToDouble()];
            double[] got = [a, b, c, d];
            double norm = Math.Sqrt(want.Sum(v => v * v)), err = Math.Sqrt(want.Zip(got, (u, v) => (u - v) * (u - v)).Sum());
            worst = Math.Max(worst, err / norm);
        }
        output.WriteLine($"{fam}: worst relative error of one δ step {worst:E2}");
        Assert.True(worst < 1e-13, $"{fam}: {worst}");
    }

    // ── the families against direct iteration ──

    [Theory]
    [InlineData(30)]
    [InlineData(80)]
    public void Phoenix_Matches_Direct_Iteration(int zl)
    {
        var g = new Complex(-0.12322142872184462, -1.1969718985625264);
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = Off(StableLanding(PhoenixStep, false, g.Real, g.Imaginary, PhoenixL(g), 9, 9 + 2 * zl, zl), scale, zl);
        var got = Run2(TwoStatePlan.Phoenix(PhoenixP, 0), centre, scale, 3000);
        Check($"Phoenix 1e{zl}", got, PhoenixStep, false, centre, scale, 3000);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(80)]
    public void Spider_Matches_Direct_Iteration(int zl)
    {
        // Spider's saddle is (0, 0): J = [[0, 1], [0, 1 + d]], λ_u = 1 + d with left eigenvector (0, 1).
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = Off(StableLanding(SpiderStep, true, -0.548976273303697, 1.120015685249224, (Complex.Zero, Complex.One), 6, 6 + zl / 4, zl), scale, zl);
        var got = Run2(TwoStatePlan.Spider(SpiderD), centre, scale, 3000);
        Check($"Spider 1e{zl}", got, SpiderStep, true, centre, scale, 3000);
    }

    [Fact]
    public void A_User_Prev_Formula_Matches_Direct_Iteration()
    {
        var plan = TwoStatePlan.TryCreatePrev(CalcGenLowering.ParseAndLower("z^2 + c + 0.56667*prev"), 512.0 * 512.0, out string? why);
        Assert.True(plan != null, why);
        const int zl = 30;
        var g = new Complex(-0.12322142872184462, -1.1969718985625264);
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = Off(StableLanding(PhoenixStep, false, g.Real, g.Imaginary, PhoenixL(g), 9, 9 + 2 * zl, zl), scale, zl);
        Check("user prev 1e30", Run2(plan!, centre, scale, 3000), PhoenixStep, false, centre, scale, 3000);
    }

    // ── through the calculator ──

    [Theory]
    [InlineData(FractalType.Phoenix)]
    [InlineData(FractalType.Spider)]
    public void Switches_To_The_Engine_And_Agrees_With_SP_At_The_Switch(FractalType t)
    {
        bool spider = t == FractalType.Spider;
        var at = spider
            ? StableLanding(SpiderStep, true, -0.548976273303697, 1.120015685249224, (Complex.Zero, Complex.One), 6, 12, 20)
            : StableLanding(PhoenixStep, false, -0.12322142872184462, -1.1969718985625264, PhoenixL(new Complex(-0.12322142872184462, -1.1969718985625264)), 9, 49, 20);
        at = Off(at, 3.5 / W / EscapeTimeCalculator.DeepZoomThreshold, 20);
        EscapeTimeCalculator Go(bool engine)
        {
            var c = new EscapeTimeCalculator(W, H)
            {
                FractalType = t, Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500,
                ColorMap = ColorPalette.BuiltIns[0], AllowPerturbation = engine, FractalParameters = new FractalParameters(),
            };
            c.SetDeepCentre(at.ToDeep(), at);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var sp = Go(false); var pt = Go(true);
        Assert.Equal("SP", sp.LastPrecisionLabel);
        Assert.Equal("PT", pt.LastPrecisionLabel);
        int eq = Enumerable.Range(0, W * H).Count(i => sp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int col = Enumerable.Range(0, W * H).Count(i => sp.ColorBuffer[i] == pt.ColorBuffer[i]);
        int distinct = sp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"{t} at 1e11: engine vs SP {eq}/{W * H} counts, {col} colours, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H} equal at the switch");
        Assert.True(col >= W * H * 0.85, $"{col}/{W * H} colours equal at the switch");
        Assert.Equal(FamilyDepth.EngineDoubleDeltaMaxZoom, FamilyDepth.MaxZoomFor(t));
    }
}
