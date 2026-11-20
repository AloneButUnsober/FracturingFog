// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1957 (epic #1253, after #1921) — past double for the rest of the engine's users: the two-state
// engine (Phoenix, Spider, User Equation prev) gets the scaled-δ prelude and the scaled tangent; User
// Equation, the CalcGen template and TearDrop take a log zoom (IDeepZoom) and render with a FloatExp
// pixel scale, the derivative in the distance unit. Checks, each against something outside the engine:
//   • Phoenix and Spider on the two-state engine at 1e320: counts against direct BigFloat iteration of
//     the two-state map written out by hand (S1912's), dz/dc (distance unit) against a BigFloat finite
//     difference, at views on a saddle's stable manifold (S1912's construction, its Newton rescaled past
//     double); the prelude forced at 1e40 agrees with the double loop;
//   • through SetZoomLog10 at 1e320: TearDrop, Generated z³ and Tricorn, User Equation z² + c and
//     Phoenix (EscapeTimeCalculator, the two-state engine) — counts against direct iteration;
//   • User Equation's rotation (the engine's linear warp, scaled with the offsets): at 90° the frame is
//     the unrotated one turned a quarter;
//   • the poster path hands the log zoom to these calculators; the AA jitter (#1960) moves the exact
//     centre by the log zoom's pixel at 1e400; FamilyDepth has no limit for them.
// Mutants (by hand):
//   • caught: the two-state step not rescaled; the two-state tangent without the unit; Phoenix through the
//     calculator without the FloatExp scale (1e320: at 1e300 the 48 px pixel is still a normal double);
//     TearDrop / User Equation / the template without it; the AA jitter ignoring the log zoom; User
//     Equation's warp not declared linear;
//   • survives, with reason: the two-state prelude's escape step — no view here keeps a pixel's δ under
//     2^-900 until Phoenix's reference ends (the one-state twin is what kept Glynn off direct, D2b).

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using FracturingFog.Rendering;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;
using static FracturingFog.Server.Tests.S1921DeepFxTests;
using Gen = FracturingFog.Calculators.Generated;

namespace FracturingFog.Server.Tests;

public sealed class S1957DeepFxRestTests(ITestOutputHelper output)
{
    private const double Bail = 512.0 * 512.0;
    private const int TwoZl = 320;

    // ── two-state views (S1912's stable-manifold landing, its Newton step rescaled past double) ──

