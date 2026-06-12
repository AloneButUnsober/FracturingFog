// Models/ColorSchemes/DistanceFieldThemes.cs
//
// Distance Estimation (DE) colourings.  Consume the exterior distance estimate
//   d = mag · ln(mag) / |dz/dc|
// already written to DistanceBuffer for every escaped pixel.
//
// The raw value is in complex-plane units, so it scales with zoom.  Without
// normalising it to pixel units, themes either saturate at shallow zoom (whole
// image bright) or vanish at deep zoom (whole image dark).  We divide by the
// per-pixel complex span (MandelbrotCalculator.LastPixelScale) so that a value
// of "1" always means "one pixel from the boundary" regardless of zoom level.
//
// Three sample themes:
//   • DistanceFieldChromaticMap — rainbow gradient, boundary cool → interior warm
//   • DistanceFieldGlowMap      — bright filaments on near-black interior (Milnor/Petersen look)
//   • DistanceFieldSilverMap    — silver-on-black engraving effect

using FracturingFog;
using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Shared base for distance-estimation colour maps.  Normalises the raw
    /// distance to pixel units via <see cref="MandelbrotCalculator.LastPixelScale"/>,
    /// then maps through an exponential saturation curve.  At t=0 the pixel lies
    /// on the boundary; t→1 deep in the exterior.
    /// </summary>
    public abstract class DistanceEstimationBaseMap : GradientColorMap
    {
        /// <summary>
        /// Falloff steepness.  Higher = more pixels near the boundary fall on
        /// the dark end of the gradient (sharper filaments).  Lower = broader
        /// glow extending further from the boundary.
        /// </summary>
        protected virtual float Strength => 0.35f;

        /// <summary>
        /// Soft floor that brightens the deep interior so it never reads pure
        /// black on themes where the gradient end is dark.  0 disables.
        /// </summary>
        protected virtual float MinT => 0.0f;

        public override int Map(float smooth, float distance, int maxIterations)
        {
            // Convert complex-plane distance to pixel units.  Guarded against
            // degenerate scale values during the very first render before
            // Calculate() has run.
            double pxScale = MandelbrotCalculator.LastPixelScale;
            if (pxScale <= 0.0 || double.IsNaN(pxScale) || double.IsInfinity(pxScale))
                pxScale = 1.0;

            float dePixels = (float)(distance / pxScale);
            if (dePixels < 0f) dePixels = 0f;

            // t = 0 on the boundary, → 1 deep in the exterior.
            float t = 1f - MathF.Exp(-dePixels * Strength);
            if (t < MinT) t = MinT;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }
    }

    /// <summary>
    /// Rainbow gradient driven by pixel-normalised exterior distance.
    /// Boundary filaments read cool; the exterior recedes through warm hues.
    /// </summary>
    public sealed class DistanceFieldChromaticMap : DistanceEstimationBaseMap
    {
        public static string Name => "Distance — Chromatic";
        public static string Category => "Distance Estimation";
        public static string Description =>
            "Pixel-normalised distance estimate over a six-stop rainbow gradient. " +
            "Boundary filaments read cool; the deep exterior recedes through warm hues.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesDistance | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Perceptual;

        protected override float Strength => 0.30f;

        public DistanceFieldChromaticMap()
        {
            // t=0 (boundary) → deep indigo; sweeps through cyan, green, gold,
            // peach; t=1 (deep exterior) → near-white.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(10, 10, 60)));
            Stops.Add(new ColorStop(0.18f, Color.FromArgb(20, 80, 180)));
            Stops.Add(new ColorStop(0.38f, Color.FromArgb(30, 200, 200)));
            Stops.Add(new ColorStop(0.58f, Color.FromArgb(120, 230, 100)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(245, 200, 90)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 230)));
        }
    }

    /// <summary>
    /// Classic Milnor / Petersen distance-estimator look — bright thin
    /// filaments glowing against a near-black exterior.
    /// </summary>
    public sealed class DistanceFieldGlowMap : DistanceEstimationBaseMap
    {
        public static string Name => "Distance — Glow";
        public static string Category => "Distance Estimation";
        public static string Description =>
            "Classic distance-estimator look: bright thin filaments glowing on a " +
            "near-black exterior.  Sharper at higher zoom — boundary detail crisp at any scale.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesDistance | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        // Bright boundary, fast falloff to dark — emphasises only the
        // closest few pixels around the set.
        protected override float Strength => 0.60f;

        public DistanceFieldGlowMap()
        {
            // t=0 (boundary) → near-white; rapid falloff into deep black.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 250, 220)));
            Stops.Add(new ColorStop(0.08f, Color.FromArgb(255, 180, 80)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb(150, 50, 40)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(30, 10, 20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));
        }
    }

    /// <summary>
    /// Silver-on-black engraving effect — high-contrast monochrome ramp keyed
    /// to the pixel-normalised distance field.
    /// </summary>
    public sealed class DistanceFieldSilverMap : DistanceEstimationBaseMap
    {
        public static string Name => "Distance — Silver Etching";
        public static string Category => "Distance Estimation";
        public static string Description =>
            "Monochrome silver engraving driven by the pixel-normalised distance " +
            "field.  Reads as a metallic relief at any zoom level.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesDistance | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast | ColorMapFeatures.Perceptual;

        protected override float Strength => 0.45f;

        public DistanceFieldSilverMap()
        {
            // t=0 (boundary) → bright silver; t=1 → near-black.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(245, 245, 250)));
            Stops.Add(new ColorStop(0.15f, Color.FromArgb(180, 185, 195)));
            Stops.Add(new ColorStop(0.40f, Color.FromArgb(90, 95, 105)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(25, 25, 35)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));
        }
    }
}
