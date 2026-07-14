// Models/ColorSchemes/DomainColoringThemes.cs
//
// Domain coloring of the final escape value z_n — the standard complex-function
// visualisation applied to the escape orbit's terminal point.  Hue encodes
// arg(z_n); brightness or contour overlays encode |z_n|.
//
// All three themes consume the 9-parameter Map overload to read finalZr / finalZi.
//
// Three sample themes:
//   • DomainColorClassicMap        — H = arg(z), V from log|z|
//   • DomainColorPhasePortraitMap  — phase + modulus contours (Wegert style)
//   • DomainColorRiemannMap        — Riemann-sphere style with white pole

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Classic domain coloring of z at escape.  Hue = arg(z); brightness
    /// driven by log|z| compressed via tanh.
    /// </summary>
    public sealed class DomainColorClassicMap : IColorMap
    {
        public static string Name => "Domain Color - Classic";
        public static string Category => "Domain Coloring";
        public static string Description =>
            "Classic domain coloring of z at escape: hue = arg(z), brightness from " +
            "log|z|.  Standard complex-function visualisation applied to the orbit terminus.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesFinalZ | ColorMapFeatures.GradientBased;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            double a = Math.Atan2(finalZi, finalZr);
            double mag = Math.Sqrt((double)finalZr * finalZr + (double)finalZi * finalZi);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);
            float v = (float)Math.Tanh(Math.Log(mag + 1.0) * 0.25);
            v = 0.30f + 0.70f * v;
            var c = ColorUtils.Hsv(h, 0.85f, v);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }

    /// <summary>
    /// Wegert-style phase portrait — hue from arg(z) plus white modulus
    /// contours at each octave of |z|, and black phase-line contours at each
    /// π/6 of arg(z).
    /// </summary>
    public sealed class DomainColorPhasePortraitMap : IColorMap
    {
        public static string Name => "Domain Color - Phase Portrait";
        public static string Category => "Domain Coloring";
        public static string Description =>
            "Wegert-style phase portrait.  Hue from arg(z), white modulus contours " +
            "at every octave of |z|, black phase contours every π/6.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesFinalZ | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private const int   PhaseDivs   = 12;     // black contours every 2π / PhaseDivs
        private const float LineWidth   = 0.10f;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            double a   = Math.Atan2(finalZi, finalZr);
            double mag = Math.Sqrt((double)finalZr * finalZr + (double)finalZi * finalZi);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);
            var c = ColorUtils.Hsv(h, 0.75f, 0.85f);

            // Phase contour proximity.
            float phaseBin  = h * PhaseDivs;
            float phaseEdge = Math.Abs(phaseBin - MathF.Round(phaseBin)) / PhaseDivs;
            float phaseInk  = phaseEdge < LineWidth / PhaseDivs
                              ? 1f - phaseEdge / (LineWidth / PhaseDivs) : 0f;

            // Modulus contour: at integer log2(|z|).
            float lm        = (float)Math.Log2(mag + 1.0);
            float modFrac   = lm - MathF.Floor(lm);
            float modEdge   = Math.Min(modFrac, 1f - modFrac);
            float modInk    = modEdge < LineWidth ? 1f - modEdge / LineWidth : 0f;

            // Modulus white wash over hue; phase black over result.
            byte r = (byte)((c.R + (255 - c.R) * modInk) * (1f - phaseInk));
            byte g = (byte)((c.G + (255 - c.G) * modInk) * (1f - phaseInk));
            byte b = (byte)((c.B + (255 - c.B) * modInk) * (1f - phaseInk));
            return ColorUtils.PackArgb(r, g, b);
        }
    }

    /// <summary>
    /// Riemann-sphere projection style — hue from arg(z); the closer to the
    /// origin, the closer to black, the closer to ∞, the closer to white.
    /// Mid-modulus pixels read fully saturated.
    /// </summary>
    public sealed class DomainColorRiemannMap : IColorMap
    {
        public static string Name => "Domain Color - Riemann Sphere";
        public static string Category => "Domain Coloring";
        public static string Description =>
            "Riemann-sphere projection of z at escape.  Near-origin → black, near-∞ → " +
            "white, mid-modulus saturated hue keyed to arg(z).";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesFinalZ | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Perceptual;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            double a   = Math.Atan2(finalZi, finalZr);
            double mag = Math.Sqrt((double)finalZr * finalZr + (double)finalZi * finalZi);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);

            // Riemann latitude — y = (|z|² - 1) / (|z|² + 1) ∈ [-1, 1].
            double m2 = mag * mag;
            float lat = (float)((m2 - 1.0) / (m2 + 1.0));
            float v = (lat + 1f) * 0.5f;        // 0 at z=0, 1 at z=∞
            float s = 1f - MathF.Abs(lat);      // mid bright, poles desaturated

            var c = ColorUtils.Hsv(h, 0.30f + 0.70f * s, 0.20f + 0.80f * v);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
