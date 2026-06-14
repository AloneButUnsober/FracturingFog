// FlamePresets.cs
//
// Hand-coded built-in Flame fractal map sets. Each entry is a small (2–6)
// table of (affine + 1-2 variations + colour-index) maps driven through
// the chaos game in FlameRenderer. Weights should sum to 1.0; colour
// indices span [0, 1] (sample positions on the active gradient palette).
//
// Design notes:
//   - Most presets carry a dominant map (weight 0.55–0.85) plus one or two
//     sparse maps. The weight imbalance produces a dense core + faint
//     filament structure — the dynamic range gamma/vibrancy needs to do
//     visible work. A pure equal-weight Sierpinski has a uniform invariant
//     measure: every lit pixel sees the same hit count, log-density is
//     flat, and the entire tone-map collapses to α = 1.
//   - Where a single variation degenerates onto curves (Linear/Heart/Disc
//     applied to a contractive Sierpinski substrate) we blend in a second
//     variation via the Variation2/VariationAmount2 slots to puff the
//     1-D attractor back into a 2-D measure.

using System.Collections.Generic;

namespace FracturingFog.Models
{
    public static class FlamePresets
    {
        public static readonly Dictionary<string, List<FlameMap>> All = new()
        {
            // Linear-only Sierpinski. Identical to the IFS preset but routed
            // through the flame log-density + gamma tone-map. Equal weights
            // intentionally — kept as the "what does a balanced IFS look
            // like through the Flame pipeline" baseline.
            ["Sierpinski Linear"] = new List<FlameMap>
            {
                new(0.5, 0.0, 0.0, 0.5,  0.0,    0.0,    1.0/3, FlameVariation.Linear, 1.0, 0.05),
                new(0.5, 0.0, 0.0, 0.5,  0.5,    0.0,    1.0/3, FlameVariation.Linear, 1.0, 0.50),
                new(0.5, 0.0, 0.0, 0.5,  0.25,   0.433,  1.0/3, FlameVariation.Linear, 1.0, 0.95),
            },

            // Sierpinski substrate with a dominant Sinusoidal-warped leg
            // and two sparse linear legs. The 0.75/0.125/0.125 split
            // produces a bright sinusoidal core + thin linear filaments,
            // so gamma/vibrancy modulate visible structure.
            ["Sierpinski Variation"] = new List<FlameMap>
            {
                new(0.55, 0.05,-0.05, 0.55,  0.0,   0.0,   0.75,
                    FlameVariation.Sinusoidal, 0.85, 0.12,
                    FlameVariation.Linear,     0.15),
                new(0.50, 0.0,  0.0,  0.50,  0.50,  0.0,   0.125, FlameVariation.Linear, 1.0, 0.55),
                new(0.50, 0.0,  0.0,  0.50,  0.25,  0.433, 0.125, FlameVariation.Linear, 1.0, 0.90),
            },

            // Spherical inversion pair. Dominant inverter (0.70) paints
            // the bright halo; secondary inverter (0.30) sweeps the outer
            // filaments. Both maps carry a post-affine rotation so the
            // variation output gets twisted into asymmetric arcs instead
            // of concentric rings.
            //
            // Post-affine = rotation by ±35° (cos=0.819, sin=0.574).
            ["Spherical Pair"] = new List<FlameMap>
            {
                new( 0.75,  0.20, -0.20,  0.75,  0.10,  0.00, 0.70,
                    FlameVariation.Spherical, 0.85, 0.18,
                    FlameVariation.Linear,    0.20,
                    Pa:  0.819, Pb: -0.574, Pc:  0.574, Pd:  0.819,
                    Pe:  0.10,  Pf:  0.00),
                new( 0.55, -0.45,  0.45,  0.55, -0.15,  0.20, 0.30,
                    FlameVariation.Spherical, 1.0, 0.82,
                    Pa:  0.819, Pb:  0.574, Pc: -0.574, Pd:  0.819,
                    Pe: -0.05,  Pf:  0.05),
            },

            // 3-leg gasket with one dominant Swirl-warped leg + two sparse
            // Linear legs. Swirl rotates by r² → tight filaments unwind
            // near the origin and tighten at the rim. Density swing is
            // produced by the 0.70/0.15/0.15 weight bias.
            //
            // Each Linear leg carries a post-affine scale-down (0.85x)
            // and small offset so its sub-attractor lands inside the
            // dominant swirl basin and contributes filament detail to
            // the gasket rather than ghost copies offset to the side.
            ["Swirl Gasket"] = new List<FlameMap>
            {
                new(0.50, 0.10,-0.10, 0.50,  0.00,  0.00, 0.70,
                    FlameVariation.Swirl,  0.80, 0.20,
                    FlameVariation.Linear, 0.30),
                new(0.45, 0.0, 0.0, 0.45,  0.55,  0.00, 0.15,
                    FlameVariation.Linear, 1.0, 0.55,
                    Pa: 0.85, Pb: 0.0, Pc: 0.0, Pd: 0.85,
                    Pe: -0.1, Pf: 0.05),
                new(0.45, 0.0, 0.0, 0.45,  0.28,  0.48, 0.15,
                    FlameVariation.Linear, 1.0, 0.90,
                    Pa: 0.85, Pb: 0.0, Pc: 0.0, Pd: 0.85,
                    Pe: 0.05, Pf: -0.1),
            },

            // Heart-warped Sierpinski. Pure Heart collapses contractive
            // Sierpinski iterates onto thin sinusoidal curves, so each map
            // runs Linear as the dominant variation with a Heart spice on
            // top. The Heart amount stays small (0.15) — enough to bend
            // each Sierpinski sub-triangle into a cusped lobe without
            // losing the 2-D fill measure.
            ["Heart Sierpinski"] = new List<FlameMap>
            {
                new(0.50, 0.08, -0.08, 0.50,  0.0,    0.0,   0.70,
                    FlameVariation.Linear, 1.0,  0.05,
                    FlameVariation.Heart,  0.15),
                new(0.50, 0.0,  0.0,   0.50,  0.50,   0.0,   0.15,
                    FlameVariation.Linear, 1.0,  0.55,
                    FlameVariation.Heart,  0.15),
                new(0.50, 0.0,  0.0,   0.50,  0.25,   0.433, 0.15,
                    FlameVariation.Linear, 1.0,  0.92,
                    FlameVariation.Heart,  0.15),
            },

            // Polar + Julia blend. Polar (0.55) unwraps the angle into a
            // strip — dominant brightness band. Julia (0.30) seeds the
            // square-root branch cusps. Tiny Disc (0.15) speckles the
            // background. Polar's Linear secondary keeps the strip from
            // collapsing onto a thin θ-line.
            //
            // The Polar map's post-affine scales the (θ/π, r-1) strip
            // anisotropically so the Julia cusps land in the same y-band
            // as the Polar strip, producing a single coherent attractor
            // instead of two disjoint clouds.
            ["Polar Julia"] = new List<FlameMap>
            {
                new(0.60,  0.20, -0.20,  0.60,  0.0,   0.0, 0.55,
                    FlameVariation.Polar,  0.85, 0.10,
                    FlameVariation.Linear, 0.20,
                    Pa: 1.2, Pb: 0.0, Pc: 0.0, Pd: 0.7,
                    Pe: 0.0, Pf: 0.3),
                new(0.50, -0.40,  0.40,  0.50,  0.10, -0.10, 0.30,
                    FlameVariation.Julia,  1.0,  0.60,
                    Pa: 0.95, Pb: 0.10, Pc: -0.10, Pd: 0.95,
                    Pe: 0.0,  Pf: 0.0),
                new(0.30,  0.0,   0.0,   0.30, -0.30,  0.30, 0.15,
                    FlameVariation.Disc,   1.0,  0.95),
            },
        };
    }
}
