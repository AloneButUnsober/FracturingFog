// Models/ColorSchemes/OrbitTrapThemes.cs
//
// Orbit-trap colourings.  Per iteration, the calculator records the minimum
// distance from z_n to a theme-defined trap shape (point, axis cross, circle).
// The escape colour comes from that minimum, mapped through a gradient.
//
// Implements IOrbitAwareColorMap so the calculator routes these themes through
// the dedicated scalar SP path (see MandelbrotCalculator.CalculateOrbitAware).
//
// Three sample themes:
//   • OrbitTrapPointMap    — distance to origin
//   • OrbitTrapCrossMap    — distance to nearer real / imaginary axis
//   • OrbitTrapCircleMap   — distance to unit circle centred at (1,0)

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

        public void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
        }

        public abstract void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter);

        public int MapWithOrbit(float smooth, float distance, int iterations,
                                float nx, float ny, in OrbitAccumulator acc)
        {
            float trap = acc.TrapMin == float.MaxValue ? TrapScale : acc.TrapMin;
            float t = MathF.Log(1f + trap / TrapScale) / MathF.Log(2f);
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
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
        public static string Name => "Orbit Trap — Point";
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
        public static string Name => "Orbit Trap — Cross";
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
        public static string Name => "Orbit Trap — Circle";
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
}
