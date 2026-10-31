// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneMap.cs
//
// #1552 (Math Plane S5) — the 2x2 linear map a Math Plane view applies, and its
// read-outs. Design: MathPlane-DesignPlan.md §3.5.
//
// Interpolation from the identity (t = 0) to M (t = 1):
//   Linear  (1 - t) I + t M. This is the grid morph of 3Blue1Brown's linear-
//           algebra videos (see the bibliography's Sanderson entry). It keeps M's
//           eigenvectors fixed all the way, because (1 - t) I + t M has the same
//           eigenvectors as M.
//   Polar   M = R(theta) S, with R a rotation and S symmetric (the polar
//           decomposition). The frame at t is R(t theta) ((1 - t) I + t S): the
//           rotation turns, so a pure rotation keeps |det| = 1 instead of collapsing
//           through 0 half-way. For 2x2 with det M > 0, theta = atan2(c - b, a + d).
//           det M <= 0 has no rotation-only factor, so Polar falls back to Linear.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>A 2x2 matrix [[A, B], [C, D]] acting on column vectors.</summary>
    public readonly record struct Mat2(double A, double B, double C, double D)
    {
        public static readonly Mat2 Identity = new(1, 0, 0, 1);

        public double Det => A * D - B * C;
        public double Trace => A + D;

        public (double X, double Y) Apply(double x, double y) => (A * x + B * y, C * x + D * y);

        public static Mat2 operator *(Mat2 m, Mat2 n) => new(
            m.A * n.A + m.B * n.C, m.A * n.B + m.B * n.D,
            m.C * n.A + m.D * n.C, m.C * n.B + m.D * n.D);

        public static Mat2 Rotation(double theta) => new(Math.Cos(theta), -Math.Sin(theta), Math.Sin(theta), Math.Cos(theta));

        public static Mat2 Lerp(Mat2 m, Mat2 n, double t) => new(
            m.A + t * (n.A - m.A), m.B + t * (n.B - m.B), m.C + t * (n.C - m.C), m.D + t * (n.D - m.D));

        /// <summary>The inverse, or null when |det| is below <paramref name="eps"/>.</summary>
        public Mat2? Inverse(double eps = 1e-12)
        {
            double det = Det;
            if (!(Math.Abs(det) > eps)) return null;
            return new Mat2(D / det, -B / det, -C / det, A / det);
        }

        /// <summary>The largest stretch (spectral norm, largest singular value).</summary>
        public double MaxStretch
        {
            get
            {
                double s = A * A + B * B + C * C + D * D;
                double det = Det;
                return Math.Sqrt(0.5 * (s + Math.Sqrt(Math.Max(0, s * s - 4 * det * det))));
            }
        }

        public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1;
    }

    /// <summary>A real eigenpair; <see cref="X"/>, <see cref="Y"/> is a unit eigenvector.</summary>
    public readonly record struct MathPlaneEigen(double Lambda, double X, double Y);

    public static class MathPlaneMap
    {
        /// <summary>The matrix in a parameter set.</summary>
        public static Mat2 Matrix(FractalParameters p)
            => new(p.MathPlaneM11, p.MathPlaneM12, p.MathPlaneM21, p.MathPlaneM22);

        /// <summary>The map a view draws with: identity when the map is off, else
        /// <see cref="At"/> of the matrix at <c>MathPlaneT</c>.</summary>
        public static Mat2 Current(FractalParameters p)
            => p.MathPlaneMapMode == MathPlaneMapMode.Matrix
                ? At(Matrix(p), p.MathPlaneT, p.MathPlaneInterpolation)
                : Mat2.Identity;

        /// <summary>The frame at progress <paramref name="t"/> (clamped to [0, 1]); see file header.</summary>
        public static Mat2 At(Mat2 m, double t, MathPlaneInterpolation mode)
        {
            t = double.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : 1.0;
            if (mode == MathPlaneInterpolation.Polar && m.Det > 0)
            {
                var (theta, s) = PolarDecompose(m);
                return Mat2.Rotation(t * theta) * Mat2.Lerp(Mat2.Identity, s, t);
            }
            return Mat2.Lerp(Mat2.Identity, m, t);
        }

        /// <summary>M = R(theta) S with S symmetric, for det M &gt; 0. theta in (-pi, pi].</summary>
        public static (double Theta, Mat2 S) PolarDecompose(Mat2 m)
        {
            double theta = Math.Atan2(m.C - m.B, m.A + m.D);
            return (theta, Mat2.Rotation(-theta) * m);
        }

        /// <summary>The real eigenpairs (0, 1 or 2). <paramref name="allVectors"/> is true
        /// when the matrix is a multiple of the identity (every vector is an eigenvector).</summary>
        public static IReadOnlyList<MathPlaneEigen> Eigen(Mat2 m, out bool allVectors)
        {
            allVectors = false;
            var result = new List<MathPlaneEigen>(2);
            double scale = Math.Max(1e-300, Math.Abs(m.A) + Math.Abs(m.B) + Math.Abs(m.C) + Math.Abs(m.D));
            if (Math.Abs(m.B) <= 1e-12 * scale && Math.Abs(m.C) <= 1e-12 * scale && Math.Abs(m.A - m.D) <= 1e-12 * scale)
            {
                allVectors = true;
                return result;
            }
            double half = m.Trace / 2, disc = half * half - m.Det;
            if (disc < -1e-12 * scale * scale) return result;
            double r = Math.Sqrt(Math.Max(0, disc));
            foreach (double lambda in r == 0 ? new[] { half } : new[] { half + r, half - r })
            {
                // (M - lambda I) v = 0: pick the better-conditioned row.
                double x, y;
                if (Math.Abs(m.B) + Math.Abs(m.A - lambda) >= Math.Abs(m.C) + Math.Abs(m.D - lambda))
                { x = m.B; y = lambda - m.A; }
                else
                { x = lambda - m.D; y = m.C; }
                double n = Math.Sqrt(x * x + y * y);
                if (n == 0) continue;
                result.Add(new MathPlaneEigen(lambda, x / n, y / n));
            }
            return result;
        }
    }
}
