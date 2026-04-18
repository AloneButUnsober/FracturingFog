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
        None         = 0,
        UsesSmooth   = 1 << 0,   // iteration count drives the colour
        UsesDistance = 1 << 1,   // exterior distance estimate influences colour
        UsesNormals   = 1 << 2,   // map reads nx, ny for 3D lighting
        Cyclic       = 1 << 3,   // gradient repeats — doesn't go dark at deep zoom
        Perceptual   = 1 << 4,   // perceptually uniform (lightness progression)
        HighContrast = 1 << 5,   // strong light/dark contrast
        GradientBased= 1 << 6,   // uses linear stop interpolation
        ThreeDEffect  = 1 << 7,   // map produces a strong 3D visual
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

        public static string Name        { get; } = "Unnamed";

        public ColorPaletteType Type { get; }

        public static string           Category    { get; } = "General";

        public static string           Description { get; } = "";

        public static ColorMapFeatures Features    { get; } = ColorMapFeatures.UsesSmooth;


        // ── Per-instance state ────────────────────────────────────────────────

        public int MaxIterations { get; set; }

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

        // ── Convenience helpers ───────────────────────────────────────────────

        /// <summary>
        /// Representative colour for use in UI swatches.
        /// Samples at 30 % of MaxIterations with a small distance value and
        /// a gently tilted surface (nx=0.3, ny=0.2) so 3D themes show shading.
        /// </summary>
        int SwatchSample
            => Map(MaxIterations * 0.30f, 0.05f, MaxIterations, 0.30f, 0.20f);
    }
}