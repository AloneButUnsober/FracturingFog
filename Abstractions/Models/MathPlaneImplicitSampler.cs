// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneImplicitSampler.cs
//
// #1551 (Math Plane S4) — implicit curves F(x, y) = 0 and shaded regions.
// Design: MathPlane-DesignPlan.md §3.4.
//
// Implicit curves use marching squares, the 2D form of marching cubes
// (Lorensen & Cline 1987, see Docs/Resources-Bibliography.md
// #lorensen-cline-marching-cubes). F is sampled on a lattice of CellPixels-wide
// cells covering the view. Each cell whose corners change sign gets a segment
// between the linearly interpolated edge crossings. A saddle cell (opposite
// corners alike, four crossings) is resolved by F at the cell centre, as the
// design plan says.
//
// Poles are not roots: F = y - tan(x) changes sign across x = pi/2 too. Each
// crossing is located by bisection on its edge (BisectionSteps halvings, which
// also places it far more accurately than linear interpolation). At a root |F|
// shrinks with the bracket; at a pole it grows. A crossing whose final |F| is not
// well below the edge's end values is a pole, and its segment is dropped.
//
// Regions: the condition is evaluated at every pixel centre; a pixel is inside
// when the value is finite and non-zero (comparisons give 1 / 0). Rows are
// independent, so the parallel evaluation is deterministic.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FracturingFog.Models
{
    public static class MathPlaneImplicitSampler
    {
        /// <summary>Lattice cell size in pixels for implicit curves.</summary>
        public const int CellPixels = 2;
        /// <summary>Segment cap per curve (bounds pathological expressions).</summary>
        public const int MaxSegments = 1_000_000;

        /// <summary>The zero set of an implicit curve in <paramref name="view"/>, as
        /// world-space segments. Empty when the object is not an implicit curve or
        /// does not parse.</summary>
        public static List<((double X, double Y) A, (double X, double Y) B)> Segments(
            MathPlaneObject curve, MathPlaneViewBox view, int cellPixels = CellPixels)
        {
            var segs = new List<((double, double), (double, double))>();
            if (curve.Kind != MathPlaneObjectKind.Curve || curve.CurveKind != MathPlaneCurveKind.Implicit
                || curve.Expressions.Count < 1 || view.Pitch <= 0 || cellPixels < 1) return segs;
            if (!PlotExpression.TryParse(curve.Expressions[0], out var f, out _)) return segs;

            double h = cellPixels * view.Pitch;
            // One cell of margin round the view so curves reach the frame edges.
            double x0 = view.XMin - h, y0 = view.YMin - h;
            int nx = (int)Math.Ceiling(view.Width / h) + 2, ny = (int)Math.Ceiling(view.Height / h) + 2;
            if ((long)(nx + 1) * (ny + 1) > 50_000_000) return segs;

            // Node values, row j at y0 + j h (y up).
            var v = new double[(nx + 1) * (ny + 1)];
            Parallel.For(0, ny + 1, j =>
            {
                double y = y0 + j * h;
                for (int i = 0; i <= nx; i++) v[j * (nx + 1) + i] = f!.Eval(x: x0 + i * h, y: y);
            });
            double V(int i, int j) => v[j * (nx + 1) + i];

            for (int j = 0; j < ny && segs.Count < MaxSegments; j++)
            {
                double ya = y0 + j * h, yb = ya + h;
                for (int i = 0; i < nx; i++)
                {
                    double v00 = V(i, j), v10 = V(i + 1, j), v11 = V(i + 1, j + 1), v01 = V(i, j + 1);
                    if (!double.IsFinite(v00) || !double.IsFinite(v10) || !double.IsFinite(v11) || !double.IsFinite(v01)) continue;
                    bool s00 = v00 >= 0, s10 = v10 >= 0, s11 = v11 >= 0, s01 = v01 >= 0;
                    if (s00 == s10 && s10 == s11 && s11 == s01) continue;
                    double xa = x0 + i * h, xb = xa + h;

                    // Edge crossings: 0 bottom, 1 right, 2 top, 3 left.
                    (double X, double Y)? E(int e) => e switch
                    {
                        0 => s00 != s10 ? Cross(f!, xa, ya, v00, xb, ya, v10) : null,
                        1 => s10 != s11 ? Cross(f!, xb, ya, v10, xb, yb, v11) : null,
                        2 => s01 != s11 ? Cross(f!, xa, yb, v01, xb, yb, v11) : null,
                        _ => s00 != s01 ? Cross(f!, xa, ya, v00, xa, yb, v01) : null,
                    };
                    void Add(int ea, int eb)
                    {
                        if (E(ea) is { } p && E(eb) is { } q) segs.Add((p, q));
                    }

                    bool saddle = s00 == s11 && s10 == s01 && s00 != s10;
                    if (saddle)
                    {
                        double vc = f!.Eval(x: xa + h / 2, y: ya + h / 2);
                        if ((vc >= 0) == s00) { Add(0, 1); Add(3, 2); }   // c00-c11 joined: cut c10, c01
                        else { Add(0, 3); Add(1, 2); }                     // cut c00, c11
                        continue;
                    }
                    var edges = new List<int>(2);
                    if (s00 != s10) edges.Add(0);
                    if (s10 != s11) edges.Add(1);
                    if (s01 != s11) edges.Add(2);
                    if (s00 != s01) edges.Add(3);
                    if (edges.Count == 2) Add(edges[0], edges[1]);
                }
            }
            return segs;
        }

        /// <summary>Halvings per edge crossing (bracket = cell / 2^steps).</summary>
        public const int BisectionSteps = 10;

        /// <summary>A crossing is a root when its final |F| is below this fraction of
        /// the larger end value. A smooth root after 10 halvings sits near 1/2048 of
        /// it; a pole is above 1.</summary>
        public const double RootRatio = 0.05;

        // The sign change on an edge, by bisection; null when it is a pole or jump.
        private static (double X, double Y)? Cross(PlotExpression f, double xa, double ya, double va,
                                                   double xb, double yb, double vb)
        {
            double lo = 0, hi = 1, flo = va;
            double x = 0, y = 0, fm = 0;
            for (int k = 0; k < BisectionSteps; k++)
            {
                double m = 0.5 * (lo + hi);
                x = xa + m * (xb - xa); y = ya + m * (yb - ya);
                fm = f.Eval(x: x, y: y);
                if (!double.IsFinite(fm)) return null;
                if ((fm >= 0) == (flo >= 0)) { lo = m; flo = fm; } else hi = m;
            }
            if (Math.Abs(fm) > RootRatio * Math.Max(Math.Abs(va), Math.Abs(vb))) return null;
            // Final linear step inside the tiny bracket for a smooth line.
            double fl = f.Eval(x: xa + lo * (xb - xa), y: ya + lo * (yb - ya));
            double fh = f.Eval(x: xa + hi * (xb - xa), y: ya + hi * (yb - ya));
            double t = fl != fh ? lo + (hi - lo) * fl / (fl - fh) : 0.5 * (lo + hi);
            if (!(t >= lo && t <= hi)) t = 0.5 * (lo + hi);
            return (xa + t * (xb - xa), ya + t * (yb - ya));
        }

        /// <summary>Region membership per pixel (row-major, row 0 at the top) for a
        /// <paramref name="width"/> x <paramref name="height"/> render of
        /// <paramref name="view"/>, tested at pixel centres. Null when the object is
        /// not a region or does not parse.</summary>
        public static bool[]? RegionMask(MathPlaneObject region, MathPlaneViewBox view, int width, int height)
        {
            if (region.Kind != MathPlaneObjectKind.Region || region.Expressions.Count < 1 || width <= 0 || height <= 0)
                return null;
            if (!PlotExpression.TryParse(region.Expressions[0], out var f, out _)) return null;
            var mask = new bool[width * height];
            Parallel.For(0, height, py =>
            {
                double y = view.YMax - (py + 0.5) * view.Pitch;
                for (int px = 0; px < width; px++)
                {
                    double val = f!.Eval(x: view.XMin + (px + 0.5) * view.Pitch, y: y);
                    mask[py * width + px] = double.IsFinite(val) && val != 0;
                }
            });
            return mask;
        }
    }
}
