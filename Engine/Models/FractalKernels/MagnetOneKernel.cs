// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Magnet 1 (Pickover): z = ((z² + c − 1) / (2z + c − 2))². Rational map
    /// with a removable singularity along the curve 2z + c − 2 = 0; the Step
    /// floors |den|² so a near-pole pixel produces a finite bailout rather
    /// than NaN. Bailout is 10² (not 2²) because the orbit grows linearly
    /// near attractors, so the standard Mandelbrot radius would trap many
    /// escaping points. Derivative tracking is intentionally omitted —
    /// quotient-rule dz/dc is well-defined but expensive and the family's
    /// visual interest is the in-set basin around the fixed point z = 1
    /// rather than DE-driven exterior detail. Distance + normal themes fall
    /// back to flat exterior; smooth-iter themes work as on any escape-time
    /// family.
    /// </summary>
    public readonly struct MagnetOneKernel : IFractalKernel
    {
        public double BailoutRadius2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => 100.0; }
        public bool HasCardioidSkip { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitState(double cx, double cy, out double zr, out double zi, out double dr, out double di)
        {
            zr = 0; zi = 0; dr = 0; di = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInTrivialInSet(double cx, double cy) => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            // num = z² + c − 1
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            double nr = zr2 - zi2 + cx - 1.0;
            double ni = 2.0 * zr * zi + cy;

            // den = 2z + c − 2
            double dnr = 2.0 * zr + cx - 2.0;
            double dni = 2.0 * zi + cy;

            // Pole clamp — see class summary.
            double denMag2 = dnr * dnr + dni * dni;
            if (denMag2 < 1e-12) denMag2 = 1e-12;

            // g = num / den  via  num · conj(den) / |den|²
            double inv = 1.0 / denMag2;
            double gr = (nr * dnr + ni * dni) * inv;
            double gi = (ni * dnr - nr * dni) * inv;

            // z = g²
            zr = gr * gr - gi * gi;
            zi = 2.0 * gr * gi;
        }
    }
}
