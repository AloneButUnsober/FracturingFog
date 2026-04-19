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
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            // Cycle so deep-zoom images stay vivid rather than going flat white.
            // Primary cycle: one full grey ramp every ~50 smooth-units.
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;

            // Secondary banding layer for fine detail.
            float band = 0.5f + 0.5f * MathF.Sin(smooth * 0.12f);

            // Mix primary and secondary for contrast at all depths.
            float v = t * 0.75f + band * 0.25f;
            v = System.Math.Clamp(v, 0f, 1f);

            byte c = (byte)(v * 255f);
            return unchecked((int)0xFF000000 | (c << 16) | (c << 8) | c);
        }
    }
}
