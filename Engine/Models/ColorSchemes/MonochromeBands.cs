// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class MonoBandMap : IColorMap, IGpuHlslPalette
    {
        public static string Name => "Monochrome Bands";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public int MaxIterations { get; set; } = 1000;

        public int Map(float smooth, float distance, int maxIterations)
        {
            float v = 0.5f + 0.5f * MathF.Sin(smooth * 0.1f);
            byte b = (byte)(v * 255);
            return unchecked((int)0xFF000000 | (b << 16) | (b << 8) | b);
        }

        public string HlslPrelude => string.Empty;

        public string HlslPaletteBody => @"
    float v = 0.5 + 0.5 * sin(in_smooth * 0.1);
    return float3(v, v, v);
";

        public string PaletteId => "MonoBandMap/v1";
    }

}
