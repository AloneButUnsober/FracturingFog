using System.Runtime.CompilerServices;

using FracturingFog.Interefaces;

namespace FracturingFog.Models.FractalKernels
{
    /// <summary>
    /// Spider fractal: two-state recurrence
    ///     z  = z² + c
    ///     c  = decay · c + z
    /// where decay ∈ [0, 1] controls how much of the previous c survives.
    /// Decay = 1 degenerates to standard Mandelbrot (c never mutates);
    /// decay = 0.5 is the canonical Spider; decay = 0 collapses c onto z
    /// each step.
    ///
    /// c mutates per iteration — that is NOT part of the standard
    /// <see cref="IFractalKernel.Step"/> contract (Step takes c by
    /// value), so SpiderKernel exposes a dedicated <see cref="StepMutatingC"/>
    /// overload and EscapeTimeCalculator routes Spider through its own
    /// loop (<c>CalculateSpider</c>) the same way Phoenix routes through
    /// <c>CalculatePhoenix</c> for its prev-z carry. Step() is implemented
    /// for interface compliance but only runs the z² + c half — calling it
    /// directly degenerates to Mandelbrot.
    /// </summary>
    public readonly struct SpiderKernel : IFractalKernel
    {
        private readonly double _decay;

        public SpiderKernel(double decay) { _decay = decay; }

        public double Decay => _decay;

        public double BailoutRadius2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => 512.0 * 512.0; }
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
            // Degenerate Mandelbrot step — Spider's c mutation needs the
            // dedicated CalculateSpider path. See class summary.
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            double newZr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = newZr;
        }

        /// <summary>
        /// True Spider step. Updates (zr, zi) via z² + c and mutates
        /// (cx, cy) via decay · c + z. Caller carries cx/cy state per pixel.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void StepMutatingC(ref double zr, ref double zi, ref double cx, ref double cy)
        {
            double zr2 = zr * zr;
            double zi2 = zi * zi;
            double newZr = zr2 - zi2 + cx;
            double newZi = 2.0 * zr * zi + cy;
            cx = _decay * cx + newZr;
            cy = _decay * cy + newZi;
            zr = newZr;
            zi = newZi;
        }
    }
}
