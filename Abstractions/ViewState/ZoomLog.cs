// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewState/ZoomLog.cs
//
// #1836 (epic #1428, C3) — zoom past double's range. A double zoom overflows at about
// 1.8e308; log10 zoom does not (a double log10 keeps ~1e-13 relative zoom accuracy even at
// 1e1000, far below a pixel). FractalViewState keeps `double Zoom` as the render contract
// (saturated at double.MaxValue past its range) and carries the log next to it; these
// helpers convert, show and read zoom at any exponent. Up to double's range every helper
// gives exactly what the plain double path gave.

using System;
using System.Globalization;

namespace FracturingFog.ViewState
{
    /// <summary>#1836 — log10 zoom helpers.</summary>
    public static class ZoomLog
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>log10(double.MaxValue) ≈ 308.2547: past it a double zoom can't hold the value.</summary>
        public static readonly double MaxDoubleLog10 = Math.Log10(double.MaxValue);

        /// <summary>The double zoom for a log10 zoom: 10^log, saturated at
        /// <see cref="double.MaxValue"/> past double's range.</summary>
        public static double FromLog10(double log10)
        {
            double z = Math.Pow(10, log10);
            return double.IsPositiveInfinity(z) ? double.MaxValue : z;
        }

        /// <summary>True when <paramref name="log10"/> is past what a double zoom holds.</summary>
        public static bool BeyondDouble(double log10) => log10 > MaxDoubleLog10;

        /// <summary>Zoom as text, .NET "G" style with <paramref name="digits"/> significant
        /// digits. While the double holds the zoom (<paramref name="log10"/> NaN, or not past
        /// double) this is exactly <c>zoom.ToString("G" + digits)</c>; past it, the mantissa
        /// and exponent come from the log ("2.5E+1000").</summary>
        public static string Format(double zoom, double log10, int digits = 6)
        {
            if (double.IsNaN(log10) || !BeyondDouble(log10))
                return zoom.ToString("G" + digits.ToString(Inv), Inv);
            return FormatLog10(log10, digits);
        }

        /// <summary>A log10 zoom as "G"-style scientific text ("1.23457E+400").</summary>
        public static string FormatLog10(double log10, int digits = 6)
        {
            if (double.IsPositiveInfinity(log10)) return "Infinity";
            double e = Math.Floor(log10);
            double m = Math.Round(Math.Pow(10, log10 - e), Math.Clamp(digits - 1, 0, 15));
            if (m >= 10) { m /= 10; e += 1; }
            string ms = m.ToString("G" + digits.ToString(Inv), Inv);
            return e >= 0 ? $"{ms}E+{e.ToString("0", Inv)}" : $"{ms}E-{(-e).ToString("00", Inv)}";
        }

        /// <summary>Reads zoom text: anything double reads (unchanged), plus scientific text
        /// past double's range ("1e500", "2.5E+1000") that double reads as infinity. Gives the
        /// double zoom (saturated past double) and its log10. False for non-positive or
        /// unreadable text.</summary>
        public static bool TryParse(string? text, out double zoom, out double log10)
        {
            zoom = 0; log10 = double.NaN;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (double.TryParse(t, NumberStyles.Float, Inv, out double d) && double.IsFinite(d))
            {
                if (!(d > 0)) return false;
                zoom = d; log10 = Math.Log10(d);
                return true;
            }
            int ei = t.IndexOfAny(new[] { 'e', 'E' });
            if (ei <= 0) return false;
            if (!double.TryParse(t.AsSpan(0, ei), NumberStyles.Float, Inv, out double m) || !(m > 0) || !double.IsFinite(m))
                return false;
            if (!long.TryParse(t.AsSpan(ei + 1), NumberStyles.AllowLeadingSign, Inv, out long ex)
                || Math.Abs(ex) > 1_000_000_000L)
                return false;
            log10 = Math.Log10(m) + ex;
            if (!double.IsFinite(log10)) return false;
            zoom = FromLog10(log10);
            return zoom > 0;
        }
    }
}
