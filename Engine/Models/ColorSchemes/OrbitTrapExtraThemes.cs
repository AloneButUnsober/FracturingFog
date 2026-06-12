// Models/ColorSchemes/OrbitTrapExtraThemes.cs
//
// Additional orbit-trap shapes beyond the core set in OrbitTrapThemes.cs.
// Design rules learned from the originals:
//
//   1. Mapping curve.  Many trap shapes here are CLOSED CURVES that nearly
//      every escape orbit must cross at least once (a unit circle, a square,
//      an integer grid line, etc.).  When that happens TrapMin collapses
//      toward 0 for almost every pixel and the default log curve squashes
//      the entire image into the brightest end of the gradient — flat.
//      OrbitTrapPowerBaseMap below replaces the log compression with a
//      power curve (default exponent 0.35) that EXPANDS the small-trap
//      range across the gradient body, so tiny variations in closest
//      approach become visible filaments rather than collapsing to a wash.
//
//   2. Geometry placement.  Centred closed shapes get crossed trivially.
//      Where appropriate, shapes are offset away from the origin (so orbits
//      that don't visit the offset region keep a non-zero TrapMin), or
//      kept centred but sized to match interesting orbit dynamics (e.g.
//      the unit-radius escape circle).
//
// Shapes:
//   • Square         — unit square boundary (Chebyshev norm)
//   • Ring           — circle of radius 0.3 at (-1, 0)  (period-3 bulb area)
//   • Hyperbola      — |zr·zi| = 1 filaments
//   • Lemniscate     — Bernoulli figure-8
//   • Cardioid       — main Mandelbrot cardioid curve
//   • Diagonal Cross — nearer of y = x / y = −x
//   • Triangle       — equilateral triangle edges
//   • Hexagon        — regular hexagon edges
//   • Heart          — implicit heart curve
//   • Sine Wave      — y = sin(π·x)
//   • Concentric     — rings at integer radii
//   • Grid           — half-integer lattice
//   • Pinwheel       — 8-armed rotated star with phase offset
//   • Polar Rose     — r = cos(3θ) rose curve

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    // =========================================================================
    // SHARED BASE — power-curve trap response.
    //
    // Replaces the default log mapping in OrbitTrapBaseMap with
    //
    //     t = pow(min(trap / TrapScale, 1), TrapPower)
    //
    // Exponent < 1 expands the tiny-trap range (where most pixels of closed
    // traps live) into the centre of the gradient, restoring filament detail.
    // =========================================================================

    public abstract class OrbitTrapPowerBaseMap : OrbitTrapBaseMap
    {
        /// <summary>
        /// Exponent of the trap-distance response curve.  Smaller → more
        /// expansion of small TrapMin values into the gradient body.
        /// </summary>
        protected virtual float TrapPower => 0.35f;

        public override int MapWithOrbit(float smooth, float distance, int iterations,
                                         float nx, float ny, in OrbitAccumulator acc)
        {
            float trap = acc.TrapMin == float.MaxValue ? TrapScale : acc.TrapMin;
            float ratio = MathF.Min(trap / TrapScale, 1f);
            float t = MathF.Pow(ratio, TrapPower);
            return MapNormalized(System.Math.Clamp(t, 0f, 1f), distance);
        }
    }

    // =========================================================================
    // SQUARE — Chebyshev-norm distance to unit square boundary.
    // =========================================================================
    public sealed class OrbitTrapSquareMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Square";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the unit-square " +
            "boundary under the Chebyshev (max) norm.  Power-curve response " +
            "draws out rectilinear lattice filaments through the iteration field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.30f;

        public OrbitTrapSquareMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(240, 255, 240)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(120, 220, 140)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 40, 130,  90)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 15,  60,  50)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  5,  20,  20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double m = Math.Max(Math.Abs(zr), Math.Abs(zi));
            float d = (float)Math.Abs(m - 1.0);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // RING — circle of radius 0.3 centred at (-1, 0) — the period-3 bulb area.
    // Off-axis placement breaks the inevitable unit-circle crossing that
    // collapses centred rings to a flat wash.
    // =========================================================================
    public sealed class OrbitTrapRingMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Ring";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to a circle of " +
            "radius 0.3 centred at (-1, 0) — the period-3 bulb neighbourhood. " +
            "Off-axis placement yields concentric ring filaments only where " +
            "the orbit visits that region.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased;

        protected override float TrapScale => 0.35f;
        protected override float TrapPower => 0.40f;

        private const double Cx = -1.0;
        private const double Cy =  0.0;
        private const double R  =  0.3;

        public OrbitTrapRingMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 255)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(220, 150, 230)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(140,  60, 170)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 60,  20, 100)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 15,   5,  40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double dx = zr - Cx;
            double dy = zi - Cy;
            double r = Math.Sqrt(dx * dx + dy * dy);
            float d = (float)Math.Abs(r - R);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // HYPERBOLA — distance to |Re·Im| = 1 filaments.
    // =========================================================================
    public sealed class OrbitTrapHyperbolaMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Hyperbola";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the curve " +
            "|Re·Im| = 1.  Power-curve response surfaces hyperbolic-arm " +
            "filaments reaching into each quadrant.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.40f;
        protected override float TrapPower => 0.35f;

        public OrbitTrapHyperbolaMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 230, 200)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 150,  80)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(180,  50,  60)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 80,  20,  60)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,   5,  20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double f = Math.Abs(zr * zi) - 1.0;
            double gradMag = Math.Sqrt(zr * zr + zi * zi);
            if (gradMag < 1e-6) gradMag = 1e-6;
            float d = (float)(Math.Abs(f) / gradMag);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // LEMNISCATE — Bernoulli figure-8 (Re²+Im²)² = 2(Re²−Im²).
    // =========================================================================
    public sealed class OrbitTrapLemniscateMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Lemniscate";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the Bernoulli " +
            "lemniscate (Re²+Im²)² = 2(Re²−Im²).  Twinned figure-8 lobe " +
            "filaments emerge through the iteration field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.40f;
        protected override float TrapPower => 0.35f;

        public OrbitTrapLemniscateMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 245, 240)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 130, 170)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(200,  40, 130)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 90,  15,  90)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,   5,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double r2 = zr * zr + zi * zi;
            double f = r2 * r2 - 2.0 * (zr * zr - zi * zi);
            double dfx = 4.0 * zr * r2 - 4.0 * zr;
            double dfy = 4.0 * zi * r2 + 4.0 * zi;
            double gradMag = Math.Sqrt(dfx * dfx + dfy * dfy);
            if (gradMag < 1e-6) gradMag = 1e-6;
            float d = (float)(Math.Abs(f) / gradMag);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // CARDIOID — main Mandelbrot cardioid r = (1 − cos θ)/2.
    // Polar-distance approximation; gradient magnitude correction stabilises
    // the implicit-curve distance near the cusp.
    // =========================================================================
    public sealed class OrbitTrapCardioidMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Cardioid";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the main " +
            "Mandelbrot cardioid r = (1 − cos θ)/2.  Highlights orbits that " +
            "graze the parent-body boundary.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.35f;

        public OrbitTrapCardioidMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(240, 250, 255)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(120, 200, 250)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 40, 100, 200)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 25,  30, 120)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  8,  10,  40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double r = Math.Sqrt(zr * zr + zi * zi);
            double theta = Math.Atan2(zi, zr);
            double rCurve = 0.5 * (1.0 - Math.Cos(theta));
            float d = (float)Math.Abs(r - rCurve);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // DIAGONAL CROSS — nearer of y = x / y = −x.
    // =========================================================================
    public sealed class OrbitTrapDiagonalCrossMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Diagonal Cross";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the nearer of " +
            "y = x or y = −x.  Interlocking diagonal filaments — rotated 45° " +
            "version of the axis-cross.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.40f;
        protected override float TrapPower => 0.40f;

        public OrbitTrapDiagonalCrossMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 245, 220)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 180,  60)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(180,  90,  40)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 90,  40,  60)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,  10,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            const double inv = 0.7071067811865475;  // 1/√2
            double d1 = Math.Abs(zr - zi) * inv;
            double d2 = Math.Abs(zr + zi) * inv;
            float d = (float)Math.Min(d1, d2);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // TRIANGLE — equilateral triangle edges via IQ SDF.
    // =========================================================================
    public sealed class OrbitTrapTriangleMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Triangle";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the edges of " +
            "an equilateral triangle centred on the origin.  Three-fold " +
            "symmetric filaments.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.30f;

        public OrbitTrapTriangleMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 215)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(230, 130,  80)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(160,  50,  90)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 60,  20,  90)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 10,   5,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            const double k = 1.7320508075688772;        // √3
            double px = Math.Abs(zr) - 1.0;
            double py = zi + 1.0 / k;
            if (px + k * py > 0.0)
            {
                double nx = (px - k * py) * 0.5;
                double ny = (-k * px - py) * 0.5;
                px = nx; py = ny;
            }
            px -= Math.Clamp(px, -2.0, 0.0);
            float d = (float)Math.Sqrt(px * px + py * py);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // HEXAGON — regular hexagon edges via IQ SDF.
    // =========================================================================
    public sealed class OrbitTrapHexagonMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Hexagon";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the edges of " +
            "a regular hexagon centred on the origin.  Six-fold symmetric " +
            "honeycomb filaments.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.30f;

        public OrbitTrapHexagonMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 245, 200)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(220, 180,  50)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(140, 110,  30)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 70,  50,  20)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,  15,   8)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            const double kx = -0.8660254037844387;      // −√3/2
            const double ky =  0.5;
            const double kz =  0.5773502691896257;      // tan(π/6)
            double px = Math.Abs(zr);
            double py = Math.Abs(zi);
            double dot2 = 2.0 * Math.Min(kx * px + ky * py, 0.0);
            px -= dot2 * kx;
            py -= dot2 * ky;
            px -= Math.Clamp(px, -kz, kz);
            py -= 1.0;
            float d = (float)Math.Sqrt(px * px + py * py);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // HEART — implicit heart curve (x²+y²−1)³ = x²y³.
    // =========================================================================
    public sealed class OrbitTrapHeartMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Heart";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the implicit " +
            "heart curve (x²+y²−1)³ = x²·y³.  Orbits brushing the heart " +
            "boundary light up — romantic / kitsch aesthetic.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.35f;
        protected override float TrapPower => 0.30f;

        public OrbitTrapHeartMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 235, 240)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 120, 160)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(220,  30,  80)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(110,  15,  50)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 25,   5,  15)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double x = zr;
            double y = -zi;
            double r2 = x * x + y * y;
            double term = r2 - 1.0;
            double f = term * term * term - x * x * y * y * y;
            double dfx = 6.0 * x * term * term - 2.0 * x * y * y * y;
            double dfy = 6.0 * y * term * term - 3.0 * x * x * y * y;
            double gradMag = Math.Sqrt(dfx * dfx + dfy * dfy);
            if (gradMag < 1e-6) gradMag = 1e-6;
            float d = (float)(Math.Abs(f) / gradMag);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // SINE WAVE — distance to y = sin(π·x).
    // =========================================================================
    public sealed class OrbitTrapSineWaveMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Sine Wave";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the curve " +
            "y = sin(π·x).  Sinuous ripple filaments cross the iteration " +
            "field — organic motion absent from rectilinear traps.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.35f;
        protected override float TrapPower => 0.35f;

        private const double K = Math.PI;

        public OrbitTrapSineWaveMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(230, 255, 245)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb( 80, 230, 200)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 30, 130, 180)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 15,  50, 110)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  5,  15,  40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double f = zi - Math.Sin(K * zr);
            double dfx = -K * Math.Cos(K * zr);
            double gradMag = Math.Sqrt(dfx * dfx + 1.0);
            float d = (float)(Math.Abs(f) / gradMag);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // CONCENTRIC — rings at integer radii (r = 1, 2, 3 …).  Step widened
    // from 0.5 → 1.0 so escape orbits hit two or three crossings, not eight.
    // =========================================================================
    public sealed class OrbitTrapConcentricMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Concentric";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the nearest " +
            "of concentric rings spaced 1.0 apart.  Bullseye structure with " +
            "self-similar layering at different scales.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased;

        protected override float TrapScale => 0.40f;
        protected override float TrapPower => 0.35f;

        private const double RingStep = 1.0;

        public OrbitTrapConcentricMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 255, 230)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 200, 100)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(220, 100,  60)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(120,  40,  60)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 30,  10,  25)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double r = Math.Sqrt(zr * zr + zi * zi);
            double frac = r / RingStep;
            frac -= Math.Floor(frac);
            double d = RingStep * Math.Min(frac, 1.0 - frac);
            if ((float)d < acc.TrapMin) acc.TrapMin = (float)d;
        }
    }

    // =========================================================================
    // GRID — half-integer lattice (lines at every 0.5 in x or y) — fine
    // cellular network through the iteration field.
    // =========================================================================
    public sealed class OrbitTrapGridMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Grid";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the half-integer " +
            "lattice grid lines (nearest x or y multiple of 0.5).  Cellular " +
            "network with crisp intersections.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.20f;
        protected override float TrapPower => 0.30f;

        private const double Step = 0.5;

        public OrbitTrapGridMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(240, 255, 255)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(120, 210, 220)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 40, 110, 150)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 20,  40,  90)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  5,  10,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double fx = zr / Step; fx -= Math.Floor(fx);
            double fy = zi / Step; fy -= Math.Floor(fy);
            double dx = Step * Math.Min(fx, 1.0 - fx);
            double dy = Step * Math.Min(fy, 1.0 - fy);
            float d = (float)Math.Min(dx, dy);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // PINWHEEL — 8-armed rotated star with phase offset.
    // =========================================================================
    public sealed class OrbitTrapPinwheelMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Pinwheel";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the nearest of " +
            "eight rotated lines through the origin (45° spacing, half-step " +
            "phase offset).  Pinwheel filaments distinct from the 5-pointed " +
            "Star trap.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.40f;

        private const int Arms = 8;
        private const double PhaseOffset = Math.PI / 16.0;

        public OrbitTrapPinwheelMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 250)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(200, 120, 240)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(110,  50, 190)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 50,  20, 100)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 12,   5,  35)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double ang = Math.Atan2(zi, zr) - PhaseOffset;
            double wedge = Math.PI / Arms;
            double folded = ang - Math.Round(ang / wedge) * wedge;
            double r = Math.Sqrt(zr * zr + zi * zi);
            float d = (float)Math.Abs(r * Math.Sin(folded));
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // POLAR ROSE — r = cos(3θ).
    // =========================================================================
    public sealed class OrbitTrapPolarRoseMap : OrbitTrapPowerBaseMap
    {
        public static string Name => "Orbit Trap — Polar Rose";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Orbit-trap colouring: minimum distance from z_n to the rose " +
            "curve r = |cos(3θ)| — three petals radiating from the origin.  " +
            "Floral filaments through the iteration field.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.GradientBased | ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.35f;
        protected override float TrapPower => 0.35f;

        private const int K = 3;

        public OrbitTrapPolarRoseMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 250, 235)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 170, 120)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(200,  80, 100)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 80,  30,  90)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,  10,  35)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            double r = Math.Sqrt(zr * zr + zi * zi);
            double theta = Math.Atan2(zi, zr);
            double rCurve = Math.Abs(Math.Cos(K * theta));
            float d = (float)Math.Abs(r - rCurve);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }
}
