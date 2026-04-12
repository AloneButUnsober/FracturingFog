using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class TriColorMap : GradientColorMap
    {
        public static string Name => "Tri-Color Stripe";
        public TriColorMap()
        {
            Stops.Add(new ColorStop(0.0f, Color.Black));
            Stops.Add(new ColorStop(0.33f, Color.Red));
            Stops.Add(new ColorStop(0.66f, Color.Lime));
            Stops.Add(new ColorStop(1.0f, Color.Blue));
        }
    }

}
