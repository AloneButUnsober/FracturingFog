// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

    /// <summary>
    /// Colour space the gradient blends stops in (Phase A / F1). Only affects
    /// how the 256-entry LUT is *built* — zero per-pixel cost. <c>Srgb</c> is
    /// the historical byte-lerp; <c>OkLab</c> gives perceptually smooth
    /// mid-tones between distant hues; <c>Hsv</c> sweeps hue along the shorter
    /// arc for rainbow ramps.
    /// </summary>
    public enum GradientColorSpace
    {
        /// <summary>Linear byte lerp in sRGB (historical default — byte-identical).</summary>
        Srgb,
        /// <summary>Perceptually uniform OkLab blend (Björn Ottosson).</summary>
        OkLab,
        /// <summary>HSV with shorter-arc hue interpolation.</summary>
        Hsv,
    }

    /// <summary>
    /// How the cycling parameter wraps at the [0,1] boundary (Phase A / F5).
    /// <c>Repeat</c> is the historical modulo wrap; <c>PingPong</c> mirrors so
    /// there is no hard seam where the palette jumps 1→0; <c>Clamp</c> holds
    /// the endpoints.
    /// </summary>
    public enum ColorWrapMode
    {
        /// <summary>Modulo wrap (historical default).</summary>
        Repeat,
        /// <summary>Triangle-wave mirror — seamless.</summary>
        PingPong,
        /// <summary>Clamp to [0,1].</summary>
        Clamp,
    }

    /// <summary>
    /// Shape of the blend within a gradient segment (Phase B / F2). Baked into
    /// the LUT — zero per-pixel cost. <c>Linear</c> is the historical default.
    /// <c>Cubic</c> is a Catmull-Rom spline through the stops (evaluated in
    /// sRGB, independent of <see cref="GradientColorSpace"/>).
    /// </summary>
    public enum InterpolationCurve
    {
        /// <summary>Straight lerp (historical default).</summary>
        Linear,
        /// <summary>Cosine ease at both stops.</summary>
        Cosine,
        /// <summary>Catmull-Rom spline through neighbouring stops (sRGB).</summary>
        Cubic,
        /// <summary>Hard bands — hold the lower stop.</summary>
        Step,
    }

    /// <summary>
    /// Remaps the mapping scalar <c>t</c> before palette lookup (Phase B / F3;
    /// Ultra Fractal "transfer function"). All curves fix <c>f(0)=0, f(1)=1</c>
    /// so cycling seams stay continuous. Applied to Gradient + Cycling kinds
    /// (3D albedo is left on the linear scalar so material bands stay put).
    /// </summary>
    public enum TransferFunction
    {
        /// <summary>Identity (historical default).</summary>
        Linear,
        /// <summary><c>t^0.5</c> — lifts shadow detail.</summary>
        Sqrt,
        /// <summary><c>t^3</c> — compresses shadows, expands highlights.</summary>
        Cubic,
        /// <summary>Logarithmic — spreads deep detail.</summary>
        Log,
        /// <summary>Raised cosine S-curve.</summary>
        Sine,
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

        /// <summary>Interior alpha (F10). 255 = opaque (default), so themes that
        /// omit it keep the historical opaque interior byte-for-byte.</summary>
        public byte A { get; set; } = 255;

        public InSetColorData() { }

        public InSetColorData(byte r, byte g, byte b)
        {
            R = r; G = g; B = b;
        }

        /// <summary>Packs the colour as AARRGGBB. A defaults to 255, so this is
        /// the historical opaque 0xFFRRGGBB unless a theme sets a lower alpha.</summary>
        public uint ToPackedArgb()
            => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
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

        /// <summary>
        /// Colour space the gradient LUT is built in (Phase A / F1). Absent /
        /// <see cref="GradientColorSpace.Srgb"/> ⇒ byte-identical to the
        /// historical render.
        /// </summary>
        public GradientColorSpace InterpolationSpace { get; set; } = GradientColorSpace.Srgb;

        /// <summary>Segment blend shape (Phase B / F2). Default Linear.</summary>
        public InterpolationCurve InterpolationCurve { get; set; } = InterpolationCurve.Linear;

        /// <summary>
        /// Transfer curve applied to the mapping scalar (Phase B / F3). Default
        /// Linear (identity).
        /// </summary>
        public TransferFunction TransferFunction { get; set; } = TransferFunction.Linear;

        /// <summary>
        /// Blend of identity↔transfer curve in [0,1] (Phase B / F3). 1 = full
        /// curve (default), 0 = identity.
        /// </summary>
        public float TransferStrength { get; set; } = 1f;

        /// <summary>
        /// Per-theme palette gamma (Phase C / F6). Baked into the gradient LUT
        /// (<c>out = pow(in, 1/gamma)</c> per channel) → free per pixel. 1.0 =
        /// neutral (default). Independent of, and compounds with, the host's
        /// live image-gamma slider.
        /// </summary>
        public float PaletteGamma { get; set; } = 1f;

        // ── Cycling / 3D ──────────────────────────────────────────────────────

        public float CycleSpeed { get; set; } = 0.02f;

        /// <summary>
        /// Additive phase applied to the cycling parameter (Phase A / F4),
        /// rotating the palette along the iteration axis. Default 0.
        /// </summary>
        public float ColorOffset { get; set; } = 0f;

        /// <summary>
        /// Multiplies the cycling frequency (Phase A / F4) — how many palette
        /// cycles fit per <c>1/CycleSpeed</c> smooth-units. Default 1
        /// (unchanged). Distinct from <see cref="CycleSpeed"/> so density can
        /// be tuned/animated without disturbing the base rhythm.
        /// </summary>
        public float ColorDensity { get; set; } = 1f;

        /// <summary>
        /// Boundary behaviour of the cycling parameter (Phase A / F5). Default
        /// <see cref="ColorWrapMode.Repeat"/> (historical modulo wrap).
        /// </summary>
        public ColorWrapMode WrapMode { get; set; } = ColorWrapMode.Repeat;

        /// <summary>
        /// Sparkle post-fx stride (#254 / IDEA-4). Every Nth of the 256 LUT
        /// entries is brightened by <see cref="SparkleBoost"/> — a cheap
        /// glitter / lightning accent baked into the LUT (free per pixel).
        /// 0 = disabled (default), so the LUT is byte-identical.
        /// </summary>
        public int SparkleStride { get; set; } = 0;

        /// <summary>
        /// Sparkle brightness boost (#254 / IDEA-4) as a fraction of full white
        /// added to each sparkled entry (clamped at white). 0 = disabled.
        /// </summary>
        public float SparkleBoost { get; set; } = 0f;

        /// <summary>
        /// Seamless-under-rotation toggle (#255 / IDEA-5). When true the gradient
        /// LUT closes the loop (last entry ramps back to the first) so palette
        /// cycling shows no seam. Opt-in creative choice: default false leaves
        /// the palette exactly as authored (a hard seam is sometimes wanted).
        /// </summary>
        public bool SeamlessCycle { get; set; } = false;

        /// <summary>
        /// XOR index post-transform level count (#252 / IDEA-2). When &gt; 1 the
        /// mapping scalar is quantised to this many levels, XOR-ed with
        /// <see cref="XorMask"/>, and renormalised — shattering the gradient into
        /// a demoscene plaid / moiré on any field. 0 = disabled (default).
        /// </summary>
        public int XorLevels { get; set; } = 0;

        /// <summary>XOR mask applied to the quantised index (#252 / IDEA-2).
        /// Different masks give different moiré weaves. Ignored when
        /// <see cref="XorLevels"/> is 0.</summary>
        public int XorMask { get; set; } = 0;

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