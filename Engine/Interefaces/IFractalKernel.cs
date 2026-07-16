// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Numerics;
using System.Runtime.CompilerServices;

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// Marker + SIMD-step interface for kernels that admit a Vector&lt;double&gt;
    /// inner loop. Implementations are pure polynomial in zr/zi (Mandelbrot,
    /// Julia, BurningShip, Tricorn, Multibrot d∈{3,4,5}). Multibrot d≥6 (polar
    /// form) and Phoenix (prev-z memory) fight vectorisation — they stay scalar.
    ///
    /// EscapeTimeCalculator checks the runtime kernel type against this
    /// interface and routes to a SIMD inner loop when supported. JIT
    /// specialises the generic per concrete kernel struct so the type check
    /// is constant-folded.
    /// </summary>
    public interface ISimdFractalKernel : IFractalKernel
    {
        /// <summary>
        /// Per-lane initial state. Mandelbrot family: zr=zi=0, dr=1, di=0.
        /// Julia: zr=cx, zi=cy, dr=1, di=0 (pixel is z0; c is the captured
        /// constant the kernel substitutes in StepSimd).
        /// </summary>
        void InitStateSimd(
            Vector<double> cx, Vector<double> cy,
            out Vector<double> zr, out Vector<double> zi,
            out Vector<double> dr, out Vector<double> di);

        /// <summary>
        /// One iteration step on VecLen lanes. Same algebra as Step but on
        /// Vector&lt;double&gt;. The caller broadcasts c and gathers the
        /// per-lane pixel inputs.
        /// </summary>
        void StepSimd(
            ref Vector<double> zr, ref Vector<double> zi,
            ref Vector<double> dr, ref Vector<double> di,
            Vector<double> cx, Vector<double> cy);
    }

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
