// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>
    /// Hand-coded affine-map sets for classical IFS fractals. Each entry's
    /// weight is the per-iteration probability of picking that map in the
    /// chaos game; weights should sum to 1.0 per preset.
    /// </summary>
    public static class IFSPresets
    {
        public static readonly Dictionary<string, List<AffineMap>> All = new()
        {
            ["Sierpinski Triangle"] = new List<AffineMap>
            {
                new(0.5, 0.0, 0.0, 0.5, 0.0, 0.0, 1.0 / 3),
                new(0.5, 0.0, 0.0, 0.5, 0.5, 0.0, 1.0 / 3),
                new(0.5, 0.0, 0.0, 0.5, 0.25, 0.433, 1.0 / 3),
            },

            ["Sierpinski Carpet"] = BuildSierpinskiCarpet(),

            ["Barnsley Fern"] = new List<AffineMap>
            {
                new(0.0,   0.0,   0.0,   0.16,  0.0,  0.0, 0.01),
                new(0.85,  0.04, -0.04,  0.85,  0.0,  1.6, 0.85),
                new(0.20, -0.26,  0.23,  0.22,  0.0,  1.6, 0.07),
                new(-0.15, 0.28,  0.26,  0.24,  0.0,  0.44, 0.07),
            },

            ["Heighway Dragon"] = new List<AffineMap>
            {
                new(0.5, -0.5, 0.5, 0.5, 0.0, 0.0, 0.5),
                new(-0.5, -0.5, 0.5, -0.5, 1.0, 0.0, 0.5),
            },

            ["Koch Curve"] = new List<AffineMap>
            {
                new(1.0/3, 0.0,   0.0, 1.0/3,  0.0,            0.0, 0.25),
                new(1.0/6, -0.289, 0.289, 1.0/6, 1.0/3,        0.0, 0.25),
                new(1.0/6,  0.289, -0.289, 1.0/6, 0.5,         0.289, 0.25),
                new(1.0/3, 0.0,   0.0, 1.0/3,   2.0/3,         0.0, 0.25),
            },

            ["Pythagoras Tree"] = new List<AffineMap>
            {
                new(0.6, 0.0, 0.0, 0.6, 0.0, 0.0, 0.4),
                new(0.42426, -0.42426, 0.42426, 0.42426, 0.0, 0.6, 0.3),
                new(0.42426, 0.42426, -0.42426, 0.42426, 0.6, 0.6, 0.3),
            },
        };

        private static List<AffineMap> BuildSierpinskiCarpet()
        {
            // 8 maps: copies at the 8 surrounding cells of a 3×3 grid (center removed).
            var list = new List<AffineMap>(8);
            double w = 1.0 / 8.0;
            for (int ry = 0; ry < 3; ry++)
            {
                for (int rx = 0; rx < 3; rx++)
                {
                    if (rx == 1 && ry == 1) continue;
                    list.Add(new AffineMap(1.0/3, 0, 0, 1.0/3, rx / 3.0, ry / 3.0, w));
                }
            }
            return list;
        }
    }
}
