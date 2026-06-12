using System;
using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Multibrot: z_{n+1} = z^d + c, integer exponent d ≥ 2.
    /// Implemented via polar conversion (r^d, d·θ) which is cleaner than
    /// repeated complex multiplication for d > 2.
    /// </summary>
    public readonly struct MultibrotKernel : IFractalKernel
    {
        private readonly int _d;

        public MultibrotKernel(int d) { _d = d < 2 ? 2 : d; }

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
            // z' = z^d + c via polar form.
            double r2 = zr * zr + zi * zi;
            if (r2 == 0.0)
            {
                zr = cx; zi = cy;
                // derivative degenerate at origin; leave dr/di as is
                return;
            }
            double r = Math.Sqrt(r2);
            double theta = Math.Atan2(zi, zr);
            double rd = Math.Pow(r, _d);
            double td = _d * theta;
            double newZr = rd * Math.Cos(td) + cx;
            double newZi = rd * Math.Sin(td) + cy;

            // dz/dc evolves as d·z^(d-1)·dz/dc + 1
            double rdm1 = Math.Pow(r, _d - 1);
            double tdm1 = (_d - 1) * theta;
            double pr = _d * rdm1 * Math.Cos(tdm1);
            double pi = _d * rdm1 * Math.Sin(tdm1);
            double newDr = pr * dr - pi * di + 1.0;
            double newDi = pr * di + pi * dr;

            zr = newZr; zi = newZi;
            dr = newDr; di = newDi;
        }
    }
}
