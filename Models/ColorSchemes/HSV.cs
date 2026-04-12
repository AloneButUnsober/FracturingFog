using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class HsvPalette : IColorMap
    {
        public static string Name => "Hsv";

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations)
        {
            float hue = (smooth * 0.02f) % 1.0f;
            hue -= MathF.Floor(hue);

            float saturation = 1.0f;
            float baseValue = smooth < iterations ? 1.0f : 0.0f;
            float lightness = 1.0f - MathF.Min(distance * 0.08f, 1.0f);
            float value = baseValue * lightness;

            return Fractals.HsvToRgb(hue, saturation, value);
        }
    }
}
