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
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
        public static readonly DD One = new(1.0, 0.0);

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

        /// <summary>
        /// DD-center variant of <see cref="FromCenterOffset(double, double, double)"/>.
        /// The center's Lo bits are essential at zoom ≳ 1e15, where the Hi-only
        /// form would round the per-pixel result back onto a coarse double grid.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD FromCenterOffset(DD center, double pixelOffset, double scale)
        {
            var (offHi, offLo) = TwoProduct(pixelOffset, scale);
            var (s, e) = TwoSum(center.Hi, offHi);
            e += offLo + center.Lo;
            var (r1, r2) = QuickTwoSum(s, e);
            return new DD(r1, r2);
        }

        public override string ToString() => $"DD({Hi:G17} + {Lo:G6})";
    }

    // Math/DD4.cs
    //
    // 4-wide SIMD double-double arithmetic using AVX2 + FMA.
    //
    // Layout: Hi = [Hi0|Hi1|Hi2|Hi3], Lo = [Lo0|Lo1|Lo2|Lo3]
    // The four lanes are independent Mandelbrot pixels, so all TwoProduct
    // chains execute in parallel, hiding the ~4-cycle multiply latency
    // that serialises the scalar DD path. Expected speedup: 3–3.5×.

    public readonly struct DD4
    {
        public readonly Vector256<double> Hi;
        public readonly Vector256<double> Lo;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DD4(Vector256<double> hi, Vector256<double> lo) { Hi = hi; Lo = lo; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD4 Broadcast(double v)
            => new(Vector256.Create(v), Vector256<double>.Zero);

        // ── Exact primitives ─────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static (Vector256<double> s, Vector256<double> e) TwoSum(
            Vector256<double> a, Vector256<double> b)
        {
            var s = Avx.Add(a, b);
            var v = Avx.Subtract(s, a);
            var e = Avx.Add(Avx.Subtract(a, Avx.Subtract(s, v)),
                            Avx.Subtract(b, v));
            return (s, e);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static (Vector256<double> s, Vector256<double> e) QuickTwoSum(
            Vector256<double> a, Vector256<double> b)
        {
            var s = Avx.Add(a, b);
            var e = Avx.Subtract(b, Avx.Subtract(s, a));
            return (s, e);
        }

        // VFNMADD: e = -(a*b) + p  →  exact round-off of p = a*b
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static (Vector256<double> p, Vector256<double> e) TwoProduct(
            Vector256<double> a, Vector256<double> b)
        {
            var p = Avx.Multiply(a, b);
            var e = Fma.MultiplyAddNegated(a, b, p);   // -(a*b)+p
            return (p, e);
        }

        // ── Operators ────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD4 operator +(DD4 a, DD4 b)
        {
            var (s1, s2) = TwoSum(a.Hi, b.Hi);
            var (t1, t2) = TwoSum(a.Lo, b.Lo);
            s2 = Avx.Add(s2, t1);
            var (r1, r2) = QuickTwoSum(s1, s2);
            r2 = Avx.Add(r2, t2);
            var (p1, p2) = QuickTwoSum(r1, r2);
            return new DD4(p1, p2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD4 operator -(DD4 a, DD4 b)
        {
            var negHi = Avx.Subtract(Vector256<double>.Zero, b.Hi);
            var negLo = Avx.Subtract(Vector256<double>.Zero, b.Lo);
            return a + new DD4(negHi, negLo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD4 operator *(DD4 a, DD4 b)
        {
            var (p1, p2) = TwoProduct(a.Hi, b.Hi);
            // Cross terms: Hi*Lo + Lo*Hi (FMA saves one ADD each)
            p2 = Fma.MultiplyAdd(a.Hi, b.Lo,
                 Fma.MultiplyAdd(a.Lo, b.Hi, p2));
            var (r1, r2) = QuickTwoSum(p1, p2);
            return new DD4(r1, r2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD4 operator *(DD4 a, double b)
        {
            var bv = Vector256.Create(b);
            var (p1, p2) = TwoProduct(a.Hi, bv);
            p2 = Fma.MultiplyAdd(a.Lo, bv, p2);
            var (r1, r2) = QuickTwoSum(p1, p2);
            return new DD4(r1, r2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD4 operator +(DD4 a, double b)
        {
            var bv = Vector256.Create(b);
            var (s1, s2) = TwoSum(a.Hi, bv);
            s2 = Avx.Add(s2, a.Lo);
            var (r1, r2) = QuickTwoSum(s1, s2);
            return new DD4(r1, r2);
        }

        /// <summary>Optimised squaring — saves one TwoProduct vs operator *.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DD4 Square()
        {
            var two = Vector256.Create(2.0);
            var (p1, p2) = TwoProduct(Hi, Hi);
            p2 = Avx.Add(p2, Avx.Multiply(two, Avx.Multiply(Hi, Lo)));
            var (r1, r2) = QuickTwoSum(p1, p2);
            return new DD4(r1, r2);
        }

        // ── Escape check ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns a 4-bit mask (bits 0–3) where set = lane has |z|² >= threshold.
        /// Comparing Hi is sufficient — see DD.cs for the rationale.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int EscapeMask(DD4 mag2, double threshold)
            => Avx.MoveMask(
                   Avx.CompareGreaterThanOrEqual(
                       mag2.Hi, Vector256.Create(threshold)));

        // ── Coordinate factory ────────────────────────────────────────────────

        /// <summary>
        /// center + pixelOffsets[0..3] × scale with full DD accuracy.
        /// Equivalent to calling DD.FromCenterOffset four times, but uses
        /// vectorised TwoProduct / TwoSum so all four results compute in parallel.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD4 FromCenterOffset(
            double center, Vector256<double> pixelOffsets, double scale)
        {
            var sv = Vector256.Create(scale);
            var cv = Vector256.Create(center);
            var (offHi, offLo) = TwoProduct(pixelOffsets, sv);
            var (s, e) = TwoSum(cv, offHi);
            e = Avx.Add(e, offLo);
            var (r1, r2) = QuickTwoSum(s, e);
            return new DD4(r1, r2);
        }

        /// <summary>
        /// Overload that accepts a DD centre so the Lo word (sub-double precision
        /// accumulated over zoom steps) flows into every pixel's coordinate.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static DD4 FromCenterOffset(
            DD center, Vector256<double> pixelOffsets, double scale)
        {
            var sv  = Vector256.Create(scale);
            var cv  = Vector256.Create(center.Hi);
            var clo = Vector256.Create(center.Lo);
            var (offHi, offLo) = TwoProduct(pixelOffsets, sv);
            var (s, e) = TwoSum(cv, offHi);
            e = Avx.Add(e, Avx.Add(clo, offLo));
            var (r1, r2) = QuickTwoSum(s, e);
            return new DD4(r1, r2);
        }

        // ── Lane extraction ───────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double GetHi(int lane) => Hi.GetElement(lane);

        // ── CPU capability guard ──────────────────────────────────────────────

        /// <summary>True when AVX2 + FMA are both present. Fall back to scalar DD otherwise.</summary>
        public static bool IsSupported => Avx2.IsSupported && Fma.IsSupported;
    }
}
