// Models/ColorSchemes/LavaLamp.cs
// Warm lava-lamp cycling palette: dark maroon base → deep orange →
// bright amber → pale yellow highlight, looping back continuously.
// The cycling base class prevents the image going dark at deep zoom.

using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    /// <summary>
    /// Lava-lamp cycling palette — maroon → orange → amber → pale yellow,
    /// continuously cycling so deep structures remain vivid.
    /// </summary>
    public class LavaLampMap : CyclingGradientColorMap
    {
        public static string Name        => "Lava Lamp";
        public static string Category    => "Artistic";
        public static string Description => "Warm maroon→orange→amber cycling — molten lava effect.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.018f;

        public LavaLampMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 10,   2,   0)));  // near-black
            Stops.Add(new ColorStop(0.15f, Color.FromArgb( 80,  10,   5)));  // dark maroon
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(180,  40,   0)));  // deep red-orange
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(240, 100,  10)));  // orange
            Stops.Add(new ColorStop(0.68f, Color.FromArgb(255, 185,  30)));  // amber
            Stops.Add(new ColorStop(0.83f, Color.FromArgb(255, 240, 130)));  // pale yellow
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 10,   2,   0)));  // back to near-black
        }
    }
}
