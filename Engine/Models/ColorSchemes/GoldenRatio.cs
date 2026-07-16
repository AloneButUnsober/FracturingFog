// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class GoldenRatioMap : IColorMap, IGpuHlslPalette
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

        public string HlslPrelude => HlslPaletteHelpers.HsvAndMods;

        public string HlslPaletteBody => @"
    float h0 = in_smooth * 0.61803398875;
    float h = h0 - floor(h0);
    return cg_hsv_to_rgb(h, 0.8, 1.0);
";

        public string PaletteId => "GoldenRatioMap/v1";
    }

}
