using System;
using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Burning Ship: z_{n+1} = (|Re(z)| + i|Im(z)|)² + c.
    /// Take absolute values before squaring. Set lives roughly in
    /// Re∈[-2.5, 1.5], Im∈[-2, 1.5]; conventionally rendered with Im axis
    /// inverted so the "ship" appears upright.
    /// </summary>
    public readonly struct BurningShipKernel : IFractalKernel
    {
        public double BailoutRadius2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => 512.0 * 512.0; }
        public bool HasCardioidSkip { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitState(double cx, double cy, out double zr, out double zi, out double dr, out double di)
        {
            zr = 0; zi = 0; dr = 1; di = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInTrivialInSet(double cx, double cy) => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            double azr = Math.Abs(zr);
            double azi = Math.Abs(zi);
            // Derivative is discontinuous at axes; track the analytic form
            // for points off the axes (good enough for distance/normal).
            double sgnR = zr >= 0 ? 1.0 : -1.0;
            double sgnI = zi >= 0 ? 1.0 : -1.0;
            double newDr = 2.0 * (azr * dr - azi * di) * sgnR + 1.0;
            double newDi = 2.0 * (azr * di + azi * dr) * sgnI;
            dr = newDr; di = newDi;
            double newZr = azr * azr - azi * azi + cx;
            zi = 2.0 * azr * azi + cy;
            zr = newZr;
        }
    }
}
