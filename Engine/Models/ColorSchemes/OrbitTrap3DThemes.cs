// Models/ColorSchemes/OrbitTrap3DThemes.cs
//
// 3D-lit variants of the gradient orbit-trap colour themes from
// OrbitTrapThemes.cs and OrbitTrapExtraThemes.cs.  Each Phong3D variant:
//
//   1.  Inherits IOrbitAwareColorMap via OrbitTrapBaseMap → reuses the
//       calculator's orbit-aware dispatch path (per-iteration z samples).
//   2.  Reuses the trap-distance → t mapping of its flat sibling (log curve
//       for open shapes, power curve for closed shapes), with TrapScale /
//       TrapPower defaulted to the same values its flat sibling uses.
//   3.  Samples the gradient at t to produce a per-pixel albedo, then runs
//       the standard 2-light Blinn-Phong rig (KeyLight + FillLight) on the
//       distance-estimator normal (nx, ny) — identical model to
//       GradientPhong3DBase so visual feel matches existing 3D themes.
//
// Pickover-Stalks and Biomorph are intentionally NOT lit — their flat
// versions synthesise colour directly from two trap channels without an
// albedo gradient, so a Phong overlay would discard their essence.
//
// New themes are routed through the calculator's orbit-aware switch in
// MandelbrotCalculator.cs; see the matching case lines added there.

using FracturingFog.Interefaces;
using System;
using System.Drawing;

namespace FracturingFog.Models
{
    // =========================================================================
    // SHARED BASE — log-curve trap response + 2-light Blinn-Phong shading.
    //
    // Mirrors the lighting model in GradientPhong3DBase:
    //   • Ambient  (default 0.12)
    //   • Key + Fill (configurable directions / colours / shininess)
    //   • Fill diffuse scaled down (0.35) so key dominates
    //   • Key + Fill specular scaled (0.85 / 0.25)
    //   • Optional Rim light (off by default for orbit traps)
    //
    // Subclasses configure lights in their constructor and override Sample()
    // (and optionally TrapScale).  Closed-curve traps should instead inherit
    // OrbitTrapPowerPhong3DBase for the pow-curve mapping.
    // =========================================================================

    public abstract class OrbitTrapPhong3DBase : OrbitTrapBaseMap
    {
        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // ── Lighting rig (set in subclass ctor) ───────────────────────────────

        protected LightSource KeyLight;
        protected LightSource FillLight;
        protected LightSource RimLight;
        protected bool UseRimLight;

        // ── Tunables — defaults match GradientPhong3DBase ────────────────────

        protected virtual float Steepness => 1.6f;
        protected virtual float Ambient => 0.12f;
        protected virtual float KeySpecScale => 0.85f;
        protected virtual float FillSpecScale => 0.25f;
        protected virtual float FillDiffScale => 0.35f;
        protected virtual float RimSpecScale => 1.0f;
        protected virtual float RimDiffScale => 0.20f;

        // ── Export accessors (parity with GradientPhong3DBase) ────────────────

        public LightSource ExportKeyLight => KeyLight;
        public LightSource ExportFillLight => FillLight;
        public LightSource ExportRimLight => RimLight;
        public bool ExportUseRimLight => UseRimLight;
        public float ExportSteepness => Steepness;
        public float ExportAmbient => Ambient;
        public float ExportKeySpecScale => KeySpecScale;
        public float ExportFillSpecScale => FillSpecScale;
        public float ExportFillDiffScale => FillDiffScale;
        public float ExportRimSpecScale => RimSpecScale;
        public float ExportRimDiffScale => RimDiffScale;

        // ── Trap → t curve (overridable; power base overrides) ───────────────

        /// <summary>
        /// Maps the running minimum trap distance to a normalised gradient
        /// parameter in [0,1].  Default is the log curve used by
        /// <see cref="OrbitTrapBaseMap"/>.  Closed-shape traps override this
        /// in <see cref="OrbitTrapPowerPhong3DBase"/>.
        /// </summary>
        protected virtual float ComputeTrapT(float trap)
        {
            float scale = TrapScale;
            return System.Math.Clamp(MathF.Log(1f + trap / scale) / MathF.Log(2f), 0f, 1f);
        }

