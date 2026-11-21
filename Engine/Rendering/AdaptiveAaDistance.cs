// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/AdaptiveAaDistance.cs
//
// #1981 part 2 — the distance-estimate refinement of the adaptive AA mask (idea: pauldelbrot,
// fractalforums.org topic 5693, Docs/Resources-Bibliography.md → pauldelbrot-early-outs-5693): a
// pixel whose distance estimate puts the nearest boundary further than its centre-to-corner
// distance (√2/2 px) holds no boundary, so its sub-samples need not be taken. The neighbour test
// (Supersampling.BuildMask) marks such pixels on steep colour bands; this clears them.
//
// The bounds here are LOWER bounds on the true distance, so a cleared pixel really has no boundary:
//  • exterior (Mandelbrot, connected Julia sets, Multibrot): with Green's function
//    G = ln|z_n| / dⁿ and D = |z_n| ln|z_n| / |z_n′| = G / |G′| (the frame's DistanceBuffer),
//    the Koebe quarter theorem applied to the exterior's Böttcher map gives
//    dist > sinh G / (2 e^G |G′|) = D · (1 − e^{−2G}) / (4G) → D / 2 as G → 0 (bibliography →
//    distance-bound-koebe, details awaiting verification). For a disconnected Julia set the
//    Böttcher map does not cover the exterior and the bound is a heuristic.
//  • rotation domains (#1981 part 1): the |f′| / |f″| estimate came out 1.85–3.1× the measured
//    distance on the golden-mean Siegel disk (S1981RotationDomainTests), so a quarter of it is
//    used — an empirical margin, not a theorem.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Rendering
{
    public static class AdaptiveAaDistance
    {
        /// <summary>The rotation-domain estimate's safety factor (measured ≤ 3.1×, see header).</summary>
        public const double RotationFactor = 0.25;

        /// <summary>Exterior lower bound in pixels per pixel (0 for in-set / unknown pixels).
        /// <paramref name="dist"/> and <paramref name="pixelScale"/> share one unit;
        /// <paramref name="degree"/> is the map's degree d.</summary>
        public static float[] Exterior(int[] iter, int maxIter, float[] dist, double pixelScale,
                                       float[] fzr, float[] fzi, double degree)
        {
            int n = Math.Min(Math.Min(iter.Length, dist.Length), Math.Min(fzr.Length, fzi.Length));
            var o = new float[iter.Length];
            if (!(pixelScale > 0) || degree < 2) return o;
            double lnD = Math.Log(degree);
            Parallel.For(0, n, i =>
            {
                double d = dist[i];
                if (iter[i] >= maxIter || !(d > 0) || !double.IsFinite(d)) return;
                double mag = Math.Sqrt((double)fzr[i] * fzr[i] + (double)fzi[i] * fzi[i]);
                if (!(mag > 1)) return;
                double g = Math.Exp(Math.Log(Math.Log(mag)) - iter[i] * lnD);   // G = ln|z_n| / dⁿ
                double f = g < 1e-9 ? 0.5 : (1 - Math.Exp(-2 * g)) / (4 * g);
                o[i] = (float)(d * f / pixelScale);
            });
            return o;
        }

        /// <summary>Raise <paramref name="bound"/> with the rotation-domain distances
        /// (pixels, 0 = none) times <see cref="RotationFactor"/>.</summary>
        public static void MergeRotation(float[] bound, float[]? rotationPx)
        {
            if (rotationPx == null) return;
            int n = Math.Min(bound.Length, rotationPx.Length);
            for (int i = 0; i < n; i++)
            {
                float r = (float)(rotationPx[i] * RotationFactor);
                if (r > bound[i]) bound[i] = r;
            }
        }
    }
}
