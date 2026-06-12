using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class RainbowColorMap : IColorMap
    {
        public static string Name => "Rainbow";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            float h = (smooth * 0.015f) % 1f;
            var c = ColorUtils.Hsv(h, 1f, 1f);
            return unchecked((int)0xFF000000 | (c.R << 16) | (c.G << 8) | c.B);
        }
    }

}