        // ── Main shading path ─────────────────────────────────────────────────

        public override int MapWithOrbit(float smooth, float distance, int iterations,
                                         float nx, float ny, in OrbitAccumulator acc)
        {
            float trap = acc.TrapMin == float.MaxValue ? TrapScale : acc.TrapMin;
            float t = ComputeTrapT(trap);

            int albedoI = MapNormalized(t, distance);
            float aR = ((albedoI >> 16) & 0xFF) / 255f;
            float aG = ((albedoI >>  8) & 0xFF) / 255f;
            float aB = ( albedoI        & 0xFF) / 255f;

            // Build 3D surface normal — same convention as GradientPhong3DBase.
            float ry  = -ny;
            float len = MathF.Sqrt(nx * nx + ry * ry + Steepness * Steepness);
            float Nx, Ny, Nz;
            if (len > 1e-8f) { Nx = nx / len;  Ny = ry / len;  Nz = Steepness / len; }
            else             { Nx = 0f;        Ny = 0f;        Nz = 1f; }

            // Ambient.
            float r = aR * Ambient;
            float g = aG * Ambient;
            float b = aB * Ambient;

            // Key light diffuse.
            float dk = MathF.Max(0f, Nx * KeyLight.Lx + Ny * KeyLight.Ly + Nz * KeyLight.Lz);
            r += dk * KeyLight.DiffR * aR;
            g += dk * KeyLight.DiffG * aG;
            b += dk * KeyLight.DiffB * aB;

            // Key specular (Blinn-Phong half-vector H = normalise(L + V), V=(0,0,1)).
            float hkx = KeyLight.Lx, hky = KeyLight.Ly, hkz = KeyLight.Lz + 1f;
            float hkl = MathF.Sqrt(hkx * hkx + hky * hky + hkz * hkz);
            if (hkl > 1e-8f)
            {
                hkx /= hkl; hky /= hkl; hkz /= hkl;
                float sk = MathF.Pow(MathF.Max(0f, Nx * hkx + Ny * hky + Nz * hkz), KeyLight.Shininess) * KeySpecScale;
                r += sk * KeyLight.SpecR;
                g += sk * KeyLight.SpecG;
                b += sk * KeyLight.SpecB;
            }

            // Fill diffuse (scaled down).
            float df = MathF.Max(0f, Nx * FillLight.Lx + Ny * FillLight.Ly + Nz * FillLight.Lz);
            r += df * FillLight.DiffR * aR * FillDiffScale;
            g += df * FillLight.DiffG * aG * FillDiffScale;
            b += df * FillLight.DiffB * aB * FillDiffScale;

            // Fill specular.
            float hfx = FillLight.Lx, hfy = FillLight.Ly, hfz = FillLight.Lz + 1f;
            float hfl = MathF.Sqrt(hfx * hfx + hfy * hfy + hfz * hfz);
            if (hfl > 1e-8f)
            {
                hfx /= hfl; hfy /= hfl; hfz /= hfl;
                float sf = MathF.Pow(MathF.Max(0f, Nx * hfx + Ny * hfy + Nz * hfz), FillLight.Shininess) * FillSpecScale;
                r += sf * FillLight.SpecR;
                g += sf * FillLight.SpecG;
                b += sf * FillLight.SpecB;
            }

            if (UseRimLight)
            {
                float dr = MathF.Max(0f, Nx * RimLight.Lx + Ny * RimLight.Ly + Nz * RimLight.Lz);
                r += dr * RimLight.DiffR * aR * RimDiffScale;
                g += dr * RimLight.DiffG * aG * RimDiffScale;
                b += dr * RimLight.DiffB * aB * RimDiffScale;

                float hrx = RimLight.Lx, hry = RimLight.Ly, hrz = RimLight.Lz + 1f;
                float hrl = MathF.Sqrt(hrx * hrx + hry * hry + hrz * hrz);
                if (hrl > 1e-8f)
                {
                    hrx /= hrl; hry /= hrl; hrz /= hrl;
                    float sr = MathF.Pow(MathF.Max(0f, Nx * hrx + Ny * hry + Nz * hrz), RimLight.Shininess) * RimSpecScale;
                    r += sr * RimLight.SpecR;
                    g += sr * RimLight.SpecG;
                    b += sr * RimLight.SpecB;
                }
            }

            byte R = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
            byte G = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
            byte B = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
            return unchecked((int)0xFF000000 | (R << 16) | (G << 8) | B);
        }

