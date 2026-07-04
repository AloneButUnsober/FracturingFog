// Abstractions/Math/QdCoordCodec.cs
//
// Quad-double coordinate codec — single-string decimal serialization for a
// 4-limb (Hi, Lo, X2, X3) double-precision number. Used by UI widgets that
// must accept both legacy pipe-delimited "Hi|Lo|X2|X3" paste and the new
// single-string form.
//
// Pure BCL (System.Numerics.BigInteger) — no WinForms / Avalonia / OS deps —
// so both the WinForms shell (FracturingFog.Views.FormHelpers) and the
// cross-platform Avalonia host (FracturingFog.Hosting.AvaloniaDialogs) can
// reference the same implementation through Abstractions.
//
// Carved out of Views/Controls.cs FormHelpers (Wave 1.C1, 2026-06-22).

using System;
using System.Numerics;
using System.Text;

namespace FracturingFog.Abstractions.Math
{
    public static class QdCoordCodec
    {
        private static (BigInteger m, int e) DecomposeDouble(double d)
        {
            if (d == 0.0) return (BigInteger.Zero, 0);
            long bits = BitConverter.DoubleToInt64Bits(d);
            int sign = (int)((bits >> 63) & 1);
            int rawExp = (int)((bits >> 52) & 0x7FF);
            long rawMant = bits & 0xFFFFFFFFFFFFFL;
            int exp;
            long mant;
            if (rawExp == 0)
            {
                mant = rawMant;
                exp = -1074;
            }
            else
            {
                mant = rawMant | (1L << 52);
                exp = rawExp - 1023 - 52;
            }
            BigInteger m = mant;
            if (sign == 1) m = -m;
            return (m, exp);
        }

        private static (BigInteger num, int e2) ExactSum(params double[] limbs)
        {
            int eMin = int.MaxValue;
            var parts = new (BigInteger m, int e)[limbs.Length];
            for (int i = 0; i < limbs.Length; i++)
            {
                if (limbs[i] == 0.0) continue;
                parts[i] = DecomposeDouble(limbs[i]);
                if (parts[i].e < eMin) eMin = parts[i].e;
            }
            if (eMin == int.MaxValue) return (BigInteger.Zero, 0);
            BigInteger sum = BigInteger.Zero;
            for (int i = 0; i < limbs.Length; i++)
            {
                if (limbs[i] == 0.0) continue;
                int shift = parts[i].e - eMin;
                sum += parts[i].m << shift;
            }
            return (sum, eMin);
        }

        public static string FormatCoordSingle(double hi, double lo, double x2, double x3)
        {
            var (num, e2) = ExactSum(hi, lo, x2, x3);
            if (num.IsZero) return "0";

            bool neg = num.Sign < 0;
            if (neg) num = -num;

            BigInteger numerator, denominator;
            if (e2 >= 0)
            {
                numerator = num << e2;
                denominator = BigInteger.One;
            }
            else
            {
                numerator = num;
                denominator = BigInteger.One << (-e2);
            }

            BigInteger intPart = BigInteger.DivRem(numerator, denominator, out BigInteger frac);

            var sb = new StringBuilder();
            if (neg) sb.Append('-');
            sb.Append(intPart.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!frac.IsZero)
            {
                sb.Append('.');
                const int MaxFracDigits = 80;
                int produced = 0;
                while (!frac.IsZero && produced < MaxFracDigits)
                {
                    frac *= 10;
                    BigInteger d = BigInteger.DivRem(frac, denominator, out frac);
                    sb.Append((char)('0' + (int)d));
                    produced++;
                }
                while (sb.Length > 0 && sb[sb.Length - 1] == '0') sb.Length--;
                if (sb.Length > 0 && sb[sb.Length - 1] == '.') sb.Length--;
            }

            return sb.ToString();
        }

        private static double RationalToDouble(BigInteger num, BigInteger den)
        {
            if (num.IsZero) return 0.0;
            int nb = (int)num.GetBitLength();
            int db = (int)den.GetBitLength();
            int shift = 64 + db - nb;
            BigInteger shifted = shift >= 0 ? num << shift : num >> -shift;
            BigInteger q = BigInteger.DivRem(shifted, den, out _);
            double dq = (double)q;
            return System.Math.ScaleB(dq, -shift);
        }

        public static bool TryParseCoordSingle(string text,
            out double hi, out double lo, out double x2, out double x3)
        {
            hi = lo = x2 = x3 = 0.0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();

            bool neg = false;
            int idx = 0;
            if (s[idx] == '+') { idx++; }
            else if (s[idx] == '-') { neg = true; idx++; }
            if (idx >= s.Length) return false;

            int eIdx = s.IndexOfAny(new[] { 'e', 'E' }, idx);
            string mantStr = eIdx < 0 ? s.Substring(idx) : s.Substring(idx, eIdx - idx);
            int exp10 = 0;
            if (eIdx >= 0)
            {
                if (!int.TryParse(s.AsSpan(eIdx + 1),
                                  System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  out exp10))
                    return false;
            }

            int dot = mantStr.IndexOf('.');
            string digits;
            int fracLen;
            if (dot < 0) { digits = mantStr; fracLen = 0; }
            else
            {
                digits = mantStr.Substring(0, dot) + mantStr.Substring(dot + 1);
                fracLen = mantStr.Length - dot - 1;
            }
            if (digits.Length == 0) return false;

            if (!BigInteger.TryParse(digits,
                                     System.Globalization.NumberStyles.Integer,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out BigInteger mant))
                return false;

            int netE10 = exp10 - fracLen;
            BigInteger numerator = mant;
            BigInteger denominator = BigInteger.One;
            if (netE10 >= 0) numerator *= BigInteger.Pow(10, netE10);
            else denominator = BigInteger.Pow(10, -netE10);

            if (neg) numerator = -numerator;

            double[] limbs = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (numerator.IsZero) break;
                bool nNeg = numerator.Sign < 0;
                BigInteger absN = nNeg ? -numerator : numerator;
                double d = RationalToDouble(absN, denominator);
                if (nNeg) d = -d;
                if (d == 0.0 || double.IsInfinity(d) || double.IsNaN(d)) break;
                limbs[i] = d;

                var (dm, de) = DecomposeDouble(d);
                BigInteger dNum, dDen;
                if (de >= 0) { dNum = dm << de; dDen = BigInteger.One; }
                else { dNum = dm; dDen = BigInteger.One << (-de); }

                numerator = numerator * dDen - dNum * denominator;
                denominator = denominator * dDen;
            }

            hi = limbs[0]; lo = limbs[1]; x2 = limbs[2]; x3 = limbs[3];
            return true;
        }

        public static bool TryParseCoordAny(string text,
            out double hi, out double lo, out double x2, out double x3)
        {
            hi = lo = x2 = x3 = 0.0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s.IndexOf('|') >= 0)
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Float;
                var parts = s.Split('|');
                if (!double.TryParse(parts[0].Trim(), ns, ic, out hi)) return false;
                if (parts.Length > 1 && !double.TryParse(parts[1].Trim(), ns, ic, out lo)) return false;
                if (parts.Length > 2 && !double.TryParse(parts[2].Trim(), ns, ic, out x2)) return false;
                if (parts.Length > 3 && !double.TryParse(parts[3].Trim(), ns, ic, out x3)) return false;
                return true;
            }
            return TryParseCoordSingle(s, out hi, out lo, out x2, out x3);
        }
    }
}
