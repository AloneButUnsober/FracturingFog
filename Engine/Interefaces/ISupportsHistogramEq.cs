// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Interefaces/ISupportsHistogramEq.cs
//
// Capability marker for calculators that support the "Adaptive" histogram-
// equalization post-pass (#144 / #145). Kept off IFractalCalculator so the
// non-escape-time families (IFS, L-system, raymarch 3D, Monte-Carlo density,
// …) don't have to stub a meaningless method — the render host / poster /
// batch paths pattern-match `calc is ISupportsHistogramEq` and skip HE for
// everything else.
//
// The method surface matches the long-standing MandelbrotCalculator signatures
// so that calculator satisfies the interface without new members; the shared
// implementation lives in HistogramEqualizer.

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// An escape-time calculator whose ColorBuffer can be recoloured through a
    /// rank-order histogram equalization of its smooth-iteration distribution.
    /// See <c>HistogramEqualizer</c> for the shared core.
    /// </summary>
    public interface ISupportsHistogramEq
    {
        /// <summary>
        /// Builds the equalization CDF for the current buffers without applying
        /// it. Returns false (zeroed outputs) when the view has no escaped
        /// pixels — treat as the identity case. Lets the video path lock the CDF
        /// for a leg so per-frame mapping doesn't flicker as statistics drift.
        /// </summary>
        bool BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter);

        /// <summary>Builds the CDF for the current frame and applies it at
        /// <paramref name="strength"/>. Falls back to leaving the
        /// Calculate-coloured buffer untouched when there are no escaped
        /// pixels.</summary>
        void ApplyHistogramEqualization(double strength);

        /// <summary>Applies a previously-built CDF at <paramref name="strength"/>.</summary>
        void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength);

        /// <summary>
        /// As above, plus a stable per-pixel band-edge dither, and reports the
        /// escaped / saturated pixel counts so a locked CDF that has drifted out
        /// of range can be detected and rebuilt.
        /// </summary>
        void ApplyHistogramEqualizationWithCdf(
            double[] cdf, int bins, int sourceMaxIter, double strength, double ditherIterStrength,
            out long escapedCount, out long saturatedCount);
    }
}