        // ── Default light rig (warm key, cool fill — neutral starting point) ─

        protected static LightSource DefaultKey() =>
            new LightSource(0.6f, 0.55f, 0.6f,
                            1.00f, 0.95f, 0.85f,
                            1.00f, 0.95f, 0.85f,
                            48f);

        protected static LightSource DefaultFill() =>
            new LightSource(-0.6f, -0.4f, 0.7f,
                            0.55f, 0.65f, 0.80f,
                            0.45f, 0.55f, 0.80f,
                            16f);
    }

    // =========================================================================
    // POWER-CURVE BASE — for closed-shape traps that collapse under log curve.
    // Mirrors OrbitTrapPowerBaseMap from OrbitTrapExtraThemes.cs.
    // =========================================================================

    public abstract class OrbitTrapPowerPhong3DBase : OrbitTrapPhong3DBase
    {
        /// <summary>
        /// Exponent of the trap-distance response curve.  Smaller → more
        /// expansion of small TrapMin values into the gradient body.
        /// </summary>
        protected virtual float TrapPower => 0.35f;

        protected override float ComputeTrapT(float trap)
        {
            float scale = TrapScale;
            float ratio = MathF.Min(trap / scale, 1f);
            return System.Math.Clamp(MathF.Pow(ratio, TrapPower), 0f, 1f);
        }
    }

    // =========================================================================
    // POINT (3D) — distance to origin
    // =========================================================================
    public sealed class OrbitTrapPointPhong3DMap : OrbitTrapPhong3DBase
    {
        public static string Name => "Orbit Trap - Point 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the origin, " +
            "rendered as a Phong-shaded relief surface using the distance-" +
            "estimator normal.  Warm-fire albedo carved by directional lights.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 1.5f;

