// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/OrbitMap.cs
//
// Interesting-location finder S7 (#1191): the families beyond z² + c as real
// 2-D maps. One step takes (z, c) in R² × R² to z' and supplies the two 2×2
// Jacobians A = ∂z'/∂z and B = ∂z'/∂c. A holomorphic map is the special case
// where A and B are rotation-scalings. The fold maps (Burning Ship), the
// anti-holomorphic Tricorn and arbitrary User Equations then share one Newton
// engine (GeneralFinder) — the Kalles Fraktaler approach of Newton in R².
//
// Every map here must match its calculator's iteration formula exactly; the
// tests check that by rendering found points through the real calculators.

using System;
using System.Numerics;

using FracturingFog.Models;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>A point / vector in R².</summary>
public readonly record struct V2(double X, double Y)
{
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator *(double s, V2 a) => new(s * a.X, s * a.Y);
    public double Length => SMath.Sqrt(X * X + Y * Y);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

/// <summary>A 2×2 real matrix [[A, B], [C, D]].</summary>
public readonly record struct M2(double A, double B, double C, double D)
{
    public static readonly M2 Identity = new(1, 0, 0, 1);
    public static readonly M2 Zero = new(0, 0, 0, 0);

    /// <summary>Multiplication by the complex number (re, im).</summary>
    public static M2 Complex(double re, double im) => new(re, -im, im, re);

    public static M2 operator +(M2 m, M2 n) => new(m.A + n.A, m.B + n.B, m.C + n.C, m.D + n.D);
    public static M2 operator -(M2 m, M2 n) => new(m.A - n.A, m.B - n.B, m.C - n.C, m.D - n.D);
    public static M2 operator *(M2 m, M2 n)
        => new(m.A * n.A + m.B * n.C, m.A * n.B + m.B * n.D,
               m.C * n.A + m.D * n.C, m.C * n.B + m.D * n.D);
    public static V2 operator *(M2 m, V2 v) => new(m.A * v.X + m.B * v.Y, m.C * v.X + m.D * v.Y);

    public double Det => A * D - B * C;
    public double Norm => SMath.Sqrt(A * A + B * B + C * C + D * D);
    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D);

    /// <summary>Solve m·x = v; false when m is (numerically) singular.</summary>
    public bool TrySolve(V2 v, out V2 x)
    {
        double det = Det;
        double scale = SMath.Max(1e-300, Norm * Norm);
        if (!(SMath.Abs(det) > 1e-30 * scale) || !double.IsFinite(det)) { x = default; return false; }
        x = new V2((D * v.X - B * v.Y) / det, (A * v.Y - C * v.X) / det);
        return x.IsFinite;
    }

    /// <summary>Largest |eigenvalue|.</summary>
    public double SpectralRadius
    {
        get
        {
            double tr = A + D, det = Det;
            double disc = tr * tr / 4 - det;
            if (disc >= 0)
            {
                double s = SMath.Sqrt(disc);
                return SMath.Max(SMath.Abs(tr / 2 + s), SMath.Abs(tr / 2 - s));
            }
            return SMath.Sqrt(SMath.Abs(det));   // complex pair: |λ|² = det
        }
    }

    /// <summary>True when m is a rotation-scaling (multiplication by a complex
    /// number), to a relative tolerance.</summary>
    public bool IsConformal(double tol = 1e-6)
        => SMath.Abs(A - D) + SMath.Abs(B + C) <= tol * SMath.Max(1e-300, Norm);
}

/// <summary>A family's iteration as a real 2-D map (see file header).</summary>
public interface IOrbitMap
{
    string Name { get; }
    /// <summary>Escape radius the family's calculator uses.</summary>
    double EscapeRadius { get; }
    /// <summary>The orbit's start z₀(c) and ∂z₀/∂c.</summary>
    void Start(V2 c, out V2 z0, out M2 dz0dc);
    /// <summary>One step: z' = f(z, c), A = ∂z'/∂z, B = ∂z'/∂c.</summary>
    void Step(V2 z, V2 c, out V2 next, out M2 a, out M2 b);
    /// <summary>One step without derivatives (escape tests).</summary>
    V2 Next(V2 z, V2 c);
    /// <summary>Relative cost of <see cref="Step"/> in plain steps (work budgets).</summary>
    int StepCost { get; }
}

/// <summary>z^d + c, z₀ = 0 (Multibrot; d = 2 is the Mandelbrot set).</summary>
public sealed class MultibrotMap : IOrbitMap
{
    public MultibrotMap(int d) { Degree = SMath.Max(2, d); }
    public int Degree { get; }
    public string Name => $"z^{Degree} + c";
    public double EscapeRadius => 512;

    public void Start(V2 c, out V2 z0, out M2 dz0dc) { z0 = default; dz0dc = M2.Zero; }

