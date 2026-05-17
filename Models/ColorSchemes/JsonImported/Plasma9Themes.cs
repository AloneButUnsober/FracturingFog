// Models/ColorSchemes/JsonImported/Plasma9Themes.cs
//
// Three Plasma 9 TST variants — perceptually uniform violet→pink→orange→yellow
// cycling gradients with different inserted spike stops, generated from
// Resources/ColorThemes/colorthemes.json.

using FracturingFog.Interefaces;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>"Plasma 9 TST" — base plasma with white/black/yellow spike.</summary>
    public sealed class Plasma9TstMap : CyclingGradientColorMap
    {
        public static string Name => "Plasma 9 TST";
        public static string Category => "Scientific";
        public static string Description =>
            "Perceptually uniform violet→pink→orange→yellow. High contrast.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Perceptual |
            ColorMapFeatures.HighContrast | ColorMapFeatures.Cyclic |
            ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.02f;

        public Plasma9TstMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 13,   8, 135)));
            Stops.Add(new ColorStop(0.11f, Color.FromArgb( 84,   2, 163)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb(139,  10, 165)));
            Stops.Add(new ColorStop(0.33f, Color.FromArgb(185,  50, 137)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(219,  92, 104)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb(244, 136,  73)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(254, 188,  43)));
            Stops.Add(new ColorStop(0.77f, Color.FromArgb(255, 255, 255)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.98f, Color.FromArgb(254, 188,  43)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(240, 249,  33)));
        }
    }

    /// <summary>"Plasma 9 TST (2)" — variant with earlier black spike.</summary>
    public sealed class Plasma9Tst2Map : CyclingGradientColorMap
    {
        public static string Name => "Plasma 9 TST (2)";
        public static string Category => "Scientific";
        public static string Description =>
            "Perceptually uniform violet→pink→orange→yellow. High contrast.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Perceptual |
            ColorMapFeatures.HighContrast | ColorMapFeatures.Cyclic |
            ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.02f;

        public Plasma9Tst2Map()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 13,   8, 135)));
            Stops.Add(new ColorStop(0.11f, Color.FromArgb( 84,   2, 163)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb(139,  10, 165)));
            Stops.Add(new ColorStop(0.33f, Color.FromArgb(185,  50, 137)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(219,  92, 104)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(244, 136,  73)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(254, 188,  43)));
            Stops.Add(new ColorStop(0.77f, Color.FromArgb(255, 255, 255)));
            Stops.Add(new ColorStop(0.82f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.98f, Color.FromArgb(254, 188,  43)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(240, 249,  33)));
        }
    }

    /// <summary>"Plasma 9 TST (4)" — variant with red+dark spike instead of white/black.</summary>
    public sealed class Plasma9Tst4Map : CyclingGradientColorMap
    {
        public static string Name => "Plasma 9 TST (4)";
        public static string Category => "Scientific";
        public static string Description =>
            "Perceptually uniform violet→pink→orange→yellow. High contrast.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Perceptual |
            ColorMapFeatures.HighContrast | ColorMapFeatures.Cyclic |
            ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.02f;

        public Plasma9Tst4Map()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 13,   8, 135)));
            Stops.Add(new ColorStop(0.11f, Color.FromArgb( 84,   2, 163)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb(139,  10, 165)));
            Stops.Add(new ColorStop(0.33f, Color.FromArgb(185,  50, 137)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(219,  92, 104)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(244, 136,  73)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(254, 188,  43)));
            Stops.Add(new ColorStop(0.77f, Color.FromArgb(255,  10,  25)));
            Stops.Add(new ColorStop(0.82f, Color.FromArgb(  7,   0,  20)));
            Stops.Add(new ColorStop(0.98f, Color.FromArgb(254, 188,  43)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(240, 249,  33)));
        }
    }
}
