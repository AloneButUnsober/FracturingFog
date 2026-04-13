// Models/ColorSchemes/Plasma.cs
// Plasma — perceptually uniform sequential colour map.
// Stop values sampled from the matplotlib plasma LUT (Matplotlib contributors,
// BSD licence).  High-contrast sister palette to Viridis.

using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    /// <summary>
    /// Perceptually uniform deep violet → magenta → orange → bright yellow gradient.
    /// High perceptual contrast; complementary to Viridis.
    /// </summary>
    public class PlasmaColorMap : GradientColorMap
    {
        public static string Name        => "Plasma";
        public static string Category    => "Scientific";
        public static string Description => "Perceptually uniform violet→pink→orange→yellow. High contrast.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Perceptual |
            ColorMapFeatures.HighContrast | ColorMapFeatures.GradientBased;

        public PlasmaColorMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 13,   8, 135)));
            Stops.Add(new ColorStop(0.14f, Color.FromArgb( 84,   2, 163)));
            Stops.Add(new ColorStop(0.29f, Color.FromArgb(139,  10, 165)));
            Stops.Add(new ColorStop(0.43f, Color.FromArgb(185,  50, 137)));
            Stops.Add(new ColorStop(0.57f, Color.FromArgb(219,  92, 104)));
            Stops.Add(new ColorStop(0.71f, Color.FromArgb(244, 136,  73)));
            Stops.Add(new ColorStop(0.86f, Color.FromArgb(254, 188,  43)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(240, 249,  33)));
        }
    }
}
