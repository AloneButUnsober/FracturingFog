// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Math/BigFloat.cs
//
// #1830 (epic #1428, slice C1) — an arbitrary-precision binary floating-point number:
// value = Mantissa · 2^Exponent, with a System.Numerics.BigInteger mantissa held to a
// chosen number of significant bits (the precision). Every operation rounds its exact
// result to the precision, round to nearest, ties to even (IEEE 754's default rule,
// applied at P bits instead of 53).
//
// What it is for: past OD's ~124 digits (the 1e100 Extreme cap) the view centre, the
// reference orbit and the finders' Newton steps need more precision; per-pixel deltas
// stay double / float-exp (perturbation). Precision follows the zoom
// (PrecisionForZoom). The design and the benchmark against OD are in
// Docs/Technical/Arbitrary-Precision-DeepZoom.md.
//
// Not a wrapper of MPFR or any other library: the arithmetic is the textbook one
// (align, add / multiply / divide the integers exactly, round once), on .NET's
// BigInteger. Decimal parse and format are exact (integer arithmetic with powers of
// ten), so a coordinate string round-trips.
//
// Credit: the rounding rule is IEEE 754's (IEEE Std 754-2019); the align-operate-round
// arithmetic and exact radix conversion are the textbook methods of Knuth, The Art of
// Computer Programming Vol. 2, §4.2. See Docs/Resources-Bibliography.md#ieee-754 and
// #knuth-taocp-2; MPFR (#mpfr) was considered and not used.

