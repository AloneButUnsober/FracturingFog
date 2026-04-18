using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class GrayscalePalette : IColorMap
    {
        public static string Name => "Greyscale";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;


        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations)
        {
            float t = smooth / iterations;
            byte c = (byte)(t * 255);
            return (255 << 24) | (c << 16) | (c << 8) | c;
        }
    }
}
