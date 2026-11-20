// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1914 (epic #1253, D9) — elementary functions for BigFloat, so a transcendental formula's reference
// orbit (D8, #1913) is the true orbit of its point at the view's precision. Each is argument reduction
// plus a series, computed with guard bits and rounded to the caller's precision. Sources (Docs/
// Resources-Bibliography.md, #wikipedia-elementary-series), read 2026-11-19:
//   • exp — the power series Σ xⁿ/n! (Wikipedia, "Exponential function"), after reducing x = k·ln 2 + r
//     and r → r/2^s; the result is squared s times and scaled by 2^k. Both reductions are the exponent
//     law (e^{a+b} = e^a·e^b), stated here rather than taken from a source;
//   • ln — the area-hyperbolic-tangent series ln x = 2·Σ y^{2k+1}/(2k+1), y = (x − 1)/(x + 1)
//     (Wikipedia, "Natural logarithm", which calls it the fastest-converging series it lists), on the
//     mantissa in [½, 1), plus e·ln 2; ln 2 = 2·atanh(1/3) by the same series;
//   • sin / cos — their Taylor series (Wikipedia, "Sine and cosine"), after reducing modulo 2π and
//     halving s times, then the double-angle formulas sin 2θ = 2 sin θ cos θ, cos 2θ = 1 − 2 sin² θ (same
//     page) s times;
//   • atan — Gregory's series Σ (−1)ⁿ z^{2n+1}/(2n+1) after arctan x = 2·arctan(x/(1 + √(1 + x²)))
//     s times, and arctan x = ±π/2 − arctan(1/x) for |x| > 1 (Wikipedia, "Inverse trigonometric
//     functions");
//   • π — Machin's formula π/4 = 4·arctan(1/5) − arctan(1/239), 1706 (Wikipedia, "Machin-like formula"),
//     with Gregory's series;
//   • √ — Newton's iteration y ← (y + x/y)/2 from a double estimate.
// MPFR (bibliography #mpfr) implements these with proven error bounds; FF uses none of its code and
// claims no correct rounding: the guard bits are generous, and S1914 checks identities at 200–2,000
// bits. Methods chosen for simplicity, not speed (no binary splitting, no AGM).

using System;
using System.Collections.Concurrent;
using System.Numerics;

namespace FracturingFog.FFMath
{
    public static class BigFloatMath
    {
        private static readonly ConcurrentDictionary<int, BigFloat> PiCache = new(), Ln2Cache = new();

        // log₂|x| rounded down (x ≠ 0).
        private static long Ilog2(BigFloat x) => x.Exponent + (long)x.Mantissa.GetBitLength() - 1;

        private static BigFloat One(int p) => BigFloat.FromDouble(1, p);

        private static bool Below(BigFloat term, BigFloat sum, int bits)
            => term.IsZero || (!sum.IsZero && Ilog2(term) < Ilog2(sum) - bits);

        /// <summary>√x (x ≥ 0).</summary>
        public static BigFloat Sqrt(BigFloat x, int p)
        {
            if (x.Sign < 0) throw new ArgumentOutOfRangeException(nameof(x), "negative");
            if (x.IsZero) return BigFloat.Zero(p);
            int wp = p + 32;
            // Scale to [1, 4) so the double estimate is in range: √(m·4^k) = √m·2^k.
            long e = Ilog2(x) / 2 * 2;
            var m = x.ScaleB(-e).WithPrecision(wp);
            var y = BigFloat.FromDouble(Math.Sqrt(m.ToDouble()), wp);
            for (int bits = 50; bits < 2 * wp; bits *= 2)
                y = BigFloat.Add(y, BigFloat.Div(m, y, wp), wp).ScaleB(-1);
            y = BigFloat.Add(y, BigFloat.Div(m, y, wp), wp).ScaleB(-1);
            return y.ScaleB(e / 2).WithPrecision(p);
        }

        // Gregory's series for |z| ≤ ~0.5 at wp bits.
        private static BigFloat AtanSeries(BigFloat z, int wp)
        {
            var z2 = BigFloat.Mul(z, z, wp);
            BigFloat sum = z, pow = z;
            for (int n = 1; ; n++)
            {
                pow = -BigFloat.Mul(pow, z2, wp);
                var term = BigFloat.Div(pow, BigFloat.FromDouble(2 * n + 1, wp), wp);
                sum = BigFloat.Add(sum, term, wp);
                if (Below(term, sum, wp + 4)) return sum;
            }
        }

