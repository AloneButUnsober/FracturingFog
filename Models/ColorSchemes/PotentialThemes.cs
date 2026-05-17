// Models/ColorSchemes/PotentialThemes.cs
//
// Douady-Hubbard potential — the harmonic Green's function of the exterior of
// the Mandelbrot set.  At escape iteration n with z_n outside the bailout disk,
//
//   G(c)  ≈  log|z_n| / 2^n
//
// All three themes consume the 9-parameter Map overload to read finalZr / finalZi.
//
// Three sample themes:
//   • PotentialEquipotentialMap — discrete bands of equal potential
//   • PotentialSmoothMap        — continuous gradient of log G
//   • PotentialContourMap       — thin contour lines at each octave of G

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Discrete equipotential bands.  Each band covers one octave of the
    /// Douady-Hubbard potential G(c), so the bands accumulate exponentially
    /// near the boundary.
    /// </summary>
    public sealed class PotentialEquipotentialMap : IColorMap
    {
        public static string Name => "Potential — Equipotential Bands";
        public static string Category => "Douady-Hubbard Potential";
        public static string Description =>
            "Discrete bands of the Douady-Hubbard potential G(c) = log|z|/2^n.  " +
            "Each band is one octave wide; bands crowd near the boundary.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        private static readonly int[] BandColors = new[]
        {
            unchecked((int)0xFF202048u), unchecked((int)0xFFB02818u),
            unchecked((int)0xFFF0B838u), unchecked((int)0xFF20A8B0u),
            unchecked((int)0xFFF0F0E0u), unchecked((int)0xFF707080u),
            unchecked((int)0xFFA85020u), unchecked((int)0xFF285030u),
        };

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            float potential = ComputePotential(smooth, finalZr, finalZi);
            if (potential <= 0f) return BandColors[0];
            int band = (int)(Math.Log2(potential) + 12f);
            if (band < 0) band = 0;
            return BandColors[band % BandColors.Length];
        }

        internal static float ComputePotential(float smooth, float zr, float zi)
        {
            double mag2 = (double)zr * zr + (double)zi * zi;
            if (mag2 < 1.0) return 0f;
            int n = (int)smooth;
            if (n < 1) n = 1;
            double pot = Math.Log(mag2) * 0.5 / Math.Pow(2.0, n);
            return (float)pot;
        }
    }

    /// <summary>
    /// Smooth gradient of log G(c).  Plots the potential as a perceptually
    /// uniform field from boundary (deep) to exterior (bright).
    /// </summary>
    public sealed class PotentialSmoothMap : GradientColorMap
    {
        public static string Name => "Potential — Smooth";
        public static string Category => "Douady-Hubbard Potential";
        public static string Description =>
            "Continuous gradient of log G(c) — smooth visualisation of the " +
            "Mandelbrot exterior as a harmonic potential field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Perceptual;

        public PotentialSmoothMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 10,  10,  40)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb( 40,  50, 120)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(140, 110, 180)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(240, 180, 140)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 230)));
        }

        public override int Map(float smooth, float distance, int maxIterations) =>
            MapNormalized(0f, distance);

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            float pot = PotentialEquipotentialMap.ComputePotential(smooth, finalZr, finalZi);
            if (pot <= 0f) return MapNormalized(0f, distance);
            // log scale — typical potential range 1e-12..1e-2.
            float t = (MathF.Log(pot) + 25f) / 25f;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return MapNormalized(t, distance);
        }
    }

    /// <summary>
    /// Thin contour lines at each octave of G(c).  Reads as a topographic
    /// engraving of the exterior potential field on a near-black background.
    /// </summary>
    public sealed class PotentialContourMap : IColorMap
    {
        public static string Name => "Potential — Octave Contours";
        public static string Category => "Douady-Hubbard Potential";
        public static string Description =>
            "Thin contour lines at each octave of G(c).  Topographic engraving of " +
            "the exterior potential field on a near-black background.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ |
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        // Line thickness in log-units; smaller = thinner lines.
        private const float LineWidth = 0.12f;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            float pot = PotentialEquipotentialMap.ComputePotential(smooth, finalZr, finalZi);
            if (pot <= 0f) return unchecked((int)0xFF050505u);
            float logp = MathF.Log2(pot);
            float frac = logp - MathF.Floor(logp);
            float d = Math.Min(frac, 1f - frac);
            float intensity = d < LineWidth ? 1f - d / LineWidth : 0f;
            byte v = (byte)(intensity * 230f + 12f);
            return ColorUtils.PackArgb(v, (byte)(v * 0.95f), (byte)(v * 0.7f));
        }
    }
}
