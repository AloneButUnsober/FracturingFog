// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneComplexMap.cs
//
// #1553 (Math Plane S6) — a complex map f(z) applied to the plane, its
// derivative read-out, and the phase-portrait colours. Design:
// MathPlane-DesignPlan.md §3.4, §3.5.
//
// The map is the scene's `map "f(z)"` (plot language, reads z and c), active
// when MathPlaneMapMode is Complex. The frame at progress t is
//   g_t(z) = (1 - t) z + t f(z),
// the straight-line morph the matrix maps use, so t = 0 is the plain plane and
// t = 1 is f. c = MathPlaneCRe + i MathPlaneCIm.
//
// f'(z) is numerical: the central difference (f(z + h) - f(z - h)) / 2h along the
// real axis, which for a holomorphic f is the complex derivative. Near z a
// holomorphic map is multiplication by f'(z): a rotation by arg f' and a scaling
// by |f'|, the bridge back to S5's linear maps (the Jacobian is the rotate-and-
// scale matrix [[a, -b], [b, a]] for f' = a + bi).
//
// Phase portrait: each pixel z is coloured by arg f(z), the phase portraits of
// Wegert, Visual Complex Functions (2012; Docs/Resources-Bibliography.md
// #wegert-visual-complex-functions). The cyclic colour wheel is FF's own: linear
// RGB blends between six Okabe-Ito colours (bibliography #okabe-ito-cud), so it stays
// readable with red-green colour blindness (no red-green axis). It is not a
// published cyclic map. "contours" multiplies the brightness by a sawtooth in
// log2 |f|, so it steps where |f| crosses a power of 2. A zero, pole or
// non-finite value has no phase and shows the plain background.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace FracturingFog.Models
{
    public sealed class MathPlaneComplexMap
    {
        private readonly PlotExpression _f;

        public string Source { get; }
        public Complex C { get; }
        /// <summary>Progress, clamped to [0, 1].</summary>
        public double T { get; }

        private MathPlaneComplexMap(PlotExpression f, string source, Complex c, double t)
        { _f = f; Source = source; C = c; T = t; }

        /// <summary>A map from source text, or null when it does not parse.</summary>
        public static MathPlaneComplexMap? Create(string? source, Complex c, double t)
        {
            if (!PlotExpression.TryParse(source, out var f, out _)) return null;
            t = double.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : 1.0;
            return new MathPlaneComplexMap(f!, source!, c, t);
        }

        /// <summary>The map a view draws with: the scene's map when the mode is
        /// Complex, else null.</summary>
        public static MathPlaneComplexMap? Current(FractalParameters p, MathPlaneScene scene)
            => p.MathPlaneMapMode == MathPlaneMapMode.Complex && scene.MapSource != null
                ? Create(scene.MapSource, new Complex(p.MathPlaneCRe, p.MathPlaneCIm), p.MathPlaneT)
                : null;

        /// <summary>f(z) (thread-safe).</summary>
        public Complex F(Complex z) => _f.EvalComplex(z, C);

        /// <summary>The frame at <see cref="T"/>: (1 - t) z + t f(z).</summary>
        public Complex At(Complex z) => T >= 1 ? F(z) : T <= 0 ? z : (1 - T) * z + T * F(z);

        public (double X, double Y) Apply(double x, double y)
        {
            var w = At(new Complex(x, y));
            return (w.Real, w.Imaginary);
        }

        /// <summary>f'(z), by central difference; NaN when f is not finite near z.</summary>
        public Complex Derivative(Complex z) => Derivative(F, z);

        /// <summary>The frame's derivative (1 - t) + t f'(z).</summary>
        public Complex FrameDerivative(Complex z) => (1 - T) + T * Derivative(z);

        public static Complex Derivative(Func<Complex, Complex> f, Complex z)
        {
            double h = 1e-5 * Math.Max(1.0, Complex.Abs(z));
            return (f(z + h) - f(z - h)) / (2 * h);
        }

        /// <summary>A z with At(z) = <paramref name="w"/>, by Newton's method from
        /// <paramref name="guess"/> (the preimage a drag continues from). Null when it
        /// does not converge (a critical point, or no nearby preimage).</summary>
        public Complex? Preimage(Complex w, Complex guess)
        {
            if (T <= 0) return w;
            var z = guess;
            double tol = 1e-12 * Math.Max(1.0, Complex.Abs(w));
            for (int i = 0; i < 60; i++)
            {
                var r = At(z) - w;
                if (!(Complex.Abs(r) <= double.MaxValue)) return null;
                if (Complex.Abs(r) <= tol) return z;
                var d = FrameDerivative(z);
                if (!(Complex.Abs(d) > 1e-14)) return null;
                var step = r / d;
                // Damp big steps so a far guess walks instead of jumping branches.
                double s = Complex.Abs(step), cap = 0.5 * Math.Max(1.0, Complex.Abs(z));
                if (s > cap) step *= cap / s;
                z -= step;
            }
            return Complex.Abs(At(z) - w) <= 1e-6 * Math.Max(1.0, Complex.Abs(w)) ? z : null;
        }

        // ── Phase portrait ──────────────────────────────────────────────────

        /// <summary>The wheel's stops at arg 0, 60, 120, ... 300 degrees (Okabe-Ito
        /// vermillion, orange, yellow, sky blue, blue, reddish purple).</summary>
        public static readonly uint[] PhaseStops =
            { 0xFFD55E00, 0xFFE69F00, 0xFFF0E442, 0xFF56B4E9, 0xFF0072B2, 0xFFCC79A7 };

        /// <summary>The wheel colour of an argument in radians (any range).</summary>
        public static uint PhaseColor(double arg)
        {
            double u = arg / (2 * Math.PI);
            u -= Math.Floor(u);
            double s = u * PhaseStops.Length;
            int i = Math.Min((int)s, PhaseStops.Length - 1);
            double f = s - i;
            uint a = PhaseStops[i], b = PhaseStops[(i + 1) % PhaseStops.Length];
            static uint Ch(uint a, uint b, int shift, double f)
                => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - f) + ((b >> shift) & 0xFF) * f);
            return 0xFF000000u | (Ch(a, b, 16, f) << 16) | (Ch(a, b, 8, f) << 8) | Ch(a, b, 0, f);
        }

        /// <summary>Brightness of a phase pixel: dimmed so the grid and objects stay
        /// readable over it.</summary>
        public const double PhaseBrightness = 0.7;

        /// <summary>The background colour for a value f(z); <paramref name="fallback"/>
        /// where it has no phase (zero, pole, non-finite).</summary>
        public static uint PhasePixel(Complex w, bool contours, uint fallback)
        {
            double m = Complex.Abs(w);
            if (!double.IsFinite(m) || m == 0 || !double.IsFinite(w.Real) || !double.IsFinite(w.Imaginary)) return fallback;
            uint c = PhaseColor(Math.Atan2(w.Imaginary, w.Real));
            double k = PhaseBrightness;
            if (contours)
            {
                double l = Math.Log2(m);
                k *= 0.55 + 0.45 * (l - Math.Floor(l));
            }
            uint r = (uint)Math.Round(((c >> 16) & 0xFF) * k), g = (uint)Math.Round(((c >> 8) & 0xFF) * k), b = (uint)Math.Round((c & 0xFF) * k);
            return 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        /// <summary>Fill <paramref name="buffer"/> (row-major, row 0 at the top) with the
        /// phase portrait of f over <paramref name="view"/>, at pixel centres. Rows are
        /// independent, so the parallel fill is deterministic.</summary>
        public void FillPhase(uint[] buffer, MathPlaneViewBox view, int width, int height, bool contours, uint fallback)
        {
            System.Threading.Tasks.Parallel.For(0, height, py =>
            {
                double y = view.YMax - (py + 0.5) * view.Pitch;
                int row = py * width;
                for (int px = 0; px < width; px++)
                {
                    double x = view.XMin + (px + 0.5) * view.Pitch;
                    buffer[row + px] = PhasePixel(F(new Complex(x, y)), contours, fallback);
                }
            });
        }

        // ── Lines ───────────────────────────────────────────────────────────

        /// <summary>The image of the segment a..b under the frame, as polylines in
        /// world coordinates, sampled adaptively against <paramref name="view"/> (the
        /// curve sampler: bisect while the image bends, break at gaps and poles).</summary>
        public List<List<(double X, double Y)>> MapSegment((double X, double Y) a, (double X, double Y) b, MathPlaneViewBox view)
            => MathPlaneCurveSampler.SampleFunction(
                s => Apply(a.X + s * (b.X - a.X), a.Y + s * (b.Y - a.Y)), 0.0, 1.0, view, initialSegments: 64);
    }
}
