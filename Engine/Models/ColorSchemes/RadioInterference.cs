using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class RedAndBlack : IColorMap, IGpuHlslPalette
    {
        public static string Name => "Radio Interference Original";

        public ColorPaletteType Type { get; } = ColorPaletteType.Scientific;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations)
        {
            // hue intentionally overflows the 0..1 range HsvToRgb expects —
            // the wrap via floor/modulo is what produces the concentric
            // rainbow rings and the "interference" banding at deep zoom.
            float hue = smooth * 8.0f % 360.0f;
            float saturation = 0.85f;

            // Per-pixel brightness ramp: bright on the outside, fading
            // toward the set boundary. The original used iterations /
            // MaxIterations (per-frame constant), but the calculator now
            // syncs MaxIterations = iterations, collapsing that to 1.0
            // and producing solid black. Driving value from smooth keeps
            // the theme working at any iteration count / zoom level.
            double t = System.Math.Min(smooth / (double)iterations, 1.0);
            float value = 1.0f - (float)System.Math.Pow(t, 0.2);
            value = System.Math.Clamp(value, 0f, 1f);

            return Fractals.HsvToRgb(hue, saturation, value);
        }

        public string HlslPrelude => HlslPaletteHelpers.HsvAndMods;

        public string HlslPaletteBody => @"
    float hue = cg_mods(in_smooth * 8.0, 360.0);
    float t = min(in_smooth / max(in_maxIter, 1.0), 1.0);
    float value = saturate(1.0 - pow(t, 0.2));
    return cg_hsv_to_rgb(hue, 0.85, value);
";

        public string PaletteId => "RedAndBlack/v1";
    }
}
//using FracturingFog.Interefaces;
//using System;
//using System.Collections.Generic;
//using System.Text;

//namespace FracturingFog.Models
//{
//    public class RedAndBlack : IColorMap
//    {
//        public static string Name => "Radio Interference Original";

//        public ColorPaletteType Type { get; } = ColorPaletteType.Scientific;

//        public int MaxIterations { get; set; } = 1000;

//        public int Map(float smooth, float distance, int iterations)
//        {
//            // hue intentionally overflows the 0..1 range HsvToRgb expects —
//            // the wrap via floor/modulo is what produces the concentric
//            // rainbow rings and the "interference" banding at deep zoom.
//            float hue = smooth * 8.0f % 360.0f;
//            float saturation = 0.85f;

//            // Per-pixel brightness ramp: bright on the outside, fading
//            // toward the set boundary. The original used iterations /
//            // MaxIterations (per-frame constant), but the calculator now
//            // syncs MaxIterations = iterations, collapsing that to 1.0
//            // and producing solid black. Driving value from smooth keeps
//            // the theme working at any iteration count / zoom level.
//            double t = System.Math.Min(smooth / (double)iterations, 1.0);
//            float value = 1.0f - (float)System.Math.Pow(t, 0.2);
//            value = System.Math.Clamp(value, 0f, 1f);

//            return Fractals.HsvToRgb(hue, saturation, value);
//        }
//    }
//}
////using FracturingFog.Interefaces;
////using System;
////using System.Collections.Generic;
////using System.Text;

////namespace FracturingFog.Models
////{
////    public class RedAndBlack : IColorMap
////    {
////        public static string Name => "Radio Interference Original";

////        public ColorPaletteType Type { get; } = ColorPaletteType.Scientific;

////        public int MaxIterations { get; set; } = 1000;

////        public int Map(float smooth, float distance, int iterations)
////        {
////            // hue intentionally overflows the 0..1 range HsvToRgb expects —
////            // the wrap via floor/modulo is what produces the concentric
////            // rainbow rings and the "interference" banding at deep zoom.
////            float hue = smooth * 8.0f % 360.0f;
////            float saturation = 0.85f;

////            // Pinned to a fixed reference instead of MaxIterations: the
////            // calculator now syncs ColorMap.MaxIterations = iterations
////            // before each render, which collapses iterations/MaxIterations
////            // to 1.0 and drops value to 0 (the "all black" symptom). 1000
////            // matches the original default the theme was tuned against.
////            const double ReferenceMax = 1000.0;
////            double t = System.Math.Min(iterations / ReferenceMax, 1.0);
////            float value = 1.0f - (float)System.Math.Pow(t, 0.2);
////            value = System.Math.Clamp(value, 0f, 1f);

////            return Fractals.HsvToRgb(hue, saturation, value);
////        }
////    }
////}
//////using FracturingFog.Interefaces;
//////using System;
//////using System.Collections.Generic;
//////using System.Text;

//////namespace FracturingFog.Models
//////{
//////    public class RedAndBlack : IColorMap
//////    {
//////        public static string Name => "Radio Interference Original";

//////        public ColorPaletteType Type { get; } = ColorPaletteType.Scientific;

//////        public int MaxIterations { get; set; } = 1000;

//////        public int Map(float smooth, float distance, int iterations)
//////        {
//////            //float baseValue = smooth < iterations ? 1.0f : 0.0f;
//////            //float lightness = 1.0f - MathF.Min(distance * 0.08f, 1.0f);

//////            // 8 full hue cycles across the iteration range → classic spiral gradient.
//////            float hue = smooth * 8.0f % 360.0f;
//////            float saturation = 0.85f;
//////            float value = 1.0f - (float)System.Math.Pow(iterations / (double)MaxIterations, 0.2);
//////            value = System.Math.Clamp(value, 0f, 1f);

//////            return Fractals.HsvToRgb(hue, saturation, value);
//////        }
//////    }
//////}
