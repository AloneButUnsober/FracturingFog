// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Math/FloatExp.cs
//
// #1842 (epic #1428, C5a) — an extended-exponent float: a double mantissa with a long
// binary exponent. Past double's range (~1e308 zoom) a pixel's perturbation δ, its dc and
// the pixel scale underflow double; FloatExp keeps double's 53-bit precision with an
// exponent that does not run out. The idea is Kalles Fraktaler 2+'s "floatexp" extended
// floating point (Heiland-Allen / Runmo, KF2+ manual; Docs/Resources-Bibliography.md
// #kf-floatexp); FF's layout here is its own. FF's GPU float-exp kernels (#1331) use the
// fp32 form of the same idea.
//
// Value = M · 2^E with M == 0 or 1 ≤ |M| < 2. Every operation is one double operation on
// the mantissas (so one rounding) plus exact exponent bookkeeping.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.FFMath
{
    /// <summary>#1842 — extended-exponent float: M · 2^E, 1 ≤ |M| &lt; 2 (or 0).</summary>
    public readonly struct FloatExp
    {
        /// <summary>The mantissa: 0, or 1 ≤ |M| &lt; 2.</summary>
        public readonly double M;
        /// <summary>The binary exponent.</summary>
        public readonly long E;

        private FloatExp(double m, long e) { M = m; E = e; }

        public static readonly FloatExp Zero = new(0, 0);
        public static readonly FloatExp One = new(1, 0);

        /// <summary>#1847 — a value above every finite operand (2^(2^60)): x / 0 in the LA
        /// builders' min(…, x / y), where double gives +∞. Only ever compared or scaled by a
        /// finite factor.</summary>
        public static readonly FloatExp Huge = new(1, 1L << 60);

        public bool IsZero => M == 0;

        /// <summary>#1847 — |this|.</summary>
        public FloatExp Abs() => new(Math.Abs(M), E);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static FloatExp Norm(double m, long e)
        {
            if (m == 0 || !double.IsFinite(m)) return m == 0 ? Zero : new FloatExp(m, e);
            int k = Math.ILogB(m);
            return new FloatExp(Math.ScaleB(m, -k), e + k);
        }

        /// <summary>A double, exactly.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp FromDouble(double d) => Norm(d, 0);

        /// <summary>2^<paramref name="log2"/> (rounded once).</summary>
        public static FloatExp FromLog2(double log2)
        {
            double e = Math.Floor(log2);
            return Norm(Math.Pow(2, log2 - e), (long)e);
        }

        /// <summary>10^<paramref name="log10"/>.</summary>
        public static FloatExp FromLog10(double log10) => FromLog2(log10 * 3.3219280948873622);

        /// <summary>The value as a double: 0 below double's range, ±∞ above it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double ToDouble()
            => M == 0 ? 0 : E > 1100 ? (M > 0 ? double.PositiveInfinity : double.NegativeInfinity)
             : E < -1100 ? 0 : Math.ScaleB(M, (int)E);

        /// <summary>log2 |value| (−∞ for 0).</summary>
        public double Log2Abs() => M == 0 ? double.NegativeInfinity : E + Math.Log2(Math.Abs(M));

        /// <summary>This · 2^<paramref name="n"/>, exactly.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FloatExp ScaleB(long n) => M == 0 ? this : new FloatExp(M, E + n);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp operator *(FloatExp a, FloatExp b) => Norm(a.M * b.M, a.E + b.E);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp operator *(FloatExp a, double b) => a * FromDouble(b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp operator -(FloatExp a) => new(-a.M, a.E);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp operator +(FloatExp a, FloatExp b)
        {
            if (a.M == 0) return b;
            if (b.M == 0) return a;
            long d = a.E - b.E;
            if (d > 60) return a;    // b is below a's last bit
            if (d < -60) return b;
            return d >= 0 ? Norm(a.M + Math.ScaleB(b.M, (int)-d), a.E)
                          : Norm(Math.ScaleB(a.M, (int)d) + b.M, b.E);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp operator +(FloatExp a, double b) => a + FromDouble(b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp operator -(FloatExp a, FloatExp b) => a + -b;

        /// <summary>#1845 — a / b (b ≠ 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp operator /(FloatExp a, FloatExp b) => Norm(a.M / b.M, a.E - b.E);

        /// <summary>#1845 — √this (this ≥ 0; 0 for 0).</summary>
        public FloatExp Sqrt()
        {
            if (M <= 0) return Zero;
            double m = M; long e = E;
            if ((e & 1) != 0) { m *= 2; e -= 1; }   // even exponent (also for negative e)
            return Norm(Math.Sqrt(m), e / 2);
        }

        /// <summary>#1845 — the larger of two non-negative values.</summary>
        public static FloatExp Max(FloatExp a, FloatExp b) => AbsLess(a, b) ? b : a;

        /// <summary>#1845 — the smaller of two non-negative values.</summary>
        public static FloatExp Min(FloatExp a, FloatExp b) => AbsLess(a, b) ? a : b;

        /// <summary>|a| &lt; |b|.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AbsLess(FloatExp a, FloatExp b)
        {
            if (b.M == 0) return false;
            if (a.M == 0) return true;
            return a.E != b.E ? a.E < b.E : Math.Abs(a.M) < Math.Abs(b.M);
        }

        /// <summary>re² + im² (the squared magnitude of a complex pair).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FloatExp Norm2(FloatExp re, FloatExp im) => re * re + im * im;

        public override string ToString() => M == 0 ? "0" : $"{M:R}·2^{E}";
    }
}