    private static ExactCentre StableLanding(S1912TwoStateTests.Step2 f, bool spider, double gr, double gi,
                                             (Complex L0, Complex L1) l, int k0, int k, int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl) + 64 + 2 * k;
        int kk = k0;
        (BigFloat, BigFloat) U(BigFloat cr, BigFloat ci)
        {
            var (zr, zi, wr, wi) = S1912TwoStateTests.Init(spider, cr, ci, p);
            for (int n = 0; n < kk; n++) f(ref zr, ref zi, ref wr, ref wi, cr, ci, p);
            BigFloat az = zr, bz = zi, aw = wr, bw = wi;
            f(ref zr, ref zi, ref wr, ref wi, cr, ci, p);
            var (dzr, dzi) = (BigFloat.Sub(zr, az, p), BigFloat.Sub(zi, bz, p));
            var (dwr, dwi) = (BigFloat.Sub(wr, aw, p), BigFloat.Sub(wi, bw, p));
            var (x1, y1) = S1912TwoStateTests.M(BigFloat.FromDouble(l.L0.Real, p), BigFloat.FromDouble(l.L0.Imaginary, p), dzr, dzi, p);
            var (x2, y2) = S1912TwoStateTests.M(BigFloat.FromDouble(l.L1.Real, p), BigFloat.FromDouble(l.L1.Imaginary, p), dwr, dwi, p);
            return (BigFloat.Add(x1, x2, p), BigFloat.Add(y1, y2, p));
        }
        BigFloat r = BigFloat.FromDouble(gr, p), i = BigFloat.FromDouble(gi, p);
        int hb = p / 2;
        var h = BigFloat.FromDouble(1, p).ScaleB(-hb);
        double final = -(zl * 3.3219280948873622 + 40);
        for (int it = 0; it < 1200; it++)
        {
            var (u1, u2) = U(r, i);
            var (a1, a2) = U(BigFloat.Add(r, h, p), i);   // holomorphic in c: one direction gives U′
            var du = new Complex(BigFloat.Sub(a1, u1, p).ScaleB(hb).ToDouble(), BigFloat.Sub(a2, u2, p).ScaleB(hb).ToDouble());
            long q = -Math.Max(Log2(u1), Log2(u2));
            var step = new Complex(u1.ScaleB(q).ToDouble(), u2.ScaleB(q).ToDouble()) / du;   // the step · 2^q
            r = BigFloat.Sub(r, BigFloat.FromDouble(step.Real, p).ScaleB(-q), p);
            i = BigFloat.Sub(i, BigFloat.FromDouble(step.Imaginary, p).ScaleB(-q), p);
            double log2Step = Math.Log2(step.Magnitude) - q;
            bool settled = log2Step < (kk < k ? Math.Log2(1e-14) - (kk - k0) : final);
            if (settled && kk >= k) return new ExactCentre(r, i);
            if (settled || it % 6 == 5) kk = Math.Min(k, kk + 2);
        }
        throw new InvalidOperationException("precondition: the landing did not settle");
    }

    private static readonly Lazy<ExactCentre> PhoenixLanding = new(() =>
    {
        var g = new Complex(-0.12322142872184462, -1.1969718985625264);
        return StableLanding(S1912TwoStateTests.PhoenixStep, false, g.Real, g.Imaginary, S1912TwoStateTests.PhoenixL(g), 9, 9 + 2 * TwoZl, TwoZl);
    });

    private static readonly Lazy<ExactCentre> SpiderLanding = new(() =>
        StableLanding(S1912TwoStateTests.SpiderStep, true, -0.548976273303697, 1.120015685249224, (Complex.Zero, Complex.One), 6, 6 + TwoZl / 4, TwoZl));

    // Counts and dz/dc (· 2^-unitE) of a two-state frame against direct iteration.
    private void Check2(string name, int[] it, double[] dr, double[] di, S1912TwoStateTests.Step2 f, bool spider, View v, int maxIter, int unitE, bool derivative = true)
    {
        int pf = BigFloat.PrecisionForZoom(v.Zl) + 96;
        var h = BigFloat.FromDouble(1e-9 * v.Sm, pf).ScaleB(v.S0);
        (int, BigFloat, BigFloat) Orbit(BigFloat cr, BigFloat ci, int stopAt)
        {
            var (zr, zi, wr, wi) = S1912TwoStateTests.Init(spider, cr, ci, pf);
            for (int n = 0; n < maxIter; n++)
            {
                if (n == stopAt) return (n, zr, zi);
                double a = zr.ToDouble(), b = zi.ToDouble();
                if (stopAt < 0 && a * a + b * b >= Bail) return (n, zr, zi);
                f(ref zr, ref zi, ref wr, ref wi, cr, ci, pf);
            }
            return (maxIter, zr, zi);
        }
        int total = 0, eq = 0, dvBad = 0, dvChecked = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        var gate = new object();
        string ex = "";
        var samples = (from y in Enumerable.Range(0, H) where y % 3 == 1 from x in Enumerable.Range(0, W) where x % 3 == 1 select (x, y)).ToArray();
        Parallel.ForEach(samples, xy =>
        {
            var (x, y) = xy;
            var cr = v.Off(x, W, v.Centre.Re, pf);
            var ci = v.Off(y, H, v.Centre.Im, pf);
            var (n, zr, zi) = Orbit(cr, ci, -1);
            int idx = y * W + x;
            double fr = 0, fi = 0;
            bool dv = derivative && n < maxIter && it[idx] == n;
            if (dv)
            {
                var (_, zr2, zi2) = Orbit(BigFloat.Add(cr, h, pf), ci, n);
                fr = BigFloat.Div(BigFloat.Sub(zr2, zr, pf), h, pf).ScaleB(-unitE).ToDouble();
                fi = BigFloat.Div(BigFloat.Sub(zi2, zi, pf), h, pf).ScaleB(-unitE).ToDouble();
            }
            lock (gate)
            {
                total++; seen.Add(n);
                if (it[idx] == n) eq++;
                else if (ex.Length < 160) ex += $"({x},{y}) {it[idx]} vs {n}; ";
                if (dv)
                {
                    dvChecked++;
                    double err = Math.Sqrt(Math.Pow(dr[idx] - fr, 2) + Math.Pow(di[idx] - fi, 2));
                    if (!(err < 1e-2 * Math.Sqrt(fr * fr + fi * fi))) dvBad++;
                }
            }
        });
        output.WriteLine($"{name}: {eq}/{total} counts equal, {seen.Count} distinct, derivative off on {dvBad}/{dvChecked}");
        Assert.True(seen.Count >= 5, $"{name}: precondition, structure ({seen.Count} distinct counts)");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total}. {ex}");
        if (derivative)
        {
            Assert.True(dvChecked >= total / 2, $"{name}: precondition, escaped pixels ({dvChecked})");
            Assert.True(dvBad <= total / 50, $"{name}: derivative off on {dvBad}/{dvChecked}");
        }
    }

    private static (int[] It, double[] Dr, double[] Di, PerturbationStats St) Run2(TwoStatePlan plan, View v, int maxIter, int unitE, int fxBelow = PerturbationEngine.FxScaleLog2)
    {
        var it = new int[W * H]; var dr = new double[W * H]; var di = new double[W * H];
        var st = PerturbationEngine.RenderTwoState(plan, v.Centre, 0, W, H, maxIter,
            (idx, n, zr, zi, a, b) => { it[idx] = n; dr[idx] = a; di[idx] = b; }, CancellationToken.None,
            pixelScale: v.Scale, derivExp2: unitE, fxBelowLog2: fxBelow);
        return (it, dr, di, st);
    }

    [Theory]
    [InlineData("phoenix")]
    [InlineData("spider")]
    public void Two_State_Families_Match_Direct_Iteration_Past_Double(string which)
    {
        bool spider = which == "spider";
        var v = ViewAt(spider ? SpiderLanding.Value : PhoenixLanding.Value, TwoZl);
        var plan = spider ? TwoStatePlan.Spider(S1912TwoStateTests.SpiderD) : TwoStatePlan.Phoenix(S1912TwoStateTests.PhoenixP, 0);
        var (it, dr, di, st) = Run2(plan, v, 3000, -v.S0);
        output.WriteLine($"{which}: {st}");
        Assert.True(st.ScaledSteps > 0, "precondition: the scaled-δ prelude ran");
        // The prelude's escape step at the reference's end (Phoenix's reference escapes): without it the
        // pixels still close to the reference there are glitched and go to secondary references.
        Assert.True(st.GlitchedPixels <= W * H / 50, $"glitched pixels: {st.GlitchedPixels}");
        Check2($"{which} 1e{TwoZl}", it, dr, di, spider ? S1912TwoStateTests.SpiderStep : S1912TwoStateTests.PhoenixStep, spider, v, 3000, -v.S0);
    }

    [Fact]
    public void The_Two_State_Prelude_Forced_At_A_Double_Depth_Agrees_With_The_Double_Loop()
    {
        var g = new Complex(-0.12322142872184462, -1.1969718985625264);
        var m = StableLanding(S1912TwoStateTests.PhoenixStep, false, g.Real, g.Imaginary, S1912TwoStateTests.PhoenixL(g), 9, 9 + 2 * 40, 40);
        var v = ViewAt(m, 40);
        var plan = TwoStatePlan.Phoenix(S1912TwoStateTests.PhoenixP, 0);
        var dbl = Run2(plan, v, 3000, 0);
        var fx = Run2(plan, v, 3000, 0, fxBelow: 0);
        Assert.Equal(0, dbl.St.ScaledSteps);
        Assert.True(fx.St.ScaledSteps > 0, "precondition: the prelude ran");
        int n = W * H, eq = Enumerable.Range(0, n).Count(i => dbl.It[i] == fx.It[i]);
        int dvOff = Enumerable.Range(0, n).Count(i => dbl.It[i] == fx.It[i] && dbl.It[i] < 3000
            && !(Math.Sqrt(Math.Pow(dbl.Dr[i] - fx.Dr[i], 2) + Math.Pow(dbl.Di[i] - fx.Di[i], 2)) <= 1e-6 * Math.Sqrt(dbl.Dr[i] * dbl.Dr[i] + dbl.Di[i] * dbl.Di[i])));
        output.WriteLine($"Phoenix 1e40: prelude vs double {eq}/{n}, derivative off on {dvOff}");
        Assert.True(eq >= n * 0.99, $"{eq}/{n}");
        Assert.True(dvOff <= n / 100, $"derivative off on {dvOff}");
    }

    // ── through the calculators' SetZoomLog10 ──

    private static int[] Frame(IFractalCalculator c, View v)
    {
        ((IDeepCentre)c).SetDeepCentre(v.Centre.ToDeep(), v.Centre);
        ((IDeepZoom)c).SetZoomLog10(v.Zl);
        c.Calculate(CancellationToken.None);
        return ((int[])((dynamic)c).IterationBuffer).Take(W * H).ToArray();
    }

    private void CheckCalc(string name, IFractalCalculator c, Step f, double gr, double gi, int zl, int maxIter, double bail = Bail)
    {
        var v = ViewAt(S1921DeepFxTests.Misiurewicz(f, gr, gi, zl), zl);
        c.MaxIterations = maxIter; c.ColorMap = ColorPalette.BuiltIns[0];
        var it = Frame(c, v);
        Assert.Equal("PT", (string)((dynamic)c).LastPrecisionLabel);
        new S1921DeepFxTests(output).Check(name, new Got(it, new double[W * H], new double[W * H], default), f, v, maxIter, 0,
                                           derivative: false, bail: bail);
    }

    [Fact]
    public void TearDrop_Renders_Past_Double()
        => CheckCalc("TearDrop 1e320", new TearDropCalculator(W, H) { Quality = QualityPreset.Ultra }, S1918TearDropDeepTests.TearDropStep,
                     -1.1572550410888338, 0.2566039639387012, 320, 2000, 1024.0);

    [Fact]
    public void Generated_ZCubed_Renders_Past_Double()
        => CheckCalc("Generated z^3 1e320", new Gen.MandelbrotZ3Calculator(W, H), Power(3), -0.23026540906830995, 1.3276480964335278, 320, 2000);

    [Fact]
    public void Generated_Tricorn_Renders_Past_Double()
        => CheckCalc("Generated Tricorn 1e320", new Gen.TricornCalculator(W, H), TricornF, 0.7718445063460382, 1.336873900534257, 320, 2000);

    private static UserEquationCalculator User(string eq, double rotDeg = 0, int w = W, int h = H) => new(w, h)
    {
        AllowPerturbation = true, ColorMap = ColorPalette.BuiltIns[0],
        FractalParameters = new FractalParameters { UserEquationSource = eq, EscapeRadius = 512, UserEquationRotationDegrees = rotDeg },
    };

    [Fact]
    public void User_Equation_Renders_Past_Double()
        => CheckCalc("User z^2 + c 1e320", User("z^2 + c"), Power(2), -0.22815549365396182, 1.1151425080399373, 320, 2000);

    [Fact]
    public void Phoenix_Through_The_Calculator_Renders_Past_Double()
    {
        var v = ViewAt(PhoenixLanding.Value, TwoZl);
        var c = new EscapeTimeCalculator(W, H)
        {
            FractalType = FractalType.Phoenix, Quality = QualityPreset.Ultra, MaxIterations = 3000, ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = new FractalParameters(),
        };
        var it = Frame(c, v);
        Assert.Equal("PT", c.LastPrecisionLabel);
        Check2($"Phoenix calculator 1e{TwoZl}", it, new double[W * H], new double[W * H], S1912TwoStateTests.PhoenixStep, false, v, 3000, 0, derivative: false);
    }

    [Fact]
    public void User_Equation_Rotation_Is_The_Frame_Turned_A_Quarter()
    {
        // A square frame: at 90° the pixel offset (dx, dy) samples (−dy, dx), i.e. pixel (n − y, x) of the
        // unrotated frame (n = the side). The rotation is the engine's linear warp on the 2^s0 offsets.
        const int n = 32, zl = 320;
        var m = S1921DeepFxTests.Misiurewicz(Power(2), -0.22815549365396182, 1.1151425080399373, zl);
        var fx = FloatExp.FromLog10(Math.Log10(3.5 / n) - zl);
        int p = BigFloat.PrecisionForZoom(zl);
        var c = new ExactCentre(BigFloat.Add(m.Re.WithPrecision(p), BigFloat.FromDouble(0.37 * fx.M, p).ScaleB(fx.E), p),
                                BigFloat.Add(m.Im.WithPrecision(p), BigFloat.FromDouble(0.21 * fx.M, p).ScaleB(fx.E), p));
        int[] Render(double deg)
        {
            var u = User("z^2 + c", deg, n, n);
            u.MaxIterations = 2000;
            u.SetDeepCentre(c.ToDeep(), c);
            u.SetZoomLog10(zl);
            u.Calculate(CancellationToken.None);
            Assert.Equal("PT", u.LastPrecisionLabel);
            return u.IterationBuffer.Take(n * n).ToArray();
        }
        var flat = Render(0); var rot = Render(90);
        int same = 0, total = 0;
        for (int y = 1; y < n; y++)
            for (int x = 0; x < n; x++) { total++; if (rot[y * n + x] == flat[x * n + (n - y)]) same++; }
        output.WriteLine($"rotation 90° at 1e{zl}: {same}/{total}; {flat.Distinct().Count()} distinct");
        Assert.True(flat.Distinct().Count() >= 5, "precondition: structure");
        Assert.True(same >= total * 0.97, $"{same}/{total}");
    }

    [Theory]
    [InlineData(FractalType.TearDrop)]
    [InlineData(FractalType.GeneratedMandelbrotZ3)]
    [InlineData(FractalType.UserEquation)]
    [InlineData(FractalType.Phoenix)]
    public void The_Poster_Path_Hands_Over_The_Log_Zoom(FractalType t)
    {
        double zl = 400.5;
        var req = new PosterRequest
        {
            FractalType = t, Width = 8, Height = 8, Zoom = FracturingFog.ViewState.ZoomLog.FromLog10(zl), ZoomLog10 = zl,
            MaxIterations = 64, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard, FractalParameters = new FractalParameters(),
        };
        var calc = PosterRenderer.BuildCaptureCalculator(req);
        Assert.Equal(zl, Assert.IsAssignableFrom<IDeepZoom>(calc).ZoomLog10, 9);
    }

    [Fact]
    public void The_AA_Jitter_Moves_The_Exact_Centre_By_The_Log_Zooms_Pixel()
    {
        const double zl = 400;
        int p = BigFloat.PrecisionForZoom(zl) + 64;
        var c = new ExactCentre(BigFloat.FromDouble(-0.75, p), BigFloat.FromDouble(0.1, p));
        var calc = new EscapeTimeCalculator(W, H) { FractalType = FractalType.Julia };
        calc.SetDeepCentre(c.ToDeep(), c);
        calc.SetZoomLog10(zl);
        var j = new Supersampling.AltJitter(calc);
        j.Apply(0.375, -0.125);
        var e = Assert.NotNull(calc.CenterExact);
        var px = FloatExp.FromLog10(Math.Log10(3.5 / W) - zl);
        var pxB = BigFloat.FromDouble(px.M, p).ScaleB(px.E);
        Assert.Equal(0.375, BigFloat.Div(BigFloat.Sub(e.Re, c.Re, p), pxB, p).ToDouble(), 6);
        Assert.Equal(-0.125, BigFloat.Div(BigFloat.Sub(e.Im, c.Im, p), pxB, p).ToDouble(), 6);
        j.Restore();
        Assert.Equal(c, calc.CenterExact);
    }

    [Fact]
    public void The_Engine_Families_Have_No_Limit()
    {
        foreach (var t in new[] { FractalType.Phoenix, FractalType.Spider, FractalType.TearDrop,
                                  FractalType.GeneratedMandelbrotZ2, FractalType.GeneratedMandelbrotZ3, FractalType.GeneratedMandelbrotZ4,
                                  FractalType.GeneratedMandelbrotZ5, FractalType.GeneratedTricorn, FractalType.GeneratedBurningShip })
            Assert.Equal(QualityPreset.Deepest.ZoomMax, FamilyDepth.MaxZoomFor(t));
    }
}
