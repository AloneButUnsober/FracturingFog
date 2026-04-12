using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class BlackbodyColorMap : GradientColorMap
    {
        public static string Name => "Black Body Rad";

        public BlackbodyColorMap()
        {
            Stops.Add(new ColorStop(0.0f, Color.Black));
            Stops.Add(new ColorStop(0.2f, ColorTranslator.FromHtml("#1F0C00")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#7A1E00")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#FF6A00")));
            Stops.Add(new ColorStop(0.8f, ColorTranslator.FromHtml("#FFD700")));
            Stops.Add(new ColorStop(1.0f, Color.White));
        }
    }

}
