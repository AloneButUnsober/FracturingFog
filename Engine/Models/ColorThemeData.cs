// Models/ColorThemeData.cs
//
// JSON-serializable data transfer objects describing a colour theme.
//
// A colour theme normally lives as a concrete C# class (e.g., CesiumSpectrumPhong3D)
// so its Map() method can run hot in the inner render loop.  These DTOs capture
// just the *parameters* of a theme — gradient stops, cycle rate, Phong/PBR
// lighting — so a theme can be exported to JSON, hand-edited, shared, and
// re-imported as a "data driven" theme that renders identically (or close to it).
//
// JSON layout is hand-tuned for human editing:
//   • Colours stored as plain byte R/G/B (not hex strings or System.Drawing.Color),
//     so users can tweak values in any text editor.
//   • Lighting and PBR fields are nullable — only present in the JSON when the
//     theme kind needs them.

using System.Collections.Generic;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Discriminator selecting which data-driven runtime class to instantiate.
    /// </summary>
    public enum ColorThemeKind
    {
        /// <summary>Linear gradient stretched once across the iteration range.</summary>
        Gradient,
        /// <summary>Gradient that repeats every 1/CycleSpeed smooth-units.</summary>
        Cycling,
        /// <summary>Cycling gradient with Blinn-Phong 3D lighting.</summary>
        Phong3D,
        /// <summary>Cycling gradient with Cook-Torrance PBR lighting.</summary>
        Pbr3D
    }

    // ColorStopData moved to Abstractions/Models/ColorStopData.cs so the
    // shared lib (PaletteBuilder.Lib) and this host can both reference the
    // same type. The ColorStop interop helpers (ctor from ColorStop +
    // ToColorStop()) live as extension methods in
    // Models/ColorStopDataExtensions.cs to keep System.Drawing out of the
    // abstraction surface.

    /// <summary>
    /// Directional light source parameters used by Phong/PBR themes.
    /// </summary>
    public sealed class LightSourceData
    {
        public float Lx { get; set; }
        public float Ly { get; set; }
        public float Lz { get; set; }
        public float DiffR { get; set; }
        public float DiffG { get; set; }
        public float DiffB { get; set; }
        public float SpecR { get; set; }
        public float SpecG { get; set; }
        public float SpecB { get; set; }
        public float Shininess { get; set; } = 32f;

        public LightSourceData() { }

        public LightSourceData(LightSource src)
        {
            Lx = src.Lx; Ly = src.Ly; Lz = src.Lz;
            DiffR = src.DiffR; DiffG = src.DiffG; DiffB = src.DiffB;
            SpecR = src.SpecR; SpecG = src.SpecG; SpecB = src.SpecB;
            Shininess = src.Shininess;
        }

        public LightSource ToLightSource()
            => new LightSource(Lx, Ly, Lz,
                               DiffR, DiffG, DiffB,
                               SpecR, SpecG, SpecB,
                               Shininess);
    }

    /// <summary>
    /// One band of the PBR metal/roughness piecewise function.
    /// Bands are evaluated in list order; the first band whose
    /// <see cref="UpperT"/> exceeds <c>t</c> wins.  The final band acts as the
    /// fallback for any <c>t</c> past all earlier thresholds (set its
    /// <see cref="UpperT"/> to <c>1.0</c> or higher).
    /// </summary>
    public sealed class PbrMaterialBandData
    {
        public float UpperT { get; set; } = 1.0f;
        public float Metal { get; set; } = 0.0f;
        public float Roughness { get; set; } = 0.7f;
    }

    /// <summary>
    /// Optional override colour for in-set (interior) pixels.  When present on a
    /// theme, the calculator paints unescaped pixels with this colour instead of
    /// the default opaque black (0xFF000000).  Themes that omit this field keep
    /// the historical black interior.
    /// </summary>
    public sealed class InSetColorData
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        public InSetColorData() { }

        public InSetColorData(byte r, byte g, byte b)
        {
            R = r; G = g; B = b;
        }

        /// <summary>Packs the colour as opaque 0xFFRRGGBB.</summary>
        public uint ToPackedArgb()
            => 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    }

    /// <summary>
    /// Full data definition of a colour theme.  Persisted to JSON; consumed by
    /// the data-driven runtime classes in <see cref="DataDrivenColorThemes"/>.
    /// </summary>
    public sealed class ColorThemeData
    {
        // ── Identity / display ────────────────────────────────────────────────

        public string Name { get; set; } = "Unnamed Theme";
        public string Category { get; set; } = "User";
        public string Description { get; set; } = "";

        /// <summary>
        /// Optional cap on the zoom factor at which this theme is recommended
        /// for automated viewing (slideshow / video zoom). Null = no cap.
        /// Themes whose colour signal degrades at deep zoom (e.g. orbit-aware
        /// or distance-estimation themes) carry a finite value so the automated
        /// viewers exclude them when navigating past the cap.
        /// </summary>
        public double? MaxRecommendedZoom { get; set; }

        public ColorThemeKind Kind { get; set; } = ColorThemeKind.Gradient;

        // ── Gradient (all kinds) ──────────────────────────────────────────────

        public List<ColorStopData> Stops { get; set; } = new();

        // ── Cycling / 3D ──────────────────────────────────────────────────────

        public float CycleSpeed { get; set; } = 0.02f;

        // ── 3D shared (Phong + PBR) ───────────────────────────────────────────

        public float Steepness { get; set; } = 1.6f;
        public float Ambient { get; set; } = 0.12f;
        public LightSourceData? KeyLight { get; set; }
        public LightSourceData? FillLight { get; set; }

        /// <summary>
        /// Optional third (rim) light. Null = disabled (backwards-compatible default).
        /// Intended for back/side accent highlights — typically opposite the key
        /// with high shininess and low diffuse.
        /// </summary>
        public LightSourceData? RimLight { get; set; }

        // ── Phong3D extras ────────────────────────────────────────────────────

        public float KeySpecScale { get; set; } = 0.85f;
        public float FillSpecScale { get; set; } = 0.25f;
        public float FillDiffScale { get; set; } = 0.35f;
        public float RimSpecScale { get; set; } = 1.0f;
        public float RimDiffScale { get; set; } = 0.20f;

        // ── PBR extras ────────────────────────────────────────────────────────

        public PbrLightingMode PbrLightingMode { get; set; } = PbrLightingMode.PBRRealistic;

        /// <summary>
        /// Glow boost is computed as <c>scale * pow(t, exponent)</c>.
        /// Set <see cref="GlowBoostScale"/> to 0 to disable.
        /// </summary>
        public float GlowBoostExponent { get; set; } = 8f;
        public float GlowBoostScale { get; set; } = 0f;

        /// <summary>
        /// Piecewise metal/roughness bands for PBR.  Empty list = single
        /// fallback band (metal 0, roughness 0.7).
        /// </summary>
        public List<PbrMaterialBandData> MaterialBands { get; set; } = new();

        // ── In-set override (all kinds) ───────────────────────────────────────

        /// <summary>
        /// Optional alternative colour for in-set pixels.  Null = default black.
        /// </summary>
        public InSetColorData? InSetColor { get; set; }

        // ── Post-FX defaults (optional) ───────────────────────────────────────
        // Nullable on purpose: null = "theme has no opinion, leave slider alone".
        // A non-null value tells the host to snap its post-FX slider to this
        // value when the theme is selected (unless the user has locked that
        // slider). Stored as the same integer scale as the FloatingMenu
        // sliders so JSON stays human-readable.

        /// <summary>Brightness offset in [-100, 100]; null = no default.</summary>
        public int? Brightness { get; set; }

        /// <summary>Contrast offset in [-100, 100]; null = no default.</summary>
        public int? Contrast { get; set; }

        /// <summary>Adaptive contrast (histogram eq) strength in [0, 100]; null = no default.</summary>
        public int? Adaptive { get; set; }

        // ── Lighting + post-FX preset (Phase 9, optional) ────────────────────
        //
        // Themes that ship a tuned light rig + AO/shadow/fog/bloom/tonemap
        // setup attach a non-null LightingPreset. The host applies the
        // preset to FractalParameters.Lighting on theme selection (unless
        // the user has locked their current lighting). Null = "theme has no
        // opinion, preserve user lighting" — keeps every existing theme JSON
        // bit-for-bit compatible.

        /// <summary>
        /// Optional bundled "Lighting &amp; FX" preset. Null = theme leaves
        /// the active lighting block alone (legacy behaviour); non-null =
        /// host snaps <see cref="FractalParameters.Lighting"/> to the values
        /// here when the theme is selected. Mirrors the same opt-in pattern
        /// as <see cref="Brightness"/>/<see cref="Contrast"/>/<see cref="Adaptive"/>.
        /// </summary>
        public LightingFxPresetData? LightingPreset { get; set; }
    }
}