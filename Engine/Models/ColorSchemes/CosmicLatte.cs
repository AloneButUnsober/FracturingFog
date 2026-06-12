// Models/ColorSchemes/CosmicLatte.cs
// Named after the 2002 Johns Hopkins University finding that the average colour
// of the universe is a pale beige (#FFF8E7).
// Warm cream-through-caramel-to-gold cycling palette — high iteration depth
// remains visually rich because the gradient repeats rather than going dark.

using System.Drawing;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    /// <summary>
    /// Warm cream → honey → caramel → gold cycling palette inspired by the
    /// average colour of the universe.
    /// </summary>
    public class CosmicLatteMap : CyclingGradientColorMap
    {
        public static string Name        => "Cosmic Latte";
        public static string Category    => "Artistic";
        public static string Description => "Warm cream-to-gold cycling — inspired by the average colour of the universe.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        // One full gradient cycle every ~40 smooth-units.
        protected override float CycleSpeed => 0.025f;

        public CosmicLatteMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 15,  10,   5)));  // near-black warm
            Stops.Add(new ColorStop(0.15f, Color.FromArgb( 80,  50,  20)));  // dark coffee
            Stops.Add(new ColorStop(0.35f, Color.FromArgb(180, 120,  50)));  // caramel
            Stops.Add(new ColorStop(0.55f, Color.FromArgb(240, 200, 130)));  // honey
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(255, 240, 200)));  // cream
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(255, 220, 100)));  // warm gold
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(200, 160,  40)));  // amber — wraps back to dark
        }
    }
}
