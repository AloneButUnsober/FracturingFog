// Math/OctupleDouble.cs
//
// Octuple-Double (OD) floating-point arithmetic — 8 doubles unevaluated sum.
// Provides ~124 decimal digits of precision (8× double, 2× QD).
//
// Used to compute the Mandelbrot reference orbit at zoom levels beyond QD's
// ~5×10⁵⁸ ceiling (engaged at Zoom > 1e50 in MandelbrotCalculator). Per-pixel
// δ stays double; only the reference orbit and view-centre coordinate need
// this much precision.
//
// Algorithms extend the Hida-Li-Bailey QD pattern (LBNL 2007). Each operator
// uses sloppy variants: drops O(eps⁸) tail terms, sufficient bound for our
// ~124-digit target. The renormalization is a 9-term QuickTwoSum cascade
// reducing back to canonical 8-term form.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.FFMath
{
    /// <summary>
    /// Octuple-double floating-point number — sum of eight doubles
    /// X0+X1+…+X7 with non-overlapping mantissas. Provides ~124 decimal
    /// digits of precision, supporting Mandelbrot zoom up to ~10¹¹⁶.
    /// </summary>
    public readonly struct OD
    {
        public readonly double X0, X1, X2, X3, X4, X5, X6, X7;

        public static readonly OD Zero = new(0, 0, 0, 0, 0, 0, 0, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OD(double x0, double x1, double x2, double x3,
                  double x4, double x5, double x6, double x7)
        { X0 = x0; X1 = x1; X2 = x2; X3 = x3; X4 = x4; X5 = x5; X6 = x6; X7 = x7; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OD(double v)
        { X0 = v; X1 = 0; X2 = 0; X3 = 0; X4 = 0; X5 = 0; X6 = 0; X7 = 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OD(DD dd)
        { X0 = dd.Hi; X1 = dd.Lo; X2 = 0; X3 = 0; X4 = 0; X5 = 0; X6 = 0; X7 = 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OD(QD q)
        { X0 = q.X0; X1 = q.X1; X2 = q.X2; X3 = q.X3; X4 = 0; X5 = 0; X6 = 0; X7 = 0; }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e1, double e2) ThreeSum(
            double a, double b, double c)
        {
            var (t1, t2) = TwoSum(a, b);
            var (s, t3) = TwoSum(c, t1);
            var (e1, e2) = TwoSum(t2, t3);
            return (s, e1, e2);
        }

        // Renormalize a 9-term expansion to canonical 8-term form.
        // QuickTwoSum cascade — non-overlap relies on monotone-magnitude input.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static OD Renorm9(double c0, double c1, double c2, double c3,
                                  double c4, double c5, double c6, double c7,
                                  double c8)
        {
            // Pass 1 — bottom-up bubble of low-order error into upper limbs.
            (c7, c8) = QuickTwoSum(c7, c8);
            (c6, c7) = QuickTwoSum(c6, c7);
            (c5, c6) = QuickTwoSum(c5, c6);
            (c4, c5) = QuickTwoSum(c4, c5);
            (c3, c4) = QuickTwoSum(c3, c4);
            (c2, c3) = QuickTwoSum(c2, c3);
            (c1, c2) = QuickTwoSum(c1, c2);
            (c0, c1) = QuickTwoSum(c0, c1);

            // Pass 2 — compact zero-error limbs, drop the tail (c8 already
            // folded into c7 above; remaining residual is bounded by 2·eps^8
            // and fits well below the OD non-overlap floor).
            double s0 = c0, s1 = c1, s2 = c2, s3 = c3,
                   s4 = c4, s5 = c5, s6 = c6, s7 = c7;
            double[] src = { s1, s2, s3, s4, s5, s6, s7, c8 };
            double[] dst = new double[8];
            int k = 0;
            for (int i = 0; i < 8 && k < 8; i++)
            {
                if (src[i] == 0.0) continue;
                if (k == 0)
                {
                    // First non-zero feeds back into s0 via QuickTwoSum.
                    (s0, dst[0]) = QuickTwoSum(s0, src[i]);
                    k = 1;
                    continue;
                }
                (dst[k - 1], dst[k]) = QuickTwoSum(dst[k - 1], src[i]);
                if (dst[k] != 0.0) k++;
            }
            return new OD(s0, dst[0], dst[1], dst[2], dst[3], dst[4], dst[5], dst[6]);
        }

        // ── Addition (sloppy variant — eight pairwise TwoSums + cascade) ─────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD operator +(OD a, OD b)
        {
            // Pairwise TwoSum across matching limbs.
            var (s0, e0) = TwoSum(a.X0, b.X0);
            var (s1, e1) = TwoSum(a.X1, b.X1);
            var (s2, e2) = TwoSum(a.X2, b.X2);
            var (s3, e3) = TwoSum(a.X3, b.X3);
            var (s4, e4) = TwoSum(a.X4, b.X4);
            var (s5, e5) = TwoSum(a.X5, b.X5);
            var (s6, e6) = TwoSum(a.X6, b.X6);
            double s7 = a.X7 + b.X7;

            // Carry residuals up via TwoSum chain (one level — sloppy).
            (s1, e0) = TwoSum(s1, e0);
            (s2, e1) = TwoSum(s2, e1);
            (s3, e2) = TwoSum(s3, e2);
            (s4, e3) = TwoSum(s4, e3);
            (s5, e4) = TwoSum(s5, e4);
            (s6, e5) = TwoSum(s6, e5);
            s7 += e6;

            // Second residual sweep.
            (s2, e0) = TwoSum(s2, e0);
            (s3, e1) = TwoSum(s3, e1);
            (s4, e2) = TwoSum(s4, e2);
            (s5, e3) = TwoSum(s5, e3);
            (s6, e4) = TwoSum(s6, e4);
            s7 += e5;

            // Final fold — remaining residuals into tail.
            s3 += e0;
            s4 += e1;
            s5 += e2;
            s6 += e3;
            s7 += e4;

            return Renorm9(s0, s1, s2, s3, s4, s5, s6, s7, 0.0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD operator -(OD a)
            => new(-a.X0, -a.X1, -a.X2, -a.X3, -a.X4, -a.X5, -a.X6, -a.X7);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD operator -(OD a, OD b) => a + (-b);

        // OD + double — cascade carry through every limb via TwoSum chain.
        // Mirrors QD's full-chain form so navigation past 1e80 doesn't lose
        // pixel offset into a discarded slot.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD operator +(OD a, double b)
        {
            var (s0, e0) = TwoSum(a.X0, b);
            var (s1, e1) = TwoSum(a.X1, e0);
            var (s2, e2) = TwoSum(a.X2, e1);
            var (s3, e3) = TwoSum(a.X3, e2);
            var (s4, e4) = TwoSum(a.X4, e3);
            var (s5, e5) = TwoSum(a.X5, e4);
            var (s6, e6) = TwoSum(a.X6, e5);
            double s7 = a.X7 + e6;
            return Renorm9(s0, s1, s2, s3, s4, s5, s6, s7, 0.0);
        }

        // ── Multiplication ───────────────────────────────────────────────────
        //
        // Collects ALL partial products into a 9-slot expansion, then
        // canonicalises via Renorm9. Each TwoProduct(a.Xi, b.Xj) produces
        // (p, q): p contributes to expansion slot i+j, q to slot i+j+1.
        // Adding into a slot uses a TwoSum chain that propagates residuals
        // forward — no precision is silently dropped.
        //
        // History: the earlier sloppy variant reused ThreeSum residual names
        // across diagonals (r1a/r1b → r2a/r2b → r3a/r3b in sequence) and
        // overwrote tier-1 residuals before they were folded into s2/s3.
        // The lost ~1e-32 mass accumulated through repeated squaring and
        // bubbled into X0 by iter ~127 of a typical Mandelbrot ref orbit,
        // collapsing every pixel to one colour at zoom > 1e40.
        public static OD operator *(OD a, OD b)
        {
            // 9-slot expansion. We discard contributions to slot 8+ (sloppy
            // tail — magnitudes ≤ eps^8 ≈ 1e-128, well below the OD floor).
            Span<double> e = stackalloc double[9];

            // Drop a value v into slot k, cascading carries up via TwoSum
            // until the residual vanishes or we run off the end. Manually
            // unrolled to avoid Span+loop spilling.
            // (We accept the ~64 inlined call sites — each is ~8 TwoSum ops
            // max but typically terminates after 2-3.)
            //
            // For tier 0..6, both p (slot i+j) and q (slot i+j+1) are added.
            // For tier 7, only the p contribution survives — q would land
            // in slot 8 which we discard.

            // Tier 0
            AddPair(e, 0, a.X0, b.X0);
            // Tier 1
            AddPair(e, 1, a.X0, b.X1);
            AddPair(e, 1, a.X1, b.X0);
            // Tier 2
            AddPair(e, 2, a.X0, b.X2);
            AddPair(e, 2, a.X1, b.X1);
            AddPair(e, 2, a.X2, b.X0);
            // Tier 3
            AddPair(e, 3, a.X0, b.X3);
            AddPair(e, 3, a.X1, b.X2);
            AddPair(e, 3, a.X2, b.X1);
            AddPair(e, 3, a.X3, b.X0);
            // Tier 4
            AddPair(e, 4, a.X0, b.X4);
            AddPair(e, 4, a.X1, b.X3);
            AddPair(e, 4, a.X2, b.X2);
            AddPair(e, 4, a.X3, b.X1);
            AddPair(e, 4, a.X4, b.X0);
            // Tier 5
            AddPair(e, 5, a.X0, b.X5);
            AddPair(e, 5, a.X1, b.X4);
            AddPair(e, 5, a.X2, b.X3);
            AddPair(e, 5, a.X3, b.X2);
            AddPair(e, 5, a.X4, b.X1);
            AddPair(e, 5, a.X5, b.X0);
            // Tier 6
            AddPair(e, 6, a.X0, b.X6);
            AddPair(e, 6, a.X1, b.X5);
            AddPair(e, 6, a.X2, b.X4);
            AddPair(e, 6, a.X3, b.X3);
            AddPair(e, 6, a.X4, b.X2);
            AddPair(e, 6, a.X5, b.X1);
            AddPair(e, 6, a.X6, b.X0);
            // Tier 7 — p only (q would land in discarded slot 8)
            AddProduct(e, 7, a.X0, b.X7);
            AddProduct(e, 7, a.X1, b.X6);
            AddProduct(e, 7, a.X2, b.X5);
            AddProduct(e, 7, a.X3, b.X4);
            AddProduct(e, 7, a.X4, b.X3);
            AddProduct(e, 7, a.X5, b.X2);
            AddProduct(e, 7, a.X6, b.X1);
            AddProduct(e, 7, a.X7, b.X0);

            return Renorm9(e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7], e[8]);
        }

        // Add value `v` to expansion slot `slot`, cascading residuals via
        // TwoSum until exhausted or out of slots.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddToExpansion(Span<double> exp, int slot, double v)
        {
            for (int i = slot; i < exp.Length; i++)
            {
                var (s, err) = TwoSum(exp[i], v);
                exp[i] = s;
                v = err;
                if (v == 0.0) break;
            }
        }

        // Compute TwoProduct(a, b) and add both halves into the expansion
        // (high half at `slot`, low half at `slot+1`).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddPair(Span<double> exp, int slot, double a, double b)
        {
            var (p, q) = TwoProduct(a, b);
            AddToExpansion(exp, slot, p);
            AddToExpansion(exp, slot + 1, q);
        }

        // Single-product accumulation (no TwoProduct — caller wants scalar
        // contribution only, e.g. tier 7 where q would land out of range).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddProduct(Span<double> exp, int slot, double a, double b)
        {
            AddToExpansion(exp, slot, a * b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD operator *(OD a, double b)
        {
            var (p0, q0) = TwoProduct(a.X0, b);
            var (p1, q1) = TwoProduct(a.X1, b);
            var (p2, q2) = TwoProduct(a.X2, b);
            var (p3, q3) = TwoProduct(a.X3, b);
            var (p4, q4) = TwoProduct(a.X4, b);
            var (p5, q5) = TwoProduct(a.X5, b);
            var (p6, q6) = TwoProduct(a.X6, b);
            double p7 = a.X7 * b;

            // Cascade carries: q_i (low part of a.X_i * b) feeds into p_{i+1}.
            (p1, q0) = TwoSum(p1, q0);
            (p2, q1) = TwoSum(p2, q1);
            (p3, q2) = TwoSum(p3, q2);
            (p4, q3) = TwoSum(p4, q3);
            (p5, q4) = TwoSum(p5, q4);
            (p6, q5) = TwoSum(p6, q5);
            p7 += q6;

            // Second-pass carry sweep.
            (p2, q0) = TwoSum(p2, q0);
            (p3, q1) = TwoSum(p3, q1);
            (p4, q2) = TwoSum(p4, q2);
            (p5, q3) = TwoSum(p5, q3);
            (p6, q4) = TwoSum(p6, q4);
            p7 += q5;

            p3 += q0;
            p4 += q1;
            p5 += q2;
            p6 += q3;
            p7 += q4;

            return Renorm9(p0, p1, p2, p3, p4, p5, p6, p7, 0.0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD operator *(double a, OD b) => b * a;

        /// <summary>this², saves cross-product duplication vs operator *.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public OD Square() => this * this;

        /// <summary>OD / OD — long-division by repeated Newton refinement on the Hi limb.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD operator /(OD a, OD b)
        {
            double q0 = a.X0 / b.X0;
            OD r = a - b * q0;

            double q1 = r.X0 / b.X0;
            r = r - b * q1;

            double q2 = r.X0 / b.X0;
            r = r - b * q2;

            double q3 = r.X0 / b.X0;
            r = r - b * q3;

            double q4 = r.X0 / b.X0;
            r = r - b * q4;

            double q5 = r.X0 / b.X0;
            r = r - b * q5;

            double q6 = r.X0 / b.X0;
            r = r - b * q6;

            double q7 = r.X0 / b.X0;

            return Renorm9(q0, q1, q2, q3, q4, q5, q6, q7, 0.0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD operator /(OD a, double b) => a / new OD(b);

        // ── Comparisons (Hi-only — sufficient for escape check) ──────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(OD a, double b) => a.X0 >= b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(OD a, double b) => a.X0 <= b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(OD a, double b) => a.X0 < b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(OD a, double b) => a.X0 > b;

        // ── Conversions ───────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator double(OD o)
            => o.X0 + o.X1 + o.X2 + o.X3 + o.X4 + o.X5 + o.X6 + o.X7;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator OD(double d) => new(d, 0, 0, 0, 0, 0, 0, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DD ToDD() => new(X0, X1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QD ToQD() => new(X0, X1, X2, X3);

        // ── Coordinate factory ────────────────────────────────────────────────

        /// <summary>
        /// center + pixelOffset × scale with full OD accuracy. Used to position
        /// individual pixels in the complex plane at extreme zoom (&gt; 1e50).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static OD FromCenterOffset(OD center, double pixelOffset, double scale)
        {
            var (offHi, offLo) = TwoProduct(pixelOffset, scale);
            // Add via the OD+double operator (full-cascade), NOT center + OD(offHi,…).
            // The OD+OD sloppy operator only carries a residual down ~3 limbs, so a
            // deep-zoom offset landing at limb X4+ (|off| ~ 1e-64, zoom > ~1e64) gets
            // parked against X3 and rounded away — every pixel collapsing to the
            // centre. OD+double propagates the addend's residual through all 8 limbs,
            // placing it at its true magnitude and extending the coordinate floor to
            // OD's real ~1e112 reach.
            return (center + offHi) + offLo;
        }

        public override string ToString()
            => $"OD({X0:G17} + {X1:G6} + {X2:G6} + {X3:G6} + {X4:G6} + {X5:G6} + {X6:G6} + {X7:G6})";
    }
}
