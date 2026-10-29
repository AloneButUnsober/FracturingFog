// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/ViewState/GridAxisLabels.cs
//
// #1559 — Cartesian grid ticks and their labels, shared by the grid overlay
// (FractalOverlayCompositor) and the Math Plane render mode (#1547 S1).
//
// Every tick sits at k * step with step = s * 10^e (s = 1, 2 or 5), so its value
// is exactly the decimal M * 10^e with M = k * s. The ticks are found from the
// view centre's limbs summed EXACTLY (BigInteger dyadic rational, no double sums),
// so the grid keeps distinct lines and exact labels at any zoom. Before #1559 the
// overlay used only the double CenterX and summed absolute tick values, so past
// about 1e15 zoom every tick collapsed onto one double.
//
// Label modes (GridLabelMode):
//   Auto       — decimals from the step (the fewest that keep neighbours apart),
//                trailing zeros trimmed. Falls back to scientific when values are
//                long because they are large, then to Offset when they are long
//                because the centre carries many digits (deep zoom).
//   Fixed      — the user's number of decimals (labels may coincide if too few).
//   Scientific — the user's number of significant figures.
//   Offset     — one anchor (the tick nearest the centre) printed in full, and
//                each tick as its exact offset from it ("+2e-20").

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace FracturingFog.ViewState
{
    /// <summary>#1559 — how grid axis labels are written. See file header.</summary>
    public enum GridLabelMode
    {
        Auto = 0,
        Fixed = 1,
        Scientific = 2,
        Offset = 3,
    }

    /// <summary>#1559 — grid label preference. <see cref="Digits"/> is the decimal
    /// count for <see cref="GridLabelMode.Fixed"/> and the significant-figure count for
    /// <see cref="GridLabelMode.Scientific"/>; Auto and Offset ignore it.</summary>
    public sealed record GridLabelSettings(GridLabelMode Mode = GridLabelMode.Auto, int Digits = 6)
    {
        public const int MinDigits = 0;
        public const int MaxDigits = 30;

        /// <summary>Auto switches away from plain decimals when a label would be
        /// longer than this many characters.</summary>
        public const int AutoMaxLength = 12;

        public static readonly GridLabelSettings Default = new();

        /// <summary><see cref="Digits"/> clamped to its range (Scientific needs ≥ 1).</summary>
        public int ClampedDigits => Math.Clamp(Digits, Mode == GridLabelMode.Scientific ? 1 : MinDigits, MaxDigits);
    }

    /// <summary>One grid tick: where it is relative to the view centre, and its exact
    /// value <c>Mantissa * 10^Exponent</c> (exponent shared by the axis).</summary>
    public readonly record struct GridTick(double OffsetFromCentre, BigInteger Mantissa)
    {
        public bool IsZero => Mantissa.IsZero;
    }

    /// <summary>The ticks of one axis inside a view. See file header.</summary>
    public sealed class GridAxis
    {
        /// <summary>Hard cap on ticks per axis; the step aims for about seven.</summary>
        public const int MaxTicks = 64;

        public int StepMantissa { get; }
        public int Exponent { get; }
        public double Step { get; }
        public IReadOnlyList<GridTick> Ticks { get; }

        private GridAxis(int s, int e, double step, IReadOnlyList<GridTick> ticks)
        { StepMantissa = s; Exponent = e; Step = step; Ticks = ticks; }

        /// <summary>The 1-2-5 step nearest above <paramref name="raw"/>, as
        /// <c>s * 10^e</c>. Non-positive or non-finite input gives 1.</summary>
        public static (int S, int E) NiceStep(double raw)
        {
            if (!(raw > 0) || double.IsInfinity(raw)) return (1, 0);
            int e = (int)Math.Floor(Math.Log10(raw));
            double norm = raw / Math.Pow(10, e);
            // Log10 can land one decade off near exact powers of ten.
            if (norm >= 10) { e++; norm /= 10; }
            else if (norm < 1) { e--; norm *= 10; }
            int s = norm <= 1.0 ? 1 : norm <= 2.0 ? 2 : norm <= 5.0 ? 5 : 10;
            if (s == 10) { s = 1; e++; }
            return (s, e);
        }

        /// <summary>Ticks of step <c>s * 10^e</c> within ±<paramref name="halfSpan"/>
        /// of the centre whose exact value is the sum of <paramref name="centreLimbs"/>.</summary>
        public static GridAxis Build(ReadOnlySpan<double> centreLimbs, double halfSpan, int s, int e)
        {
            double step = s * Math.Pow(10, e);
            var ticks = new List<GridTick>();
            if (!(halfSpan > 0) || double.IsInfinity(halfSpan) || s <= 0)
                return new GridAxis(s, e, step, ticks);
            foreach (double l in centreLimbs)
                if (!double.IsFinite(l)) return new GridAxis(s, e, step, ticks);

            // Everything as rationals over a common denominator:
            //   centre = cNum / 2^K,  half = hNum / 2^K,  step = sNum / sDen.
            int k = 0;
            foreach (double l in centreLimbs) k = Math.Max(k, Dyadic.MinShift(l));
            k = Math.Max(k, Dyadic.MinShift(halfSpan));
            BigInteger cNum = BigInteger.Zero;
            foreach (double l in centreLimbs) cNum += Dyadic.Scaled(l, k);
            BigInteger hNum = Dyadic.Scaled(halfSpan, k);
            BigInteger twoK = BigInteger.One << k;

            BigInteger pow10 = BigInteger.Pow(10, Math.Abs(e));
            BigInteger sNum = e >= 0 ? s * pow10 : s;
            BigInteger sDen = e >= 0 ? BigInteger.One : pow10;

            // tick index range: lower / step <= j <= upper / step,
            // x / step = xNum * sDen / (2^K * sNum).
            BigInteger den = twoK * sNum;
            BigInteger jMin = CeilDiv((cNum - hNum) * sDen, den);
            BigInteger jMax = FloorDiv((cNum + hNum) * sDen, den);
            if (jMax - jMin + 1 > MaxTicks) jMax = jMin + MaxTicks - 1;

            // offset_j = j * step - centre = (j * sNum * 2^K - cNum * sDen) / (sDen * 2^K)
            BigInteger offDen = sDen * twoK;
            for (BigInteger j = jMin; j <= jMax; j++)
            {
                BigInteger offNum = j * sNum * twoK - cNum * sDen;
                ticks.Add(new GridTick(Dyadic.ToDouble(offNum, offDen), j * s));
            }
            return new GridAxis(s, e, step, ticks);
        }

        private static BigInteger FloorDiv(BigInteger a, BigInteger b)
        {
            BigInteger q = BigInteger.DivRem(a, b, out BigInteger r);
            return (r.Sign != 0 && (r.Sign < 0) != (b.Sign < 0)) ? q - 1 : q;
        }

        private static BigInteger CeilDiv(BigInteger a, BigInteger b) => -FloorDiv(-a, b);
    }

    /// <summary>Labels for one axis: one per tick, plus the anchor when the axis is
    /// in offset form.</summary>
    public sealed record GridAxisLabelSet(IReadOnlyList<string> Labels, string? Anchor, GridLabelMode Used);

    /// <summary>#1559 — writes a <see cref="GridAxis"/>'s labels. See file header.</summary>
    public static class GridLabelFormatter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static GridAxisLabelSet Format(GridAxis axis, GridLabelSettings settings)
        {
            int e = axis.Exponent;
            var ticks = axis.Ticks;
            switch (settings.Mode)
            {
                case GridLabelMode.Fixed:
                    return Each(ticks, t => FixedDecimals(t.Mantissa, e, settings.ClampedDigits), GridLabelMode.Fixed);
                case GridLabelMode.Scientific:
                    return Each(ticks, t => Scientific(t.Mantissa, e, settings.ClampedDigits), GridLabelMode.Scientific);
                case GridLabelMode.Offset:
                    return OffsetLabels(ticks, e);
            }

            var plain = Each(ticks, t => Plain(t.Mantissa, e), GridLabelMode.Auto);
            if (MaxLength(plain.Labels) <= GridLabelSettings.AutoMaxLength) return plain;
            var sci = Each(ticks, t => ScientificMinimal(t.Mantissa, e), GridLabelMode.Scientific);
            if (MaxLength(sci.Labels) <= GridLabelSettings.AutoMaxLength) return sci;
            return OffsetLabels(ticks, e);
        }

        /// <summary>Exact <c>m * 10^e</c> as a plain decimal, trailing zeros trimmed.</summary>
        public static string Plain(BigInteger m, int e)
        {
            if (m.IsZero) return "0";
            string digits = BigInteger.Abs(m).ToString(Inv);
            string body;
            if (e >= 0) body = digits + new string('0', e);
            else
            {
                int dec = -e;
                if (digits.Length <= dec) digits = new string('0', dec - digits.Length + 1) + digits;
                string intPart = digits[..^dec];
                string frac = digits[^dec..].TrimEnd('0');
                body = frac.Length == 0 ? intPart : intPart + "." + frac;
            }
            return m.Sign < 0 ? "-" + body : body;
        }

        /// <summary><c>m * 10^e</c> rounded half away from zero to exactly
        /// <paramref name="decimals"/> places.</summary>
        public static string FixedDecimals(BigInteger m, int e, int decimals)
        {
            if (m.IsZero) return decimals == 0 ? "0" : "0." + new string('0', decimals);
            // Re-express at exponent -decimals.
            BigInteger q = -e <= decimals
                ? m * BigInteger.Pow(10, decimals + e)
                : RoundDiv(m, BigInteger.Pow(10, -e - decimals));
            if (q.IsZero) return decimals == 0 ? "0" : "0." + new string('0', decimals);
            string digits = BigInteger.Abs(q).ToString(Inv);
            if (decimals > 0 && digits.Length <= decimals)
                digits = new string('0', decimals - digits.Length + 1) + digits;
            string body = decimals == 0 ? digits : digits[..^decimals] + "." + digits[^decimals..];
            return q.Sign < 0 ? "-" + body : body;
        }

        /// <summary><c>m * 10^e</c> in scientific form with exactly
        /// <paramref name="sig"/> significant figures (rounded half away from zero).</summary>
        public static string Scientific(BigInteger m, int e, int sig)
        {
            sig = Math.Max(1, sig);
            if (m.IsZero) return "0";
            string digits = BigInteger.Abs(m).ToString(Inv);
            int exp10 = e + digits.Length - 1;
            if (digits.Length > sig)
            {
                BigInteger q = RoundDiv(BigInteger.Abs(m), BigInteger.Pow(10, digits.Length - sig));
                digits = q.ToString(Inv);
                if (digits.Length > sig) { exp10++; digits = digits[..sig]; } // 9.99 -> 10.0
            }
            else digits = digits.PadRight(sig, '0');
            return Sci(m.Sign < 0, digits, exp10);
        }

        /// <summary>Scientific form with just enough digits to be exact
        /// (trailing zeros trimmed).</summary>
        public static string ScientificMinimal(BigInteger m, int e)
        {
            if (m.IsZero) return "0";
            string digits = BigInteger.Abs(m).ToString(Inv);
            int exp10 = e + digits.Length - 1;
            digits = digits.TrimEnd('0');
            return Sci(m.Sign < 0, digits, exp10);
        }

        private static string Sci(bool negative, string digits, int exp10)
        {
            var sb = new StringBuilder();
            if (negative) sb.Append('-');
            sb.Append(digits[0]);
            if (digits.Length > 1) sb.Append('.').Append(digits, 1, digits.Length - 1);
            sb.Append('e').Append(exp10.ToString(Inv));
            return sb.ToString();
        }

        private static GridAxisLabelSet OffsetLabels(IReadOnlyList<GridTick> ticks, int e)
        {
            if (ticks.Count == 0) return new GridAxisLabelSet(Array.Empty<string>(), null, GridLabelMode.Offset);
            int a = 0;
            for (int i = 1; i < ticks.Count; i++)
                if (Math.Abs(ticks[i].OffsetFromCentre) < Math.Abs(ticks[a].OffsetFromCentre)) a = i;
            BigInteger anchor = ticks[a].Mantissa;
            var labels = new string[ticks.Count];
            for (int i = 0; i < ticks.Count; i++)
            {
                BigInteger d = ticks[i].Mantissa - anchor;
                labels[i] = d.IsZero ? "0" : (d.Sign > 0 ? "+" : "") + ScientificMinimal(d, e);
            }
            return new GridAxisLabelSet(labels, Plain(anchor, e), GridLabelMode.Offset);
        }

        private static GridAxisLabelSet Each(IReadOnlyList<GridTick> ticks, Func<GridTick, string> f, GridLabelMode used)
        {
            var labels = new string[ticks.Count];
            for (int i = 0; i < ticks.Count; i++) labels[i] = f(ticks[i]);
            return new GridAxisLabelSet(labels, null, used);
        }

        private static int MaxLength(IReadOnlyList<string> labels)
        {
            int n = 0;
            foreach (var l in labels) n = Math.Max(n, l.Length);
            return n;
        }

        // a / b rounded half away from zero, b > 0.
        private static BigInteger RoundDiv(BigInteger a, BigInteger b)
        {
            BigInteger q = BigInteger.DivRem(BigInteger.Abs(a), b, out BigInteger r);
            if (r * 2 >= b) q += 1;
            return a.Sign < 0 ? -q : q;
        }
    }

    /// <summary>Exact double ↔ BigInteger dyadic helpers for <see cref="GridAxis"/>.</summary>
    internal static class Dyadic
    {
        // d = mant * 2^exp exactly (mant odd or zero).
        private static (long Mant, int Exp) Split(double d)
        {
            if (d == 0) return (0, 0);
            long bits = BitConverter.DoubleToInt64Bits(d);
            bool neg = bits < 0;
            int rawExp = (int)((bits >> 52) & 0x7FF);
            long mant = bits & 0xFFFFFFFFFFFFFL;
            int exp;
            if (rawExp == 0) exp = -1074;              // subnormal
            else { mant |= 1L << 52; exp = rawExp - 1075; }
            while ((mant & 1) == 0) { mant >>= 1; exp++; }
            return (neg ? -mant : mant, exp);
        }

        /// <summary>Smallest K ≥ 0 with d * 2^K an integer.</summary>
        public static int MinShift(double d) => Math.Max(0, -Split(d).Exp);

        /// <summary>d * 2^k as an integer (k ≥ MinShift(d)).</summary>
        public static BigInteger Scaled(double d, int k)
        {
            var (m, exp) = Split(d);
            return new BigInteger(m) << (exp + k);
        }

        /// <summary>num / den (den > 0) as the nearest-below double, to ~63 bits.</summary>
        public static double ToDouble(BigInteger num, BigInteger den)
        {
            if (num.IsZero) return 0.0;
            long nb = (long)BigInteger.Abs(num).GetBitLength();
            long db = (long)den.GetBitLength();
            int shift = (int)(62 - (nb - db));       // quotient lands near 2^62
            BigInteger q = shift >= 0 ? (num << shift) / den : num / (den << -shift);
            return Math.ScaleB((double)q, -shift);
        }
    }
}
