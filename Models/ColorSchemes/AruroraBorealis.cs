using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class AuroraColorMap : GradientColorMap
    {
        public static string Name => "Aurora Borealis";

        public AuroraColorMap()
        {
            Stops.Add(new ColorStop(0.0f, Color.Black));
            Stops.Add(new ColorStop(0.2f, ColorTranslator.FromHtml("#002040")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#004080")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#00FF80")));
            Stops.Add(new ColorStop(0.8f, ColorTranslator.FromHtml("#80FF80")));
            Stops.Add(new ColorStop(1.0f, Color.White));
        }
    }

}
