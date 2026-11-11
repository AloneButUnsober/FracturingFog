// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlane3DMap.cs
//
// #1715 (Math Plane 3D T3, #1556) — the 3×3 linear map a Math Plane 3D view applies,
// its interpolation from the identity and its read-outs. Design:
// Docs/Technical/MathPlane3D-DesignPlan.md §3.4.
//
// Interpolation from the identity (t = 0) to M (t = 1):
//   Linear  (1 − t) I + t M, as in 2D: space's grid morphing with the origin fixed,
//           the look of 3Blue1Brown's "Three-dimensional linear transformations"
//           (Docs/Resources-Bibliography.md#sanderson-3d-transformations).
//   Polar   for det M > 0: M = R S with R a rotation and S symmetric positive definite.
//           R comes from Higham's Newton iteration X ← ½ (X + X⁻ᵀ), which converges
//           quadratically to the orthogonal polar factor
//           (Docs/Resources-Bibliography.md#higham-polar-decomposition); S = Rᵀ M,
//           symmetrised. The frame at t is R(t) ((1 − t) I + t S), where R(t) is the
//           quaternion slerp from the identity to R
//           (Docs/Resources-Bibliography.md#shoemake-slerp), so a pure rotation turns at
//           a constant rate instead of shrinking half-way. det M ≤ 0 has no rotation-only
//           factor, so polar falls back to linear (the 2D rule).
//
// Eigenpairs: the real roots of the characteristic cubic are found by bisection on
// the intervals between its turning points (no closed-form cubic formula), then each
// eigenvector is the null space of M − λI from the cross product of two of its rows.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>#1715 — a 3×3 matrix acting on column vectors, row-major (Rrc).</summary>
    public readonly record struct Mat3(
        double M11, double M12, double M13,
        double M21, double M22, double M23,
        double M31, double M32, double M33)
    {
        public static readonly Mat3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

        /// <summary>The determinant by cofactor expansion along the first row.</summary>
        public double Det => M11 * (M22 * M33 - M23 * M32) - M12 * (M21 * M33 - M23 * M31) + M13 * (M21 * M32 - M22 * M31);

        public double Trace => M11 + M22 + M33;

        public (double X, double Y, double Z) Apply(double x, double y, double z)
            => (M11 * x + M12 * y + M13 * z, M21 * x + M22 * y + M23 * z, M31 * x + M32 * y + M33 * z);

        public (double X, double Y, double Z) Apply((double X, double Y, double Z) v) => Apply(v.X, v.Y, v.Z);

        /// <summary>Column <paramref name="c"/> (0-2): the image of i, j or k.</summary>
        public (double X, double Y, double Z) Column(int c) => c switch
        {
            0 => (M11, M21, M31),
            1 => (M12, M22, M32),
            _ => (M13, M23, M33),
        };

        /// <summary>The transpose. A method, not a property: a record's ToString prints its properties, and a
        /// Mat3-valued property would print itself without end (stack overflow).</summary>
        public Mat3 Transposed() => new(M11, M21, M31, M12, M22, M32, M13, M23, M33);

        public static Mat3 operator *(Mat3 a, Mat3 b) => new(
            a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31, a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32, a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,
            a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31, a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32, a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,
            a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31, a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32, a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33);

        public static Mat3 operator +(Mat3 a, Mat3 b) => new(
            a.M11 + b.M11, a.M12 + b.M12, a.M13 + b.M13, a.M21 + b.M21, a.M22 + b.M22, a.M23 + b.M23, a.M31 + b.M31, a.M32 + b.M32, a.M33 + b.M33);

        public static Mat3 operator *(double k, Mat3 a) => new(
            k * a.M11, k * a.M12, k * a.M13, k * a.M21, k * a.M22, k * a.M23, k * a.M31, k * a.M32, k * a.M33);

        public static Mat3 Lerp(Mat3 a, Mat3 b, double t) => (1 - t) * a + t * b;

        /// <summary>The inverse (adjugate / det), or null when |det| is not above <paramref name="eps"/>.</summary>
        public Mat3? Inverse(double eps = 1e-300)
        {
            double det = Det;
            if (!(Math.Abs(det) > eps) || !double.IsFinite(det)) return null;
            double k = 1 / det;
            return new Mat3(
                k * (M22 * M33 - M23 * M32), k * (M13 * M32 - M12 * M33), k * (M12 * M23 - M13 * M22),
                k * (M23 * M31 - M21 * M33), k * (M11 * M33 - M13 * M31), k * (M13 * M21 - M11 * M23),
                k * (M21 * M32 - M22 * M31), k * (M12 * M31 - M11 * M32), k * (M11 * M22 - M12 * M21));
        }

        /// <summary>The Frobenius norm.</summary>
        public double Norm => Math.Sqrt(M11 * M11 + M12 * M12 + M13 * M13 + M21 * M21 + M22 * M22 + M23 * M23 + M31 * M31 + M32 * M32 + M33 * M33);

        public bool IsIdentity => this == Identity;
    }

    /// <summary>#1715 — a quaternion w + xi + yj + zk; unit ones are rotations.</summary>
    public readonly record struct MathPlaneQuat(double W, double X, double Y, double Z)
    {
        public static readonly MathPlaneQuat Identity = new(1, 0, 0, 0);

        public double Length => Math.Sqrt(W * W + X * X + Y * Y + Z * Z);

        public static double Dot(MathPlaneQuat a, MathPlaneQuat b) => a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public MathPlaneQuat Normalized()
        {
            double l = Length;
            return l > 0 ? new MathPlaneQuat(W / l, X / l, Y / l, Z / l) : Identity;
        }

        /// <summary>The rotation matrix of this unit quaternion.</summary>
        public Mat3 ToMatrix()
        {
            double w = W, x = X, y = Y, z = Z;
            return new Mat3(
                1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
                2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
                2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y));
        }

        /// <summary>The unit quaternion of rotation matrix <paramref name="r"/> (w ≥ 0). It inverts
        /// <see cref="ToMatrix"/>, taking the square root of the largest of the four diagonal
        /// combinations so it stays accurate near half-turns.</summary>
        public static MathPlaneQuat FromMatrix(Mat3 r)
        {
            double tr = r.Trace;
            MathPlaneQuat q;
            if (tr >= r.M11 && tr >= r.M22 && tr >= r.M33)
            {
                double s = 2 * Math.Sqrt(Math.Max(0, 1 + tr));
                q = new(0.25 * s, (r.M32 - r.M23) / s, (r.M13 - r.M31) / s, (r.M21 - r.M12) / s);
            }
            else if (r.M11 >= r.M22 && r.M11 >= r.M33)
            {
                double s = 2 * Math.Sqrt(Math.Max(0, 1 + r.M11 - r.M22 - r.M33));
                q = new((r.M32 - r.M23) / s, 0.25 * s, (r.M12 + r.M21) / s, (r.M13 + r.M31) / s);
            }
            else if (r.M22 >= r.M33)
            {
                double s = 2 * Math.Sqrt(Math.Max(0, 1 - r.M11 + r.M22 - r.M33));
                q = new((r.M13 - r.M31) / s, (r.M12 + r.M21) / s, 0.25 * s, (r.M23 + r.M32) / s);
            }
            else
            {
                double s = 2 * Math.Sqrt(Math.Max(0, 1 - r.M11 - r.M22 + r.M33));
                q = new((r.M21 - r.M12) / s, (r.M13 + r.M31) / s, (r.M23 + r.M32) / s, 0.25 * s);
            }
            if (q.W < 0) q = new(-q.W, -q.X, -q.Y, -q.Z);
            return q.Normalized();
        }

        /// <summary>Shoemake's spherical linear interpolation from <paramref name="a"/> (t = 0) to
        /// <paramref name="b"/> (t = 1) along the shorter arc:
        /// (sin((1 − t)θ) a + sin(tθ) b) / sin θ, with cos θ = a·b.</summary>
        public static MathPlaneQuat Slerp(MathPlaneQuat a, MathPlaneQuat b, double t)
        {
            double d = Dot(a, b);
            if (d < 0) { b = new(-b.W, -b.X, -b.Y, -b.Z); d = -d; }
            d = Math.Min(1, d);
            double theta = Math.Acos(d);
            double ka, kb;
            if (theta < 1e-9) { ka = 1 - t; kb = t; }   // nearly equal: the chord is the arc
            else
            {
                double s = Math.Sin(theta);
                ka = Math.Sin((1 - t) * theta) / s;
                kb = Math.Sin(t * theta) / s;
            }
            return new MathPlaneQuat(ka * a.W + kb * b.W, ka * a.X + kb * b.X, ka * a.Y + kb * b.Y, ka * a.Z + kb * b.Z).Normalized();
        }
    }

    /// <summary>#1715 — a real eigenpair; (X, Y, Z) is a unit eigenvector.</summary>
    public readonly record struct MathPlane3DEigen(double Lambda, double X, double Y, double Z);

    public static class MathPlane3DMap
    {
        /// <summary>The matrix in a parameter set.</summary>
        public static Mat3 Matrix(FractalParameters p) => new(
            p.MathPlane3DM11, p.MathPlane3DM12, p.MathPlane3DM13,
            p.MathPlane3DM21, p.MathPlane3DM22, p.MathPlane3DM23,
            p.MathPlane3DM31, p.MathPlane3DM32, p.MathPlane3DM33);

        /// <summary>The map a view draws with: identity when the map is off, else
        /// <see cref="At"/> of the matrix at <c>MathPlane3DT</c>.</summary>
        public static Mat3 Current(FractalParameters p)
            => p.MathPlane3DMapOn ? At(Matrix(p), p.MathPlane3DT, p.MathPlane3DInterpolation) : Mat3.Identity;

        /// <summary>True when polar was asked for but M has det ≤ 0, so linear is drawn.</summary>
        public static bool PolarFallsBack(FractalParameters p)
            => p.MathPlane3DMapOn && p.MathPlane3DInterpolation == MathPlaneInterpolation.Polar && !(Matrix(p).Det > 0);

        /// <summary>The frame at progress <paramref name="t"/> (clamped to [0, 1]); see the file header.</summary>
        public static Mat3 At(Mat3 m, double t, MathPlaneInterpolation mode)
        {
            t = double.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : 1.0;
            if (mode == MathPlaneInterpolation.Polar && m.Det > 0 && PolarDecompose(m) is { } f)
            {
                var r = MathPlaneQuat.Slerp(MathPlaneQuat.Identity, MathPlaneQuat.FromMatrix(f.R), t).ToMatrix();
                return r * Mat3.Lerp(Mat3.Identity, f.S, t);
            }
            return Mat3.Lerp(Mat3.Identity, m, t);
        }

        /// <summary>Higham's Newton iteration for M = R S (det M &gt; 0): X₀ = M,
        /// X ← ½ (X + X⁻ᵀ) until it stops moving, R = X, S = sym(Rᵀ M). Null when M is
        /// singular or the iteration does not settle.</summary>
        public static (Mat3 R, Mat3 S)? PolarDecompose(Mat3 m, int maxIterations = 100)
        {
            var x = m;
            for (int k = 0; k < maxIterations; k++)
            {
                if (x.Inverse() is not { } inv) return null;
                var next = 0.5 * (x + inv.Transposed());
                double change = (next + (-1) * x).Norm;
                x = next;
                if (change <= 1e-15 * x.Norm) break;
                if (k == maxIterations - 1) return null;
            }
            var s = x.Transposed() * m;
            s = 0.5 * (s + s.Transposed());
            return (x, s);
        }

        /// <summary>The 8 corners of the unit cube's image under <paramref name="m"/>: corner
        /// (a, b, c) with a, b, c ∈ {0, 1} at index a + 2b + 4c.</summary>
        public static (double X, double Y, double Z)[] Parallelepiped(Mat3 m)
        {
            var v = new (double, double, double)[8];
            for (int k = 0; k < 8; k++) v[k] = m.Apply(k & 1, (k >> 1) & 1, (k >> 2) & 1);
            return v;
        }

        /// <summary>The real eigenpairs (0 to 3). A repeated eigenvalue whose eigenspace is a
        /// plane gives two orthonormal vectors in it. <paramref name="allVectors"/> is true when
        /// M is a multiple of the identity (every vector is an eigenvector).</summary>
        public static IReadOnlyList<MathPlane3DEigen> Eigen(Mat3 m, out bool allVectors)
        {
            allVectors = false;
            var result = new List<MathPlane3DEigen>(3);
            double scale = m.Norm;
            if (!(scale > 0) || !double.IsFinite(scale)) { allVectors = scale == 0; return result; }
            double eps = 1e-10 * scale;
            if (Math.Abs(m.M12) <= eps && Math.Abs(m.M13) <= eps && Math.Abs(m.M21) <= eps && Math.Abs(m.M23) <= eps
                && Math.Abs(m.M31) <= eps && Math.Abs(m.M32) <= eps
                && Math.Abs(m.M11 - m.M22) <= eps && Math.Abs(m.M11 - m.M33) <= eps)
            {
                allVectors = true;
                return result;
            }

            foreach (double lambda in RealRoots(m))
            {
                var a = new Mat3(m.M11 - lambda, m.M12, m.M13, m.M21, m.M22 - lambda, m.M23, m.M31, m.M32, m.M33 - lambda);
                var r0 = (a.M11, a.M12, a.M13); var r1 = (a.M21, a.M22, a.M23); var r2 = (a.M31, a.M32, a.M33);
                var c = new[] { Cross(r0, r1), Cross(r0, r2), Cross(r1, r2) };
                int best = 0;
                for (int k = 1; k < 3; k++) if (Len(c[k]) > Len(c[best])) best = k;
                if (Len(c[best]) > 1e-9 * scale * scale)
                {
                    Add(result, lambda, c[best]);
                    continue;
                }
                // Rank 1: the eigenspace is the plane orthogonal to the largest row.
                var rows = new[] { r0, r1, r2 };
                int big = 0;
                for (int k = 1; k < 3; k++) if (Len(rows[k]) > Len(rows[big])) big = k;
                var n = rows[big];
                if (Len(n) <= 1e-9 * scale) continue;
                var any = Math.Abs(n.Item1) < 0.9 * Len(n) ? (1.0, 0.0, 0.0) : (0.0, 1.0, 0.0);
                var u = Cross(n, any);
                Add(result, lambda, u);
                Add(result, lambda, Cross(n, u));
            }
            return result;

            static void Add(List<MathPlane3DEigen> list, double l, (double X, double Y, double Z) v)
            {
                double n = Len(v);
                if (n > 0) list.Add(new MathPlane3DEigen(l, v.X / n, v.Y / n, v.Z / n));
            }
        }

        /// <summary>The distinct real roots of det(λI − M) = λ³ − tr λ² + c₂ λ − det, ascending:
        /// bisection on the monotone intervals between the cubic's turning points, within ±‖M‖
        /// (every eigenvalue's size is at most the Frobenius norm).</summary>
        public static IReadOnlyList<double> RealRoots(Mat3 m)
        {
            double tr = m.Trace, det = m.Det;
            double c2 = m.M11 * m.M22 - m.M12 * m.M21 + m.M11 * m.M33 - m.M13 * m.M31 + m.M22 * m.M33 - m.M23 * m.M32;
            double P(double l) => ((l - tr) * l + c2) * l - det;
            double bound = m.Norm + 1;
            double scale = Math.Max(1, bound * bound * bound);

            // Turning points: P'(λ) = 3λ² − 2 tr λ + c₂.
            var knots = new List<double> { -bound };
            double disc = tr * tr - 3 * c2;
            // A triple root sits at the inflection tr/3, where bisection is only accurate to the
            // cube root of the rounding; take it exactly.
            if (Math.Abs(disc) <= 1e-12 * bound * bound && Math.Abs(P(tr / 3)) <= 1e-12 * scale)
                return new[] { tr / 3 };
            if (disc > 0)
            {
                double r = Math.Sqrt(disc);
                knots.Add((tr - r) / 3);
                knots.Add((tr + r) / 3);
            }
            knots.Add(bound);

            var roots = new List<double>();
            void Push(double v)
            {
                if (roots.Count == 0 || Math.Abs(v - roots[^1]) > 1e-9 * bound) roots.Add(v);
            }
            for (int k = 0; k + 1 < knots.Count; k++)
            {
                double lo = knots[k], hi = knots[k + 1];
                double plo = P(lo), phi = P(hi);
                // A turning point touching zero is a double root.
                if (Math.Abs(plo) <= 1e-12 * scale && k > 0) Push(lo);
                if (Math.Abs(phi) <= 1e-12 * scale && k + 2 < knots.Count) { Push(hi); continue; }
                if (Math.Sign(plo) == Math.Sign(phi) || plo == 0) continue;
                for (int it = 0; it < 200 && hi - lo > 1e-15 * Math.Max(1, Math.Abs(lo)); it++)
                {
                    double mid = 0.5 * (lo + hi), pm = P(mid);
                    if (pm == 0) { lo = hi = mid; break; }
                    if (Math.Sign(pm) == Math.Sign(plo)) { lo = mid; plo = pm; } else hi = mid;
                }
                Push(0.5 * (lo + hi));
            }
            return roots;
        }

        private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
            => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private static double Len((double X, double Y, double Z) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }
}
