// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/ViewState/KfrLocation.cs
//
// #1615 — read a Kalles Fraktaler location file (.kfr) into FF's view.
// Kalles Fraktaler by Karl Runmo, Kalles Fraktaler 2+ by Claude Heiland-Allen
// (https://mathr.co.uk/kf/kf.html, AGPL-3.0); credited in
// Docs/Resources-Bibliography.md (#kalles-fraktaler, #kf-kfr-format). Only
// the format is used, no KF code. What was checked in KF 2+'s own source
// (utils/kf-tile.cpp, fraktal_sft/fraktal_sft.cpp):
//   • A .kfr is "Key: value" text lines (kf-tile's read(): split at the first
//     ':', value after ": "). Re / Im are long decimals, Zoom is scientific
//     ("1.00e050", "1.256030E13"), Iterations an integer.
//   • KF's pixel spacing is 2·ZoomRadius / height with zoom = 2 / ZoomRadius,
//     so the image HEIGHT spans 4 / zoom. FF's Mandelbrot spans 3.5 / zoom
//     across max(width, height), so FF zoom = KF zoom · 3.5·H / (4·max(W, H))
//     (0.4921875 at 3840×2160).
//   • KF's pixel → c map adds +v·scale for image row v below the centre, the
//     same direction as FF; "ImagPointsUp: 1" (KF 2.15.4+, the "imaginary axis
//     points up" option) negates it, so only then is the FF centre the
//     conjugate. A maths-convention renderer (imaginary axis up) draws the
//     same file mirrored top-to-bottom.
//   • RotateAngle / StretchAngle / StretchAmount and the old Rotate / Ratio
//     skew transform the view; FF has no view rotation, so those are refused.
// Every other key (colouring, formula seeds, …) is ignored.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace FracturingFog.ViewState
{
    public sealed record KfrLocation(string Re, string Im, double KfZoom, int? Iterations, bool ImagPointsUp)
    {
        /// <summary>Limbs per coordinate: octuple-double, as the batch / view state carry.</summary>
        public const int Limbs = 8;

        /// <summary>FF's zoom for a W×H frame (Mandelbrot: 3.5 / zoom over max(W, H)).</summary>
        public double FfZoom(int width, int height)
            => KfZoom * 3.5 * height / (4.0 * Math.Max(width, height));

        /// <summary>FF's centre as octuple-double limbs, each the nearest double to
        /// what the decimal still has left, so their sum is the .kfr decimal to
        /// ~124 digits.</summary>
        public (double[] X, double[] Y) Centre()
        {
            var x = DecimalToLimbs(Re, Limbs);
            var y = DecimalToLimbs(Im, Limbs);
            if (ImagPointsUp) for (int i = 0; i < y.Length; i++) y[i] = -y[i];
            return (x, y);
        }

        public static bool TryParse(string text, out KfrLocation? location, out string? error)
        {
            location = null;
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int ix = line.IndexOf(':');
                if (ix <= 0) continue;
                kv[line[..ix].Trim()] = line[(ix + 1)..].Trim();
            }

            if (!kv.TryGetValue("Re", out string? re) || !kv.TryGetValue("Im", out string? im) || !kv.TryGetValue("Zoom", out string? zs))
                { error = "not a Kalles Fraktaler location: it needs Re, Im and Zoom lines."; return false; }
            if (!IsDecimal(re)) { error = $"Re '{re}' is not a decimal number."; return false; }
            if (!IsDecimal(im)) { error = $"Im '{im}' is not a decimal number."; return false; }
            if (!double.TryParse(zs, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom)
                || !double.IsFinite(zoom) || zoom <= 0)
                { error = $"Zoom '{zs}' is not a positive number."; return false; }

            int? iter = null;
            if (kv.TryGetValue("Iterations", out string? its))
            {
                if (!int.TryParse(its, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                    { error = $"Iterations '{its}' is not a positive integer."; return false; }
                iter = n;
            }

            // Only the plain Mandelbrot set z² + c imports (KF formula 0, power 2).
            if (!IsNumber(kv, "FractalType", 0) || !IsNumber(kv, "Power", 2))
                { error = $"only the Mandelbrot set imports (FractalType 0, Power 2); this file has FractalType {Get(kv, "FractalType")}, Power {Get(kv, "Power")}."; return false; }
            foreach (var (key, plain) in new[] { ("RotateAngle", 0.0), ("StretchAngle", 0.0), ("StretchAmount", 0.0), ("Rotate", 0.0), ("Ratio", 360.0) })
                if (!IsNumber(kv, key, plain))
                    { error = $"{key} is {kv[key]}: the view is rotated or stretched, and FF has no view rotation."; return false; }

            bool up = kv.TryGetValue("ImagPointsUp", out string? ups)
                      && int.TryParse(ups, NumberStyles.Integer, CultureInfo.InvariantCulture, out int u) && u != 0;
            location = new KfrLocation(re, im, zoom, iter, up);
            error = null;
            return true;
        }

        private static string Get(Dictionary<string, string> kv, string key)
            => kv.TryGetValue(key, out string? v) ? v : "(absent)";

        // Absent counts as the plain value (KF's defaults).
        private static bool IsNumber(Dictionary<string, string> kv, string key, double plain)
            => !kv.TryGetValue(key, out string? v)
               || (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == plain);

        private static bool IsDecimal(string s) => TryRational(s, out _, out _);

        /// <summary>Split a decimal string exactly into <paramref name="count"/> doubles:
        /// limb k is the nearest double (ties to even) to the decimal minus limbs 0..k-1,
        /// computed in exact rational arithmetic, never through a float.</summary>
        public static double[] DecimalToLimbs(string s, int count)
        {
            if (!TryRational(s, out BigInteger num, out BigInteger den))
                throw new FormatException($"'{s}' is not a decimal number.");
            var limbs = new double[count];
            for (int k = 0; k < count && !num.IsZero; k++)
            {
                double d = Nearest(num, den, out BigInteger m, out int e);
                if (d == 0) break;   // residual below the double range
                limbs[k] = d;
                // num/den -= m·2^e
                if (e >= 0) num -= (m << e) * den;
                else { num = (num << -e) - m * den; den <<= -e; }
            }
            return limbs;
        }

        // Parse [sign]digits[.digits][e[sign]digits] as num/den (den > 0).
        private static bool TryRational(string s, out BigInteger num, out BigInteger den)
        {
            num = BigInteger.Zero; den = BigInteger.One;
            s = s.Trim();
            int i = 0;
            bool neg = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) { neg = s[i] == '-'; i++; }
            int digits = 0, frac = 0;
            bool dot = false;
            var mant = new System.Text.StringBuilder();
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '0' && c <= '9') { mant.Append(c); digits++; if (dot) frac++; }
                else if (c == '.' && !dot) dot = true;
                else break;
            }
            if (digits == 0) return false;
            int exp = 0;
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                if (!int.TryParse(s[(i + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exp)) return false;
                i = s.Length;
            }
            if (i != s.Length) return false;
            num = BigInteger.Parse(mant.ToString(), CultureInfo.InvariantCulture);
            if (neg) num = -num;
            int pow = exp - frac;
            if (pow >= 0) num *= BigInteger.Pow(10, pow);
            else den = BigInteger.Pow(10, -pow);
            return true;
        }

        // The double nearest num/den (ties to even), as m·2^e with |m| < 2^53.
        private static double Nearest(BigInteger num, BigInteger den, out BigInteger m, out int e)
        {
            int sign = num.Sign;
            BigInteger a = BigInteger.Abs(num);
            // Pick e so that q = a / (den·2^e) lands in [2^52, 2^53).
            e = (int)(a.GetBitLength() - den.GetBitLength()) - 53;
            BigInteger q, r, d;
            while (true)
            {
                BigInteger n = e >= 0 ? a : a << -e;
                d = e >= 0 ? den << e : den;
                q = BigInteger.DivRem(n, d, out r);
                if (q >= (BigInteger.One << 53)) { e++; continue; }
                if (q < (BigInteger.One << 52)) { e--; continue; }
                break;
            }
            int cmp = (r << 1).CompareTo(d);
            if (cmp > 0 || (cmp == 0 && !q.IsEven)) q += 1;
            if (q == (BigInteger.One << 53)) { q >>= 1; e++; }
            if (e < -1074) { m = BigInteger.Zero; return 0; }   // below the normal range: stop
            m = sign < 0 ? -q : q;
            return Math.ScaleB((double)q, e) * sign;
        }
    }
}
