// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
        UsesPostProcess = 1 << 10, // runs an extra full-screen pass after main render
        UsesDerivative = 1 << 11,  // reads dz/dc at escape — derivative bailout colourings
        UsesHistogram = 1 << 12,  // designed for histogram equalisation; pair with the EQ slider
        UsesFinalZ = 1 << 13,  // reads z (zr, zi) at escape — binary/angle decomp, potential, field lines, domain coloring
        UsesInterior = 1 << 14,  // reads per-pixel cycle period / attractor / multiplier for in-set colouring
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

        /// <summary>
        /// Maximum zoom factor at which this theme renders correctly. Themes that
        /// rely on data that degrades at deep zoom (orbit traps, distance
        /// estimation, derivative bailout, interior cycle detection, etc.) should
        /// override this with a finite cap. Used by the slideshow and video-zoom
        /// automated viewers to exclude themes whose chosen zoom exceeds the cap.
        /// Default is <see cref="double.PositiveInfinity"/> (no restriction).
        /// </summary>
        public static double MaxRecommendedZoom { get; } = double.PositiveInfinity;


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

        /// <summary>
        /// Per-instance equivalent of <see cref="IColorMap.MaxRecommendedZoom"/>.
        /// Default implementation returns <see cref="double.PositiveInfinity"/>
        /// (no restriction); JSON-loaded themes carrying a cap override this.
        /// </summary>
        double DisplayMaxRecommendedZoom => double.PositiveInfinity;
    }

    /// <summary>
    /// Optional metadata carried by a runtime IColorMap instance: default
    /// post-FX values the theme would like applied on selection. A null field
    /// means "no opinion" (host slider untouched / reset to neutral). The
    /// scale matches the FloatingMenu sliders verbatim (no rescale needed).
    /// Implemented by data-driven user themes; built-in themes return null.
    /// </summary>
    public interface IThemePostFx
    {
        /// <summary>Default brightness in [-100, 100]; null = no opinion.</summary>
        int? ThemeBrightness { get; }

        /// <summary>Default contrast in [-100, 100]; null = no opinion.</summary>
        int? ThemeContrast { get; }

        /// <summary>Default adaptive contrast (histogram eq) in [0, 100]; null = no opinion.</summary>
        int? ThemeAdaptive { get; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pixel-scale extension
    //
    // DE-style colour maps (distance-field glow, edge highlighting) need to
    // know the complex-plane size of one screen pixel so the raw distance
    // estimate (in complex-plane units) can be normalised to pixel units —
    // making the same theme look correct at every zoom level. The calculator
    // assigns PixelScale once per frame before the per-pixel render loop.
    // Themes that do NOT need pixel scale simply skip this interface.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Colour map that wants the complex-plane width of a single screen pixel
    /// supplied each frame. Set once by the calculator before the render loop.
    /// </summary>
    public interface IColorMapWithPixelScale : IColorMap
    {
        /// <summary>
        /// Complex-plane width of one screen pixel for the current frame.
        /// Used to normalise raw distance estimates into pixel units.
        /// </summary>
        double PixelScale { set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // In-set delegation extension
    //
    // By default the calculator paints in-set (interior) pixels with the
    // theme's <see cref="IColorMap.InSetColor"/> property without calling
    // Map(), and passes <c>maxIter</c> as the iteration argument for escaped
    // pixels. Themes that implement IColorMapHandlesInSet opt out of both:
    //   • Interior pixels are routed through Map() with iters = maxIter so
    //     the theme can colour the inside of the set procedurally.
    //   • Escaped pixels receive their actual escape iteration as the third
    //     argument, so the theme can distinguish exterior from interior via
    //     iters >= maxIter.
    // Required by ColorGen-generated themes so the DSL inputs `iter` and
    // `isInSet` carry their documented semantics.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Marker interface: theme wants Map() invoked for in-set pixels and
    /// wants the true escape iteration count (not maxIter) for exterior
    /// pixels. Implementations must handle all of <c>smooth = 0</c>,
    /// <c>distance = 0</c>, and <c>iterations = MaxIterations</c> as the
    /// in-set sentinel.
    /// </summary>
    public interface IColorMapHandlesInSet : IColorMap { }

    // ─────────────────────────────────────────────────────────────────────────
    // Post-process extension
    //
    // Some effects need to read NEIGHBOURING pixels — Sobel emboss, ambient
    // occlusion, soft shadows.  These cannot be computed inside the per-pixel
    // Map() call.  A colour map can opt into a second pass over the finished
    // ColorBuffer by implementing IPostProcessColorMap.  The calculator invokes
    // PostProcess() once after the main render completes, giving the theme
    // access to the full screen-sized buffers.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Colour map that runs an extra full-screen pass after the per-pixel
    /// Map() / MapWithOrbit() calls have populated ColorBuffer.  The pass may
    /// read neighbour samples from any of the input float buffers
    /// (smooth/distance/normal) and modify <paramref name="colorBuf"/> in place.
    /// </summary>
    public interface IPostProcessColorMap : IColorMap
    {
        /// <summary>
        /// Apply post-process effect over the full framebuffer.
        /// Called once per render, after all per-pixel Map() calls have finished.
        /// </summary>
        /// <param name="colorBuf">ARGB output buffer (read/write).</param>
        /// <param name="smooth">Smooth iteration count per pixel.</param>
        /// <param name="nx">Surface normal X component per pixel.</param>
        /// <param name="ny">Surface normal Y component per pixel.</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="iterations">Max iterations for the current frame.</param>
        void PostProcess(uint[] colorBuf, float[] smooth, float[] nx, float[] ny,
                         int width, int height, int iterations);
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

        /// <summary>
        /// Colour for an IN-SET (non-escaping) pixel from its accumulated orbit
        /// state. On maps whose interesting region is bounded (transcendental
        /// Julia maps, Newton / Magnet basins) the orbit is sampled for the full
        /// iteration budget and <paramref name="acc"/> holds a full-orbit
        /// statistic (trap minimum, stripe / TIA sums) even though the pixel
        /// never escaped — so the interior can be coloured like Fragmentarium-
        /// style all-pixel orbit colourings rather than a flat fill.
        ///
        /// Default delegates to <see cref="MapWithOrbit"/> with a zero smooth /
        /// distance / normal (no escape data exists for an in-set pixel): orbit-
        /// trap themes colour purely from <c>acc.TrapMin</c> so this already
        /// yields the correct interior lace; stripe / TIA themes use their
        /// accumulated sums. Themes wanting a distinct interior treatment
        /// override this.
        ///
        /// Only invoked when the caller opts in (e.g. the User-Equation path's
        /// <c>UserEquationColorInterior</c> flag); the default render still paints
        /// <see cref="IColorMap.InSetColor"/>, so behaviour is unchanged unless
        /// the flag is set.
        /// </summary>
        int MapInteriorWithOrbit(int iterations, in OrbitAccumulator acc)
            => MapWithOrbit(0f, 0f, iterations, 0f, 0f, in acc);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Interior-aware extension
    //
    // Mandelbrot in-set points orbit toward a finite attracting cycle of some
    // period p ≥ 1.  Five colourings — Atom Domains, Argument, Multiplier,
    // Cycle Period, Fake DE — colour the interior of the set using this orbit
    // structure rather than escape time (which is meaningless for in-set
    // pixels).  Themes that need it implement IInteriorAwareColorMap; the
    // calculator runs a separate cycle-detection pass (Brent's algorithm) over
    // in-set pixels and fills InteriorPeriodBuffer / AttractorZrBuffer /
    // AttractorZiBuffer / MultiplierMagBuffer.  MapInterior() is then invoked
    // once per in-set pixel to produce the interior colour.
    //
    // Exterior pixels are coloured normally via Map().
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Colour map that colours the in-set (interior) region of the Mandelbrot
    /// set using attracting-cycle data captured by Brent cycle detection.
    /// </summary>
    public interface IInteriorAwareColorMap : IColorMap
    {
        /// <summary>
        /// Colour an in-set pixel from its detected cycle data.
        /// </summary>
        /// <param name="period">Detected attracting-cycle period (1..MaxPeriod); 0 if no cycle detected within search budget.</param>
        /// <param name="attractorZr">Real part of a point on the detected cycle (0 if undetected).</param>
        /// <param name="attractorZi">Imaginary part of a point on the detected cycle.</param>
        /// <param name="multiplierMag">|λ| = magnitude of the cycle multiplier ∏ 2 z_k over one period (0 if undetected). 0 ≤ |λ| ≤ 1 for hyperbolic in-set components.</param>
        /// <param name="cx">c.real (used by atom-domain / argument themes).</param>
        /// <param name="cy">c.imag.</param>
        int MapInterior(int period, float attractorZr, float attractorZi, float multiplierMag, double cx, double cy);
    }
}