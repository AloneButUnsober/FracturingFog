// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ThreeBody.cs (#1943, gravitational-scattering set §2.6)
//
// The planar circular restricted three-body problem in the rotating frame: two primaries
// of masses 1 − μ and μ on circular orbits about their centre of mass, unit separation,
// unit angular velocity, so they rest at (−μ, 0) and (1 − μ, 0); a massless test particle
// moves under
//
//     ẍ − 2ẏ = ∂Ω/∂x,   ÿ + 2ẋ = ∂Ω/∂y,   Ω = (x² + y²)/2 + (1 − μ)/r₁ + μ/r₂,
//
// with the Jacobi constant C = 2Ω − (ẋ² + ẏ²) conserved. (Some authors add the constant
// μ(1 − μ)/2 to Ω; FF does not, so C here is the plain 2Ω − v².) Motion is confined to
// 2Ω ≥ C; the zero-velocity curve 2Ω = C bounds it.
//
// Two views of the same system:
//  • Fates (Nagler 2004 for μ = 1/2, the Copenhagen problem; Nagler 2005 for general μ):
//    each launch at Jacobi constant C either crashes into a primary (comes within the crash
//    radius of it), escapes (passes the escape radius), or stays bounded up to the time
//    limit. Traced with the shared Dormand-Prince core (ScatteringIntegrator, #1923).
//  • Lagrange basins (Zotos 2016 used the same multivariate Newton-Raphson map on ∇Ω, with
//    oblateness and radiation pressure added): each start point is iterated by Newton's
//    method on ∇Ω = 0 and coloured by which of the five equilibria L1-L5 it reaches.

using System;

using FracturingFog.Models;

namespace FracturingFog.Scattering;

/// <summary>#1943 — restricted three-body equations of motion and fate tests, for
/// <see cref="ScatteringIntegrator"/>. State = (x, y, vx, vy) in the rotating frame.</summary>
public struct ThreeBodySystem : IScatteringSystem
{
    /// <summary>Fate gates: crash into primary 1 (mass 1 − μ), primary 2 (mass μ), escape.</summary>
    public const int Primary1Gate = 0, Primary2Gate = 1, EscapeGate = 2, GateCount = 3;

    public double Mu;
    public double CrashRadius;
    public double EscapeRadius;
    public double TimeCap;
    /// <summary>Time travelled so far.</summary>
    public double Time;
    /// <summary>Set by <see cref="Classify"/>: which gate ended the trajectory (−1 = bounded).</summary>
    public int Gate;

    public readonly int Dimension => 4;

    /// <summary>The effective potential Ω.</summary>
    public static double Omega(double mu, double x, double y)
    {
        double r1 = Math.Sqrt((x + mu) * (x + mu) + y * y);
        double r2 = Math.Sqrt((x - 1.0 + mu) * (x - 1.0 + mu) + y * y);
        return 0.5 * (x * x + y * y) + (1.0 - mu) / r1 + mu / r2;
    }

    /// <summary>∇Ω at (x, y).</summary>
    public static (double Ox, double Oy) Gradient(double mu, double x, double y)
    {
        double dx1 = x + mu, dx2 = x - 1.0 + mu;
        double r1s = dx1 * dx1 + y * y, r2s = dx2 * dx2 + y * y;
        double r13 = r1s * Math.Sqrt(r1s), r23 = r2s * Math.Sqrt(r2s);
        double a = (1.0 - mu) / r13, b = mu / r23;
        return (x - a * dx1 - b * dx2, y - a * y - b * y);
    }

    /// <summary>The Jacobi constant C = 2Ω − v² of state (x, y, vx, vy).</summary>
    public static double Jacobi(double mu, ReadOnlySpan<double> s)
        => 2.0 * Omega(mu, s[0], s[1]) - (s[2] * s[2] + s[3] * s[3]);

    public readonly void Derivative(ReadOnlySpan<double> s, Span<double> ds)
    {
        var (ox, oy) = Gradient(Mu, s[0], s[1]);
        ds[0] = s[2];
        ds[1] = s[3];
        ds[2] = ox + 2.0 * s[3];
        ds[3] = oy - 2.0 * s[2];
    }

