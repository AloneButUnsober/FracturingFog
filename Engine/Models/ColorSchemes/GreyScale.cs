// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class GrayscalePalette : IColorMap, IGpuHlslPalette
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

        public string HlslPrelude => string.Empty;

        public string HlslPaletteBody => @"
    if (in_isInSet > 0.5) return float3(0.0, 0.0, 0.0);
    float traw = in_smooth * 0.020;
    float t = traw - floor(traw);
    float band = 0.5 + 0.5 * sin(in_smooth * 0.12);
    float v = saturate(t * 0.75 + band * 0.25);
    return float3(v, v, v);
";

        public string PaletteId => "GrayscalePalette/v1";
    }
}
