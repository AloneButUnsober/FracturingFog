// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/IterFinalZThemes.cs
//
// "iter + final-z" combination colourings (Ultra Fractal family).  The smooth
// iteration count is blended with the real / imaginary parts of z at the escape
// iteration (and, in slice P2, their ratio) to produce cross-hatched, marbled
// secondary texture layered over the escape-time bands.
//
// Design: Docs/Technical/Coloring-IterFinalZ-DesignPlan.md
// Tracking: #69   Slice: #358 (P1 — shared base + iter+real + iter+imag)
//
// No new calculator path is required: finalZr / finalZi already reach the
// nine-parameter IColorMap.Map overload on BOTH the scalar fast path
// (MandelbrotCalculator.cs FillAuxAndColor) and the HP / perturbation deep-zoom
// path (FillAuxAndColorHP), so these themes are valid at all zoom depths — no
// MaxRecommendedZoom cap (mirrors the Binary / Argument decomposition themes).
//
// Normalisation note: z is UNBOUNDED after escape (|z| ranges from the bailout
// radius up to ~bailout²), so the real / imaginary channels are compressed with
// a sign-preserving arctangent into [0,1) before they can index a cyclic
// gradient.  The ratio channel (slice P2) uses atan2 to dodge the finalZi -> 0
// pole.

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Shared base for the "iter + final-z" combination themes.  Forms a single
    /// cyclic index <c>u ∈ [0,1)</c> from the smooth iteration count and one or
    /// more compressed final-z channels, then renders it as an HSV hue so the
    /// combined field reads as a smooth marbled pinwheel over the escape bands.
    ///
    /// Subclasses supply <see cref="ChannelTerm"/> — the weighted contribution
    /// of the final-z channel(s) they consume.
    /// </summary>
    public abstract class IterFinalZBaseMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        /// <summary>
        /// Iteration cycling period.  <c>u</c> repeats every <c>Period</c>
        /// smooth-iteration counts.  Default (0) means "use MaxIterations", i.e.
        /// one full cycle across the whole escape range.  Exposed as a tunable
        /// field; larger values stretch the bands, smaller values tighten them.
        /// </summary>
        public double Period { get; set; } = 0.0;

        /// <summary>Weight of the real channel in the combined index.</summary>
        public double WeightReal { get; set; } = 1.0;

        /// <summary>Weight of the imaginary channel in the combined index.</summary>
        public double WeightImag { get; set; } = 1.0;

        /// <summary>Weight of the ratio (angle) channel in the combined index.</summary>
        public double WeightRatio { get; set; } = 1.0;

        // ── Channel compression helpers ──────────────────────────────────────

        /// <summary>
        /// Sign-preserving arctangent compression of an unbounded final-z
        /// component into [0,1).  0.5 maps to the origin; ±∞ map to 1 / 0.
        /// </summary>
        protected static double Compress(double x) => 0.5 + Math.Atan(x) / Math.PI;

        /// <summary>
        /// Angle-based ratio channel: atan2(re, im) normalised to [0,1).  Encodes
        /// re/im continuously with no divide-by-zero at im -> 0.
        /// </summary>
        protected static double Ratio01(double re, double im)
            => 0.5 + Math.Atan2(re, im) / (2.0 * Math.PI);

        private static double Frac(double x)
        {
            double f = x - Math.Floor(x);
            return f < 0.0 ? f + 1.0 : f;
        }

        /// <summary>
        /// Weighted contribution of the final-z channel(s) this theme consumes.
        /// Added to the cyclic iteration term before taking the fractional part.
        /// </summary>
        protected abstract double ChannelTerm(double r01, double i01, double q01);

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            // In-set sentinel: the calculator zeroes finalZ for interior pixels.
            // (It also passes iterations == maxIter for escaped pixels, so the
            //  iteration count alone cannot distinguish interior — use finalZ.)
            if (finalZr == 0f && finalZi == 0f)
                return unchecked((int)((IColorMap)this).InSetColor);

            double period = Period > 0.0 ? Period : Math.Max(1, MaxIterations);
            double t = Frac(smooth / period);

            double r01 = Compress(finalZr);
            double i01 = Compress(finalZi);
            double q01 = Ratio01(finalZr, finalZi);

            double u = Frac(t + ChannelTerm(r01, i01, q01));

            var c = ColorUtils.Hsv((float)u, 0.82f, 0.95f);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }

    /// <summary>
    /// "Iter + Real" — smooth iteration bands cross-modulated by the compressed
    /// real part of z at escape.  <c>u = frac(t + wR·r01)</c>.
    /// </summary>
    public sealed class IterPlusRealMap : IterFinalZBaseMap
    {
        public static string Name => "Iter + Real";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Smooth iteration bands cross-modulated by the real part of z at " +
            "escape (arctangent-compressed).  Ultra Fractal 'iter+real' family.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Cyclic;

        protected override double ChannelTerm(double r01, double i01, double q01)
            => WeightReal * r01;
    }

    /// <summary>
    /// "Iter + Imag" — smooth iteration bands cross-modulated by the compressed
    /// imaginary part of z at escape.  <c>u = frac(t + wI·i01)</c>.
    /// </summary>
    public sealed class IterPlusImagMap : IterFinalZBaseMap
    {
        public static string Name => "Iter + Imag";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Smooth iteration bands cross-modulated by the imaginary part of z " +
            "at escape (arctangent-compressed).  Ultra Fractal 'iter+imag' family.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Cyclic;

        protected override double ChannelTerm(double r01, double i01, double q01)
            => WeightImag * i01;
    }

    /// <summary>
    /// "Iter + Real/Imag" — smooth iteration bands cross-modulated by the ratio
    /// of the real and imaginary parts of z at escape.  The ratio is taken as an
    /// ANGLE (<c>atan2(re, im)</c>) so it stays continuous with no divide-by-zero
    /// at the <c>finalZi -> 0</c> pole.  <c>u = frac(t + wQ·q01)</c>.
    /// </summary>
    public sealed class IterPlusRatioMap : IterFinalZBaseMap
    {
        public static string Name => "Iter + Real/Imag";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Smooth iteration bands cross-modulated by the real/imaginary ratio " +
            "of z at escape, encoded as atan2(re,im) to avoid the im->0 pole.  " +
            "Ultra Fractal 'iter+real/imag' family.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Cyclic;

        protected override double ChannelTerm(double r01, double i01, double q01)
            => WeightRatio * q01;
    }

    /// <summary>
    /// "Iter + Real + Imag + Ratio" — the full four-way composite: smooth
    /// iteration bands cross-modulated by all three compressed final-z channels
    /// (real, imaginary, and their atan2 ratio) summed with per-channel weights.
    /// <c>u = frac(t + wR·r01 + wI·i01 + wQ·q01)</c>.
    /// </summary>
    public sealed class IterRealImagRatioMap : IterFinalZBaseMap
    {
        public static string Name => "Iter + Real + Imag + Ratio";
        public static string Category => "Binary / Argument Decomposition";
        public static string Description =>
            "Four-way composite: smooth iteration bands modulated by the real, " +
            "imaginary and atan2-ratio channels of z at escape (per-channel " +
            "weights).  Densest of the Ultra Fractal 'iter+real+imag' family.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Cyclic;

        protected override double ChannelTerm(double r01, double i01, double q01)
            => WeightReal * r01 + WeightImag * i01 + WeightRatio * q01;
    }
}
