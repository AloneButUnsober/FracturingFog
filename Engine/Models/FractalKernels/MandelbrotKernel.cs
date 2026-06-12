using System.Numerics;
using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Standard Mandelbrot: z_{n+1} = z² + c. Mirrors the existing inner loop
    /// in MandelbrotCalculator.cs. Provided here so the generic EscapeTimeCalculator
    /// can exercise the kernel path uniformly. Production Mandelbrot rendering
    /// still routes to MandelbrotCalculator.cs for the SIMD/PT/SA/BLA pipeline.
    /// </summary>
    public readonly struct MandelbrotKernel : ISimdFractalKernel
    {
        public double BailoutRadius2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => 512.0 * 512.0; }
        public bool HasCardioidSkip { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => true; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InitState(double cx, double cy, out double zr, out double zi, out double dr, out double di)
        {
            zr = 0; zi = 0; dr = 1; di = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInTrivialInSet(double cx, double cy)
        {
            double bx = cx + 1.0;
            if (bx * bx + cy * cy <= 0.0625) return true;
            double xm = cx - 0.25;
            double q = xm * xm + cy * cy;
            return q * (q + xm) <= 0.25 * cy * cy;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Step(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy)
        {
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            double newDr = 2.0 * (zr * dr - zi * di) + 1.0;
            double newDi = 2.0 * (zr * di + zi * dr);
            dr = newDr; di = newDi;
            double newZr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
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
            var one = Vector<double>.One;
            var zr2 = zr * zr;
            var zi2 = zi * zi;
            var newDr = two * (zr * dr - zi * di) + one;
            var newDi = two * (zr * di + zi * dr);
            dr = newDr; di = newDi;
            var newZr = zr2 - zi2 + cx;
            zi = two * zr * zi + cy;
            zr = newZr;
        }
    }
}