    public int StepCost => 1;
    public V2 Next(V2 z, V2 c) { Step(z, c, out var n, out _, out _); return n; }

    public void Step(V2 z, V2 c, out V2 next, out M2 a, out M2 b)
    {
        // w = z^(d-1); next = z·w + c; ∂/∂z = d·w.
        double wr = 1, wi = 0;
        for (int k = 1; k < Degree; k++) (wr, wi) = (wr * z.X - wi * z.Y, wr * z.Y + wi * z.X);
        next = new V2(z.X * wr - z.Y * wi + c.X, z.X * wi + z.Y * wr + c.Y);
        a = M2.Complex(Degree * wr, Degree * wi);
        b = M2.Identity;
    }
}

/// <summary>Tricorn / Mandelbar: z' = conj(z)² + c, z₀ = 0 (anti-holomorphic).</summary>
public sealed class TricornMap : IOrbitMap
{
    public string Name => "conj(z)^2 + c";
    public double EscapeRadius => 512;
    public void Start(V2 c, out V2 z0, out M2 dz0dc) { z0 = default; dz0dc = M2.Zero; }

    public int StepCost => 1;
    public V2 Next(V2 z, V2 c) => new(z.X * z.X - z.Y * z.Y + c.X, -2 * z.X * z.Y + c.Y);

    public void Step(V2 z, V2 c, out V2 next, out M2 a, out M2 b)
    {
        next = new V2(z.X * z.X - z.Y * z.Y + c.X, -2 * z.X * z.Y + c.Y);
        a = new M2(2 * z.X, -2 * z.Y, -2 * z.Y, -2 * z.X);
        b = M2.Identity;
    }
}

/// <summary>Burning Ship: z' = (x² − y² + cx, 2|x||y| + cy), z₀ = 0. The
/// abs folds make it piecewise polynomial; the Jacobian is the one of the
/// active piece (constant sign pattern inside a Newton basin).</summary>
public sealed class BurningShipMap : IOrbitMap
{
    public string Name => "Burning Ship";
    public double EscapeRadius => 512;
    public void Start(V2 c, out V2 z0, out M2 dz0dc) { z0 = default; dz0dc = M2.Zero; }

    public int StepCost => 1;
    public V2 Next(V2 z, V2 c) => new(z.X * z.X - z.Y * z.Y + c.X, 2 * SMath.Abs(z.X) * SMath.Abs(z.Y) + c.Y);

    public void Step(V2 z, V2 c, out V2 next, out M2 a, out M2 b)
    {
        double ax = SMath.Abs(z.X), ay = SMath.Abs(z.Y);
        double sx = z.X >= 0 ? 1 : -1, sy = z.Y >= 0 ? 1 : -1;
        next = new V2(z.X * z.X - z.Y * z.Y + c.X, 2 * ax * ay + c.Y);
        a = new M2(2 * z.X, -2 * z.Y, 2 * sx * ay, 2 * ax * sy);
        b = M2.Identity;
    }
}

/// <summary>A User Equation step (Sandbox DSL) with an optional z₀ seed
/// expression over c. Jacobians by central finite differences, so it works
/// for non-holomorphic equations (conj, abs, re, im) too.</summary>
public sealed class UserEquationMap : IOrbitMap
{
    private readonly SandboxExpression _step;
    private readonly SandboxExpression? _seed;
    // One env per thread: the interpreter writes its slots while evaluating.
    [ThreadStatic] private static SbxVal[]? t_env;
    [ThreadStatic] private static SbxVal[]? t_seedEnv;
    [ThreadStatic] private static object? t_owner;

    /// <summary>Relative finite-difference step.</summary>
    public const double DiffStep = 1e-7;

    public UserEquationMap(SandboxExpression step, SandboxExpression? seed, double escapeRadius, string name)
    {
        _step = step; _seed = seed; Name = name;
        EscapeRadius = escapeRadius > 0 ? escapeRadius : SMath.Sqrt(1024.0);   // the calculator's default |z|² = 1024
    }

    public string Name { get; }
    public double EscapeRadius { get; }
    /// <summary>Nine interpreter evaluations per derivative step.</summary>
    public int StepCost => 9;
    public V2 Next(V2 z, V2 c) => F(z, c);

    private (SbxVal[] Env, SbxVal[]? SeedEnv) Envs()
    {
        if (!ReferenceEquals(t_owner, this) || t_env == null)
        {
            t_owner = this;
            t_env = _step.NewEnv();
            t_seedEnv = _seed?.NewEnv();
        }
        return (t_env, t_seedEnv);
    }

    private V2 F(V2 z, V2 c)
    {
        var (env, _) = Envs();
        var zc = new Complex(z.X, z.Y);
        var r = _step.EvalStep(zc, new Complex(c.X, c.Y), 0, env, zc);
        return new V2(r.Real, r.Imaginary);
    }

