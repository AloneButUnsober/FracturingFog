// Models/ColorSchemes/ChernobylReactorTheme.cs
//
// "Chernobyl Reactor Core" — palette built from the documented visual
// signature of the open reactor pit on the night of 26 April 1986.
// Firefighters on the roof of Reactor 4 looked into the breached core and
// saw a layered hellscape: a white-hot incandescent void where graphite
// burned at 2000–2500 °C, molten amber rivers of melted control rods,
// sickly chartreuse from ionised air chemiluminescence, Cherenkov-blue
// radiation glow at the edges of the cavity, and oily charcoal smoke
// wrapping the whole scene.  This palette walks that stack from smoke
// inward to the incandescent core and out to the ionising blue rim, then
// folds back to smoke so deep zoom keeps cycling through the catastrophe.

using FracturingFog.Interefaces;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Chernobyl Reactor Core — charcoal smoke, oxidised crimson embers,
    /// molten amber graphite, white-hot incandescent peak, sickly chartreuse
    /// chemiluminescence, Cherenkov-blue ionisation, cobalt void.  Slow
    /// cycle so the catastrophe reads as breathing, not strobing.
    /// </summary>
    public sealed class ChernobylReactorCoreMap : CyclingGradientColorMap
    {
        public static string Name => "Chernobyl Reactor Core";
        public static string Category => "Atomic";
        public static string Description =>
            "What the firefighters saw down the breach of Reactor 4: charcoal " +
            "smoke wrapping oxidised crimson embers, molten amber graphite, a " +
            "white-hot incandescent peak, sickly chartreuse chemiluminescence " +
            "from ionised air, and Cherenkov-blue radiation glow at the rim.  " +
            "Slow cycle — the disaster breathes.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.022f;

        public ChernobylReactorCoreMap()
        {
            // Smoke void — oily charcoal with a brown bias from burning bitumen
            // on the roof.
            Stops.Add(new ColorStop(0.000f, Color.FromArgb( 14,  10,   8)));

            // First ember layer: dim oxidised iron-red, smouldering graphite
            // visible through smoke.
            Stops.Add(new ColorStop(0.060f, Color.FromArgb( 90,  20,  10)));
            Stops.Add(new ColorStop(0.120f, Color.FromArgb(170,  35,  15)));

            // Molten zone — vermillion → tangerine → amber as the graphite
            // climbs through 1500–2000 °C.
            Stops.Add(new ColorStop(0.190f, Color.FromArgb(230,  70,  20)));
            Stops.Add(new ColorStop(0.260f, Color.FromArgb(255, 130,  25)));
            Stops.Add(new ColorStop(0.330f, Color.FromArgb(255, 180,  40)));

            // Sodium-yellow flare from boiling alkali debris.
            Stops.Add(new ColorStop(0.390f, Color.FromArgb(255, 225,  90)));

            // White-hot incandescent peak — the actual breach throat at
            // ~2500 °C.  Slightly blue-shifted to read as ionising, not warm.
            Stops.Add(new ColorStop(0.440f, Color.FromArgb(255, 250, 220)));
            Stops.Add(new ColorStop(0.480f, Color.FromArgb(245, 250, 255)));

            // Chemiluminescence — sickly chartreuse / uranyl green from
            // ionised nitrogen and oxidising metal salts in the smoke column.
            Stops.Add(new ColorStop(0.560f, Color.FromArgb(180, 230,  80)));
            Stops.Add(new ColorStop(0.620f, Color.FromArgb( 90, 200, 100)));

            // Cherenkov glow — high-energy beta particles ionising the air
            // and any moisture above the open core.  Saturated electric blue.
            Stops.Add(new ColorStop(0.700f, Color.FromArgb( 40, 200, 255)));
            Stops.Add(new ColorStop(0.770f, Color.FromArgb(  0, 120, 230)));

            // Cobalt void — deeper radiation glow fading into shadow at the
            // far rim of the cavity.
            Stops.Add(new ColorStop(0.850f, Color.FromArgb( 10,  40, 130)));
            Stops.Add(new ColorStop(0.920f, Color.FromArgb(  8,  12,  50)));

            // Back to smoke — wraps the cycle so deep zoom keeps churning.
            Stops.Add(new ColorStop(1.000f, Color.FromArgb( 14,  10,   8)));
        }
    }
}
