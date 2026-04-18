using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class HsvModified : IColorMap
    {
        public static string Name => "Hsv-Modified";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;


        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations)
        {
            float hue = (smooth * 0.05f) % 1.1f;
            hue -= MathF.Floor(hue);

            float saturation = 0.9f;
            float baseValue = smooth < iterations ? 1.0f : -0.01f;
            float lightness = 1.35f - MathF.Min(distance * 0.04f, 1.0f);
            float value = baseValue * lightness;

            return Fractals.HsvToRgb(hue, saturation, value);
        }
    }
}
