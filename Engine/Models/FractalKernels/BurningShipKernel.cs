using System;
using System.Numerics;
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
    public readonly struct BurningShipKernel : ISimdFractalKernel
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
            var negOne = new Vector<double>(-1.0);
            var zero = Vector<double>.Zero;

            var azr = Vector.Abs(zr);
            var azi = Vector.Abs(zi);
            // Per-lane sign(+1 / -1) via ConditionalSelect on >= 0 mask.
            var signMaskR = Vector.GreaterThanOrEqual(zr, zero);
            var signMaskI = Vector.GreaterThanOrEqual(zi, zero);
            var sgnR = Vector.ConditionalSelect(signMaskR, one, negOne);
            var sgnI = Vector.ConditionalSelect(signMaskI, one, negOne);

            var newDr = two * (azr * dr - azi * di) * sgnR + one;
            var newDi = two * (azr * di + azi * dr) * sgnI;
            dr = newDr; di = newDi;
            var newZr = azr * azr - azi * azi + cx;
            zi = two * azr * azi + cy;
            zr = newZr;
        }
    }
}
