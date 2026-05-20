using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Julia set: z_{n+1} = z² + c0, where c0 is fixed and z0 is the pixel.
    /// The caller passes the pixel coordinates as (cx, cy); InitState uses them
    /// as z0 and the kernel substitutes its captured constant for c during Step.
    /// </summary>
    public readonly struct JuliaKernel : IFractalKernel
    {
        private readonly double _cr;
        private readonly double _ci;

        public JuliaKernel(double cr, double ci) { _cr = cr; _ci = ci; }

        public double BailoutRadius2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => 512.0 * 512.0; }
        public bool HasCardioidSkip { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => false; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitState(double cx, double cy, out double zr, out double zi, out double dr, out double di)
        {
            zr = cx; zi = cy; dr = 1; di = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInTrivialInSet(double cx, double cy) => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            // dz/dz0 evolves as 2 z · dz/dz0 (no +1 — c is constant w.r.t. z0).
            double newDr = 2.0 * (zr * dr - zi * di);
            double newDi = 2.0 * (zr * di + zi * dr);
            dr = newDr; di = newDi;
            double newZr = zr2 - zi2 + _cr;
            zi = 2.0 * zr * zi + _ci;
            zr = newZr;
        }
    }
}
