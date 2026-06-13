using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class GoldenRatioMap : IColorMap
    {
        public static string Name => "Golden Ratio";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;


        public int MaxIterations { get; set; } = 1000;

        private const float Phi = 0.61803398875f;

        public int Map(float smooth, float distance, int maxIterations)
        {
            float h = (smooth * Phi) % 1f;
            var c = ColorUtils.Hsv(h, 0.8f, 1f);
            return unchecked((int)0xFF000000 | (c.R << 16) | (c.G << 8) | c.B);
        }
    }

}
