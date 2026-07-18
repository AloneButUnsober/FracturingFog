// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/OrbitAverageThemes.cs
//
// Statistical averaging colour maps that sample z at every orbit step and
// reduce the sequence to a single normalised value mapped through a gradient.
//
//   • Curvature Average        — mean |Δ arg(seg_n)| between successive segments
//   • Lyapunov Exponent        — mean log|f'(z_n)| = mean log|2 z_n|
//   • Gaussian Integer Trap    — mean distance from z_n to nearest Gaussian integer
//   • Exponential Smoothing    — mean of e^{−|z_n|}  (Kerry Mitchell)
//
// All themes implement IOrbitAwareColorMap and route through PATH C of the
// calculator (scalar SP).  Deep zoom is supported only at the precision of
// PATH C — Lyapunov is the friendliest of the four for deep zoom because its
// magnitude grows with iteration count and remains stable under perturbation.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    // =========================================================================
    // Curvature average
    // =========================================================================

    /// <summary>
    /// Accumulates the absolute angle change between successive orbit segments
    /// <c>seg_n = z_n − z_{n−1}</c>.  Reveals swirls and spiral substructure
    /// that smooth iteration alone cannot expose.
    /// </summary>
    public sealed class CurvatureAverageMap : GradientColorMap, IOrbitAwareColorMap
    {
        public static string Name => "Curvature Average";
        public static string Category => "Statistical";
        public static string Description =>
            "Accumulates absolute angle change between successive orbit segments. " +
            "Reveals spiral substructure invisible to smooth iteration count.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesStripeAvg |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Perceptual;

        /// <summary>
        /// Divisor used to normalise the mean angle change.  π → values in
        /// [0,1] for orbits that fully reverse on average; tweak per gradient.
        /// </summary>
        private const double AngleScale = Math.PI;

        public CurvatureAverageMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(8, 12, 30)));
            Stops.Add(new ColorStop(0.25f, Color.FromArgb(60, 90, 180)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(180, 210, 230)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(240, 200, 90)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 240, 200)));
        }

        public void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
        }

        public void Sample(ref OrbitAccumulator acc,
                           double zr, double zi,
                           double cr, double ci, int iter)
        {
            // State machine driven by per-orbit call index.
            // iter==1: record z_1 as PrevZ; no segment yet.
            // iter==2: build first segment from z_2 − z_1; store as PrevSeg.
            // iter>=3: build new segment, compare to PrevSeg, accumulate |Δθ|.
            if (iter == 1)
            {
                acc.PrevZr = zr; acc.PrevZi = zi;
                return;
            }

            double segR = zr - acc.PrevZr;
            double segI = zi - acc.PrevZi;

            if (iter == 2)
            {
                acc.PrevSegR = segR; acc.PrevSegI = segI;
                acc.PrevZr = zr; acc.PrevZi = zi;
                return;
            }

            // Angle between PrevSeg and seg = atan2(cross, dot).
            double cross = acc.PrevSegR * segI - acc.PrevSegI * segR;
            double dot   = acc.PrevSegR * segR + acc.PrevSegI * segI;
            if (cross != 0.0 || dot != 0.0)
            {
                acc.CurvatureSum += Math.Abs(Math.Atan2(cross, dot));
                acc.CurvatureCount++;
            }

            acc.PrevSegR = segR; acc.PrevSegI = segI;
            acc.PrevZr   = zr;   acc.PrevZi   = zi;
        }

        public int MapWithOrbit(float smooth, float distance, int iterations,
                                float nx, float ny, in OrbitAccumulator acc)
        {
            double mean = acc.CurvatureCount > 0
                ? acc.CurvatureSum / acc.CurvatureCount
                : 0.0;
            float t = (float)System.Math.Clamp(mean / AngleScale, 0.0, 1.0);
            return MapNormalized(t, distance);
        }

        public override int Map(float smooth, float distance, int maxIterations)
        {
            float t = maxIterations > 0 ? smooth / maxIterations : 0f;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }
    }

    // =========================================================================
    // Lyapunov exponent
    // =========================================================================

    /// <summary>
    /// Mean of <c>log|f'(z_n)| = log|2 z_n|</c> over the orbit.  Positive for
    /// strongly-divergent orbits, near zero at the boundary.  Stays informative
    /// at extreme zoom because the magnitude grows roughly linearly with iter.
    /// </summary>
    public sealed class LyapunovExponentMap : GradientColorMap, IOrbitAwareColorMap
    {
        public static string Name => "Lyapunov Exponent";
        public static string Category => "Statistical";
        public static string Description =>
            "Average log|f'(z_n)| = log|2 z_n| along the orbit.  Measures local divergence " +
            "rate.  Deep-zoom friendly — magnitude grows with iteration and remains stable.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesStripeAvg |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Perceptual;

        /// <summary>Range divisor: mean exponents are mapped t = (mean − Min)/(Max − Min).</summary>
        private const double MinExp = -1.0;
        private const double MaxExp =  4.5;

        public LyapunovExponentMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(5, 5, 25)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(50, 30, 110)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(180, 60, 100)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(245, 165, 60)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(250, 235, 180)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 255, 240)));
        }

        public void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
        }

        public void Sample(ref OrbitAccumulator acc,
                           double zr, double zi,
                           double cr, double ci, int iter)
        {
            double absZ = Math.Sqrt(zr * zr + zi * zi);
            if (absZ > 1e-12)
            {
                acc.LyapunovSum += Math.Log(2.0 * absZ);
                acc.LyapunovCount++;
            }
        }

        public int MapWithOrbit(float smooth, float distance, int iterations,
                                float nx, float ny, in OrbitAccumulator acc)
        {
            double mean = acc.LyapunovCount > 0
                ? acc.LyapunovSum / acc.LyapunovCount
                : 0.0;
            double range = MaxExp - MinExp;
            float t = range > 1e-12
                ? (float)System.Math.Clamp((mean - MinExp) / range, 0.0, 1.0)
                : 0f;
            return MapNormalized(t, distance);
        }

        public override int Map(float smooth, float distance, int maxIterations)
        {
            float t = maxIterations > 0 ? smooth / maxIterations : 0f;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }
    }

    // =========================================================================
    // Gaussian integer trap
    // =========================================================================

    /// <summary>
    /// Mean distance from each orbit point to the nearest Gaussian integer
    /// <c>(round(Re z), round(Im z))</c>.  Produces a regular lattice-like
    /// modulation across the iteration field.
    /// </summary>
    public sealed class GaussianIntegerMap : GradientColorMap, IOrbitAwareColorMap
    {
        public static string Name => "Gaussian Integer";
        public static string Category => "Statistical";
        public static string Description =>
            "Mean distance from each orbit point to the nearest Gaussian integer. " +
            "Produces a regular lattice modulation through the iteration field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesStripeAvg |
            ColorMapFeatures.GradientBased;

        public GaussianIntegerMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(20, 30, 40)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(70, 130, 140)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb(150, 210, 210)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb(230, 240, 200)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 230)));
        }

        public void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
        }

        public void Sample(ref OrbitAccumulator acc,
                           double zr, double zi,
                           double cr, double ci, int iter)
        {
            double dr = zr - Math.Round(zr);
            double di = zi - Math.Round(zi);
            acc.GaussianSum += Math.Sqrt(dr * dr + di * di);
            acc.GaussianCount++;
        }

        public int MapWithOrbit(float smooth, float distance, int iterations,
                                float nx, float ny, in OrbitAccumulator acc)
        {
            // Mean distance is bounded by √2 / 2 ≈ 0.707 (cell diagonal radius);
            // multiply by √2 to fully stretch across [0, 1].
            double mean = acc.GaussianCount > 0
                ? acc.GaussianSum / acc.GaussianCount
                : 0.0;
            float t = (float)System.Math.Clamp(mean * Math.Sqrt(2.0), 0.0, 1.0);
            return MapNormalized(t, distance);
        }

        public override int Map(float smooth, float distance, int maxIterations)
        {
            float t = maxIterations > 0 ? smooth / maxIterations : 0f;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }
    }

    // =========================================================================
    // Exponential smoothing (Kerry Mitchell)
    // =========================================================================

    /// <summary>
    /// Mean of <c>e^{−|z_n|}</c> along the orbit.  Strongly weights orbits that
    /// linger near the origin before escape; produces soft glowy filaments.
    /// </summary>
    public sealed class ExponentialSmoothingMap : GradientColorMap, IOrbitAwareColorMap
    {
        public static string Name => "Exponential Smoothing";
        public static string Category => "Statistical";
        public static string Description =>
            "Mean of e^{−|z_n|} along the orbit (Kerry Mitchell).  Strongly weights orbits " +
            "that linger near the origin.  Soft glowy filaments.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesStripeAvg |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Perceptual;

        /// <summary>Contrast multiplier around 0.5.</summary>
        private const float Contrast = 1.5f;

        public ExponentialSmoothingMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(2, 2, 8)));
            Stops.Add(new ColorStop(0.25f, Color.FromArgb(40, 25, 80)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(180, 100, 60)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(255, 200, 110)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 220)));
        }

        public void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
        }

        public void Sample(ref OrbitAccumulator acc,
                           double zr, double zi,
                           double cr, double ci, int iter)
        {
            double absZ = Math.Sqrt(zr * zr + zi * zi);
            acc.ExpSum += Math.Exp(-absZ);
            acc.ExpCount++;
        }

        public int MapWithOrbit(float smooth, float distance, int iterations,
                                float nx, float ny, in OrbitAccumulator acc)
        {
            double mean = acc.ExpCount > 0 ? acc.ExpSum / acc.ExpCount : 0.0;
            // e^{−|z|} ∈ (0, 1].  Apply contrast curve around 0.5.
            double t = 0.5 + Contrast * (mean - 0.5);
            return MapNormalized((float)System.Math.Clamp(t, 0.0, 1.0), distance);
        }

        public override int Map(float smooth, float distance, int maxIterations)
        {
            float t = maxIterations > 0 ? smooth / maxIterations : 0f;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }
    }
}
