// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneCurveSampler.cs
//
// #1550 (Math Plane S3) — turns a curve object into screen-accurate polylines
// (world coordinates) for a view. Design: MathPlane-DesignPlan.md §3.4.
//
// Every curve is a map from one parameter to a point: explicit y = f(x) is
// (x, f(x)); parametric is (x(t), y(t)); polar is (r cos theta, r sin theta).
// The parameter range starts as a uniform grid and each interval is bisected
// while its midpoint is further than the tolerance (in pixels) from the chord,
// up to a depth cap. That is the usual adaptive plotting scheme.
//
// Breaks: a non-finite value ends the polyline (a gap: sqrt(x) for x < 0, poles
// that evaluate to infinity). An interval still longer than the view after the
// depth cap is a jump, not a steep piece (tan x at pi/2), so no connector is
// drawn across it. A pole between two finite samples is caught the same way.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>The visible world rectangle and the world size of one pixel.</summary>
    public readonly record struct MathPlaneViewBox(double XMin, double XMax, double YMin, double YMax, double Pitch)
    {
        public double Width => XMax - XMin;
        public double Height => YMax - YMin;
    }

    public static class MathPlaneCurveSampler
    {
        /// <summary>Chord tolerance in pixels.</summary>
        public const double TolerancePixels = 0.35;
        /// <summary>Uniform intervals before refinement.</summary>
        public const int InitialSegments = 256;
        /// <summary>Bisection depth cap per initial interval.</summary>
        public const int MaxDepth = 12;
        /// <summary>Total sample cap per curve (bounds pathological expressions).</summary>
        public const int MaxSamples = 400_000;

        /// <summary>Polylines (each two or more world points) for <paramref name="curve"/>
        /// in <paramref name="view"/>. Empty when the curve does not parse or has
        /// nothing visible. #1553: with <paramref name="map"/>, each point is mapped
        /// before the tolerance tests (so the image is refined, not the preimage), and
        /// an explicit curve spans <paramref name="domain"/> instead of the view.</summary>
        public static List<List<(double X, double Y)>> Sample(MathPlaneObject curve, MathPlaneViewBox view,
            Func<double, double, (double X, double Y)>? map = null, MathPlaneViewBox? domain = null)
        {
            var result = new List<List<(double, double)>>();
            if (curve.Kind != MathPlaneObjectKind.Curve || view.Pitch <= 0) return result;
            var exprs = new List<PlotExpression>();
            foreach (var src in curve.Expressions)
            {
                if (!PlotExpression.TryParse(src, out var e, out _)) return result;
                exprs.Add(e!);
            }

            Func<double, (double, double)> f;
            double a, b;
            switch (curve.CurveKind)
            {
                case MathPlaneCurveKind.Explicit:
                {
                    if (exprs.Count < 1) return result;
                    var fx = exprs[0];
                    f = x => (x, fx.Eval(x: x));
                    // One pixel of margin so a curve reaches the frame edges.
                    var span = domain ?? view;
                    a = span.XMin - span.Pitch; b = span.XMax + span.Pitch;
                    if (curve.Domain is { } d) { a = Math.Max(a, d.Min); b = Math.Min(b, d.Max); }
                    break;
                }
                case MathPlaneCurveKind.Parametric:
                {
                    if (exprs.Count < 2 || curve.Domain is not { } d) return result;
                    var ex = exprs[0]; var ey = exprs[1];
                    f = t => (ex.Eval(t: t), ey.Eval(t: t));
                    a = d.Min; b = d.Max;
                    break;
                }
                default:
                {
                    if (exprs.Count < 1) return result;
                    var er = exprs[0];
                    f = th => { double r = er.Eval(theta: th); return (r * Math.Cos(th), r * Math.Sin(th)); };
                    (a, b) = curve.Domain ?? (0.0, 2 * Math.PI);
                    break;
                }
            }
            if (!(b > a) || !double.IsFinite(a) || !double.IsFinite(b)) return result;

            if (map != null)
            {
                var g = f;
                f = p => { var (x, y) = g(p); return map(x, y); };
            }
            return SampleFunction(f, a, b, view);
        }

        /// <summary>#1553 — polylines for any one-parameter map p -> (x, y) over [a, b],
        /// refined against <paramref name="view"/> (the same tolerance, breaks and caps
        /// as a curve).</summary>
        public static List<List<(double X, double Y)>> SampleFunction(Func<double, (double X, double Y)> f,
            double a, double b, MathPlaneViewBox view, int initialSegments = InitialSegments)
        {
            if (!(b > a) || view.Pitch <= 0) return new List<List<(double, double)>>();
            var s = new Sampler(f, view, initialSegments);
            s.Run(a, b);
            return s.Lines;
        }

        private sealed class Sampler
        {
            private readonly Func<double, (double X, double Y)> _f;
            private readonly MathPlaneViewBox _v;
            private readonly double _tol2;
            private readonly double _jump;
            private readonly int _initial;
            private int _samples;
            private List<(double, double)>? _line;
            public readonly List<List<(double, double)>> Lines = new();

            public Sampler(Func<double, (double, double)> f, MathPlaneViewBox v, int initial)
            {
                _f = f; _v = v; _initial = Math.Max(1, initial);
                double tol = TolerancePixels * v.Pitch;
                _tol2 = tol * tol;
                _jump = Math.Max(v.Width, v.Height);
            }

            private (double X, double Y) Eval(double p) { _samples++; return _f(p); }

            private static bool Finite((double X, double Y) q) => double.IsFinite(q.X) && double.IsFinite(q.Y);

            public void Run(double a, double b)
            {
                double h = (b - a) / _initial;
                var p0 = Eval(a);
                for (int k = 1; k <= _initial; k++)
                {
                    double t1 = k == _initial ? b : a + k * h;
                    var p1 = Eval(t1);
                    Segment(a + (k - 1) * h, p0, t1, p1, 0);
                    p0 = p1;
                }
                End();
            }

            // Draws (t0, p0] .. (t1, p1]: p0 is already the polyline's last point
            // (or a gap), and this adds what follows up to and including p1.
            private void Segment(double t0, (double X, double Y) p0, double t1, (double X, double Y) p1, int depth)
            {
                bool f0 = Finite(p0), f1 = Finite(p1);
                if (depth < MaxDepth && _samples < MaxSamples)
                {
                    double tm = 0.5 * (t0 + t1);
                    var pm = Eval(tm);
                    bool fm = Finite(pm);
                    bool refine;
                    if (f0 && f1 && fm)
                    {
                        // Midpoint distance from the chord, and off-view intervals
                        // need no detail (only their crossing in and out).
                        double cx = 0.5 * (p0.X + p1.X), cy = 0.5 * (p0.Y + p1.Y);
                        double dx = pm.X - cx, dy = pm.Y - cy;
                        refine = dx * dx + dy * dy > _tol2 && !BothOutsideSameSide(p0, p1, pm);
                    }
                    else refine = f0 || f1 || fm;   // locate where the curve starts / stops
                    if (refine)
                    {
                        Segment(t0, p0, tm, pm, depth + 1);
                        Segment(tm, pm, t1, p1, depth + 1);
                        return;
                    }
                }

                if (!f1) { End(); return; }
                if (!f0) { Start(p1); return; }
                // Still longer than the view at the depth cap: a jump, not a curve.
                double jx = p1.X - p0.X, jy = p1.Y - p0.Y;
                if (Math.Sqrt(jx * jx + jy * jy) > _jump && depth >= MaxDepth) { End(); Start(p1); return; }
                if (_line == null) Start(p0);
                _line!.Add(p1);
            }

            private bool BothOutsideSameSide((double X, double Y) a, (double X, double Y) b, (double X, double Y) m)
            {
                double m2 = _v.Pitch * 2;
                return (a.X < _v.XMin - m2 && b.X < _v.XMin - m2 && m.X < _v.XMin - m2)
                    || (a.X > _v.XMax + m2 && b.X > _v.XMax + m2 && m.X > _v.XMax + m2)
                    || (a.Y < _v.YMin - m2 && b.Y < _v.YMin - m2 && m.Y < _v.YMin - m2)
                    || (a.Y > _v.YMax + m2 && b.Y > _v.YMax + m2 && m.Y > _v.YMax + m2);
            }

            private void Start((double X, double Y) p) { End(); _line = new List<(double, double)> { p }; }

            private void End()
            {
                if (_line != null && _line.Count >= 2) Lines.Add(_line);
                _line = null;
            }
        }
    }
}