    public ScatterFate Classify(ReadOnlySpan<double> s, out int body)
    {
        body = -1;
        double x = s[0], y = s[1];
        double r1 = Math.Sqrt((x + Mu) * (x + Mu) + y * y);
        double r2 = Math.Sqrt((x - 1.0 + Mu) * (x - 1.0 + Mu) + y * y);
        if (r1 < CrashRadius) { Gate = Primary1Gate; return ScatterFate.Captured; }
        if (r2 < CrashRadius) { Gate = Primary2Gate; return ScatterFate.Captured; }
        if (x * x + y * y > EscapeRadius * EscapeRadius) { Gate = EscapeGate; return ScatterFate.Escaped; }
        if (Time >= TimeCap) { Gate = -1; return ScatterFate.Undecided; }
        return ScatterFate.Running;
    }

    /// <summary>Close to a primary the step is capped by a fraction of the distance to it (above
    /// the crash radius), so the controller cannot jump over a near collision.</summary>
    public readonly double MaxStep(ReadOnlySpan<double> s)
    {
        double x = s[0], y = s[1];
        double r1 = Math.Sqrt((x + Mu) * (x + Mu) + y * y);
        double r2 = Math.Sqrt((x - 1.0 + Mu) * (x - 1.0 + Mu) + y * y);
        double r = Math.Min(r1, r2);
        return Math.Clamp(0.1 * r, 1e-9, 0.1);
    }

    public void Observe(ReadOnlySpan<double> s, double h) => Time += h;

    /// <summary>The five equilibria (L1, L2, L3 collinear; L4, L5 at the equilateral points),
    /// found independently of the Newton map: the collinear ones by bisection of ∂Ω/∂x on the
    /// three intervals of the x axis between and beyond the primaries.</summary>
    public static (double X, double Y)[] LagrangePoints(double mu)
    {
        double x1 = -mu, x2 = 1.0 - mu;
        double Fx(double x) => Gradient(mu, x, 0.0).Ox;
        const double eps = 1e-9;
        double l1 = Bisect(Fx, x1 + eps, x2 - eps);          // between the primaries
        double l2 = Bisect(Fx, x2 + eps, x2 + 2.0);           // beyond the smaller primary
        double l3 = Bisect(Fx, x1 - 2.0, x1 - eps);           // beyond the larger primary
        double h = Math.Sqrt(3.0) / 2.0;
        return new[] { (l1, 0.0), (l2, 0.0), (l3, 0.0), (0.5 - mu, h), (0.5 - mu, -h) };
    }

    private static double Bisect(Func<double, double> f, double a, double b)
    {
        double fa = f(a);
        for (int i = 0; i < 200; i++)
        {
            double m = 0.5 * (a + b);
            double fm = f(m);
            if (Math.Sign(fm) == Math.Sign(fa)) { a = m; fa = fm; } else b = m;
            if (b - a < 1e-15) break;
        }
        return 0.5 * (a + b);
    }
}

/// <summary>#1943 — how one launch or Newton start ended.</summary>
public readonly struct ThreeBodyOutcome
{
    /// <summary>False where the launch is not allowed (2Ω &lt; C) or a start sits on a primary.</summary>
    public bool Valid { get; init; }
    /// <summary>Fates: <see cref="ThreeBodySystem.Primary1Gate"/> / <see cref="ThreeBodySystem.Primary2Gate"/> /
    /// <see cref="ThreeBodySystem.EscapeGate"/>, −1 bounded. Lagrange: 0..4 = L1..L5, −1 not converged.</summary>
    public int Gate { get; init; }
    /// <summary>Fates: time to the fate. Lagrange: Newton iterations.</summary>
    public double Time { get; init; }
    public int Steps { get; init; }
}

/// <summary>#1943 — one restricted three-body scene built from <see cref="FractalParameters"/>.</summary>
public sealed class ThreeBodyScene
{
    public double Mu { get; }
    public ThreeBodyView View { get; }
    public double Jacobi { get; }
    public ThreeBodyLaunch Launch { get; }
    /// <summary>FixedAngle launch direction, radians.</summary>
    public double LaunchAngle { get; }
    public double CrashRadius { get; }
    public double EscapeRadius { get; }
    public double TimeCap { get; }
    public ScatterSettings Settings { get; }
    public int NewtonIterations { get; }
    public (double X, double Y)[] LagrangePoints { get; }

