// Math/DoubleDouble.cs
//
// Double-Double (DD) floating-point arithmetic.
//
// A DD number is represented as the unevaluated sum  Hi + Lo  where:
//   • Hi  carries the dominant bits (a normal IEEE-754 double)
//   • Lo  carries the round-off error of Hi  (|Lo| ≤ 0.5 · ulp(Hi))
//
// This gives approximately 31 significant decimal digits — twice the precision
// of a double (15-16 digits) — which supports Mandelbrot zoom levels up to
// roughly 5×10²⁷ before aliasing becomes visible.
//
// Algorithms adapted from:
//   Hida, Li, Bailey — "Algorithms for Quad-Double Precision Floating Point
//   Arithmetic", LBNL Technical Report (2007), and the accompanying `qd` library.
//
// Key design choices for performance in the Mandelbrot inner loop:
//   • The struct is `readonly` so the JIT can keep fields in registers.
//   • All methods are AggressiveInlining.
//   • Math.FusedMultiplyAdd is used for TwoProduct — available on .NET 5+ and
//     maps to a single hardware FMA instruction on x86/ARM when supported; falls
//     back to a software implementation that is still mathematically exact.
//   • Square() is a specialized, faster variant of DD×DD when both operands
//     are equal (saves one TwoProduct call).
//   • FromCenterOffset() is the single factory needed by the Mandelbrot loop:
//     it computes  center + pixelOffset * scale  with full DD accuracy, even
//     when scale is a denormalized double below 1e-300.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.FFMath
{
    /// <summary>
    /// Double-double floating-point number (Hi + Lo) with ~31 decimal digits
    /// of precision. Suitable for Mandelbrot zooms up to ~5×10²⁷.
    /// </summary>
    public readonly struct DD
    {
        // ── Fields ────────────────────────────────────────────────────────────

        /// <summary>High word — the dominant part of the value.</summary>
        public readonly double Hi;

        /// <summary>Low word — the round-off correction; |Lo| ≤ 0.5·ulp(Hi).</summary>
        public readonly double Lo;

        // ── Constants ─────────────────────────────────────────────────────────

        public static readonly DD Zero = new(0.0, 0.0);
        public static readonly DD One  = new(1.0, 0.0);

        // ── Constructors ──────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DD(double hi, double lo) { Hi = hi; Lo = lo; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DD(double value) { Hi = value; Lo = 0.0; }

        // ── Private exact-arithmetic primitives ───────────────────────────────

        /// <summary>
        /// Exact split of (a + b) into sum s and round-off error e, such that
        /// a + b = s + e exactly.  No constraint on |a| vs |b|.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e) TwoSum(double a, double b)
        {
            double s = a + b;
            double v = s - a;
            double e = (a - (s - v)) + (b - v);
            return (s, e);
        }

        /// <summary>
        /// Faster variant of TwoSum that requires |a| ≥ |b|.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double s, double e) QuickTwoSum(double a, double b)
        {
            double s = a + b;
            double e = b - (s - a);
            return (s, e);
        }

        /// <summary>
        /// Exact split of (a × b) into product p and round-off error e, such
        /// that a × b = p + e exactly.  Uses a single FMA instruction when the
        /// hardware supports it; otherwise the .NET runtime uses a software
        /// implementation that is still IEEE-754 correct.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (double p, double e) TwoProduct(double a, double b)
        {
            double p = a * b;
            double e = System.Math.FusedMultiplyAdd(a, b, -p);
            return (p, e);
        }

        // ── Arithmetic operators ──────────────────────────────────────────────

        /// <summary>DD + DD</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD operator +(DD a, DD b)
        {
            // IEEE-correct Knuth/Priest DD addition (sloppy variant — 6 flops).
            var (s1, s2) = TwoSum(a.Hi, b.Hi);
            var (t1, t2) = TwoSum(a.Lo, b.Lo);
            s2 += t1;
            var (r1, r2) = QuickTwoSum(s1, s2);
            r2 += t2;
            var (p1, p2) = QuickTwoSum(r1, r2);
            return new DD(p1, p2);
        }

        /// <summary>DD - DD</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD operator -(DD a, DD b)
            => a + new DD(-b.Hi, -b.Lo);

        /// <summary>Negation</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD operator -(DD a) => new(-a.Hi, -a.Lo);

        /// <summary>DD + double</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD operator +(DD a, double b)
        {
            var (s1, s2) = TwoSum(a.Hi, b);
            s2 += a.Lo;
            var (r1, r2) = QuickTwoSum(s1, s2);
            return new DD(r1, r2);
        }

        /// <summary>DD - double</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD operator -(DD a, double b)
            => a + (-b);

        /// <summary>DD × DD</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD operator *(DD a, DD b)
        {
            // Standard DD multiplication: exact Hi×Hi product + first-order corrections.
            // Neglects Hi.Lo × Lo.Hi and Lo.Lo products (O(eps²) error — below DD floor).
            var (p1, p2) = TwoProduct(a.Hi, b.Hi);
            p2 += a.Hi * b.Lo + a.Lo * b.Hi;
            var (r1, r2) = QuickTwoSum(p1, p2);
            return new DD(r1, r2);
        }

        /// <summary>DD × double</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD operator *(DD a, double b)
        {
            var (p1, p2) = TwoProduct(a.Hi, b);
            p2 += a.Lo * b;
            var (r1, r2) = QuickTwoSum(p1, p2);
            return new DD(r1, r2);
        }

        /// <summary>double × DD</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD operator *(double a, DD b) => b * a;

        /// <summary>
        /// Computes this² faster than this×this by exploiting the symmetry
        /// (saves one TwoProduct call and skips the cross-term duplication).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DD Square()
        {
            var (p1, p2) = TwoProduct(Hi, Hi);
            p2 += 2.0 * Hi * Lo;   // Lo² is O(eps³) — omitted safely
            var (r1, r2) = QuickTwoSum(p1, p2);
            return new DD(r1, r2);
        }

        // ── Comparison ────────────────────────────────────────────────────────
        //
        // For the Mandelbrot escape check |z|² ≥ R², comparing the Hi word is
        // sufficient: the Lo correction is at most ~1e-31 relative, while the
        // escape radius squared (EscapeRadius² = 262144) is a large integer.
        // An edge-case ±1-iteration error at the exact threshold is invisible.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(DD a, double b) => a.Hi >= b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(DD a, double b) => a.Hi <= b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(DD a, double b) => a.Hi < b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(DD a, double b) => a.Hi > b;

        // ── Conversions ───────────────────────────────────────────────────────

        /// <summary>Explicit narrowing cast — returns Hi + Lo rounded to double.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator double(DD d) => d.Hi + d.Lo;

        /// <summary>Implicit widening from double (Lo = 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator DD(double d) => new(d, 0.0);

        // ── Mandelbrot coordinate factory ─────────────────────────────────────

        /// <summary>
        /// Computes <c>center + pixelOffset × scale</c> with full DD accuracy.
        ///
        /// At extreme zoom, <paramref name="scale"/> (world-units per pixel) is a
        /// very small double.  The product <c>pixelOffset × scale</c> is computed
        /// exactly via TwoProduct, and then added to <paramref name="center"/> via
        /// TwoSum.  This captures the tiny offset in the Lo word so the DD value
        /// locates the pixel precisely in the complex plane.
        ///
        /// Example: center = -0.74357, scale = 3.5e-20, pixelOffset = 500.
        /// product = 500 × 3.5e-20 = 1.75e-17  (tiny, but exactly represented)
        /// result  = DD(-0.74357, 1.75e-17)  ← full double-double precision
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD FromCenterOffset(double center, double pixelOffset, double scale)
        {
            // Step 1: exact product of pixelOffset × scale.
            var (offHi, offLo) = TwoProduct(pixelOffset, scale);

            // Step 2: exact sum of center (large) + offHi (tiny) via TwoSum.
            var (s, e) = TwoSum(center, offHi);

            // Absorb the product's round-off into the correction word.
            e += offLo;

            // Re-normalise (ensure |e| ≤ 0.5 ulp(s)).
            var (r1, r2) = QuickTwoSum(s, e);
            return new DD(r1, r2);
        }

        public override string ToString() => $"DD({Hi:G17} + {Lo:G6})";
    }
}
