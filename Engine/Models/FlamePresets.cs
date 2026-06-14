// FlamePresets.cs
//
// Hand-coded built-in Flame fractal map sets. Each entry is a small (2–6)
// table of (affine + variation + colour-index) maps driven through the
// chaos game in FlameRenderer. Weights should sum to 1.0; colour indices
// span [0, 1] (sample positions on the active gradient palette).

using System.Collections.Generic;

namespace FracturingFog.Models
{
    public static class FlamePresets
    {
        public static readonly Dictionary<string, List<FlameMap>> All = new()
        {
            // Linear-only Sierpinski. Identical to the IFS preset but routed
            // through the flame log-density + gamma tone-map; used as the
            // sanity-check baseline for slice 1.
            ["Sierpinski Linear"] = new List<FlameMap>
            {
                new(0.5, 0.0, 0.0, 0.5,  0.0,    0.0,    1.0/3, FlameVariation.Linear, 1.0, 0.05),
                new(0.5, 0.0, 0.0, 0.5,  0.5,    0.0,    1.0/3, FlameVariation.Linear, 1.0, 0.50),
                new(0.5, 0.0, 0.0, 0.5,  0.25,   0.433,  1.0/3, FlameVariation.Linear, 1.0, 0.95),
            },

            // Sinusoidal-on-Sierpinski. Same affine triangle, swapped to
            // Sinusoidal variation per leg — Apophysis "what's a flame
            // actually doing differently than IFS" introductory shape.
            // Active until slice 2 ships, then auto-promoted by name.
            ["Sierpinski Variation"] = new List<FlameMap>
            {
                new(0.5, 0.0, 0.0, 0.5,  0.0,    0.0,    1.0/3, FlameVariation.Sinusoidal, 1.0, 0.10),
                new(0.5, 0.0, 0.0, 0.5,  0.5,    0.0,    1.0/3, FlameVariation.Sinusoidal, 1.0, 0.55),
                new(0.5, 0.0, 0.0, 0.5,  0.25,   0.433,  1.0/3, FlameVariation.Sinusoidal, 1.0, 0.90),
            },

            // Spherical-warp pair on a contractive rotation. Two-map flame —
            // the smallest possible non-trivial generator.
            ["Spherical Pair"] = new List<FlameMap>
            {
                new( 0.7,  0.3, -0.3,  0.7,  0.1,  0.0, 0.5, FlameVariation.Spherical, 1.0, 0.15),
                new( 0.6, -0.5,  0.5,  0.6, -0.1,  0.2, 0.5, FlameVariation.Spherical, 1.0, 0.80),
            },

            // Swirl on a 3-leg gasket. Swirl rotates by r² → tight filaments
            // unwind near the origin and tighten at the rim.
            ["Swirl Gasket"] = new List<FlameMap>
            {
                new(0.45, 0.10,-0.10, 0.45,  0.00,  0.00, 1.0/3, FlameVariation.Swirl, 1.0, 0.20),
                new(0.45, 0.00, 0.00, 0.45,  0.55,  0.00, 1.0/3, FlameVariation.Swirl, 1.0, 0.55),
                new(0.45,-0.10, 0.10, 0.45,  0.28,  0.48, 1.0/3, FlameVariation.Swirl, 1.0, 0.90),
            },

            // Heart variation atop a Sierpinski substrate — concave cusp at
            // each vertex, classic Apophysis demo shape.
            ["Heart Sierpinski"] = new List<FlameMap>
            {
                new(0.5, 0.0, 0.0, 0.5,  0.0,    0.0,   1.0/3, FlameVariation.Heart, 0.8, 0.05),
                new(0.5, 0.0, 0.0, 0.5,  0.5,    0.0,   1.0/3, FlameVariation.Heart, 0.8, 0.50),
                new(0.5, 0.0, 0.0, 0.5,  0.25,   0.433, 1.0/3, FlameVariation.Heart, 0.8, 0.95),
            },

            // Polar + julia blend. Polar unwraps the angle; julia's
            // square-root branch produces the spiral cusps. The heaviest
            // contractive map sweeps colour from index 0 → 1.
            ["Polar Julia"] = new List<FlameMap>
            {
                new(0.6,  0.2, -0.2,  0.6,  0.0,  0.0, 0.4, FlameVariation.Polar, 1.0, 0.10),
                new(0.5, -0.4,  0.4,  0.5,  0.1, -0.1, 0.4, FlameVariation.Julia, 1.0, 0.60),
                new(0.3,  0.0,  0.0,  0.3, -0.3,  0.3, 0.2, FlameVariation.Disc,  1.0, 0.95),
            },
        };
    }
}