    /// <summary>Newton converges when a step is shorter than this; the end point is then matched
    /// to the nearest equilibrium within <see cref="MatchRadius"/>.</summary>
    public const double NewtonStepTolerance = 1e-12, MatchRadius = 1e-6;

    public ThreeBodyScene(double mu, ThreeBodyView view, double jacobi, ThreeBodyLaunch launch, double launchAngleDeg,
                          double crashRadius, double escapeRadius, double timeCap, double tolerance, int newtonIterations)
    {
        Mu = double.IsFinite(mu) ? Math.Clamp(mu, 1e-6, 0.5) : 0.5;
        View = view;
        Jacobi = double.IsFinite(jacobi) ? jacobi : 3.0;
        Launch = launch;
        LaunchAngle = (double.IsFinite(launchAngleDeg) ? launchAngleDeg : 0.0) * Math.PI / 180.0;
        CrashRadius = double.IsFinite(crashRadius) ? Math.Clamp(crashRadius, 1e-6, 0.4) : 0.01;
        EscapeRadius = double.IsFinite(escapeRadius) ? Math.Clamp(escapeRadius, 2.0, 1000.0) : 10.0;
        TimeCap = double.IsFinite(timeCap) ? Math.Clamp(timeCap, 1.0, 100000.0) : 100.0;
        double tol = double.IsFinite(tolerance) ? Math.Clamp(tolerance, 1e-13, 1e-3) : 1e-9;
        int steps = (int)Math.Min(int.MaxValue / 16, TimeCap / 1e-4);
        Settings = new ScatterSettings { RelTol = tol, AbsTol = tol * 1e-3, MaxSteps = steps, InitialStep = 0.01 };
        NewtonIterations = Math.Clamp(newtonIterations, 1, 10000);
        LagrangePoints = ThreeBodySystem.LagrangePoints(Mu);
    }

    public static ThreeBodyScene Build(FractalParameters p)
        => new(p.ThreeBodyMassRatio, p.ThreeBodyView, p.ThreeBodyJacobi, p.ThreeBodyLaunch, p.ThreeBodyLaunchAngle,
               p.ThreeBodyCrashRadius, p.ThreeBodyEscapeRadius, p.ThreeBodyMaxTime, p.ThreeBodyTolerance,
               p.ThreeBodyNewtonIterations);

    /// <summary>Launch state at (x, y): speed √(2Ω − C), direction from the launch rule (about the
    /// centre of mass, the origin). False where 2Ω &lt; C (inside the zero-velocity curve's forbidden
    /// region) or within the crash radius of a primary.</summary>
    public bool LaunchState(double x, double y, Span<double> s)
    {
        double r1 = Math.Sqrt((x + Mu) * (x + Mu) + y * y);
        double r2 = Math.Sqrt((x - 1.0 + Mu) * (x - 1.0 + Mu) + y * y);
        if (r1 < CrashRadius || r2 < CrashRadius) return false;
        double v2 = 2.0 * ThreeBodySystem.Omega(Mu, x, y) - Jacobi;
        if (!(v2 >= 0)) return false;
        double v = Math.Sqrt(v2);
        double r = Math.Sqrt(x * x + y * y);
        double dx, dy;
        switch (Launch)
        {
            case ThreeBodyLaunch.Retrograde:
                if (r > 0) { dx = y / r; dy = -x / r; } else { dx = 0.0; dy = -1.0; }
                break;
            case ThreeBodyLaunch.Radial:
                if (r > 0) { dx = x / r; dy = y / r; } else { dx = 1.0; dy = 0.0; }
                break;
            case ThreeBodyLaunch.FixedAngle:
                dx = Math.Cos(LaunchAngle); dy = Math.Sin(LaunchAngle);
                break;
            default:   // Prograde: anticlockwise about the origin, the primaries' sense of rotation
                if (r > 0) { dx = -y / r; dy = x / r; } else { dx = 0.0; dy = 1.0; }
                break;
        }
        s[0] = x; s[1] = y; s[2] = v * dx; s[3] = v * dy;
        return true;
    }

    /// <summary>Trace (Fates view) or iterate Newton (Lagrange view) from (x, y).
    /// <paramref name="s"/> (length ≥ 4) ends as the final state.</summary>
    public ThreeBodyOutcome Trace(double x, double y, Span<double> s)
        => View == ThreeBodyView.LagrangeBasins ? Newton(x, y) : Fate(x, y, s);

