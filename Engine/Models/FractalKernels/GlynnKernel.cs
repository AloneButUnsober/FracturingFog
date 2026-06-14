using System;
using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Glynn fractal: Julia-style iteration of z → z^1.5 + c at the canonical
    /// constant c ≈ −0.2. Power is evaluated in polar form to avoid the
    /// branch-cut ambiguity of complex log; r = 0 is treated as a fixed
    /// point (z^1.5 = 0) so the origin does not produce NaN.
    ///
    /// Pixel maps to z₀ (Julia convention); the constant c is captured
    /// here. Derivative tracking is omitted — fractional-power dz/dz₀
    /// has the same branch-cut problem as the forward map and the
    /// family's interest is the dendritic in-set basin, not exterior DE.
    /// </summary>
    public readonly struct GlynnKernel : IFractalKernel
    {
        private readonly double _cr;
        private readonly double _ci;

        public GlynnKernel(double cr, double ci) { _cr = cr; _ci = ci; }

        public double BailoutRadius2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => 4.0; }
        public bool HasCardioidSkip { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitState(double cx, double cy, out double zr, out double zi, out double dr, out double di)
        {
            zr = cx; zi = cy; dr = 0; di = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInTrivialInSet(double cx, double cy) => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            double r2 = zr * zr + zi * zi;
            if (r2 < 1e-300)
            {
                // z^1.5 at the origin = 0 — orbit reseeds to c.
                zr = _cr;
                zi = _ci;
                return;
            }
            double r = Math.Sqrt(r2);
            double theta = Math.Atan2(zi, zr);
            double r15 = r * Math.Sqrt(r); // r^1.5
            double phi = 1.5 * theta;
            zr = r15 * Math.Cos(phi) + _cr;
            zi = r15 * Math.Sin(phi) + _ci;
        }
    }
}
