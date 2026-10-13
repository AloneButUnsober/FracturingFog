// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/CriticalPoints.cs
//
// Finder S7 follow-up (#1242, epic #1184): critical points of a User Equation.
// For a holomorphic step f(z, c) the parameter plane holds one
// "Mandelbrot-like" set per critical point z* (f′(z*) = 0): the set of c whose
// orbit of z* stays bounded. The render shows only the set of its start point
// z₀, so the finder lists the critical points and lets the user pick one as
// the z₀ seed.
//
// f′ = ∂f/∂z comes EXACTLY from the DSL's forward-mode dual numbers
// (SandboxExpression.EvalStepDz), so it keeps full relative precision even
// where f itself is dominated by c (z^8 + c near z = 0). Its roots are found
// by Newton from a ring of starts, with f″ by a central difference of f′ and
// the step scaled by the root's multiplicity m (estimated from the
// contraction of successive steps, (m − 1)/m for plain Newton), so the
// multiple critical point of z^d + c converges in a few steps.
//
// A critical point that does not move with c can be applied exactly as a
// constant seed; one that does (z³ + c·z: z* = ±√(−c/3)) is only listed, with
// its value at the view centre, because no constant reproduces it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;

using FracturingFog.Models;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>A critical point z* of f(·, c): f′(z*) = 0 with a zero of order
/// <see cref="Multiplicity"/>. <see cref="DependsOnC"/> is true when it moves
/// with c (then <see cref="Z"/> is its value at the c it was found for).</summary>
public readonly record struct CriticalPoint(Complex Z, int Multiplicity, bool DependsOnC)
{
    /// <summary>The point as a DSL seed expression ("0", "1.5", "-0.5 + 0.25*i").</summary>
    public string SeedText => CriticalPoints.Format(Z);
}

public static class CriticalPoints
{
    /// <summary>Radii of the start rings (plus z = 0).</summary>
    public static readonly double[] StartRadii = { 0.25, 0.5, 1, 2, 4, 8, 16 };
    /// <summary>Starts per ring.</summary>
    public const int StartAngles = 12;
    public const int MaxNewtonSteps = 200;
    /// <summary>Most critical points listed (nearest the origin first).</summary>
    public const int MaxListed = 12;
    /// <summary>Two roots closer than this (relative to max(1, |z|)) are one.</summary>
    public const double SameRoot = 1e-7;
    /// <summary>Relative step of the central difference for f″.</summary>
    public const double DiffStep = 1e-4;

    /// <summary>The critical points of <paramref name="step"/> at parameter
    /// <paramref name="c"/>; null (with the reason) when the equation is not
    /// holomorphic. Empty when none lies within reach of the starts.</summary>
    public static IReadOnlyList<CriticalPoint>? Find(SandboxExpression step, Complex c, out string? why, CancellationToken ct = default)
    {
        why = null;
        ArgumentNullException.ThrowIfNull(step);
        if (!step.IsHolomorphic)
        {
            why = "Critical points need a holomorphic equation (no abs / conj / re / im / conditions): its derivative has no isolated zeros otherwise.";
            return null;
        }
        var denv = step.NewDualEnv();
        var roots = new List<(Complex Z, int M)>();
        foreach (var start in Starts())
        {
            ct.ThrowIfCancellationRequested();
            if (Solve(step, denv, start, c, out var z, out int m) && !Contains(roots, z))
                roots.Add((z, m));
        }
        roots.Sort((a, b) => a.Z.Magnitude.CompareTo(b.Z.Magnitude));

        // c-dependence: the root must stay put at two other parameters.
        double cs = SMath.Max(1, c.Magnitude);
        var c2 = c + new Complex(0.137, 0.071) * cs;
        var c3 = c + new Complex(-0.052, 0.113) * cs;
        var list = new List<CriticalPoint>();
        foreach (var (z, m) in roots)
        {
            if (list.Count >= MaxListed) break;
            bool fixedPoint = Stays(step, denv, z, c2) && Stays(step, denv, z, c3);
            list.Add(new CriticalPoint(z, m, !fixedPoint));
        }
        return list;
    }

    private static IEnumerable<Complex> Starts()
    {
        yield return Complex.Zero;
        foreach (double r in StartRadii)
            for (int k = 0; k < StartAngles; k++)
                yield return Complex.FromPolarCoordinates(r, 2 * SMath.PI * (k + 0.37) / StartAngles);
    }