using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace FracturingFog.FFMath
{
    /// <summary>
    /// #1830 — arbitrary-precision binary float: <see cref="Mantissa"/> · 2^<see cref="Exponent"/>,
    /// the mantissa rounded to <see cref="Precision"/> significant bits. Immutable. The result
    /// of an operation has the larger of its operands' precisions (or the one passed).
    /// </summary>
    public readonly struct BigFloat : IComparable<BigFloat>, IEquatable<BigFloat>
    {
        /// <summary>The smallest precision accepted (bits).</summary>
        public const int MinPrecision = 64;

        /// <summary>Signed mantissa; zero, or |Mantissa| with at most <see cref="Precision"/>
        /// bits and its lowest bit set (no trailing zeros), so equal values have equal fields.</summary>
        public readonly BigInteger Mantissa;
        /// <summary>Binary exponent of the mantissa's lowest bit.</summary>
        public readonly long Exponent;
        /// <summary>Significant bits kept.</summary>
        public readonly int Precision;

        private BigFloat(BigInteger m, long e, int prec)
        {
            Mantissa = m; Exponent = e; Precision = prec;
        }

        public static BigFloat Zero(int precision) => new(BigInteger.Zero, 0, CheckPrec(precision));

        public bool IsZero => Mantissa.IsZero;
        public int Sign => Mantissa.Sign;

        private static int CheckPrec(int p) => p < MinPrecision ? MinPrecision : p;

        /// <summary>Bits needed for a view at zoom 10^<paramref name="log10Zoom"/>: the pixel is
        /// about 10^-log10Zoom of the view, so the centre needs that many decimal digits, plus
        /// <paramref name="guardBits"/> for the reference orbit's rounding.</summary>
        public static int PrecisionForZoom(double log10Zoom, int guardBits = 64)
        {
            if (!(log10Zoom > 0) || double.IsNaN(log10Zoom)) log10Zoom = 0;
            return CheckPrec((int)Math.Ceiling(log10Zoom * 3.3219280948873622) + guardBits);
        }

        // ── Rounding ───────────────────────────────────────────────────────────

        // The exact value sign · mag · 2^exp (+ a sticky fraction below the lowest bit
        // when sticky), rounded to prec bits, ties to even.
        private static BigFloat Round(int sign, BigInteger mag, long exp, bool sticky, int prec)
        {
            if (mag.IsZero) return new BigFloat(BigInteger.Zero, 0, prec);
            long bits = (long)mag.GetBitLength();
            if (bits > prec)
            {
                int shift = (int)(bits - prec);
                BigInteger q = mag >> shift;
                BigInteger rem = mag - (q << shift);
                BigInteger half = BigInteger.One << (shift - 1);
                int c = rem.CompareTo(half);
                bool up = c > 0 || (c == 0 && (sticky || !q.IsEven));
                if (up) q += 1;
                mag = q; exp += shift;
                // q + 1 can carry to prec + 1 bits (a power of two): normalised below.
            }
            int tz = TrailingZeros(mag);
            if (tz > 0) { mag >>= tz; exp += tz; }
            return new BigFloat(sign < 0 ? -mag : mag, exp, prec);
        }

        private static int TrailingZeros(BigInteger m)
        {
            if (m.IsZero) return 0;
            return (int)BigInteger.TrailingZeroCount(m);
        }

        /// <summary>This value rounded to <paramref name="precision"/> bits.</summary>
        public BigFloat WithPrecision(int precision)
        {
            precision = CheckPrec(precision);
            return Round(Mantissa.Sign, BigInteger.Abs(Mantissa), Exponent, false, precision);
        }

        // ── Conversions ────────────────────────────────────────────────────────

        /// <summary>The double <paramref name="d"/> exactly (finite only), at <paramref name="precision"/> bits.</summary>
        public static BigFloat FromDouble(double d, int precision)
        {
            precision = CheckPrec(precision);
            if (!double.IsFinite(d)) throw new ArgumentOutOfRangeException(nameof(d), "finite values only");
            if (d == 0) return Zero(precision);
            long bits = BitConverter.DoubleToInt64Bits(d);
            int sign = bits < 0 ? -1 : 1;
            int be = (int)((bits >> 52) & 0x7FF);
            long frac = bits & 0xFFFFFFFFFFFFFL;
            long e;
            if (be == 0) e = -1074;           // subnormal
            else { frac |= 1L << 52; e = be - 1075; }
            return Round(sign, new BigInteger(frac), e, false, precision);
        }

        /// <summary>The OD value exactly (the sum of its eight limbs), at <paramref name="precision"/> bits.</summary>
        public static BigFloat FromOD(OD v, int precision)
        {
            precision = CheckPrec(precision);
            // Exact sum of the eight limbs (integers aligned at the lowest limb's exponent),
            // then one rounding.
            BigInteger sum = BigInteger.Zero; long e = long.MaxValue;
            var parts = new System.Collections.Generic.List<BigFloat>(8);
            foreach (double x in new[] { v.X0, v.X1, v.X2, v.X3, v.X4, v.X5, v.X6, v.X7 })
                if (x != 0) { var f = FromDouble(x, 64); parts.Add(f); e = Math.Min(e, f.Exponent); }
            if (parts.Count == 0) return Zero(precision);
            foreach (var f in parts) sum += f.Mantissa << (int)(f.Exponent - e);
            return Round(sum.Sign, BigInteger.Abs(sum), e, false, precision);
        }

        /// <summary>The nearest double (ties to even); ±∞ past double range, 0 below it.</summary>
        /// <summary>#1854 — the value as a <see cref="FloatExp"/> (53-bit mantissa, rounded
        /// toward zero from the top 62 bits; any exponent).</summary>
        public FloatExp ToFloatExp()
        {
            if (Mantissa.IsZero) return FloatExp.Zero;
            long bl = (long)Mantissa.GetBitLength();
            int drop = bl > 62 ? (int)(bl - 62) : 0;
            var top = drop > 0 ? Mantissa >> drop : Mantissa;
            return FloatExp.FromDouble((double)top).ScaleB(Exponent + drop);
        }

        /// <summary>#1866 — a FloatExp at <paramref name="precision"/> bits (exact: its
        /// 53-bit mantissa fits).</summary>
        public static BigFloat FromFloatExp(FloatExp v, int precision)
            => v.IsZero ? Zero(precision) : FromDouble(v.M, precision).ScaleB(v.E);

        public double ToDouble()
        {
            if (IsZero) return 0;
            var r = Round(Mantissa.Sign, BigInteger.Abs(Mantissa), Exponent, false, 53);
            double m = (double)r.Mantissa;   // ≤ 53 bits: exact
            long e = r.Exponent;
            if (e > 2000) return m > 0 ? double.PositiveInfinity : double.NegativeInfinity;
            if (e < -2200) return 0.0 * Math.Sign(m);
            return Math.ScaleB(m, (int)e);
        }

        public static explicit operator double(BigFloat v) => v.ToDouble();

        /// <summary>The value as OD: eight doubles from a greedy split (each the nearest double
        /// to what is left). Exact when the value has at most OD's bits.</summary>
        public OD ToOD()
        {
            var d = new double[8];
            BigFloat r = this;
            int wide = Math.Max(Precision, 8 * 53 + 64);
            r = r.WithPrecision(wide);
            for (int i = 0; i < 8; i++)
            {
                d[i] = r.ToDouble();
                if (d[i] == 0) break;
                r = Sub(r, FromDouble(d[i], wide), wide);
                if (r.IsZero) break;
            }
            return new OD(d[0], d[1], d[2], d[3], d[4], d[5], d[6], d[7]);
        }

        // ── Arithmetic ─────────────────────────────────────────────────────────

        private static int P(BigFloat a, BigFloat b) => Math.Max(a.Precision, b.Precision);

        // a + b exactly, then rounded to prec. Operands far apart: the smaller only decides
        // the rounding direction, so it is folded into a sticky bit instead of aligning
        // billions of bits.
        private static BigFloat AddExact(BigFloat a, BigFloat b, int prec)
        {
            if (b.IsZero) return a.WithPrecision(prec);
            if (a.IsZero) return b.WithPrecision(prec);
            long topA = a.Exponent + (long)BigInteger.Abs(a.Mantissa).GetBitLength();
            long topB = b.Exponent + (long)BigInteger.Abs(b.Mantissa).GetBitLength();
            if (topA < topB) (a, b, topA, topB) = (b, a, topB, topA);
            // b entirely below a's lowest kept bit (a widened to prec + 3 bits first): b only
            // decides the rounding direction. a's last bit becomes two, and b is folded in as
            // ±1 unit there. The true value lies strictly between, and the discarded field then
            // ends in a 1, so it is never mistaken for an exact tie (see the design doc §3).
            {
                var ma = BigInteger.Abs(a.Mantissa);
                long ea = a.Exponent;
                long want = prec + 3, have = (long)ma.GetBitLength();
                if (have < want) { ma <<= (int)(want - have); ea -= want - have; }
                if (topB < ea - 1)
                {
                    ma <<= 1; ea -= 1;
                    ma = a.Mantissa.Sign == b.Mantissa.Sign ? ma + 1 : ma - 1;
                    return Round(a.Mantissa.Sign, ma, ea, false, prec);
                }
            }
            long e = Math.Min(a.Exponent, b.Exponent);
            BigInteger x = a.Mantissa << (int)(a.Exponent - e);
            BigInteger y = b.Mantissa << (int)(b.Exponent - e);
            BigInteger s = x + y;
            return Round(s.Sign, BigInteger.Abs(s), e, false, prec);
        }

        public static BigFloat Add(BigFloat a, BigFloat b, int precision) => AddExact(a, b, CheckPrec(precision));
        public static BigFloat Sub(BigFloat a, BigFloat b, int precision) => AddExact(a, -b, CheckPrec(precision));

        public static BigFloat Mul(BigFloat a, BigFloat b, int precision)
        {
            precision = CheckPrec(precision);
            if (a.IsZero || b.IsZero) return Zero(precision);
            BigInteger m = a.Mantissa * b.Mantissa;
            return Round(m.Sign, BigInteger.Abs(m), a.Exponent + b.Exponent, false, precision);
        }

        /// <summary>a / b, correctly rounded. b must not be zero.</summary>
        public static BigFloat Div(BigFloat a, BigFloat b, int precision)
        {
            precision = CheckPrec(precision);
            if (b.IsZero) throw new DivideByZeroException();
            if (a.IsZero) return Zero(precision);
            var ma = BigInteger.Abs(a.Mantissa);
            var mb = BigInteger.Abs(b.Mantissa);
            // Shift so the quotient has at least prec + 2 bits; the remainder is the sticky tail.
            long shift = precision + 2 + (long)mb.GetBitLength() - (long)ma.GetBitLength();
            if (shift < 0) shift = 0;
            BigInteger q = BigInteger.DivRem(ma << (int)shift, mb, out BigInteger r);
            int sign = a.Mantissa.Sign * b.Mantissa.Sign;
            long e = a.Exponent - b.Exponent - shift;
            if (!r.IsZero) { q = (q << 1) | 1; e -= 1; }   // sticky half-unit below the kept bits
            return Round(sign, q, e, false, precision);
        }

        public BigFloat Square(int precision) => Mul(this, this, precision);
        public BigFloat Square() => Mul(this, this, Precision);

        /// <summary>This value times 2^<paramref name="n"/> (exact).</summary>
        public BigFloat ScaleB(long n) => IsZero ? this : new BigFloat(Mantissa, Exponent + n, Precision);

        public static BigFloat operator +(BigFloat a, BigFloat b) => AddExact(a, b, P(a, b));
        public static BigFloat operator -(BigFloat a, BigFloat b) => AddExact(a, -b, P(a, b));
        public static BigFloat operator -(BigFloat a) => new(-a.Mantissa, a.Exponent, a.Precision);
        public static BigFloat operator *(BigFloat a, BigFloat b) => Mul(a, b, P(a, b));
        public static BigFloat operator /(BigFloat a, BigFloat b) => Div(a, b, P(a, b));
        public static BigFloat operator *(BigFloat a, double b) => Mul(a, FromDouble(b, a.Precision), a.Precision);
        public static BigFloat operator +(BigFloat a, double b) => AddExact(a, FromDouble(b, a.Precision), a.Precision);

        public BigFloat Abs() => Mantissa.Sign < 0 ? -this : this;

        // ── Comparison ─────────────────────────────────────────────────────────

        public int CompareTo(BigFloat o)
        {
            int sa = Mantissa.Sign, sb = o.Mantissa.Sign;
            if (sa != sb) return sa.CompareTo(sb);
            if (sa == 0) return 0;
            long ta = Exponent + (long)BigInteger.Abs(Mantissa).GetBitLength();
            long tb = o.Exponent + (long)BigInteger.Abs(o.Mantissa).GetBitLength();
            if (ta != tb) return sa > 0 ? ta.CompareTo(tb) : tb.CompareTo(ta);
            long e = Math.Min(Exponent, o.Exponent);
            BigInteger x = Mantissa << (int)(Exponent - e);
            BigInteger y = o.Mantissa << (int)(o.Exponent - e);
            return x.CompareTo(y);
        }

        public bool Equals(BigFloat o) => Mantissa == o.Mantissa && (Mantissa.IsZero || Exponent == o.Exponent);
        public override bool Equals(object? obj) => obj is BigFloat b && Equals(b);
        public override int GetHashCode() => Mantissa.IsZero ? 0 : HashCode.Combine(Mantissa, Exponent);
        public static bool operator ==(BigFloat a, BigFloat b) => a.Equals(b);
        public static bool operator !=(BigFloat a, BigFloat b) => !a.Equals(b);
        public static bool operator <(BigFloat a, BigFloat b) => a.CompareTo(b) < 0;
        public static bool operator >(BigFloat a, BigFloat b) => a.CompareTo(b) > 0;
        public static bool operator <=(BigFloat a, BigFloat b) => a.CompareTo(b) <= 0;
        public static bool operator >=(BigFloat a, BigFloat b) => a.CompareTo(b) >= 0;

        // ── Decimal text ───────────────────────────────────────────────────────

        /// <summary>Decimal digits that round-trip <paramref name="precision"/> bits:
        /// ⌈P·log₁₀2⌉ + 1.</summary>
        public static int RoundTripDigits(int precision) => (int)Math.Ceiling(precision * 0.30102999566398120) + 1;

        /// <summary>Parses "[-]digits[.digits][e[±]N]" (invariant, no grouping) to the nearest
        /// value at <paramref name="precision"/> bits.</summary>
        public static bool TryParse(string? s, int precision, out BigFloat value)
        {
            precision = CheckPrec(precision);
            value = Zero(precision);
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            int i = 0, sign = 1;
            if (s[i] == '+' || s[i] == '-') { if (s[i] == '-') sign = -1; i++; }
            var digits = new StringBuilder();
            long dec = 0; bool dot = false, any = false;
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch >= '0' && ch <= '9') { digits.Append(ch); any = true; if (dot) dec++; }
                else if (ch == '.' && !dot) dot = true;
                else break;
            }
            if (!any) return false;
            long exp10 = 0;
            if (i < s.Length)
            {
                if (s[i] != 'e' && s[i] != 'E') return false;
                if (!long.TryParse(s.AsSpan(i + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp10)) return false;
                if (Math.Abs(exp10) > 100_000_000) return false;
            }
            BigInteger n = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
            long k = exp10 - dec;   // value = n · 10^k
            if (n.IsZero) { value = Zero(precision); return true; }
            if (k >= 0)
            {
                BigInteger m = n * BigInteger.Pow(10, (int)k);
                value = Round(sign, m, 0, false, precision);
            }
            else
            {
                // n / 10^-k = n / (5^-k · 2^-k): divide by the odd part exactly-rounded, then scale.
                BigInteger five = BigInteger.Pow(5, (int)-k);
                var num = new BigFloat(n, 0, precision);
                var den = new BigFloat(five, 0, precision);
                var q = Div(num, den, precision).ScaleB(k);
                value = sign < 0 ? -q : q;
            }
            return true;
        }

        public static BigFloat Parse(string s, int precision)
            => TryParse(s, precision, out var v) ? v : throw new FormatException($"not a decimal number: '{s}'");

        /// <summary>Scientific decimal with <paramref name="significantDigits"/> digits, correctly
        /// rounded (ties to even): "-d.ddd…e±N". Default: <see cref="RoundTripDigits"/>.</summary>
        public string ToString(int significantDigits)
        {
            if (significantDigits < 1) significantDigits = 1;
            if (IsZero) return "0";
            BigInteger m = BigInteger.Abs(Mantissa);
            long e2 = Exponent;
            // Decimal exponent estimate: floor(log10 |x|).
            double log10 = (BigInteger.Log10(m)) + e2 * 0.30102999566398120;
            long n10 = (long)Math.Floor(log10);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long s = significantDigits - 1 - n10;   // scaled = round(|x| · 10^s)
                BigInteger num = m, den = BigInteger.One;
                if (e2 >= 0) num <<= (int)e2; else den <<= (int)-e2;
                if (s >= 0) num *= BigInteger.Pow(10, (int)s); else den *= BigInteger.Pow(10, (int)-s);
                BigInteger q = BigInteger.DivRem(num, den, out BigInteger r);
                int c = (r * 2).CompareTo(den);
                if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
                string ds = q.ToString(CultureInfo.InvariantCulture);
                if (ds.Length == significantDigits)
                {
                    var sb = new StringBuilder();
                    if (Mantissa.Sign < 0) sb.Append('-');
                    sb.Append(ds[0]);
                    if (ds.Length > 1) { sb.Append('.'); sb.Append(ds, 1, ds.Length - 1); }
                    sb.Append('e').Append(n10.ToString(CultureInfo.InvariantCulture));
                    return sb.ToString();
                }
                n10 += ds.Length > significantDigits ? 1 : -1;   // the estimate was one off
            }
            throw new InvalidOperationException("decimal exponent did not settle");
        }

        public override string ToString() => ToString(RoundTripDigits(Precision));
    }
}
