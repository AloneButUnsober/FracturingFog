// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneDynamics.cs
//
// #1554 (Math Plane S7) — the fractal bridge's dynamics: the orbit of 0 under
// z -> f(z) (default z^2 + c) and the cobweb staircase of a real map x -> f(x).
// Design: MathPlane-DesignPlan.md §3.6.
//
// Orbit: z0 = 0, z(n+1) = f(z(n)). It stops after the requested steps, or at the
// first point past EscapeLimit (that point is kept, so the escape shows), or at a
// non-finite value (dropped). z^2 + c escapes once |z| > 2 (the escape circle the
// plot draws), and the orbit stays bounded exactly when c is in the Mandelbrot set.
//
// Cobweb (bibliography #cobweb-plot): start on the diagonal at (x0, x0); go
// vertically to the curve, (x_n, x_(n+1)), then horizontally back to the
// diagonal, (x_(n+1), x_(n+1)), and repeat. Vertices alternate between y = f(x)
// and y = x, so a fixed point shows as a staircase closing on the crossing and a
// 2-cycle as a square.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace FracturingFog.Models
{
    public static class MathPlaneDynamics
    {
        /// <summary>Orbit points beyond this modulus end the orbit (it has escaped).</summary>
        public const double EscapeLimit = 1e6;
        public const int MaxSteps = 10_000;

        /// <summary>The default map, z^2 + c.</summary>
        public static Func<Complex, Complex> Quadratic(Complex c) => z => z * z + c;

        /// <summary>The orbit's map: the scene's map "f(z)" when it parses, else z^2 + c.</summary>
        public static Func<Complex, Complex> OrbitMap(string? mapSource, Complex c)
        {
            if (mapSource != null && PlotExpression.TryParse(mapSource, out var e, out _))
                return z => e!.EvalComplex(z, c);
            return Quadratic(c);
        }

        /// <summary>The cobweb's real map: <paramref name="source"/> (reads x and c) at
        /// c = <paramref name="c"/>; NaN where the value is not real. Null when it does
        /// not parse.</summary>
        public static Func<double, double>? CobwebMap(string source, double c)
        {
            if (!PlotExpression.TryParse(source, out var e, out _)) return null;
            var cc = new Complex(c, 0);
            return x =>
            {
                var w = e!.EvalComplex(new Complex(x, 0), cc);
                return Math.Abs(w.Imaginary) <= 1e-12 * Math.Max(1.0, Math.Abs(w.Real)) ? w.Real : double.NaN;
            };
        }

        /// <summary>z0 = 0 and up to <paramref name="steps"/> iterates (see header).</summary>
        public static List<Complex> Orbit(Func<Complex, Complex> f, int steps, Complex? z0 = null)
        {
            steps = Math.Clamp(steps, 0, MaxSteps);
            var pts = new List<Complex>(steps + 1) { z0 ?? Complex.Zero };
            var z = pts[0];
            for (int n = 0; n < steps; n++)
            {
                z = f(z);
                if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary)) break;
                pts.Add(z);
                if (Complex.Abs(z) > EscapeLimit) break;
            }
            return pts;
        }

        /// <summary>The step at which the orbit first leaves |z| &lt;= <paramref name="radius"/>,
        /// or null when it never does.</summary>
        public static int? EscapeStep(IReadOnlyList<Complex> orbit, double radius = 2.0)
        {
            for (int n = 0; n < orbit.Count; n++)
                if (Complex.Abs(orbit[n]) > radius) return n;
            return null;
        }

        /// <summary>The smallest p (1..<paramref name="maxPeriod"/>) with the orbit's last
        /// point within a small tolerance of the point p steps earlier, or null.</summary>
        public static int? Period(IReadOnlyList<Complex> orbit, int maxPeriod = 64)
        {
            int last = orbit.Count - 1;
            if (last < 1) return null;
            var z = orbit[last];
            double tol = 1e-7 * Math.Max(1.0, Complex.Abs(z));
            for (int p = 1; p <= maxPeriod && p <= last; p++)
                if (Complex.Abs(z - orbit[last - p]) <= tol) return p;
            return null;
        }

        /// <summary>The cobweb's vertices for x -> f(x) from <paramref name="x0"/>: (x0, x0),
        /// (x0, x1), (x1, x1), (x1, x2), ... for up to <paramref name="steps"/> steps. Stops
        /// at a non-finite value or past <see cref="EscapeLimit"/>.</summary>
        public static List<(double X, double Y)> Cobweb(Func<double, double> f, double x0, int steps)
        {
            steps = Math.Clamp(steps, 0, MaxSteps);
            var v = new List<(double, double)>(2 * steps + 1) { (x0, x0) };
            double x = x0;
            for (int n = 0; n < steps; n++)
            {
                double y = f(x);
                if (!double.IsFinite(y)) break;
                v.Add((x, y));
                v.Add((y, y));
                x = y;
                if (Math.Abs(x) > EscapeLimit) break;
            }
            return v;
        }
    }
}
