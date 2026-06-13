// Models/ColorSchemes/JsonImported/EmeraldGhostThemes.cs
//
// "Emerald Ghost" family — pale green/black palette in cycling and gradient
// variants, generated from Resources/ColorThemes/colorthemes.json.

using FracturingFog.Interefaces;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>"Emerald Ghost" — pale green/black cycling palette.</summary>
    public sealed class EmeraldGhostCyclingMap : CyclingGradientColorMap
    {
        public static string Name => "Emerald Ghost";
        public static string Category => "Cycling Gradient";
        public static string Description => "Pale Green/black.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        protected override float CycleSpeed => 0.01f;

        public EmeraldGhostCyclingMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  2,  40,  12)));
            Stops.Add(new ColorStop(0.15f, Color.FromArgb(  8,  74,  55)));
            Stops.Add(new ColorStop(0.32f, Color.FromArgb( 13, 112,  89)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb( 45, 155, 140)));
            Stops.Add(new ColorStop(0.65f, Color.FromArgb( 12, 186,  50)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb( 82, 212,  35)));
            Stops.Add(new ColorStop(0.92f, Color.FromArgb( 82, 231,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 19,  38,  13)));
        }
    }

    /// <summary>"Emerald Ghost GRAD" — pale green/black linear gradient.</summary>
    public sealed class EmeraldGhostGradMap : GradientColorMap
    {
        public static string Name => "Emerald Ghost GRAD";
        public static string Category => "Cycling Gradient";
        public static string Description => "Pale Green/black.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        public EmeraldGhostGradMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  2,  40,  12)));
            Stops.Add(new ColorStop(0.15f, Color.FromArgb(  8,  74,  55)));
            Stops.Add(new ColorStop(0.32f, Color.FromArgb( 13, 112,  89)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb( 45, 155, 140)));
            Stops.Add(new ColorStop(0.65f, Color.FromArgb( 12, 186,  50)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb( 82, 212,  35)));
            Stops.Add(new ColorStop(0.92f, Color.FromArgb( 82, 231,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 19,  38,  13)));
        }
    }

    /// <summary>"Emerald Ghost GRAD2" — wider stop spread linear gradient.</summary>
    public sealed class EmeraldGhostGrad2Map : GradientColorMap
    {
        public static string Name => "Emerald Ghost GRAD2";
        public static string Category => "Cycling Gradient";
        public static string Description => "Pale Green/black.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        public EmeraldGhostGrad2Map()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  2,  40,  12)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(  8,  74,  55)));
            Stops.Add(new ColorStop(0.37f, Color.FromArgb( 13, 112,  89)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb( 45, 155, 140)));
            Stops.Add(new ColorStop(0.68f, Color.FromArgb( 12, 186,  50)));
            Stops.Add(new ColorStop(0.83f, Color.FromArgb( 82, 212,  35)));
            Stops.Add(new ColorStop(0.95f, Color.FromArgb( 82, 231,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 19,  38,  13)));
        }
    }
}