        /// <summary>π by Machin's formula.</summary>
        public static BigFloat Pi(int p) => PiCache.GetOrAdd(p, q =>
        {
            int wp = q + 32;
            var a = AtanSeries(BigFloat.Div(One(wp), BigFloat.FromDouble(5, wp), wp), wp);
            var b = AtanSeries(BigFloat.Div(One(wp), BigFloat.FromDouble(239, wp), wp), wp);
            return BigFloat.Sub(a.ScaleB(2), b, wp).ScaleB(2).WithPrecision(q);   // 4·(4a − b)
        });

        // 2·atanh(y) = ln((1 + y)/(1 − y)) for small |y|.
        private static BigFloat AtanhSeries2(BigFloat y, int wp)
        {
            var y2 = BigFloat.Mul(y, y, wp);
            BigFloat sum = y, pow = y;
            for (int k = 1; ; k++)
            {
                pow = BigFloat.Mul(pow, y2, wp);
                var term = BigFloat.Div(pow, BigFloat.FromDouble(2 * k + 1, wp), wp);
                sum = BigFloat.Add(sum, term, wp);
                if (Below(term, sum, wp + 4)) return sum.ScaleB(1);
            }
        }

        /// <summary>ln 2 = 2·atanh(1/3).</summary>
        public static BigFloat Ln2(int p) => Ln2Cache.GetOrAdd(p, q =>
        {
            int wp = q + 32;
            return AtanhSeries2(BigFloat.Div(One(wp), BigFloat.FromDouble(3, wp), wp), wp).WithPrecision(q);
        });

        /// <summary>e^x.</summary>
        public static BigFloat Exp(BigFloat x, int p)
        {
            if (x.IsZero) return One(p);
            double xd = x.ToDouble();
            if (!(Math.Abs(xd) < 1e15)) throw new ArgumentOutOfRangeException(nameof(x), "|x| ≥ 1e15");
            // x = k·ln 2 + r, |r| ≤ ~½ ln 2; then r/2^s, with s ~ √wp.
            long k = (long)Math.Round(xd / Math.Log(2));
            int kBits = k == 0 ? 0 : (int)Math.Ceiling(Math.Log2(Math.Abs((double)k)) + 1);
            int s = (int)Math.Sqrt(p) + 4;
            int wp = p + 32 + kBits + s;
            var r = BigFloat.Sub(x.WithPrecision(wp), BigFloat.Mul(BigFloat.FromDouble(k, wp), Ln2(wp), wp), wp).ScaleB(-s);
            BigFloat sum = One(wp), term = One(wp);
            for (int n = 1; ; n++)
            {
                term = BigFloat.Div(BigFloat.Mul(term, r, wp), BigFloat.FromDouble(n, wp), wp);
                sum = BigFloat.Add(sum, term, wp);
                if (Below(term, sum, wp + 4)) break;
            }
            for (int i = 0; i < s; i++) sum = BigFloat.Mul(sum, sum, wp);
            return sum.ScaleB(k).WithPrecision(p);
        }

        /// <summary>ln x (x &gt; 0).</summary>
        public static BigFloat Log(BigFloat x, int p)
        {
            if (x.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(x), "x ≤ 0");
            int wp = p + 32;
            // x = m·2^e, m ∈ [½, 1): ln x = ln m + e·ln 2; ln m = 2·atanh((m − 1)/(m + 1)), |y| ≤ ⅓.
            long e = Ilog2(x) + 1;
            var m = x.ScaleB(-e).WithPrecision(wp + 64);
            var y = BigFloat.Div(BigFloat.Sub(m, One(wp + 64), wp + 64), BigFloat.Add(m, One(wp + 64), wp + 64), wp);
            var lnm = y.IsZero ? BigFloat.Zero(wp) : AtanhSeries2(y, wp);
            int eBits = e == 0 ? 0 : (int)Math.Ceiling(Math.Log2(Math.Abs((double)e)) + 1);
            var res = BigFloat.Add(lnm, BigFloat.Mul(BigFloat.FromDouble(e, wp), Ln2(wp + eBits), wp + eBits), wp);
            return res.WithPrecision(p);
        }

