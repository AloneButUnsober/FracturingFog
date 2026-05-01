// Math/QuadDouble.cs
//
// Quad-Double (QD) floating-point arithmetic — 4 doubles unevaluated sum.
// Provides ~62 decimal digits of precision (4× double, 2× DD).
//
// Used to compute the Mandelbrot reference orbit at zoom levels beyond DD's
// ~5×10²⁷ ceiling. The per-pixel perturbation delta still iterates as plain
// double (sufficient since |δ| << |Z| at depth) — only the reference orbit
// and the view-centre coordinate need this precision.
//
// Algorithms adapted from:
//   Hida, Li, Bailey — "Algorithms for Quad-Double Precision Floating Point
//   Arithmetic", LBNL Technical Report (2007), and the accompanying `qd`
//   C++ library (sloppy variants — sufficient error bounds for our use).

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.FFMath
{
    /// <summary>
    /// Quad-double floating-point number — sum of four doubles X0+X1+X2+X3
    /// with non-overlapping mantissas. Provides ~62 decimal digits of
    /// precision, supporting Mandelbrot zoom up to ~5×10⁵⁸.
    /// </summary>
    public readonly struct QD
    {
        public readonly double X0, X1, X2, X3;

        public static readonly QD Zero = new(0, 0, 0, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QD(double x0, double x1, double x2, double x3)
        { X0 = x0; X1 = x1; X2 = x2; X3 = x3; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QD(double v) { X0 = v; X1 = 0; X2 = 0; X3 = 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QD(DD dd) { X0 = dd.Hi; X1 = dd.Lo; X2 = 0; X3 = 0; }

        // ── Primitives ────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e) TwoSum(double a, double b)
        {
            double s = a + b;
            double v = s - a;
            return (s, (a - (s - v)) + (b - v));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e) QuickTwoSum(double a, double b)
        {
            double s = a + b;
            return (s, b - (s - a));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double p, double e) TwoProduct(double a, double b)
        {
            double p = a * b;
            return (p, System.Math.FusedMultiplyAdd(a, b, -p));
        }

        // a + b + c → (s, e1, e2) with s the high word, e1 next, e2 lowest
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e1, double e2) ThreeSum(
            double a, double b, double c)
        {
            var (t1, t2) = TwoSum(a, b);
            var (s, t3) = TwoSum(c, t1);
            var (e1, e2) = TwoSum(t2, t3);
            return (s, e1, e2);
        }

        // a + b + c → (s, e) — drops the 3rd-order term (faster, 2-double result)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e) ThreeSum2(
            double a, double b, double c)
        {
            var (t1, t2) = TwoSum(a, b);
            var (s, t3) = TwoSum(c, t1);
            return (s, t2 + t3);
        }

        // Renormalize a 5-term expansion to canonical 4-term form.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double, double, double, double) Renorm5(
            double c0, double c1, double c2, double c3, double c4)
        {
            (c3, c4) = QuickTwoSum(c3, c4);
            (c2, c3) = QuickTwoSum(c2, c3);
            (c1, c2) = QuickTwoSum(c1, c2);
            (c0, c1) = QuickTwoSum(c0, c1);

            double s0 = c0, s1 = c1, s2 = 0, s3 = 0;
            if (s1 != 0.0)
            {
                (s1, s2) = QuickTwoSum(s1, c2);
                if (s2 != 0.0)
                {
                    (s2, s3) = QuickTwoSum(s2, c3);
                    if (s3 != 0.0) s3 += c4; else s2 += c4;
                }
                else
                {
                    (s1, s2) = QuickTwoSum(s1, c3);
                    if (s2 != 0.0) (s2, s3) = QuickTwoSum(s2, c4);
                    else (s1, s2) = QuickTwoSum(s1, c4);
                }
            }
            else
            {
                (s0, s1) = QuickTwoSum(s0, c2);
                if (s1 != 0.0)
                {
                    (s1, s2) = QuickTwoSum(s1, c3);
                    if (s2 != 0.0) (s2, s3) = QuickTwoSum(s2, c4);
                    else (s1, s2) = QuickTwoSum(s1, c4);
                }
                else
                {
                    (s0, s1) = QuickTwoSum(s0, c3);
                    if (s1 != 0.0) (s1, s2) = QuickTwoSum(s1, c4);
                    else (s0, s1) = QuickTwoSum(s0, c4);
                }
            }
            return (s0, s1, s2, s3);
        }

        // ── Addition (sloppy variant) ─────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QD operator +(QD a, QD b)
        {
            // Pairwise TwoSum, then propagate errors and renormalize.
            var (s0, e0) = TwoSum(a.X0, b.X0);
            var (s1, e1) = TwoSum(a.X1, b.X1);
            var (s2, e2) = TwoSum(a.X2, b.X2);
            double s3 = a.X3 + b.X3;

            // First level: s1 += e0
            (s1, e0) = TwoSum(s1, e0);
            // Second level: s2 += e1 + e0
            double t1, t2;
            (s2, t1, t2) = ThreeSum(s2, e1, e0);
            // Third level: s3 += e2 + t1
            (s3, e2) = ThreeSum2(s3, e2, t1);
            // Carry t2 into the residual
            double s4 = e2 + t2;

            var (r0, r1, r2, r3) = Renorm5(s0, s1, s2, s3, s4);
            return new QD(r0, r1, r2, r3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QD operator -(QD a) => new(-a.X0, -a.X1, -a.X2, -a.X3);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QD operator -(QD a, QD b) => a + (-b);

        // QD + double — cascade carry through all limbs via TwoSum chain.
        // The sloppy form (s1 = a.X1 + e, no TwoSum) drops the carry into a
        // discarded slot, keeping X2/X3 = 0 forever during navigation.  At
        // zoom > ~1e40 that makes navigation completely unrepresentable at
        // pixel precision.  The full chain costs two extra TwoSums (cheap).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QD operator +(QD a, double b)
        {
            var (s0, e0) = TwoSum(a.X0, b);
            var (s1, e1) = TwoSum(a.X1, e0);
            var (s2, e2) = TwoSum(a.X2, e1);
            double s3 = a.X3 + e2;
            var (r0, r1, r2, r3) = Renorm5(s0, s1, s2, s3, 0);
            return new QD(r0, r1, r2, r3);
        }

        // ── Multiplication (sloppy variant — drops O(eps³) terms) ────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QD operator *(QD a, QD b)
        {
            var (p0, q0) = TwoProduct(a.X0, b.X0);
            var (p1, q1) = TwoProduct(a.X0, b.X1);
            var (p2, q2) = TwoProduct(a.X1, b.X0);
            var (p3, q3) = TwoProduct(a.X0, b.X2);
            var (p4, q4) = TwoProduct(a.X1, b.X1);
            var (p5, q5) = TwoProduct(a.X2, b.X0);

            // (p1, p2, q0) — three_sum: p1 += p2 + q0
            double tmpA, tmpB;
            (p1, tmpA, tmpB) = ThreeSum(p1, p2, q0);
            // tmpA, tmpB now hold residuals to feed into next level
            p2 = tmpA;
            q0 = tmpB;

            // Six-three sum of (p2,q1,q2) + (p3,p4,p5)
            (p2, q1, q2) = ThreeSum(p2, q1, q2);
            (p3, p4, p5) = ThreeSum(p3, p4, p5);

            var (s0, t0) = TwoSum(p2, p3);
            var (s1, t1) = TwoSum(q1, p4);
            double s2 = q2 + p5;
            (s1, t0) = TwoSum(s1, t0);
            s2 += t0 + t1;

            // O(eps³) corrections — folded into s1
            s1 += a.X0 * b.X3 + a.X1 * b.X2 + a.X2 * b.X1 + a.X3 * b.X0
                + q0 + q3 + q4 + q5;

            var (r0, r1, r2, r3) = Renorm5(p0, p1, s0, s1, s2);
            return new QD(r0, r1, r2, r3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QD operator *(QD a, double b)
        {
            var (p0, q0) = TwoProduct(a.X0, b);
            var (p1, q1) = TwoProduct(a.X1, b);
            var (p2, q2) = TwoProduct(a.X2, b);
            double p3 = a.X3 * b;

            (p1, q0) = TwoSum(p1, q0);
            double t0, t1;
            (p2, t0, t1) = ThreeSum(p2, q1, q0);
            (p3, q2) = ThreeSum2(p3, q2, t0);
            double p4 = q2 + t1;

            var (r0, r1, r2, r3) = Renorm5(p0, p1, p2, p3, p4);
            return new QD(r0, r1, r2, r3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QD operator *(double a, QD b) => b * a;

        /// <summary>this², saves redundant cross-products vs operator *.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QD Square()
        {
            var (p0, q0) = TwoProduct(X0, X0);
            var (p1, q1) = TwoProduct(2.0 * X0, X1);
            var (p2, q2) = TwoProduct(2.0 * X0, X2);
            var (p3, q3) = TwoProduct(X1, X1);

            (p1, q0) = TwoSum(p1, q0);

            double t0, t1;
            (p2, t0, t1) = ThreeSum(p2, q1, q0);
            (p3, q2) = ThreeSum2(p3, q2, t0);
            double p4 = q2 + t1;

            // O(eps³) self-product corrections
            p4 += 2.0 * X0 * X3 + 2.0 * X1 * X2 + q3;

            var (r0, r1, r2, r3) = Renorm5(p0, p1, p2, p3, p4);
            return new QD(r0, r1, r2, r3);
        }

        // ── Comparisons (Hi-only — sufficient for escape check) ──────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(QD a, double b) => a.X0 >= b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(QD a, double b) => a.X0 <= b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(QD a, double b) => a.X0 < b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(QD a, double b) => a.X0 > b;

        // ── Conversions ───────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator double(QD q) => q.X0 + q.X1 + q.X2 + q.X3;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator QD(double d) => new(d, 0, 0, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DD ToDD() => new(X0, X1);

        // ── Coordinate factory ────────────────────────────────────────────────

        /// <summary>
        /// center + pixelOffset × scale with full QD accuracy. Used to position
        /// individual pixels in the complex plane at extreme zoom (>5×10²⁷).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static QD FromCenterOffset(QD center, double pixelOffset, double scale)
        {
            // Product: pixelOffset × scale (TwoProduct gives exact 2-double).
            var (offHi, offLo) = TwoProduct(pixelOffset, scale);
            // Add product to centre as QD + DD-like.
            return center + new QD(offHi, offLo, 0, 0);
        }

        public override string ToString()
            => $"QD({X0:G17} + {X1:G6} + {X2:G6} + {X3:G6})";
    }
}