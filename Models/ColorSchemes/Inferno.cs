using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace FracturingFog.Models
{
    public class InfernoColorMap : GradientColorMap
    {
        public static string Name => "Inferno";

        public InfernoColorMap()
        {
            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#000004")));
            Stops.Add(new ColorStop(0.2f, ColorTranslator.FromHtml("#420A68")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#932667")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#DD513A")));
            Stops.Add(new ColorStop(0.8f, ColorTranslator.FromHtml("#FCA50A")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#FCFFA4")));
        }
    }

}