    private ThreeBodyOutcome Fate(double x, double y, Span<double> s)
    {
        var none = default(NoPathRecorder);
        return Trace(x, y, s, ref none);
    }

    /// <summary>#1993 — the Fates trace from (x, y) with every state recorded (the launch, then
    /// each accepted step) for the Math Plane trajectory statement. Always the motion, whatever
    /// <see cref="View"/> is.</summary>
    public ThreeBodyOutcome Trace<TRec>(double x, double y, Span<double> s, ref TRec rec) where TRec : IPathRecorder
    {
        if (!LaunchState(x, y, s)) return new ThreeBodyOutcome { Valid = false, Gate = -1 };
        rec.Record(s);
        var sys = new ThreeBodySystem
        {
            Mu = Mu, CrashRadius = CrashRadius, EscapeRadius = EscapeRadius, TimeCap = TimeCap, Gate = -1,
        };
        if (x * x + y * y > EscapeRadius * EscapeRadius)
            return new ThreeBodyOutcome { Valid = true, Gate = ThreeBodySystem.EscapeGate };
        var settings = Settings;
        var wrapped = new RecordingSystem<ThreeBodySystem, TRec>(sys, rec);
        var o = ScatteringIntegrator.Trace(ref wrapped, s, in settings);
        sys = wrapped.Inner; rec = wrapped.Recorder;
        int gate = o.Fate is ScatterFate.Captured or ScatterFate.Escaped ? sys.Gate : -1;
        return new ThreeBodyOutcome { Valid = true, Gate = gate, Time = sys.Time, Steps = o.Steps };
    }

    /// <summary>Multivariate Newton-Raphson on ∇Ω = 0 with the analytic Hessian.</summary>
    public ThreeBodyOutcome Newton(double x, double y)
    {
        double mu = Mu;
        for (int k = 1; k <= NewtonIterations; k++)
        {
            double dx1 = x + mu, dx2 = x - 1.0 + mu;
            double r1s = dx1 * dx1 + y * y, r2s = dx2 * dx2 + y * y;
            if (r1s < 1e-24 || r2s < 1e-24) return new ThreeBodyOutcome { Valid = true, Gate = -1, Time = k };
            double r1 = Math.Sqrt(r1s), r2 = Math.Sqrt(r2s);
            double a3 = (1.0 - mu) / (r1s * r1), b3 = mu / (r2s * r2);
            double a5 = 3.0 * (1.0 - mu) / (r1s * r1s * r1), b5 = 3.0 * mu / (r2s * r2s * r2);
            double gx = x - a3 * dx1 - b3 * dx2;
            double gy = y - a3 * y - b3 * y;
            double hxx = 1.0 - a3 - b3 + a5 * dx1 * dx1 + b5 * dx2 * dx2;
            double hyy = 1.0 - a3 - b3 + (a5 + b5) * y * y;
            double hxy = (a5 * dx1 + b5 * dx2) * y;
            double det = hxx * hyy - hxy * hxy;
            if (!(Math.Abs(det) > 1e-300) || !double.IsFinite(det)) return new ThreeBodyOutcome { Valid = true, Gate = -1, Time = k };
            double sx = (hyy * gx - hxy * gy) / det;
            double sy = (hxx * gy - hxy * gx) / det;
            x -= sx; y -= sy;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x * x + y * y > 1e12)
                return new ThreeBodyOutcome { Valid = true, Gate = -1, Time = k };
            if (sx * sx + sy * sy < NewtonStepTolerance * NewtonStepTolerance)
            {
                int best = -1;
                double bestD = MatchRadius * MatchRadius;
                for (int i = 0; i < LagrangePoints.Length; i++)
                {
                    double ex = x - LagrangePoints[i].X, ey = y - LagrangePoints[i].Y;
                    double d = ex * ex + ey * ey;
                    if (d < bestD) { bestD = d; best = i; }
                }
                return new ThreeBodyOutcome { Valid = true, Gate = best, Time = k, Steps = k };
            }
        }
        return new ThreeBodyOutcome { Valid = true, Gate = -1, Time = NewtonIterations };
    }
}
