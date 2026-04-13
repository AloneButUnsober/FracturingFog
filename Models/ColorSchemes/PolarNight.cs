// Models/ColorSchemes/PolarNight.cs
// Inspired by Nordic polar nights — near-black deep navy transitions through
// midnight blue, dusky purple, pale aqua, to an icy almost-white at the tips
// of iteration spikes.  Quiet and contemplative; high detail visibility in
// deep structures due to the gentle luminance gradient.

using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    /// <summary>
    /// Arctic polar night — near-black navy to pale ice blue with dusky purple
    /// midtones.  Subtle, high-detail palette.
    /// </summary>
    public class PolarNightMap : GradientColorMap
    {
        public static string Name        => "Polar Night";
        public static string Category    => "Scientific";
        public static string Description => "Arctic night — navy to pale ice blue with purple midtones.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Perceptual | ColorMapFeatures.GradientBased;

        public PolarNightMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  2,   4,  15)));  // near-black navy
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  8,  20,  55)));  // midnight blue
            Stops.Add(new ColorStop(0.28f, Color.FromArgb( 25,  40, 100)));  // deep blue
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 50,  60, 140)));  // blue-violet
            Stops.Add(new ColorStop(0.60f, Color.FromArgb( 80,  90, 170)));  // periwinkle
            Stops.Add(new ColorStop(0.74f, Color.FromArgb(130, 160, 210)));  // dusty blue
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(190, 220, 240)));  // pale aqua
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(225, 245, 255)));  // icy white-blue
        }
    }
}
