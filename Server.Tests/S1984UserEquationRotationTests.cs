// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1984 — rotation-domain colouring for User Equations (#1981; idea: pauldelbrot,
// fractalforums.org topic 5693).
//   • the golden-mean Siegel Julia set typed as an equation colours as EscapeTimeCalculator's Julia
//     pass does (exact f′ there; here exact f′ from the interpreter's dual, f″ by forward difference);
//   • a parameter plane (no seed) starts the product at the first iterate: z² + c at the golden
//     parameter, whose critical orbit lies on the Siegel disk's boundary, is neutral, not −∞;
//   • an equation reading prev is a two-variable map: z² + c + p·prev colours as the Phoenix pass;
//   • a Herman ring: f(z) = e^{2πit} z²(z − 4)/(1 − 4z) (the form of Milnor's example, bibliography
//     → milnor-dynamics-one-variable, unverified) maps the unit circle to itself as an analytic
//     diffeomorphism; t is found here by bisection on the rotation number (computed from the lift),
//     so the circle map has the golden-mean rotation number. Pixels on the circle classify as
//     rotation, the basins of 0 and ∞ do not;
//   • a non-holomorphic equation is left alone; off is byte-identical; the snapshot carries it.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1984UserEquationRotationTests(ITestOutputHelper output)
{
    private const int W = 160, H = 120;
    private static string Lit(Complex c) => $"({c.Real:R} + ({c.Imaginary:R})*i)";

    private static UserEquationCalculator UserEq(string src, string seed, bool on, double cx = 0, double cy = 0, double zoom = 1.3,
                                                 int w = W, int h = H, int maxIt = 2000, Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters { UserEquationSource = src, UserEquationSeed = seed, RotationDomainColoring = on };
        tweak?.Invoke(p);
        var calc = new UserEquationCalculator(w, h)
        {
            MaxIterations = maxIt, CenterX = cx, CenterY = cy, Zoom = zoom,
            ColorMap = new HsvPalette(), FractalParameters = p,
        };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    private static int MaxCh(uint a, uint b)
    {
        int m = 0;
        for (int k = 0; k < 4; k++) m = Math.Max(m, Math.Abs((int)((a >> (8 * k)) & 0xFF) - (int)((b >> (8 * k)) & 0xFF)));
        return m;
    }

    [Fact]
    public void Siegel_Equation_Matches_The_Julia_Pass()
    {
        var u = UserEq("z^2 + " + Lit(S1981RotationDomainTests.SiegelC), "c", on: true);
        var j = new EscapeTimeCalculator(W, H)
        {
            FractalType = FractalType.Julia, MaxIterations = 2000, Zoom = 1.3, ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { JuliaC = S1981RotationDomainTests.SiegelC, RotationDomainColoring = true },
        };
        j.Calculate();
        var su = u.LastRotationDomainStats; var sj = j.LastRotationDomainStats;
        output.WriteLine($"equation: rotation {su.Rotation}, chaotic {su.Chaotic}, attracting {su.Attracting}; Julia pass: {sj.Rotation}/{sj.Chaotic}/{sj.Attracting}");
        Assert.Null(u.LastRotationDomainSkip);
        int both = 0, close = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (u.IterationBuffer[i] < 2000 || j.IterationBuffer[i] < 2000) continue;
            both++;
            if (MaxCh(u.ColorBuffer[i], j.ColorBuffer[i]) <= 2) close++;
        }
        Assert.True(both >= 5000, $"precondition ({both})");
        Assert.True(su.Rotation >= both * 0.95, $"{su.Rotation}/{both}");
        Assert.True(close >= both * 0.99, $"colours within 2 levels on {close}/{both}");
    }

    [Fact]
    public void Parameter_Plane_Starts_At_The_First_Iterate()
    {
        var c = S1981RotationDomainTests.SiegelC;
        var u = UserEq("z^2 + c", "", on: true, cx: c.Real, cy: c.Imaginary, zoom: 1e8, w: 2, h: 2, maxIt: 3000);
        var st = u.LastRotationDomainStats;
        output.WriteLine($"golden parameter: rotation {st.Rotation}, chaotic {st.Chaotic}, attracting {st.Attracting}");
        Assert.True(st.Rotation >= 3, $"{st.Rotation} of 4 neutral");
        // A constant seed (dz₀/dc = 0) is a parameter plane too.
        var seeded = UserEq("z^2 + c", "0", on: true, cx: c.Real, cy: c.Imaginary, zoom: 1e8, w: 2, h: 2, maxIt: 3000);
        Assert.Equal(st, seeded.LastRotationDomainStats);
    }

    // The pass stops where the render's convergence condition stops (those pixels keep their colour).
    [Fact]
    public void Convergence_Condition_Stops_The_Pass()
    {
        string src = "z^2 + " + Lit(S1981RotationDomainTests.SiegelC);
        var on = UserEq(src, "c", on: true, tweak: p => p.UserEquationBailoutCondition = "abs(z) < 10");
        var off = UserEq(src, "c", on: false, tweak: p => p.UserEquationBailoutCondition = "abs(z) < 10");
        Assert.Equal(0, on.LastRotationDomainStats.Rotation);
        Assert.Equal(off.ColorBuffer, on.ColorBuffer);
    }

    [Fact]
    public void Prev_Equation_Matches_The_Phoenix_Pass()
    {
        Assert.True(UserEquationCalculator.UsesPrev(SandboxExpression.Parse("z^2 + c - 0.5*prev")));
        Assert.False(UserEquationCalculator.UsesPrev(SandboxExpression.Parse("z^2 + c")));
        var u = UserEq("z^2 + c - 0.5*prev", "", on: true, cx: 0, cy: 0, zoom: 1.0, maxIt: 1500);
        var ph = new EscapeTimeCalculator(W, H)
        {
            FractalType = FractalType.Phoenix, MaxIterations = 1500, Zoom = 1.0, ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { PhoenixP = new Complex(-0.5, 0), RotationDomainColoring = true },
        };
        ph.Calculate();
        var offU = UserEq("z^2 + c - 0.5*prev", "", on: false, cx: 0, cy: 0, zoom: 1.0, maxIt: 1500);
        var phOff = new EscapeTimeCalculator(W, H)
        {
            FractalType = FractalType.Phoenix, MaxIterations = 1500, Zoom = 1.0, ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { PhoenixP = new Complex(-0.5, 0) },
        };
        phOff.Calculate();
        int both = 0, same = 0, changed = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (u.IterationBuffer[i] < 1500 || ph.IterationBuffer[i] < 1500) continue;
            both++;
            bool cu = u.ColorBuffer[i] != offU.ColorBuffer[i];
            bool cp = ph.ColorBuffer[i] != phOff.ColorBuffer[i];
            if (cu == cp) same++;
            if (cu) changed++;
        }
        var su = u.LastRotationDomainStats; var sp = ph.LastRotationDomainStats;
        output.WriteLine($"prev equation {su.Rotation}/{su.Chaotic}/{su.Attracting}; Phoenix pass {sp.Rotation}/{sp.Chaotic}/{sp.Attracting}; " +
                         $"both in-set {both}, same recolour decision {same}, recoloured {changed}");
        Assert.True(both >= 1000, $"precondition ({both})");
        Assert.True(same >= both * 0.98, $"{same}/{both}");
    }

    // ── Herman ring ─────────────────────────────────────────────────────────

    private static double Rho(double t, int n)
    {
        // Lift of the circle map: F(x) = x + 2πt + Arg(e^{ix}·M(e^{ix})), M(z) = (z − 4)/(1 − 4z);
        // the Arg stays within ±0.51, so the principal value is the continuous lift.
        double x = 0.1, sum = 0;
        for (int k = 0; k < n; k++)
        {
            var z = Complex.FromPolarCoordinates(1, x);
            double dx = 2 * Math.PI * t + (z * (z - 4) / (1 - 4 * z)).Phase;
            sum += dx; x += dx;
        }
        return sum / (2 * Math.PI * n);
    }

    internal static double HermanT()
    {
        double target = (Math.Sqrt(5) - 1) / 2, lo = 0, hi = 1;
        for (int k = 0; k < 40; k++)
        {
            double mid = 0.5 * (lo + hi);
            if (Rho(mid, 100_000) < target) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    [Fact]
    public void Herman_Ring_Pixels_Classify_As_Rotation()
    {
        double t = HermanT();
        double rho = Rho(t, 200_000);
        output.WriteLine($"t = {t:R}, rotation number {rho:F7}");
        Assert.InRange(rho, 0.618, 0.6181);

        var a = Complex.FromPolarCoordinates(1, 2 * Math.PI * t);
        var u = UserEq(Lit(a) + " * z^2 * (z - 4) / (1 - 4*z)", "c", on: true, zoom: 1.0, maxIt: 3000);
        double scale = 3.5 / Math.Max(W, H);
        int ring = 0, ringRot = 0, inner = 0, innerRot = 0;
        var off = UserEq(Lit(a) + " * z^2 * (z - 4) / (1 - 4*z)", "c", on: false, zoom: 1.0, maxIt: 3000);
        uint centre = new FractalParameters().RotationDomainColor;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                double r = new Complex((x - W * 0.5) * scale, (y - H * 0.5) * scale).Magnitude;
                bool recoloured = u.ColorBuffer[i] != off.ColorBuffer[i];
                if (Math.Abs(r - 1) < 0.02) { ring++; if (recoloured && u.IterationBuffer[i] >= 3000) ringRot++; }
                if (r < 0.1) { inner++; if (recoloured) innerRot++; }
            }
        var st = u.LastRotationDomainStats;
        output.WriteLine($"ring |z|≈1: {ringRot}/{ring} rotation; near 0: {innerRot}/{inner} recoloured; stats {st.Rotation}/{st.Chaotic}/{st.Attracting}");
        Assert.True(ring >= 100, $"precondition ({ring})");
        Assert.True(ringRot >= ring * 0.9, $"{ringRot}/{ring}");
        Assert.Equal(0, innerRot);
        Assert.True(st.Rotation > 20 * st.Chaotic, "a few chaotic pixels on the Julia set's edge at most");
    }

    // ── off, non-holomorphic, batch parity ─────────────────────────────────

    [Fact]
    public void Off_Identical_Non_Holomorphic_Skipped_And_Carried_By_The_Snapshot()
    {
        string src = "z^2 + " + Lit(S1981RotationDomainTests.SiegelC);
        var off = UserEq(src, "c", on: false);
        var offT = UserEq(src, "c", on: false, tweak: p => { p.RotationDomainEpsilon = 0.2; p.RotationDomainColor = 0xFF010203; });
        Assert.Equal(off.ColorBuffer, offT.ColorBuffer);
        Assert.Null(off.AdaptiveAaBoundaryDistancePx());

        var nh = UserEq("conj(z)^2 + " + Lit(S1981RotationDomainTests.SiegelC), "c", on: true);
        var nhOff = UserEq("conj(z)^2 + " + Lit(S1981RotationDomainTests.SiegelC), "c", on: false);
        Assert.NotNull(nh.LastRotationDomainSkip);
        Assert.Equal(nhOff.ColorBuffer, nh.ColorBuffer);

        var on = UserEq(src, "c", on: true);
        Assert.NotNull(on.AdaptiveAaBoundaryDistancePx());
        // A reused calculator: the next frame with the option off carries nothing over.
        on.FractalParameters.RotationDomainColoring = false;
        on.Calculate(CancellationToken.None);
        Assert.Null(on.AdaptiveAaBoundaryDistancePx());
        Assert.Equal(default, on.LastRotationDomainStats);
        Assert.Equal(off.ColorBuffer, on.ColorBuffer);
        // Interior alpha scales the stamped colours (the in-set colour is alpha-scaled).
        var half = UserEq(src, "c", on: true);
        half.InteriorAlpha = 128; half.Calculate(CancellationToken.None);
        for (int i = 0; i < W * H; i++) if (half.IterationBuffer[i] >= 2000) Assert.Equal(128u, half.ColorBuffer[i] >> 24);

        var snap = RegionFractalParams.Snapshot(FractalType.UserEquation, new FractalParameters { RotationDomainColoring = true, RotationDomainBound = 9 })!;
        var q = new FractalParameters();
        snap.ApplyTo(q);
        Assert.True(q.RotationDomainColoring);
        Assert.Equal(9, q.RotationDomainBound);
        Assert.Null(RegionFractalParams.Snapshot(FractalType.UserEquation, new FractalParameters())?.RotationDomainColoring);
    }
}
