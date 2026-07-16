// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class Painted : IColorMap, IGpuHlslPalette
    {
        public static string Name => "Painted";

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

        public string HlslPrelude => HlslPaletteHelpers.HsvAndMods;

        public string HlslPaletteBody => @"
    float h0 = cg_mods(in_smooth * 0.05, 1.1);
    float hue = h0 - floor(h0);
    float baseV = (in_isInSet > 0.5) ? -0.01 : 1.0;
    float lightness = 1.35 - min(in_dist * 0.04, 1.0);
    return cg_hsv_to_rgb(hue, 0.9, baseV * lightness);
";

        public string PaletteId => "Painted/v1";
    }
}
