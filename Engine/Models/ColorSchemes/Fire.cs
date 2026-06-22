using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class FirePalette : IColorMap, IGpuHlslPalette
    {
        public static string Name => "Fire";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int iterations)
        {
            if (smooth >= iterations) return unchecked((int)0xFF000000);

            // Cycle so deep-zoom images stay vivid — one cycle every ~50 smooth-units.
            float t = ((smooth * 0.020f) % 1.0f + 1.0f) % 1.0f;

            // Classic fire ramp: black → deep red → orange → yellow → white
            byte r = (byte)System.Math.Clamp(t * 3.0f * 255f, 0f, 255f);
            byte g = (byte)System.Math.Clamp((t - 0.33f) * 3.0f * 255f, 0f, 255f);
            byte b = (byte)System.Math.Clamp((t - 0.67f) * 3.0f * 255f, 0f, 255f);

            // Intensity ripple for banding detail at all zoom levels.
            float ripple = 0.85f + 0.15f * MathF.Sin(smooth * 0.11f);
            r = (byte)System.Math.Clamp(r * ripple, 0f, 255f);
            g = (byte)System.Math.Clamp(g * ripple, 0f, 255f);
            b = (byte)System.Math.Clamp(b * ripple, 0f, 255f);

            return (255 << 24) | (r << 16) | (g << 8) | b;
        }

        public string HlslPrelude => string.Empty;

        public string HlslPaletteBody => @"
    if (in_isInSet > 0.5) return float3(0.0, 0.0, 0.0);
    float traw = in_smooth * 0.020;
    float t = traw - floor(traw);
    float r = saturate(t * 3.0);
    float g = saturate((t - 0.33) * 3.0);
    float b = saturate((t - 0.67) * 3.0);
    float ripple = 0.85 + 0.15 * sin(in_smooth * 0.11);
    return float3(r, g, b) * ripple;
";

        public string PaletteId => "FirePalette/v1";
    }
}