    private V2 Z0(V2 c)
    {
        if (_seed == null) return default;
        var (_, senv) = Envs();
        var r = _seed.EvalStep(Complex.Zero, new Complex(c.X, c.Y), 0, senv!);
        return new V2(r.Real, r.Imaginary);
    }

    public void Start(V2 c, out V2 z0, out M2 dz0dc)
    {
        z0 = Z0(c);
        if (_seed == null) { dz0dc = M2.Zero; return; }
        double h = DiffStep * SMath.Max(1, c.Length);
        var gx = (1 / (2 * h)) * (Z0(c + new V2(h, 0)) - Z0(c - new V2(h, 0)));
        var gy = (1 / (2 * h)) * (Z0(c + new V2(0, h)) - Z0(c - new V2(0, h)));
        dz0dc = new M2(gx.X, gy.X, gx.Y, gy.Y);
    }

    public void Step(V2 z, V2 c, out V2 next, out M2 a, out M2 b)
    {
        next = F(z, c);
        double hz = DiffStep * SMath.Max(1, z.Length), hc = DiffStep * SMath.Max(1, c.Length);
        var ax = (1 / (2 * hz)) * (F(z + new V2(hz, 0), c) - F(z - new V2(hz, 0), c));
        var ay = (1 / (2 * hz)) * (F(z + new V2(0, hz), c) - F(z - new V2(0, hz), c));
        var bx = (1 / (2 * hc)) * (F(z, c + new V2(hc, 0)) - F(z, c - new V2(hc, 0)));
        var by = (1 / (2 * hc)) * (F(z, c + new V2(0, hc)) - F(z, c - new V2(0, hc)));
        a = new M2(ax.X, ay.X, ax.Y, ay.Y);
        b = new M2(bx.X, by.X, bx.Y, by.Y);
    }

    /// <summary>True when the step depends only on z and c (not on the
    /// iteration index n / iter or the previous iterate prev), tested by
    /// evaluating it at sample points. A non-autonomous map has no fixed
    /// nuclei to find.</summary>
    public static bool IsAutonomous(SandboxExpression step)
    {
        var env = step.NewEnv();
        var rng = new Random(1191);
        for (int t = 0; t < 8; t++)
        {
            var z = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
            var c = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
            var p = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
            Complex r0, r1, r2;
            try
            {
                r0 = step.EvalStep(z, c, 0, env, z);
                r1 = step.EvalStep(z, c, 7, env, z);
                r2 = step.EvalStep(z, c, 0, env, p);
            }
            catch { return false; }
            double tol = 1e-12 * (1 + Complex.Abs(r0));
            if (!(Complex.Abs(r0 - r1) <= tol) || !(Complex.Abs(r0 - r2) <= tol)) return false;
        }
        return true;
    }
}

/// <summary>Which finder track a family gets.</summary>
public enum FinderTrack
{
    /// <summary>No exact finder (Auto-explore only).</summary>
    None,
    /// <summary>z² + c: the octuple-double S1–S5 finders.</summary>
    Mandelbrot,
    /// <summary>Another map with a known iteration: the double-precision R²
    /// finders (GeneralFinder).</summary>
    General,
}

public static class OrbitMaps
{
    /// <summary>The finder track for a family.</summary>
    public static FinderTrack TrackFor(FractalType t) => t switch
    {
        FractalType.Mandelbrot or FractalType.GeneratedMandelbrotZ2 => FinderTrack.Mandelbrot,
        FractalType.Multibrot or FractalType.GeneratedMandelbrotZ3 or FractalType.GeneratedMandelbrotZ4
            or FractalType.GeneratedMandelbrotZ5 or FractalType.Tricorn or FractalType.GeneratedTricorn
            or FractalType.BurningShip or FractalType.GeneratedBurningShip or FractalType.UserEquation => FinderTrack.General,
        _ => FinderTrack.None,
    };

    /// <summary>The map of a General-track family with fixed formula (not
    /// User Equation, which needs its compiled source: see
    /// <see cref="UserEquationMap"/>).</summary>
    public static IOrbitMap? For(FractalType t, FractalParameters? p) => t switch
    {
        FractalType.Multibrot => new MultibrotMap(p?.MultibrotExponent ?? 3),
        FractalType.GeneratedMandelbrotZ3 => new MultibrotMap(3),
        FractalType.GeneratedMandelbrotZ4 => new MultibrotMap(4),
        FractalType.GeneratedMandelbrotZ5 => new MultibrotMap(5),
        FractalType.Tricorn or FractalType.GeneratedTricorn => new TricornMap(),
        FractalType.BurningShip or FractalType.GeneratedBurningShip => new BurningShipMap(),
        _ => null,
    };
}
