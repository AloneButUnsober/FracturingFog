using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class RedAndBlack : IColorMap
    {
        public static string Name => "Radio Interference";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations)
        {
            //float baseValue = smooth < iterations ? 1.0f : 0.0f;
            //float lightness = 1.0f - MathF.Min(distance * 0.08f, 1.0f);

            // 8 full hue cycles across the iteration range → classic spiral gradient.
            float hue = smooth * 8.0f % 360.0f;
            float saturation = 0.85f;
            float value = 1.0f - (float)System.Math.Pow(iterations / (double)MaxIterations, 0.2);
            value = System.Math.Clamp(value, 0f, 1f);

            return Fractals.HsvToRgb(hue, saturation, value);
        }
    }
}