        /// <summary>(sin x, cos x).</summary>
        public static (BigFloat Sin, BigFloat Cos) SinCos(BigFloat x, int p)
        {
            if (x.IsZero) return (BigFloat.Zero(p), One(p));
            double xd = x.ToDouble();
            if (!(Math.Abs(xd) < 1e15)) throw new ArgumentOutOfRangeException(nameof(x), "|x| ≥ 1e15");
            int s = (int)Math.Sqrt(p) / 2 + 4;
            // Reducing modulo 2π loses the bits of k; a tiny x loses none but needs relative precision.
            long k = (long)Math.Round(xd / (2 * Math.PI));
            int kBits = k == 0 ? 0 : (int)Math.Ceiling(Math.Log2(Math.Abs((double)k)) + 3);
            int tiny = (int)Math.Max(0, -Ilog2(x));
            int wp = p + 32 + 2 * s + kBits + tiny;
            var r = BigFloat.Sub(x.WithPrecision(wp), BigFloat.Mul(BigFloat.FromDouble(k, wp), Pi(wp).ScaleB(1), wp), wp).ScaleB(-s);
            var r2 = BigFloat.Mul(r, r, wp);
            BigFloat sn = r, term = r;
            for (int n = 1; ; n++)
            {
                term = -BigFloat.Div(BigFloat.Mul(term, r2, wp), BigFloat.FromDouble((2.0 * n) * (2 * n + 1), wp), wp);
                sn = BigFloat.Add(sn, term, wp);
                if (Below(term, sn, wp + 4)) break;
            }
            BigFloat cs = One(wp); term = One(wp);
            for (int n = 1; ; n++)
            {
                term = -BigFloat.Div(BigFloat.Mul(term, r2, wp), BigFloat.FromDouble((2.0 * n - 1) * (2 * n), wp), wp);
                cs = BigFloat.Add(cs, term, wp);
                if (Below(term, cs, wp + 4)) break;
            }
            for (int i = 0; i < s; i++)
            {
                var ns = BigFloat.Mul(sn, cs, wp).ScaleB(1);
                cs = BigFloat.Sub(One(wp), BigFloat.Mul(sn, sn, wp).ScaleB(1), wp);
                sn = ns;
            }
            return (sn.WithPrecision(p), cs.WithPrecision(p));
        }

        /// <summary>arctan x.</summary>
        public static BigFloat Atan(BigFloat x, int p)
        {
            if (x.IsZero) return BigFloat.Zero(p);
            int wp = p + 48;
            var ax = x.Abs().WithPrecision(wp);
            bool inv = ax > One(wp);
            if (inv) ax = BigFloat.Div(One(wp), ax, wp);
            // Halve the angle until |x| < 1/16: arctan x = 2·arctan(x / (1 + √(1 + x²))).
            int halvings = 0;
            var lim = BigFloat.FromDouble(1.0 / 16, wp);
            while (ax > lim)
            {
                var root = Sqrt(BigFloat.Add(One(wp), BigFloat.Mul(ax, ax, wp), wp), wp);
                ax = BigFloat.Div(ax, BigFloat.Add(One(wp), root, wp), wp);
                halvings++;
            }
            var a = AtanSeries(ax, wp).ScaleB(halvings);
            if (inv) a = BigFloat.Sub(Pi(wp).ScaleB(-1), a, wp);
            return (x.Sign < 0 ? -a : a).WithPrecision(p);
        }

        /// <summary>The angle of (x, y) in (−π, π] (atan2 convention; atan2(0, 0) = 0).</summary>
        public static BigFloat Atan2(BigFloat y, BigFloat x, int p)
        {
            int wp = p + 16;
            if (x.IsZero)
            {
                if (y.IsZero) return BigFloat.Zero(p);
                var h = Pi(wp).ScaleB(-1);
                return (y.Sign > 0 ? h : -h).WithPrecision(p);
            }
            if (y.IsZero) return x.Sign > 0 ? BigFloat.Zero(p) : Pi(p);
            var t = Atan(BigFloat.Div(y, x, wp), wp);
            if (x.Sign > 0) return t.WithPrecision(p);
            return (y.Sign > 0 ? BigFloat.Add(t, Pi(wp), wp) : BigFloat.Sub(t, Pi(wp), wp)).WithPrecision(p);
        }

        // ── #1915 — exact integer parts (the reference's piecewise functions) ──

        private static BigFloat FromInteger(BigInteger q, int p)
            => BigInteger.Abs(q) < (BigInteger.One << 53) ? BigFloat.FromDouble((double)q, p) : BigFloat.Parse(q.ToString(), p);

        /// <summary>⌊x⌋, exactly (BigInteger's shift rounds toward −∞).</summary>
        public static BigFloat Floor(BigFloat x, int p)
        {
            if (x.IsZero || x.Exponent >= 0) return x.WithPrecision(p);
            long sh = -x.Exponent;
            if (sh > x.Mantissa.GetBitLength() + 1) return x.Sign < 0 ? BigFloat.FromDouble(-1, p) : BigFloat.Zero(p);
            return FromInteger(x.Mantissa >> (int)sh, p);
        }

