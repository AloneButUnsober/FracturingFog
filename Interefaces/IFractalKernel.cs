using System.Runtime.CompilerServices;

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// Per-pixel iteration kernel for an escape-time fractal. Kernels are structs;
    /// generic Calculate&lt;TKernel&gt; methods take them by value so the JIT
    /// specializes the iteration loop with the kernel's Step() inlined.
    /// </summary>
    /// <remarks>
    /// Convention for Step:
    ///   z = (zr, zi), dz/dc = (dr, di), c = (cx, cy).
    ///   For pure Mandelbrot-family escape-time fractals, c is the per-pixel
    ///   coordinate and z0 = 0. Julia-family kernels swap roles internally —
    ///   the caller still passes the pixel as (cx, cy) and the kernel pretends
    ///   it is z0 by using its captured constant in place of c.
    ///
    /// Convention for cardioid skip:
    ///   Only Mandelbrot has trivial closed-form in-set regions. Other kernels
    ///   set HasCardioidSkip=false and IsInTrivialInSet returns false.
    /// </remarks>
    public interface IFractalKernel
    {
        /// <summary>Squared bailout radius. Standard Mandelbrot uses 512² for smooth iteration.</summary>
        double BailoutRadius2 { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; }

        /// <summary>True when IsInTrivialInSet is worth calling. Mandelbrot=true, others=false.</summary>
        bool HasCardioidSkip { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; }

        /// <summary>
        /// Initial (z0, dz/dc-0) values for the iteration. Mandelbrot family: z0=0, dz/dc0=1.
        /// Julia family: z0 = (cx, cy) (the pixel), dz/dc0 = 1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void InitState(double cx, double cy, out double zr, out double zi, out double dr, out double di);

        /// <summary>True if (cx, cy) is in a trivially-in-set closed-form region (skip iteration).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool IsInTrivialInSet(double cx, double cy);

        /// <summary>
        /// One iteration step. Updates z and (where the algebra cleanly admits it)
        /// the complex derivative dz/dc used by distance estimation + normals.
        /// Kernels without a clean closed-form derivative may simply leave dr/di
        /// unchanged; the caller falls back to numerical estimation or omits
        /// distance/normal effects for that fractal.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Step(ref double zr, ref double zi, ref double dr, ref double di, double cx, double cy);
    }
}
