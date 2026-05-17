// Models/ColorSchemes/StripeAverageTiaThemes.cs
//
// Stripe Average Coloring (SAC) and Triangle Inequality Average (TIA) — the
// "Ultra Fractal" look made famous by Jussi Härkönen's algorithm.
//
// Both methods sample z at every iteration of z_{n+1} = z_n^2 + c and
// accumulate a running average.  At escape, the average is blended with the
// fractional smooth-iteration count to remove discrete iteration banding,
// then mapped through a gradient.
//
//   Stripe Average:  s_n  = 0.5 + 0.5·sin(density · arg(z_n))
//                    S    = (1/N) · Σ s_n
//
//   Triangle Inequality:
//                    m_n  = | |z_{n-1}^2| − |c| |
//                    M_n  =   |z_{n-1}^2| + |c|
//                    t_n  = (|z_n| − m_n) / (M_n − m_n)
//                    T    = (1/N) · Σ t_n
//
// For Mandelbrot, z_n = z_{n-1}^2 + c, so |z_{n-1}^2| = |z_n − c|.
//
// Three sample themes:
//   • StripeAverageClassicMap — pure stripe, monochrome ramp (Ultra Fractal look)
//   • TriangleInequalityMap   — pure TIA, warm gradient
//   • StripeTiaBlendMap       — equal blend of both, electric purple/teal

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Shared base for stripe / TIA colour maps.  Subclasses tweak weights
    /// (<see cref="StripeWeight"/>, <see cref="TiaWeight"/>), stripe density,
    /// gradient stops and contrast.
    /// </summary>
    public abstract class StripeTiaBaseMap : GradientColorMap, IOrbitAwareColorMap
    {
        /// <summary>
        /// Stripe sin-argument multiplier.  Classic Ultra Fractal default ≈ 7;
        /// higher values yield finer stripe spacing.  Ignored when
        /// <see cref="StripeWeight"/> is zero.
        /// </summary>
        protected virtual double StripeDensity => 7.0;

        /// <summary>Weight applied to the stripe average in the final blend (0..1).</summary>
        protected virtual float StripeWeight => 1f;

        /// <summary>Weight applied to the TIA average in the final blend (0..1).</summary>
        protected virtual float TiaWeight => 0f;

        /// <summary>Contrast multiplier around 0.5.  &gt;1 boosts contrast, &lt;1 softens.</summary>
        protected virtual float Contrast => 1.0f;

        public void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
        }

        public void Sample(ref OrbitAccumulator acc,
                           double zr, double zi,
                           double cr, double ci, int iter)
        {
            if (StripeWeight > 0f)
            {
                double s = 0.5 + 0.5 * Math.Sin(StripeDensity * Math.Atan2(zi, zr));
                acc.LastStripe = s;
                acc.StripeSum += s;
                acc.StripeCount++;
            }

            // TIA requires |z_{n-1}^2| = |z_n − c|, valid only after the first
            // squaring step has produced z_n with a meaningful predecessor.
            if (TiaWeight > 0f && iter >= 2)
            {
                double zMc_r = zr - cr;
                double zMc_i = zi - ci;
                double absZprev2 = Math.Sqrt(zMc_r * zMc_r + zMc_i * zMc_i);
                double absC      = Math.Sqrt(cr * cr + ci * ci);
                double absZ      = Math.Sqrt(zr * zr + zi * zi);
                double m         = Math.Abs(absZprev2 - absC);
                double M         = absZprev2 + absC;
                if (M - m > 1e-12)
                {
                    double t = (absZ - m) / (M - m);
                    acc.LastTia = t;
                    acc.TiaSum += t;
                    acc.TiaCount++;
                }
            }
        }

        public int MapWithOrbit(float smooth, float distance, int iterations,
                                float nx, float ny, in OrbitAccumulator acc)
        {
            // Ultra-Fractal-style fractional smoothing: linearly interpolate
            // between the average including the last sample and the average
            // excluding it, weighted by the fractional part of the smooth
            // iteration count.  Removes discrete banding at iteration boundaries.
            double frac = smooth - Math.Floor(smooth);

            double avgWith  = acc.StripeCount > 0
                            ? acc.StripeSum / acc.StripeCount
                            : 0.0;
            double avgWithout = acc.StripeCount > 1
                            ? (acc.StripeSum - acc.LastStripe) / (acc.StripeCount - 1)
                            : avgWith;
            double stripeMixed = frac * avgWith + (1.0 - frac) * avgWithout;

            double tiaWith    = acc.TiaCount > 0
                            ? acc.TiaSum / acc.TiaCount
                            : 0.0;
            double tiaWithout = acc.TiaCount > 1
                            ? (acc.TiaSum - acc.LastTia) / (acc.TiaCount - 1)
                            : tiaWith;
            double tiaMixed   = frac * tiaWith + (1.0 - frac) * tiaWithout;

            double mix = StripeWeight * stripeMixed + TiaWeight * tiaMixed;
            float totalWeight = StripeWeight + TiaWeight;
            if (totalWeight > 0f) mix /= totalWeight;

            mix = 0.5 + Contrast * (mix - 0.5);
            return MapNormalized((float)System.Math.Clamp(mix, 0.0, 1.0), distance);
        }

        public override int Map(float smooth, float distance, int maxIterations)
        {
            float t = maxIterations > 0 ? (smooth / maxIterations) : 0f;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }
    }

    /// <summary>
    /// Pure Stripe Average colouring, Ultra Fractal style.  Monochrome ramp
    /// emphasises orbit substructure without competing hues.
    /// </summary>
    public sealed class StripeAverageClassicMap : StripeTiaBaseMap
    {
        public static string Name => "Stripe Average — Classic";
        public static string Category => "Stripe / TIA";
        public static string Description =>
            "Pure Stripe Average Coloring (Ultra Fractal look).  Smooth monochrome " +
            "ramp built from sin(7·arg(z_n)) averaged over the orbit.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesStripeAvg |
            ColorMapFeatures.GradientBased | ColorMapFeatures.Perceptual;

        protected override double StripeDensity => 7.0;
        protected override float StripeWeight => 1f;
        protected override float TiaWeight => 0f;

        public StripeAverageClassicMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(10, 10, 15)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(70, 70, 85)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb(150, 145, 135)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb(220, 215, 200)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 235)));
        }
    }

    /// <summary>
    /// Pure Triangle Inequality Average colouring.  Warm gradient highlights
    /// the spiral filament structure unique to TIA.
    /// </summary>
    public sealed class TriangleInequalityMap : StripeTiaBaseMap
    {
        public static string Name => "Triangle Inequality Average";
        public static string Category => "Stripe / TIA";
        public static string Description =>
            "Triangle Inequality Average — the (|z_n|−m)/(M−m) sequence averaged " +
            "over the orbit.  Reveals spiral filament structure invisible to smooth iteration.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesStripeAvg |
            ColorMapFeatures.GradientBased;

        protected override float StripeWeight => 0f;
        protected override float TiaWeight => 1f;
        protected override float Contrast => 1.6f;

        public TriangleInequalityMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(20, 5, 0)));
            Stops.Add(new ColorStop(0.25f, Color.FromArgb(120, 30, 10)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(220, 110, 30)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(255, 210, 100)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255, 250, 220)));
        }
    }

    /// <summary>
    /// Equal blend of Stripe Average and Triangle Inequality.  Electric
    /// purple-to-teal gradient with high contrast.
    /// </summary>
    public sealed class StripeTiaBlendMap : StripeTiaBaseMap
    {
        public static string Name => "Stripe + TIA Blend";
        public static string Category => "Stripe / TIA";
        public static string Description =>
            "50/50 blend of Stripe Average and Triangle Inequality.  Electric purple → " +
            "teal gradient with boosted contrast.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesStripeAvg |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override double StripeDensity => 5.0;
        protected override float StripeWeight => 0.5f;
        protected override float TiaWeight => 0.5f;
        protected override float Contrast => 1.4f;

        public StripeTiaBlendMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(10, 5, 30)));
            Stops.Add(new ColorStop(0.25f, Color.FromArgb(70, 20, 130)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(180, 60, 200)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(80, 220, 220)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(220, 255, 255)));
        }
    }
}