        public static BigFloat Ceil(BigFloat x, int p) => -Floor(-x, p);

        public static BigFloat Trunc(BigFloat x, int p) => x.Sign < 0 ? -Floor(-x, p) : Floor(x, p);

        /// <summary>Round half to even, as Math.Round.</summary>
        public static BigFloat Round(BigFloat x, int p)
        {
            int wp = Math.Max(p, x.Precision) + 8;
            var f = Floor(x, wp);
            var r = BigFloat.Sub(x.WithPrecision(wp), f, wp);       // in [0, 1), exact
            int c = r.CompareTo(BigFloat.FromDouble(0.5, wp));
            if (c < 0) return f.WithPrecision(p);
            var up = BigFloat.Add(f, BigFloat.FromDouble(1, wp), wp);
            if (c > 0) return up.WithPrecision(p);
            // A tie: the even one.
            bool fEven = Floor(f.ScaleB(-1), wp).ScaleB(1).CompareTo(f) == 0;
            return (fEven ? f : up).WithPrecision(p);
        }

        // ── complex, as (re, im) pairs ──

        /// <summary>e^z.</summary>
        public static (BigFloat Re, BigFloat Im) CExp(BigFloat zr, BigFloat zi, int p)
        {
            var m = Exp(zr, p + 8);
            var (s, c) = SinCos(zi, p + 8);
            return (BigFloat.Mul(m, c, p), BigFloat.Mul(m, s, p));
        }

        /// <summary>The principal log z = ln|z| + i·arg z (z ≠ 0).</summary>
        public static (BigFloat Re, BigFloat Im) CLog(BigFloat zr, BigFloat zi, int p)
        {
            int wp = p + 8;
            var n2 = BigFloat.Add(BigFloat.Mul(zr, zr, wp), BigFloat.Mul(zi, zi, wp), wp);
            return (Log(n2, wp).ScaleB(-1).WithPrecision(p), Atan2(zi, zr, p));
        }

        // (sinh y, cosh y) from e^{±y}; sinh's cancellation for small y costs log₂(1/|y|) bits.
        private static (BigFloat Sinh, BigFloat Cosh) SinhCosh(BigFloat y, int p)
        {
            if (y.IsZero) return (BigFloat.Zero(p), One(p));
            int wp = p + 16 + (int)Math.Max(0, -Ilog2(y));
            var ep = Exp(y, wp);
            var em = BigFloat.Div(One(wp), ep, wp);
            return (BigFloat.Sub(ep, em, wp).ScaleB(-1).WithPrecision(p), BigFloat.Add(ep, em, wp).ScaleB(-1).WithPrecision(p));
        }

        /// <summary>sin z = sin x cosh y + i cos x sinh y.</summary>
        public static (BigFloat Re, BigFloat Im) CSin(BigFloat zr, BigFloat zi, int p)
        {
            var (s, c) = SinCos(zr, p + 8);
            var (sh, ch) = SinhCosh(zi, p + 8);
            return (BigFloat.Mul(s, ch, p), BigFloat.Mul(c, sh, p));
        }

        /// <summary>cos z = cos x cosh y − i sin x sinh y.</summary>
        public static (BigFloat Re, BigFloat Im) CCos(BigFloat zr, BigFloat zi, int p)
        {
            var (s, c) = SinCos(zr, p + 8);
            var (sh, ch) = SinhCosh(zi, p + 8);
            return (BigFloat.Mul(c, ch, p), -BigFloat.Mul(s, sh, p));
        }

        /// <summary>The principal z^w = e^{w·log z} (0^w = 0).</summary>
        public static (BigFloat Re, BigFloat Im) CPow(BigFloat zr, BigFloat zi, BigFloat wr, BigFloat wi, int p)
        {
            if (zr.IsZero && zi.IsZero) return (BigFloat.Zero(p), BigFloat.Zero(p));
            int wp = p + 16;
            var (lr, li) = CLog(zr, zi, wp);
            var er = BigFloat.Sub(BigFloat.Mul(wr, lr, wp), BigFloat.Mul(wi, li, wp), wp);
            var ei = BigFloat.Add(BigFloat.Mul(wr, li, wp), BigFloat.Mul(wi, lr, wp), wp);
            var (a, b) = CExp(er, ei, wp);
            return (a.WithPrecision(p), b.WithPrecision(p));
        }
    }
}
