using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class FirePalette : IColorMap
    {
        public static string Name => "Fire";

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations)
        {
            float t = smooth / iterations;

            byte r = (byte)(MathF.Min(255, t * 512));
            byte g = (byte)(MathF.Min(255, t * 256));
            byte b = (byte)(MathF.Min(255, t * 128));

            return (255 << 24) | (r << 16) | (g << 8) | b;
        }
    }
}
