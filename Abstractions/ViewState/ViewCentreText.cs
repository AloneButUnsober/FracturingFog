// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewState/ViewCentreText.cs
//
// #1833 (epic #1428, C2b) — the CX / CY text boxes and the copied location text, with the
// exact centre (#1832). Shown: the exact centre as plain decimal when the view has one,
// else CoordNotation's limb formats (unchanged). Typed / pasted: CoordNotation's limb parse
// (unchanged up to 28 digits; longer decimals now land in all eight limbs exactly), plus
// the exact centre when the text holds more than OD can. Kept out of the view models so
// the tests (which can't load UI.Avalonia) reach it.

using FracturingFog.FFMath;

namespace FracturingFog.ViewState
{
    /// <summary>#1833 — CX / CY text for a view state, exact-centre aware.</summary>
    public static class ViewCentreText
    {
        /// <summary>The real part as box / location text.</summary>
        public static string FormatX(FractalViewState s, bool pipe)
            => s.CenterExact is { } e ? CoordNotation.FormatExact(e.Re)
                : CoordNotation.Format(pipe, s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3,
                    s.CenterX4, s.CenterX5, s.CenterX6, s.CenterX7);

        /// <summary>The imaginary part as box / location text.</summary>
        public static string FormatY(FractalViewState s, bool pipe)
            => s.CenterExact is { } e ? CoordNotation.FormatExact(e.Im)
                : CoordNotation.Format(pipe, s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3,
                    s.CenterY4, s.CenterY5, s.CenterY6, s.CenterY7);

        /// <summary>Applies edited CX / CY text (null = that box was not touched). Each part's
        /// limbs are set as before. When either edited text holds more than OD's precision,
        /// the exact centre is set too, the untouched part taken from the exact centre the
        /// view had (or its limbs). Returns whether anything was applied.</summary>
        public static bool Apply(FractalViewState s, string? cx, string? cy)
        {
            var prior = s.CenterExact;   // before the limb writes below drop it
            bool changed = false;
            bool xOk = CoordNotation.TryParse(cx,
                out double x0, out double x1, out double x2, out double x3,
                out double x4, out double x5, out double x6, out double x7);   // false for null
            bool yOk = CoordNotation.TryParse(cy,
                out double y0, out double y1, out double y2, out double y3,
                out double y4, out double y5, out double y6, out double y7);
            if (xOk)
            {
                s.CenterX = x0; s.CenterXLo = x1; s.CenterX2 = x2; s.CenterX3 = x3;
                s.CenterX4 = x4; s.CenterX5 = x5; s.CenterX6 = x6; s.CenterX7 = x7;
                changed = true;
            }
            if (yOk)
            {
                s.CenterY = y0; s.CenterYLo = y1; s.CenterY2 = y2; s.CenterY3 = y3;
                s.CenterY4 = y4; s.CenterY5 = y5; s.CenterY6 = y6; s.CenterY7 = y7;
                changed = true;
            }
            if (!changed) return false;

            // Both parts at one precision, as ExactCentre.TryParse reads a pair: the larger of
            // the edited texts' digit counts and the exact centre the view had.
            int bits = 0;
            if (xOk) bits = System.Math.Max(bits, ExactCentre.PrecisionForDigits(ExactCentre.SignificantDigits(cx!)));
            if (yOk) bits = System.Math.Max(bits, ExactCentre.PrecisionForDigits(ExactCentre.SignificantDigits(cy!)));
            if (prior is { } pr) bits = System.Math.Max(bits, pr.Precision);
            // Only text longer than 28 significant digits can be exact: up to there the limb
            // parse is the text's meaning (a short decimal is its shortest double, #1252).
            BigFloat? ex = xOk && Long(cx!) && CoordNotation.TryParseExact(cx, bits, out var vx) && BeyondOd(vx) ? vx : null;
            BigFloat? ey = yOk && Long(cy!) && CoordNotation.TryParseExact(cy, bits, out var vy) && BeyondOd(vy) ? vy : null;
            if (ex == null && ey == null)
            {
                // Neither edit needs more than OD. An untouched part that was exact keeps it.
                if (prior is { } p && (!xOk || !yOk))
                {
                    var re = xOk ? BigFloat.FromOD(new OD(x0, x1, x2, x3, x4, x5, x6, x7), p.Precision) : p.Re;
                    var im = yOk ? BigFloat.FromOD(new OD(y0, y1, y2, y3, y4, y5, y6, y7), p.Precision) : p.Im;
                    if (BeyondOd(re) || BeyondOd(im)) s.SetCenterExact(new ExactCentre(re, im));
                }
                return true;
            }
            BigFloat Part(BigFloat? edited, bool editedOk, BigFloat? priorPart, OD limbs)
                => edited ?? (!editedOk && priorPart is { } pp ? pp : BigFloat.FromOD(limbs, bits));
            var c = new ExactCentre(
                Part(ex, xOk, prior?.Re, s.GetCenter().Re),
                Part(ey, yOk, prior?.Im, s.GetCenter().Im));
            s.SetCenterExact(c.WithPrecision(bits));
            return true;
        }

        private static bool Long(string t) => !t.Contains('|') && ExactCentre.SignificantDigits(t) > 28;

        // The value is not its own OD limbs: OD can't hold it.
        private static bool BeyondOd(BigFloat v) => BigFloat.FromOD(v.ToOD(), v.Precision) != v;
    }
}
