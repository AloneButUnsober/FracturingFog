using System.Numerics;
using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Tricorn / Mandelbar: z_{n+1} = conj(z)² + c = (zr² − zi² + cx, −2·zr·zi + cy).
    /// Same magnitude algebra as Mandelbrot, sign-flipped imaginary update.
    /// </summary>
    public readonly struct TricornKernel : ISimdFractalKernel
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
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            // d/dc of conj(z)² + c is non-analytic; track as if Mandelbrot for
            // a reasonable (though not exact) distance estimate.
            double newDr = 2.0 * (zr * dr - zi * di) + 1.0;
            double newDi = 2.0 * (zr * di + zi * dr);
            dr = newDr; di = newDi;
            double newZr = zr2 - zi2 + cx;
            zi = -2.0 * zr * zi + cy;
            zr = newZr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitStateSimd(
            Vector<double> cx, Vector<double> cy,
            out Vector<double> zr, out Vector<double> zi,
            out Vector<double> dr, out Vector<double> di)
        {
            zr = Vector<double>.Zero;
            zi = Vector<double>.Zero;
            dr = Vector<double>.One;
            di = Vector<double>.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StepSimd(
            ref Vector<double> zr, ref Vector<double> zi,
            ref Vector<double> dr, ref Vector<double> di,
            Vector<double> cx, Vector<double> cy)
        {
            var two = new Vector<double>(2.0);
            var negTwo = new Vector<double>(-2.0);
            var one = Vector<double>.One;
            var zr2 = zr * zr;
            var zi2 = zi * zi;
            var newDr = two * (zr * dr - zi * di) + one;
            var newDi = two * (zr * di + zi * dr);
            dr = newDr; di = newDi;
            var newZr = zr2 - zi2 + cx;
            zi = negTwo * zr * zi + cy;
            zr = newZr;
        }
    }
}