    private static bool Contains(List<(Complex Z, int M)> roots, Complex z)
    {
        foreach (var (r, _) in roots)
            if ((r - z).Magnitude <= SameRoot * SMath.Max(1, z.Magnitude)) return true;
        return false;
    }

    private static bool Stays(SandboxExpression step, CxDual[] denv, Complex z, Complex c)
        => Solve(step, denv, z, c, out var z2, out _) && (z2 - z).Magnitude <= SameRoot * SMath.Max(1, z.Magnitude);

    /// <summary>f′(z) = ∂f/∂z at c, exact (dual numbers).</summary>
    public static Complex Derivative(SandboxExpression step, CxDual[] denv, Complex z, Complex c)
        => step.EvalStepDz(z, c, 0, denv, z).dfdz;

    // Multiplicity-scaled Newton on f′ from `start`; the root is verified as a
    // zero of f′ (tiny against f′ a little way off) and its order measured.
    private static bool Solve(SandboxExpression step, CxDual[] denv, Complex start, Complex c, out Complex z, out int mult)
    {
        z = start; mult = 1;
        int m = 1;
        double lastStep = double.NaN, lastRatio = double.NaN;
        for (int i = 0; i < MaxNewtonSteps; i++)
        {
            Complex g = Derivative(step, denv, z, c);
            if (g == Complex.Zero) break;
            if (!IsFinite(g)) return false;
            double h = DiffStep * z.Magnitude + 1e-30;
            Complex gp = (Derivative(step, denv, z + h, c) - Derivative(step, denv, z - h, c)) / (2 * h);
            Complex s = g / gp;
            if (!IsFinite(s)) return false;
            double sm = s.Magnitude;
            // Plain Newton contracts by (m − 1)/m towards an m-fold root.
            if (m == 1 && !double.IsNaN(lastStep))
            {
                double ratio = sm / lastStep;
                if (ratio is > 0.3 and < 0.99 && SMath.Abs(ratio - lastRatio) < 0.02)
                    m = (int)SMath.Clamp(SMath.Round(1 / (1 - ratio)), 1, 64);
                lastRatio = ratio;
            }
            lastStep = sm;
            z -= m * s;
            if (!IsFinite(z) || z.Magnitude > 1e6) return false;
            if (m * sm <= 1e-15 * SMath.Max(z.Magnitude, 1e-300)) break;
        }
        z = Snap(z);
        // Verify: f′(z) vanishes against its size a little way off.
        double off = 1e-3 * SMath.Max(1, z.Magnitude);
        double g0 = Derivative(step, denv, z, c).Magnitude;
        double g1 = Derivative(step, denv, z + off, c).Magnitude;
        double g2 = Derivative(step, denv, z + off / 10, c).Magnitude;
        if (!(g1 > 0) || !(g0 <= 1e-6 * g1)) return false;
        mult = (int)SMath.Clamp(SMath.Round(SMath.Log10(g1 / SMath.Max(g2, 1e-300))), 1, 64);
        return true;
    }

    // Clean rounding residue so a seed reads "1", not "0.99999999999998".
    private static Complex Snap(Complex z)
    {
        double scale = SMath.Max(1, z.Magnitude);
        if (z.Magnitude <= 1e-12 * scale) return Complex.Zero;
        static double S(double v, double scale)
        {
            if (SMath.Abs(v) <= 1e-12 * scale) return 0;
            double r = SMath.Round(v, 10);
            return SMath.Abs(r - v) <= 1e-12 * scale ? r : v;
        }
        return new Complex(S(z.Real, scale), S(z.Imaginary, scale));
    }

    private static bool IsFinite(Complex z) => double.IsFinite(z.Real) && double.IsFinite(z.Imaginary);

    /// <summary>A complex number as DSL text ("0", "1.5", "-0.25*i", "-0.5 + 0.25*i").</summary>
    public static string Format(Complex z)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        if (z.Imaginary == 0) return N(z.Real);
        string im = N(SMath.Abs(z.Imaginary)) + "*i";
        if (z.Real == 0) return (z.Imaginary < 0 ? "-" : "") + im;
        return N(z.Real) + (z.Imaginary < 0 ? " - " : " + ") + im;
    }
}
