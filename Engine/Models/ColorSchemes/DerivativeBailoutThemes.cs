// Models/ColorSchemes/DerivativeBailoutThemes.cs
//
// Derivative-bailout colourings — read |dz/dc| or arg(dz/dc) at the escape
// iteration.  |dz/dc| grows exponentially across the exterior; log|dz/dc|
// gives a stable, near-perceptually-uniform field.
//
// All three themes consume the 9-parameter Map overload to read dzdcR / dzdcI.
//
// Three sample themes:
//   • DerivativeMagnitudeMap — log|dz/dc| → blue-to-amber gradient
//   • DerivativeAngleMap     — arg(dz/dc) → HSV hue
//   • DerivativeFlowMap      — magnitude × angle combined for flow visualisation

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Log magnitude of the escape-time derivative.  The boundary reads dark
    /// (|d| ≈ 1); the deep exterior reads bright as |d| grows by ×4 per iter.
    /// </summary>
    public sealed class DerivativeMagnitudeMap : GradientColorMap
    {
        public static string Name => "Derivative — log|dz/dc|";
        public static string Category => "Derivative Bailout";
        public static string Description =>
            "log|dz/dc| at escape mapped through a blue-to-amber gradient.  " +
            "Boundary dark (|d|≈1); deep exterior bright as |d| grows by ×4/iter.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesDerivative | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Perceptual;

        public DerivativeMagnitudeMap()
        {
            Stops.Add(new ColorStop(0.00f, System.Drawing.Color.FromArgb( 10,  20,  60)));
            Stops.Add(new ColorStop(0.30f, System.Drawing.Color.FromArgb( 40,  80, 150)));
            Stops.Add(new ColorStop(0.55f, System.Drawing.Color.FromArgb(150, 130, 100)));
            Stops.Add(new ColorStop(0.80f, System.Drawing.Color.FromArgb(240, 200,  90)));
            Stops.Add(new ColorStop(1.00f, System.Drawing.Color.FromArgb(255, 250, 220)));
        }

        public override int Map(float smooth, float distance, int maxIterations) =>
            MapNormalized(0f, distance);

        public override int Map(float smooth, float distance, int iterations,
                                float nx, float ny,
                                float finalZr, float finalZi,
                                float dzdcR, float dzdcI)
        {
            double dMag = Math.Sqrt((double)dzdcR * dzdcR + (double)dzdcI * dzdcI);
            if (dMag < 1.0) dMag = 1.0;
            // Compress log range — typical exterior log|d| spans 0..20.
            float t = (float)(Math.Log(dMag) / 20.0);
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return MapNormalized(t, distance);
        }
    }

    /// <summary>
    /// Argument of the escape-time derivative mapped to HSV hue.  Reveals the
    /// rotational structure of dz/dc that |z|-based colourings hide.
    /// </summary>
    public sealed class DerivativeAngleMap : IColorMap
    {
        public static string Name => "Derivative — arg(dz/dc)";
        public static string Category => "Derivative Bailout";
        public static string Description =>
            "arg(dz/dc) at escape → HSV hue.  Reveals rotational structure of " +
            "the derivative invisible to magnitude-only colourings.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesDerivative | ColorMapFeatures.GradientBased;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            double a = Math.Atan2(dzdcI, dzdcR);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);
            var c = ColorUtils.Hsv(h, 0.75f, 0.95f);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }

    /// <summary>
    /// Combined flow visualisation — hue from arg(dz/dc), value from
    /// log|dz/dc|.  Reveals the full complex derivative field at escape.
    /// </summary>
    public sealed class DerivativeFlowMap : IColorMap
    {
        public static string Name => "Derivative — Flow Field";
        public static string Category => "Derivative Bailout";
        public static string Description =>
            "Complex derivative flow visualisation: hue = arg(dz/dc), " +
            "value = log|dz/dc|.  Renders the full dz/dc field at escape.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesDerivative | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations) => 0;

        public int Map(float smooth, float distance, int iterations,
                       float nx, float ny,
                       float finalZr, float finalZi,
                       float dzdcR, float dzdcI)
        {
            double dMag = Math.Sqrt((double)dzdcR * dzdcR + (double)dzdcI * dzdcI);
            double a = Math.Atan2(dzdcI, dzdcR);
            float h = (float)((a / (2.0 * Math.PI)) + 0.5);
            if (dMag < 1.0) dMag = 1.0;
            float v = (float)(0.25 + 0.75 * Math.Min(1.0, Math.Log(dMag) / 20.0));
            var c = ColorUtils.Hsv(h, 0.85f, v);
            return ColorUtils.PackArgb(c.R, c.G, c.B);
        }
    }
}
