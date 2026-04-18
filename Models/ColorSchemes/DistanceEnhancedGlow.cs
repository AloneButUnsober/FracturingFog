using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class DistanceGlowMap : IColorMap
    {
        public static string Name => "Distance Enhanced";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;


        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            float h = (smooth * 0.02f) % 1f;
            float v = MathF.Exp(-distance * 0.1f);
            var c = ColorUtils.Hsv(h, 1f, v);
            return unchecked((int)0xFF000000 | (c.R << 16) | (c.G << 8) | c.B);
        }
    }

}