        public OrbitTrapPointPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 248, 220)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 200, 90)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(220, 90, 30)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(110, 25, 25)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(30, 5, 20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            float d = (float)Math.Sqrt(zr * zr + zi * zi);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // CROSS (3D)
    // =========================================================================
    public sealed class OrbitTrapCrossPhong3DMap : OrbitTrapPhong3DBase
    {
        public static string Name => "Orbit Trap - Cross 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the nearer " +
            "coordinate axis, rendered as Phong-lit relief.  Interlocking " +
            "axis filaments carved with shadow and highlight.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.4f;

        public OrbitTrapCrossPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 150)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(245, 180, 50)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(120, 90, 160)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(40, 50, 130)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(10, 15, 50)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            float d = (float)Math.Min(Math.Abs(zr), Math.Abs(zi));
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // CIRCLE (3D)
    // =========================================================================
    public sealed class OrbitTrapCirclePhong3DMap : OrbitTrapPhong3DBase
    {
        public static string Name => "Orbit Trap - Circle 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the unit circle " +
            "centred at (1, 0).  Concentric ring filaments under Phong relief lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased;

        protected override float TrapScale => 0.3f;

        public OrbitTrapCirclePhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(220, 250, 255)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(100, 200, 230)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(30, 110, 170)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(10, 40, 100)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(5, 10, 40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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

    // =========================================================================
    // LINE (3D)
    // =========================================================================
    public sealed class OrbitTrapLinePhong3DMap : OrbitTrapPhong3DBase
    {
        public static string Name => "Orbit Trap - Line 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to a line through " +
            "the origin tilted 30°.  Parallel filaments embossed by Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.35f;

        private const double LineAngleRad = Math.PI / 6.0;

        public OrbitTrapLinePhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 220)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(230, 150, 80)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(170, 60, 80)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(60, 25, 70)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(15, 10, 30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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

    // =========================================================================
    // STAR (3D)
    // =========================================================================
    public sealed class OrbitTrapStarPhong3DMap : OrbitTrapPhong3DBase
    {
        public static string Name => "Orbit Trap - Star 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the nearest of " +
            "five lines through the origin at 72° spacing.  Five-pointed-star " +
            "filaments under Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.3f;

        private const int Points = 5;

        public OrbitTrapStarPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 255, 235)));
            Stops.Add(new ColorStop(0.18f, Color.FromArgb(255, 200, 90)));
            Stops.Add(new ColorStop(0.42f, Color.FromArgb(220, 70, 130)));
            Stops.Add(new ColorStop(0.65f, Color.FromArgb(80, 30, 130)));
            Stops.Add(new ColorStop(0.85f, Color.FromArgb(20, 10, 50)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(0, 0, 0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            const int n = Points;
            double ang = Math.Atan2(zi, zr);
            double wedge = Math.PI / n;
            double folded = ang - Math.Round(ang / wedge) * wedge;
            double r = Math.Sqrt(zr * zr + zi * zi);
            float d = (float)Math.Abs(r * Math.Sin(folded));
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // SQUARE (3D)
    // =========================================================================
    public sealed class OrbitTrapSquarePhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Square 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum Chebyshev distance from z_n to the " +
            "unit-square boundary.  Rectilinear lattice filaments embossed by Phong relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.30f;

        public OrbitTrapSquarePhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(240, 255, 240)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(120, 220, 140)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 40, 130,  90)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 15,  60,  50)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  5,  20,  20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // RING (3D) — period-3 bulb circle
    // =========================================================================
    public sealed class OrbitTrapRingPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Ring 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to a circle of " +
            "radius 0.3 at (-1, 0) — the period-3 bulb area.  Off-axis ring " +
            "filaments under Phong shading.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased;

        protected override float TrapScale => 0.35f;
        protected override float TrapPower => 0.40f;

        private const double Cx = -1.0;
        private const double Cy =  0.0;
        private const double R  =  0.3;

        public OrbitTrapRingPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 255)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(220, 150, 230)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(140,  60, 170)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 60,  20, 100)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 15,   5,  40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // HYPERBOLA (3D)
    // =========================================================================
    public sealed class OrbitTrapHyperbolaPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Hyperbola 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to |Re·Im| = 1.  " +
            "Hyperbolic-arm filaments shaded by Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.40f;
        protected override float TrapPower => 0.35f;

        public OrbitTrapHyperbolaPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 230, 200)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 150,  80)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(180,  50,  60)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 80,  20,  60)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,   5,  20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // LEMNISCATE (3D)
    // =========================================================================
    public sealed class OrbitTrapLemniscatePhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Lemniscate 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the Bernoulli " +
            "lemniscate (Re²+Im²)² = 2(Re²−Im²).  Figure-8 lobe filaments " +
            "under Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.40f;
        protected override float TrapPower => 0.35f;

        public OrbitTrapLemniscatePhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 245, 240)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 130, 170)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(200,  40, 130)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 90,  15,  90)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,   5,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // CARDIOID (3D)
    // =========================================================================
    public sealed class OrbitTrapCardioidPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Cardioid 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the main " +
            "Mandelbrot cardioid r = (1 − cos θ)/2.  Parent-body filaments " +
            "embossed by Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.35f;

        public OrbitTrapCardioidPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(240, 250, 255)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(120, 200, 250)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 40, 100, 200)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 25,  30, 120)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  8,  10,  40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // DIAGONAL CROSS (3D)
    // =========================================================================
    public sealed class OrbitTrapDiagonalCrossPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Diagonal Cross 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to y = x or y = −x.  " +
            "Diagonal filaments under Phong relief lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.40f;
        protected override float TrapPower => 0.40f;

        public OrbitTrapDiagonalCrossPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 245, 220)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 180,  60)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(180,  90,  40)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 90,  40,  60)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,  10,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            const double inv = 0.7071067811865475;
            double d1 = Math.Abs(zr - zi) * inv;
            double d2 = Math.Abs(zr + zi) * inv;
            float d = (float)Math.Min(d1, d2);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // TRIANGLE (3D)
    // =========================================================================
    public sealed class OrbitTrapTrianglePhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Triangle 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to an equilateral " +
            "triangle's edges.  Three-fold symmetric filaments embossed by Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.30f;

        public OrbitTrapTrianglePhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 215)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(230, 130,  80)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(160,  50,  90)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 60,  20,  90)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 10,   5,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            const double k = 1.7320508075688772;
            double px = Math.Abs(zr) - 1.0;
            double py = zi + 1.0 / k;
            if (px + k * py > 0.0)
            {
                double nxL = (px - k * py) * 0.5;
                double nyL = (-k * px - py) * 0.5;
                px = nxL; py = nyL;
            }
            px -= Math.Clamp(px, -2.0, 0.0);
            float d = (float)Math.Sqrt(px * px + py * py);
            if (d < acc.TrapMin) acc.TrapMin = d;
        }
    }

    // =========================================================================
    // HEXAGON (3D)
    // =========================================================================
    public sealed class OrbitTrapHexagonPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Hexagon 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to a regular " +
            "hexagon's edges.  Honeycomb filaments embossed by Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.30f;

        public OrbitTrapHexagonPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 245, 200)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(220, 180,  50)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(140, 110,  30)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 70,  50,  20)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,  15,   8)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
        }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            const double kx = -0.8660254037844387;
            const double ky =  0.5;
            const double kz =  0.5773502691896257;
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
    // HEART (3D)
    // =========================================================================
    public sealed class OrbitTrapHeartPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Heart 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the implicit " +
            "heart curve (x²+y²−1)³ = x²·y³.  Heart-boundary filaments under " +
            "Phong relief lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.35f;
        protected override float TrapPower => 0.30f;

        public OrbitTrapHeartPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 235, 240)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 120, 160)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(220,  30,  80)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(110,  15,  50)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 25,   5,  15)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // SINE WAVE (3D)
    // =========================================================================
    public sealed class OrbitTrapSineWavePhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Sine Wave 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to y = sin(π·x).  " +
            "Sinuous ripple filaments embossed by Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.35f;
        protected override float TrapPower => 0.35f;

        private const double K = Math.PI;

        public OrbitTrapSineWavePhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(230, 255, 245)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb( 80, 230, 200)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 30, 130, 180)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 15,  50, 110)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  5,  15,  40)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // CONCENTRIC (3D)
    // =========================================================================
    public sealed class OrbitTrapConcentricPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Concentric 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to concentric " +
            "rings spaced 1.0 apart.  Bullseye structure under Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased;

        protected override float TrapScale => 0.40f;
        protected override float TrapPower => 0.35f;

        private const double RingStep = 1.0;

        public OrbitTrapConcentricPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 255, 230)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 200, 100)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(220, 100,  60)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(120,  40,  60)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 30,  10,  25)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // GRID (3D)
    // =========================================================================
    public sealed class OrbitTrapGridPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Grid 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the half-integer " +
            "lattice grid.  Cellular network embossed by Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.20f;
        protected override float TrapPower => 0.30f;

        private const double Step = 0.5;

        public OrbitTrapGridPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(240, 255, 255)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(120, 210, 220)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb( 40, 110, 150)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 20,  40,  90)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  5,  10,  30)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // PINWHEEL (3D)
    // =========================================================================
    public sealed class OrbitTrapPinwheelPhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Pinwheel 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to 8 rotated lines " +
            "through the origin with half-step phase offset.  Pinwheel filaments " +
            "embossed by Phong lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.30f;
        protected override float TrapPower => 0.40f;

        private const int Arms = 8;
        private const double PhaseOffset = Math.PI / 16.0;

        public OrbitTrapPinwheelPhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 240, 250)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(200, 120, 240)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(110,  50, 190)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 50,  20, 100)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 12,   5,  35)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
    // POLAR ROSE (3D)
    // =========================================================================
    public sealed class OrbitTrapPolarRosePhong3DMap : OrbitTrapPowerPhong3DBase
    {
        public static string Name => "Orbit Trap - Polar Rose 3D";
        public static string Category => "Orbit Trap 3D";
        public static string Description =>
            "3D-lit orbit-trap: minimum distance from z_n to the rose curve " +
            "r = |cos(3θ)|.  Three petals radiating from the origin under " +
            "Phong relief lighting.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.GradientBased |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 0.35f;
        protected override float TrapPower => 0.35f;

        private const int K = 3;

        public OrbitTrapPolarRosePhong3DMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 250, 235)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255, 170, 120)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(200,  80, 100)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb( 80,  30,  90)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 20,  10,  35)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0,   0)));

            KeyLight = DefaultKey();
            FillLight = DefaultFill();
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
