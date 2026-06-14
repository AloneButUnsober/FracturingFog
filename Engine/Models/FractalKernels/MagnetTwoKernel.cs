using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Magnet 2 (Pickover): cubic-over-quadratic rational map
    ///     num = z³ + 3(c−1)z + (c−1)(c−2)
    ///     den = 3z² + 3(c−2)z + c² − 3c + 3
    ///     z   = (num / den)²
    /// Same pole-clamp + bailout rationale as <see cref="MagnetOneKernel"/>.
    /// Derivative tracking omitted for the same reason.
    /// </summary>
    public readonly struct MagnetTwoKernel : IFractalKernel
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
            // α = c − 1,  β = c − 2,  γ = α·β,  δ = c² − 3c + 3
            double ar = cx - 1.0, ai = cy;
            double br = cx - 2.0, bi = cy;
            double gr_ab = ar * br - ai * bi;
            double gi_ab = ar * bi + ai * br;
            double dr_const = cx * cx - cy * cy - 3.0 * cx + 3.0;
            double di_const = 2.0 * cx * cy - 3.0 * cy;

            // z² and z³
            double z2r = zr * zr - zi * zi;
            double z2i = 2.0 * zr * zi;
            double z3r = z2r * zr - z2i * zi;
            double z3i = z2r * zi + z2i * zr;

            // num = z³ + 3α·z + γ
            double nr = z3r + 3.0 * (ar * zr - ai * zi) + gr_ab;
            double ni = z3i + 3.0 * (ar * zi + ai * zr) + gi_ab;

            // den = 3z² + 3β·z + δ
            double dnr = 3.0 * z2r + 3.0 * (br * zr - bi * zi) + dr_const;
            double dni = 3.0 * z2i + 3.0 * (br * zi + bi * zr) + di_const;

            // Pole clamp.
            double denMag2 = dnr * dnr + dni * dni;
            if (denMag2 < 1e-12) denMag2 = 1e-12;

            // g = num / den
            double inv = 1.0 / denMag2;
            double gr = (nr * dnr + ni * dni) * inv;
            double gi = (ni * dnr - nr * dni) * inv;

            // z = g²
            zr = gr * gr - gi * gi;
            zi = 2.0 * gr * gi;
        }
    }
}
