// Models/ColorSchemes/Viridis.cs
// Viridis — perceptually uniform sequential colour map.
// Key stop values sampled from the matplotlib viridis LUT (Matplotlib contributors,
// BSD licence).  Designed to be readable by people with common colour blindness,
// and to print correctly in greyscale.

using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    /// <summary>
    /// Perceptually uniform purple → blue-green → yellow gradient.
    /// Stays readable on greyscale printouts and by colour-blind viewers.
    /// </summary>
    public class ViridisColorMap : GradientColorMap
    {
        public static string Name        => "Viridis";
        public static string Category    => "Scientific";
        public static string Description => "Perceptually uniform purple→teal→yellow. Colourblind-friendly.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Perceptual | ColorMapFeatures.GradientBased;

        public ViridisColorMap()
        {
            // 10 key stops sampled from the 256-entry matplotlib viridis LUT.
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 68,   1,  84)));
            Stops.Add(new ColorStop(0.11f, Color.FromArgb( 72,  40, 120)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb( 62,  74, 137)));
            Stops.Add(new ColorStop(0.33f, Color.FromArgb( 49, 104, 142)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb( 38, 130, 142)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb( 31, 158, 137)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb( 53, 183, 121)));
            Stops.Add(new ColorStop(0.77f, Color.FromArgb(110, 206,  88)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(181, 222,  43)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(253, 231,  37)));
        }
    }
}
