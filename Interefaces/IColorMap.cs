// Interefaces/IColorMap.cs  — v4 (3D lighting extension)
//
// The core change in this version is the addition of a second Map overload
// that receives the surface normal vector (nx, ny) estimated at escape.
//
// Backward compatibility guarantee
//   • All existing three-parameter Map(smooth, distance, iterations)
//     implementations continue to compile and work unchanged.
//   • The new five-parameter overload has a default implementation that
//     delegates to the three-parameter version, so existing themes
//     automatically ignore the normal data and keep their existing look.
//   • 3D themes override the five-parameter overload and implement lighting.
//   • The calculator calls the five-parameter version unconditionally.
//
// Surface normal convention
//   nx, ny are the components of the outward normal to the escape potential
//   level curve in the complex plane.  They are computed from the escape
//   orbit's complex derivative using the Inigo Quilez technique (see
//   MandelbrotCalculator.cs, FillNormal).
//
//   Both components are in the range [-1, 1].  They represent the 2D
//   "slope" of the fractal surface at this pixel.  3D colour maps build a
//   full 3D unit normal as normalize(nx, ny, steepness) before applying
//   the Phong illumination model.
//
//   For in-set pixels (iter >= maxIterations), both nx and ny are 0.
using System;
using System.Drawing.Imaging;

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// Capability flags that describe how a colour map uses its inputs.
    /// Used by the UI to decide which display overlays to enable, and to
    /// show informative tooltips.
    /// </summary>
    [Flags]
    public enum ColorMapFeatures
    {
        None = 0,
        UsesSmooth = 1 << 0,   // iteration count drives the colour
        UsesDistance = 1 << 1,   // exterior distance estimate influences colour
        UsesNormals = 1 << 2,   // map reads nx, ny for 3D lighting
        Cyclic = 1 << 3,   // gradient repeats — doesn't go dark at deep zoom
        Perceptual = 1 << 4,   // perceptually uniform (lightness progression)
        HighContrast = 1 << 5,   // strong light/dark contrast
        GradientBased = 1 << 6,   // uses linear stop interpolation
        ThreeDEffect = 1 << 7,   // map produces a strong 3D visual
        UsesOrbitTrap = 1 << 8,   // samples z each iteration for trap-shape distance
        UsesStripeAvg = 1 << 9,   // samples z each iteration for stripe / TIA averaging
        UsesFinalZ = 1 << 10,  // reads z (zr, zi) at escape — binary/angle decomp, potential, field lines, domain coloring
        UsesDerivative = 1 << 11,  // reads dz/dc at escape — derivative bailout colourings
        UsesHistogram = 1 << 12,  // designed for histogram equalisation; pair with the EQ slider
    }

    /// <summary>
    /// Pallet categories for UI grouping and metadata.  Not a strict taxonomy, just
    /// a convenient way to organize palettes in the user interface.
    /// </summary>
    public enum ColorPaletteType
    {
        GradientLinear,
        GradientCyclic,
        Algorithmic,
        Relief3D,
        Texture,
        Scientific
    }

    /// <summary>
    /// Maps per-pixel fractal output data (smooth iteration count, exterior
    /// distance estimate) to a packed 32-bit ARGB integer colour value.
    /// Return format: <c>unchecked((int)0xFF_RR_GG_BB)</c>.
    /// </summary>
    /// <remarks>
    /// Return format: <c>unchecked((int)0xFF000000 | (R &lt;&lt; 16) | (G &lt;&lt; 8) | B)</c>
    /// — alpha is always 0xFF (fully opaque).
    /// </remarks>
    public interface IColorMap
    {
        // ── Static display metadata (override the default per implementation) ─

        public static string Name { get; } = "Unnamed";

        public ColorPaletteType Type { get; }

        public static string Category { get; } = "General";

        public static string Description { get; } = "";

        public static ColorMapFeatures Features { get; } = ColorMapFeatures.UsesSmooth;


        // ── Per-instance state ────────────────────────────────────────────────

        public int MaxIterations { get; set; }

        /// <summary>
        /// Packed ARGB colour painted for in-set (interior) pixels.  Defaults to
        /// opaque black (0xFF000000).  Themes that want a different interior
        /// colour override this property.
        /// </summary>
        uint InSetColor => 0xFF000000u;

        // ── Core mapping — THREE-PARAMETER ────────────────────────────────────
        /// <summary>
        /// Maps fractal sample data to a packed ARGB colour.
        /// All existing colour maps implement this method.
        /// </summary>
        int Map(float smooth, float distance, int iterations);

        // ── Extended mapping — FIVE-PARAMETER (3D themes override this) ──────

        /// <summary>
        /// Maps fractal sample data plus surface normal to a packed ARGB colour.
        ///
        /// Default implementation delegates to
        /// <see cref="Map(float,float,int)"/>, so all existing colour maps
        /// automatically support this overload without any code changes.
        ///
        /// 3D colour maps override this method and use <paramref name="nx"/>
        /// and <paramref name="ny"/> to apply Phong or other lighting models.
        /// </summary>
        /// <param name="smooth">Smooth (continuous) iteration count at escape.</param>
        /// <param name="distance">Exterior distance estimate; 0 for in-set.</param>
        /// <param name="iterations">Maximum iteration depth for this frame.</param>
        /// <param name="nx">
        /// X component of the outward normal to the escape-potential level
        /// curve, in the range [-1, 1].  0 for in-set pixels.
        /// </param>
        /// <param name="ny">
        /// Y component of the outward normal, in the range [-1, 1].
        /// 0 for in-set pixels.
        /// </param>
        int Map(float smooth, float distance, int iterations, float nx, float ny)
            => Map(smooth, distance, iterations);   // default: ignore normals

        // ── Extended mapping — NINE-PARAMETER (final-state-aware themes) ─────

        /// <summary>
        /// Maps fractal sample data plus surface normal AND the final values of
        /// z and dz/dc at escape to a packed ARGB colour.  Used by themes that
        /// need the actual escape z (binary decomposition, angle decomposition,
        /// Douady-Hubbard potential, field lines, domain coloring) or the
        /// escape-time derivative (derivative bailout colourings).
        ///
        /// Default implementation delegates to the five-parameter overload, so
        /// existing themes ignore the extra data without modification.
        ///
        /// All four extra parameters are 0 for in-set pixels.
        /// </summary>
        /// <param name="finalZr">Real part of z at the escape iteration.</param>
        /// <param name="finalZi">Imaginary part of z at the escape iteration.</param>
        /// <param name="dzdcR">Real part of dz/dc at the escape iteration.</param>
        /// <param name="dzdcI">Imaginary part of dz/dc at the escape iteration.</param>
        int Map(float smooth, float distance, int iterations, float nx, float ny,
                float finalZr, float finalZi, float dzdcR, float dzdcI)
            => Map(smooth, distance, iterations, nx, ny);   // default: ignore final state

        // ── Convenience helpers ───────────────────────────────────────────────

        /// <summary>
        /// Representative colour for use in UI swatches.
        /// Samples at 30 % of MaxIterations with a small distance value and
        /// a gently tilted surface (nx=0.3, ny=0.2) so 3D themes show shading.
        /// </summary>
        int SwatchSample
            => Map(MaxIterations * 0.30f, 0.05f, MaxIterations, 0.30f, 0.20f);
    }

    /// <summary>
    /// Optional companion interface for colour maps whose display metadata is
    /// per-instance rather than per-type.  Built-in themes expose Name/Category
    /// /Description as static type-level properties (read via reflection); user-
    /// defined / data-driven themes can implement this interface so a single
    /// runtime type can carry many distinct named themes.
    /// </summary>
    public interface INamedColorMap
    {
        string DisplayName { get; }
        string DisplayCategory { get; }
        string DisplayDescription { get; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Orbit-aware extension
    //
    // Orbit traps, stripe average and triangle-inequality average colourings all
    // need access to z at EVERY iteration step — not just the final escape
    // value.  The standard fast SP / PT calculator paths do not surface this
    // data, so themes that need it implement IOrbitAwareColorMap and the
    // calculator dispatches them through a dedicated scalar path.
    //
    // The path is opt-in and slower than the fast SIMD path.  HP / perturbation
    // are NOT supported on the orbit-aware path; deep zoom themes should rely
    // on the existing fast path.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-pixel state accumulated by an orbit-aware colour map while iterating
    /// z_{n+1} = z_n^2 + c.  Fields are populated by <see cref="IOrbitAwareColorMap.Sample"/>
    /// and consumed by <see cref="IOrbitAwareColorMap.MapWithOrbit"/>.
    /// </summary>
    public struct OrbitAccumulator
    {
        /// <summary>Running minimum trap-shape distance.  Initialise to <see cref="float.MaxValue"/>.</summary>
        public float TrapMin;

        /// <summary>Secondary trap distance (e.g. for Pickover stalks: TrapMin = min|Re|, TrapMin2 = min|Im|).</summary>
        public float TrapMin2;

        /// <summary>Location (Re, Im) of orbit point at which TrapMin was achieved.  Used by image / texture traps.</summary>
        public double TrapZr;
        public double TrapZi;

        /// <summary>Sum of stripe samples 0.5+0.5·sin(s·arg(z_n)).</summary>
        public double StripeSum;
        public int StripeCount;

        /// <summary>Sum of triangle-inequality samples (|z_n|−m_n)/(M_n−m_n).</summary>
        public double TiaSum;
        public int TiaCount;

        /// <summary>Last stripe / TIA sample, used for fractional smoothing at escape.</summary>
        public double LastStripe;
        public double LastTia;

        // ── Curvature average ─────────────────────────────────────────────────
        /// <summary>Previous orbit point z_{n-1}, for segment construction.</summary>
        public double PrevZr;
        public double PrevZi;
        /// <summary>Previous segment vector z_{n-1} − z_{n-2}, for angle change.</summary>
        public double PrevSegR;
        public double PrevSegI;
        /// <summary>Sum of |Δ arg(seg)| per iteration.</summary>
        public double CurvatureSum;
        public int CurvatureCount;

        // ── Lyapunov exponent ─────────────────────────────────────────────────
        /// <summary>Sum of log|f'(z_n)| = log|2 z_n|.</summary>
        public double LyapunovSum;
        public int LyapunovCount;

        // ── Gaussian integer trap ─────────────────────────────────────────────
        /// <summary>Sum of min distance from z_n to nearest Gaussian integer.</summary>
        public double GaussianSum;
        public int GaussianCount;

        // ── Exponential smoothing (Kerry Mitchell) ────────────────────────────
        /// <summary>Sum of e^{−|z_n|} along the orbit.</summary>
        public double ExpSum;
        public int ExpCount;
    }

    /// <summary>
    /// Colour map that requires per-iteration z samples to compute its colour.
    /// The calculator routes any IColorMap implementing this interface through
    /// a scalar SP path that calls <see cref="Sample"/> once per iteration and
    /// <see cref="MapWithOrbit"/> once at escape.
    /// </summary>
    public interface IOrbitAwareColorMap : IColorMap
    {
        /// <summary>Initialise the accumulator before iteration begins.</summary>
        void InitOrbit(out OrbitAccumulator acc);

        /// <summary>
        /// Called once per iteration with the current z = z_n and c = c.
        /// Implementations update <paramref name="acc"/> as needed.
        /// </summary>
        /// <param name="iter">Iteration index (0 = before first squaring step).</param>
        void Sample(ref OrbitAccumulator acc, double zr, double zi, double cr, double ci, int iter);

        /// <summary>Final colour produced from the standard inputs plus the accumulated orbit state.</summary>
        int MapWithOrbit(float smooth, float distance, int iterations, float nx, float ny, in OrbitAccumulator acc);
    }
}