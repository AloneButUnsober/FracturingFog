using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Phoenix: z_{n+1} = z_n² + c + p · z_{n-1}.
    /// Two-step memory. Distance/derivative tracking not implemented (would
    /// require carrying two derivative limbs); leave dr/di unchanged.
    /// Step() requires access to the previous z, which is not part of the
    /// IFractalKernel state contract — the calculator path for Phoenix uses a
    /// dedicated loop helper rather than the generic stepper. Step() here is
    /// provided for completeness but the generic core uses StepWithPrev below.
    /// </summary>
    public readonly struct PhoenixKernel : IFractalKernel
    {
        private readonly double _pr;
        private readonly double _pi;

        public PhoenixKernel(double pr, double pi) { _pr = pr; _pi = pi; }

        public double PR => _pr;
        public double PI => _pi;

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
            // Single-step Phoenix without prev: degenerates to z² + c.
            // Real Phoenix iteration uses StepWithPrev (see EscapeTimeCalculator).
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            double newZr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = newZr;
        }

        /// <summary>
        /// True Phoenix step. Updates (zr, zi) and rotates (prevZr, prevZi) ← (oldZr, oldZi).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StepWithPrev(ref double zr, ref double zi, ref double prevZr, ref double prevZi, double cx, double cy)
        {
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            // p · prev
            double pPrevR = _pr * prevZr - _pi * prevZi;
            double pPrevI = _pr * prevZi + _pi * prevZr;
            double oldZr = zr;
            double oldZi = zi;
            double newZr = zr2 - zi2 + cx + pPrevR;
            double newZi = 2.0 * zr * zi + cy + pPrevI;
            prevZr = oldZr; prevZi = oldZi;
            zr = newZr; zi = newZi;
        }
    }
}
