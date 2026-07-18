// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Interefaces/IVectorColorMap.cs
//
// Opt-in SIMD batched colour mapping. Themes implementing this interface
// receive four pixels' worth of per-pixel state packed into Vector128<float>
// lanes and return four BGRA packed colours in a single Vector128<int>.
//
// Calculator paths check for this interface at the start of each 4-pixel
// SIMD block and dispatch to MapV() when available, falling back to the
// scalar Map() loop otherwise. JIT generic specialisation lets the interface
// check elide entirely when TMap is a concrete type known to implement it.
//
// Vector layout — 4 pixels per call (matches the AVX2 double-lane width):
//   lane 0 = pixel x+0, lane 1 = pixel x+1, lane 2 = pixel x+2, lane 3 = pixel x+3
//
// The vectorised path is purely a performance optimisation; output BGRA
// values must match Map()'s output exactly (no precision loss permitted).

using System.Runtime.Intrinsics;

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// Colour map that supports 4-pixel SIMD-batched mapping. Output is a
    /// Vector128&lt;int&gt; of packed ARGB values matching IColorMap.Map().
    /// </summary>
    public interface IVectorColorMap : IColorMap
    {
        /// <summary>
        /// Map four pixels of fractal sample data to four packed BGRA colours.
        /// Lane i corresponds to pixel x+i. Output lane i corresponds to the
        /// same packed-ARGB int that scalar Map() would have returned.
        /// </summary>
        Vector128<int> MapV(
            Vector128<float> smooth, Vector128<float> distance, int iterations,
            Vector128<float> nx, Vector128<float> ny,
            Vector128<float> finalZr, Vector128<float> finalZi,
            Vector128<float> dzdcR, Vector128<float> dzdcI);
    }
}
