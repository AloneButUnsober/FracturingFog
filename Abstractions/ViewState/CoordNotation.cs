// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/ViewState/CoordNotation.cs
//
// Single source of truth for how the Explore / Region-Navigation CX/CY
// coordinate boxes format and parse multi-limb (DD/QD/OD) centre values.
//
// #791 — the UI DEFAULTS TO SINGLE-VALUE notation (a plain decimal string,
// what every other fractal program shows), with pipe-delimited limb notation
// (FF's native lossless form) available behind a toggle. This has regressed
// before ("stomped on"), so the default lives here as DefaultUsePipe and is
// pinned by CoordNotationTests — do NOT flip it to pipe. Format+parse are pure
// and unit-tested so the round-trip contract can't silently rot.
//
// Precision note: single-value notation carries ~29 significant digits
// (decimal's cap). The parser peels a single string back into limbs via
// decimal, and the caller's "skip re-parse of an untouched box" guard keeps a
// pure navigation jump lossless. Pasting the pipe form always recovers every
// limb exactly. Users who need to move full QD/OD precision by hand flip the
// toggle to pipe.

using System;
using System.Globalization;
using System.Text;

namespace FracturingFog.ViewState
{
    /// <summary>Formats + parses CX/CY coordinate strings in either single-value
    /// (default) or pipe-delimited limb notation. See file header (#791).</summary>
    public static class CoordNotation
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>#791 — the UI's default notation. MUST stay false
        /// (single-value). Pinned by CoordNotationTests so the enhancement is
        /// not stomped on again. Do not change to true.</summary>
        public const bool DefaultUsePipe = false;

        /// <summary>Format a centre value. <paramref name="pipe"/> selects
        /// pipe-delimited limbs (lossless FF-native) vs single-value decimal.</summary>
        public static string Format(bool pipe,
            double hi, double lo = 0, double l2 = 0, double l3 = 0,
            double l4 = 0, double l5 = 0, double l6 = 0, double l7 = 0)
            => pipe
                ? FormatPipe(hi, lo, l2, l3, l4, l5, l6, l7)
                : FormatSingle(hi, lo, l2, l3, l4, l5, l6, l7);

        /// <summary>Single-value decimal notation: sum the limbs into a decimal
        /// and print up to ~29 significant digits (no scientific notation for
        /// everyday coords). The form every other program uses.</summary>
        public static string FormatSingle(
            double hi, double lo = 0, double l2 = 0, double l3 = 0,
            double l4 = 0, double l5 = 0, double l6 = 0, double l7 = 0)
        {
            try
            {
                decimal acc = (decimal)hi + (decimal)lo + (decimal)l2 + (decimal)l3
                            + (decimal)l4 + (decimal)l5 + (decimal)l6 + (decimal)l7;
                return acc.ToString("G29", Inv);
            }
            catch (OverflowException)
            {
                // Outside decimal's range — fall back to the raw high limb.
                return hi.ToString("G17", Inv);
            }
        }

        /// <summary>Pipe-delimited limb notation (FF native): <c>Hi|Lo|Lo2|…</c>,
        /// carrying only as many non-zero limbs as the depth needs. Lossless for
        /// DD/QD/OD precision; the only honest textbox form past ~1e15 zoom.</summary>
        public static string FormatPipe(
            double hi, double lo = 0, double l2 = 0, double l3 = 0,
            double l4 = 0, double l5 = 0, double l6 = 0, double l7 = 0)
        {
            // Highest non-zero limb → never trail zero limbs.
            int n = 1;
            if (l7 != 0.0) n = 8;
            else if (l6 != 0.0) n = 7;
            else if (l5 != 0.0) n = 6;
            else if (l4 != 0.0) n = 5;
            else if (l3 != 0.0) n = 4;
            else if (l2 != 0.0) n = 3;
            else if (lo != 0.0) n = 2;

            if (n == 1)
            {
                // Shallow — a plain decimal is exact and readable.
                try { return ((decimal)hi).ToString("G29", Inv); }
                catch (OverflowException) { return hi.ToString("G17", Inv); }
            }

            var limbs = new[] { hi, lo, l2, l3, l4, l5, l6, l7 };
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(limbs[i].ToString("G17", Inv));
            }
            return sb.ToString();
        }

        /// <summary>Parse a CX/CY string in EITHER notation into limbs. Pipe
        /// input (paste of FF-native) is split limb-by-limb; a single decimal is
        /// peeled into Hi/Lo/Lo2/Lo3 via decimal so pasted precision survives.
        /// Returns false only when nothing numeric could be read.</summary>
        public static bool TryParse(string? s,
            out double hi, out double lo, out double l2, out double l3,
            out double l4, out double l5, out double l6, out double l7)
        {
            hi = lo = l2 = l3 = l4 = l5 = l6 = l7 = 0.0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var parts = s.Split('|');
            if (parts.Length > 1)
            {
                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, Inv, out hi)) return false;
                if (parts.Length > 1) double.TryParse(parts[1].Trim(), NumberStyles.Float, Inv, out lo);
                if (parts.Length > 2) double.TryParse(parts[2].Trim(), NumberStyles.Float, Inv, out l2);
                if (parts.Length > 3) double.TryParse(parts[3].Trim(), NumberStyles.Float, Inv, out l3);
                if (parts.Length > 4) double.TryParse(parts[4].Trim(), NumberStyles.Float, Inv, out l4);
                if (parts.Length > 5) double.TryParse(parts[5].Trim(), NumberStyles.Float, Inv, out l5);
                if (parts.Length > 6) double.TryParse(parts[6].Trim(), NumberStyles.Float, Inv, out l6);
                if (parts.Length > 7) double.TryParse(parts[7].Trim(), NumberStyles.Float, Inv, out l7);
                return true;
            }

            string single = parts[0].Trim();
            if (decimal.TryParse(single, NumberStyles.Float, Inv, out decimal m))
            {
                hi = (double)m;
                try { m -= (decimal)hi; } catch (OverflowException) { return true; }
                lo = (double)m;
                try { m -= (decimal)lo; } catch (OverflowException) { return true; }
                l2 = (double)m;
                try { m -= (decimal)l2; } catch (OverflowException) { return true; }
                l3 = (double)m;
                return true;
            }

            // Outside decimal range (NaN, infinity, |x| > 7.9e28, …).
            return double.TryParse(single, NumberStyles.Float, Inv, out hi);
        }

        /// <summary>Four-limb convenience overload.</summary>
        public static bool TryParse(string? s, out double hi, out double lo, out double l2, out double l3)
            => TryParse(s, out hi, out lo, out l2, out l3, out _, out _, out _, out _);
    }
}
