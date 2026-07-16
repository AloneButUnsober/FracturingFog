// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/OrbitTrapThemes.cs
//
// Orbit-trap colourings.  Per iteration, the calculator records the minimum
// distance from z_n to a theme-defined trap shape (point, axis cross, circle).
// The escape colour comes from that minimum, mapped through a gradient.
//
// Implements IOrbitAwareColorMap so the calculator routes these themes through
// the dedicated scalar SP path (see MandelbrotCalculator.CalculateOrbitAware).
//
// Themes:
//   • OrbitTrapPointMap          — distance to origin
//   • OrbitTrapCrossMap          — distance to nearer real / imaginary axis
//   • OrbitTrapCircleMap         — distance to unit circle centred at (1,0)
//   • OrbitTrapLineMap           — distance to a single rotated line through origin
//   • OrbitTrapStarMap           — distance to nearest of N rotated lines (n-pointed star)
//   • OrbitTrapPickoverStalksMap — twin Pickover stalks (|Re| and |Im| separately, hue-blended)
//   • OrbitTrapBiomorphMap       — biomorph-filament colouring; |Re| OR |Im| below threshold

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Shared base for orbit-trap colour maps.  Subclasses define the trap
    /// shape by overriding <see cref="Sample"/>.  Mapping converts the running
    /// minimum trap distance into a normalised gradient parameter via a log
    /// curve so fine detail near the trap is exaggerated.
    /// </summary>
    public abstract class OrbitTrapBaseMap : GradientColorMap, IOrbitAwareColorMap
    {
        /// <summary>
        /// Trap distances above this clamp to t = 1 (gradient end).  Smaller
        /// values pull more pixels toward the bright end of the gradient.
        /// </summary>
        protected virtual float TrapScale => 2.0f;

        public virtual void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
        }

        public abstract void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter);

        public virtual int MapWithOrbit(float smooth, float distance, int iterations,
                                float nx, float ny, in OrbitAccumulator acc)
        {
            float trap = acc.TrapMin == float.MaxValue ? TrapScale : acc.TrapMin;
            float t = MathF.Log(1f + trap / TrapScale) / MathF.Log(2f);
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }

        /// <summary>Subclasses needing a second trap channel can override <see cref="InitOrbit"/>.</summary>
        protected void InitOrbitTwoChannel(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
            acc.TrapMin2 = float.MaxValue;
        }

        // Direct Map() is unused — calculator dispatches through MapWithOrbit.
        // Kept correct for the SwatchSample fallback so palette previews render.
        public override int Map(float smooth, float distance, int maxIterations)
        {
            float t = maxIterations > 0 ? (smooth / maxIterations) : 0f;
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }
    }

    /// <summary>
    /// Trap shape: distance to the origin.  Emphasises orbits that brush the
    /// central absorbing fixed point.  Fire-on-black gradient.
    /// </summary>
    public sealed class OrbitTrapPointMap : OrbitTrapBaseMap
    {
        public static string Name => "Orbit Trap - Point";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the origin. " +
            "Warm fire gradient highlights orbits that approach the central fixed point.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 1.5f;

        public OrbitTrapPointMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 248, 220)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 200, 90)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(220, 90, 30)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(110, 25, 25)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(30, 5, 20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            float d = (float)Math.Sqrt(zr * zr + zi * zi);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    /// <summary>
    /// Trap shape: distance to the nearer of the two coordinate axes.  Produces
    /// a perpendicular spider-web of bright filaments.  Blue / gold contrast.
    /// </summary>
    public sealed class OrbitTrapCrossMap : OrbitTrapBaseMap
    {
        public static string Name => "Orbit Trap - Cross";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the nearer " +
            "coordinate axis.  Produces an interlocking spider-web of bright filaments.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.4f;

        public OrbitTrapCrossMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 150)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(245, 180, 50)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(120, 90, 160)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(40, 50, 130)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(10, 15, 50)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            float d = (float)Math.Min(Math.Abs(zr), Math.Abs(zi));
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    /// <summary>
    /// Trap shape: distance to the unit circle centred at (1, 0) — the period-2
    /// bulb attractor.  Produces concentric ring artefacts.  Ocean tones.
    /// </summary>
    public sealed class OrbitTrapCircleMap : OrbitTrapBaseMap
    {
        public static string Name => "Orbit Trap - Circle";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the unit circle " +
            "centred at (1, 0).  Renders concentric ring filaments through the iteration field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased;

        protected override float TrapScale => 0.3f;

        public OrbitTrapCircleMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(220, 250, 255)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(100, 200, 230)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(30, 110, 170)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(10, 40, 100)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(5, 10, 40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double dx = zr - 1.0;
            double r = Math.Sqrt(dx * dx + zi * zi);
            float d = (float)Math.Abs(r - 1.0);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    /// <summary>
    /// Trap shape: distance to a single line through the origin at angle <see cref="LineAngleRad"/>.
    /// Distance = |Re(z)·sin(θ) − Im(z)·cos(θ)|.  Produces parallel bright bands.
    /// </summary>
    public sealed class OrbitTrapLineMap : OrbitTrapBaseMap
    {
        public static string Name => "Orbit Trap - Line";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to a line through the origin " +
            "tilted 30°.  Produces parallel bright filaments crossing the iteration field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.35f;

        /// <summary>Line orientation in radians; 0 = real axis.</summary>
        private const double LineAngleRad = Math.PI / 6.0;     // 30°

        public OrbitTrapLineMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 220)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(230, 150, 80)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(170, 60, 80)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(60, 25, 70)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(15, 10, 30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double s = Math.Sin(LineAngleRad);
            double c = Math.Cos(LineAngleRad);
            float d = (float)Math.Abs(zr * s - zi * c);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    /// <summary>
    /// Trap shape: minimum distance to any of <see cref="Points"/> equally-spaced
    /// lines through the origin.  N=5 → five-pointed star; N=6 → snowflake.
    /// </summary>
    public sealed class OrbitTrapStarMap : OrbitTrapBaseMap
    {
        public static string Name => "Orbit Trap - Star";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the nearest of " +
            "five lines through the origin spaced 72° apart.  Five-pointed star filaments.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.3f;

        /// <summary>Star point count (≥ 2).  Defaults to 5.</summary>
        private const int Points = 5;

        public OrbitTrapStarMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 255, 235)));
            Stops.Add(new ColorStop(0.18f, Color.FromArgb(255, 200, 90)));
            Stops.Add(new ColorStop(0.42f, Color.FromArgb(220, 70, 130)));
            Stops.Add(new ColorStop(0.65f, Color.FromArgb(80, 30, 130)));
            Stops.Add(new ColorStop(0.85f, Color.FromArgb(20, 10, 50)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            const int n = Points;
            // Fold orbit point into a single wedge of width π/n, then distance
            // to the wedge's centre axis = distance to nearest of n lines.
            double ang = Math.Atan2(zi, zr);
            double wedge = Math.PI / n;
            double folded = ang - Math.Round(ang / wedge) * wedge;
            double r = Math.Sqrt(zr * zr + zi * zi);
            float d = (float)Math.Abs(r * Math.Sin(folded));
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    /// <summary>
    /// Pickover stalks: track <c>min|Re(z)|</c> and <c>min|Im(z)|</c> separately and
    /// map them to two channels of the output colour.  Reveals the classic
    /// orthogonal filament structure of stalks emanating from each axis.
    /// </summary>
    public sealed class OrbitTrapPickoverStalksMap : OrbitTrapBaseMap
    {
        public static string Name => "Orbit Trap - Pickover Stalks";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Pickover stalks: tracks min|Re(z_n)| (warm channel) and min|Im(z_n)| (cool " +
            "channel) separately along the orbit.  Orthogonal filament network through the iteration field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.4f;

        public OrbitTrapPickoverStalksMap()
        {
            // Stops unused — Pickover overrides MapWithOrbit to combine two channels directly.
        }

        public override void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
            acc.TrapMin2 = float.MaxValue;
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            float ar = (float)Math.Abs(zr);
            float ai = (float)Math.Abs(zi);
            if (ar < acc.TrapMin) acc.TrapMin = ar;
            if (ai < acc.TrapMin2) acc.TrapMin2 = ai;
        }

        public override int MapWithOrbit(float smooth, float distance, int iterations,
                                         float nx, float ny, in OrbitAccumulator acc)
        {
            float scale = TrapScale;
            float re = acc.TrapMin == float.MaxValue ? scale : acc.TrapMin;
            float im = acc.TrapMin2 == float.MaxValue ? scale : acc.TrapMin2;

            // Log curve compresses high range so fine filaments near axes are visible.
            float tr = MathF.Log(1f + re / scale) / MathF.Log(2f);
            float ti = MathF.Log(1f + im / scale) / MathF.Log(2f);
            tr = System.Math.Clamp(tr, 0f, 1f);
            ti = System.Math.Clamp(ti, 0f, 1f);

            // Re axis stalks → warm (red/gold); Im axis stalks → cool (cyan/blue).
            // 1 − t so bright filaments correspond to near-axis hits.
            float rr = (1f - tr);
            float gg = 0.5f * (1f - tr) + 0.4f * (1f - ti);
            float bb = (1f - ti);

            // Pack with a slight ambient lift so off-axis pixels aren't pure black.
            const float ambient = 0.05f;
            rr = MathF.Min(1f, ambient + rr);
            gg = MathF.Min(1f, ambient + gg);
            bb = MathF.Min(1f, ambient + bb);

            return ColorUtils.PackArgbF(rr, gg, bb);
        }
    }

    /// <summary>
    /// Biomorph filament colouring (Pickover).  Like Pickover stalks but emphasises
    /// pixels where the FINAL z lands near either axis — produces blobby, organic
    /// "biomorph" creatures.  Implementation: hue from final |Re|/|Im| sentinel
    /// thresholds, brightness from smooth iteration.
    /// </summary>
    public sealed class OrbitTrapBiomorphMap : OrbitTrapBaseMap
    {
        public static string Name => "Orbit Trap - Biomorph";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Biomorph filaments (Pickover).  Pixels whose orbit grazes either axis are " +
            "drawn in saturated hues; off-axis orbits darken with iteration count.  Organic blobby aesthetic.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.HighContrast;

        /// <summary>Biomorph axis threshold; tighter values produce sparser blobs.</summary>
        private const float AxisThreshold = 0.5f;

        protected override float TrapScale => 0.5f;

        public OrbitTrapBiomorphMap() { }

        public override void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
            acc.TrapMin2 = float.MaxValue;
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            float ar = (float)Math.Abs(zr);
            float ai = (float)Math.Abs(zi);
            if (ar < acc.TrapMin) acc.TrapMin = ar;
            if (ai < acc.TrapMin2) acc.TrapMin2 = ai;
        }

        public override int MapWithOrbit(float smooth, float distance, int iterations,
                                         float nx, float ny, in OrbitAccumulator acc)
        {
            float re = acc.TrapMin == float.MaxValue ? 1e9f : acc.TrapMin;
            float im = acc.TrapMin2 == float.MaxValue ? 1e9f : acc.TrapMin2;

            // Background brightness from smooth iter.
            float bg = iterations > 0 ? smooth / iterations : 0f;
            bg = System.Math.Clamp(bg, 0f, 1f);

            bool hitsRe = re < AxisThreshold;
            bool hitsIm = im < AxisThreshold;

            float r, g, b;
            if (hitsRe && hitsIm)
            {
                // Both axes grazed → bright yellow biomorph body.
                float k = 1f - 0.5f * (re + im) / AxisThreshold;
                k = System.Math.Clamp(k, 0f, 1f);
                r = 0.95f * k; g = 0.85f * k; b = 0.20f * k;
            }
            else if (hitsRe)
            {
                float k = 1f - re / AxisThreshold;
                r = 0.90f * k; g = 0.30f * k; b = 0.40f * k;
            }
            else if (hitsIm)
            {
                float k = 1f - im / AxisThreshold;
                r = 0.20f * k; g = 0.45f * k; b = 0.90f * k;
            }
            else
            {
                // Off-axis → cool fade by iteration.
                float t = bg;
                r = 0.05f + 0.10f * t;
                g = 0.05f + 0.15f * t;
                b = 0.08f + 0.25f * t;
            }

            return ColorUtils.PackArgbF(r, g, b);
        }
    }
}
