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
        Pbr3D,
        /// <summary>Orbit-trap colouring (F13). The gradient maps the minimum
        /// distance from the orbit to a chosen <see cref="OrbitTrapShape"/>.
        /// Orbit-aware: rendered through the calculator's per-iteration sampling
        /// path. Reuses all the gradient (F1-F9) knobs.</summary>
        OrbitTrap,
        /// <summary>#630 — categorical billiard/scatter colouring. Stops are the
        /// per-gate palette, InSetColor is the trapped colour, and
        /// <see cref="ColorThemeData.BilliardDrive"/> selects the secondary
        /// modulation. Instantiates <c>DataDrivenBilliard</c>
        /// (an <c>IBilliardColorMap</c>).</summary>
        Categorical,
        /// <summary>#1634 — prime escape-count bands: escape counts that are prime get
        /// flat colours by class (<see cref="ColorThemeData.PrimeModulus"/>), every other
        /// count the theme's gradient dimmed by <see cref="ColorThemeData.PrimeCompositeDim"/>.
        /// The editable form of the built-in Prime Escape (Gold-Cyan) (#1486).</summary>
        PrimeBands
    }

    /// <summary>#630 — how a categorical billiard theme's secondary channel
    /// modulates the per-gate colour. Member order must match
    /// <c>BilliardDriveDef</c> so the host adapter can cast.</summary>
    public enum BilliardDrive
    {
        /// <summary>Flat per-gate colour, no secondary modulation.</summary>
        Flat,
        /// <summary>Per-gate hue, brightness fades with bounce count.</summary>
        BounceShade,
        /// <summary>Ignore the gate; ramp the stops as a gradient by path length.</summary>
        PathLength,
        /// <summary>Cycle the gate palette by bounce count.</summary>
        BounceCyclic
    }

    /// <summary>
    /// Trap shape for a <see cref="ColorThemeKind.OrbitTrap"/> theme (F13). Each
    /// value selects the geometric shape the per-iteration orbit distance is
    /// measured against; the runtime delegates the distance measurement to the
    /// matching built-in trap sampler, so no shape maths is duplicated.
    /// </summary>
    /// <summary>#1634 — how the PrimeBands kind sorts primes into colour classes.</summary>
    public enum PrimeBandModulus
    {
        /// <summary>Every odd prime in class A; 2 special.</summary>
        Single,
        /// <summary>p ≡ 1 (mod 4) class A, p ≡ 3 class B; 2 special (the built-in's split).</summary>
        Mod4,
        /// <summary>p ≡ 1 (mod 6) class A, p ≡ 5 class B; 2 and 3 special.</summary>
        Mod6,
    }

    public enum OrbitTrapShape
    {
        Point, Cross, Circle, Line, Star,
        Square, Ring, Hyperbola, Lemniscate, Cardioid,
        DiagonalCross, Triangle, Hexagon, Heart, SineWave,
        Concentric, Grid, Pinwheel, PolarRose,
        /// <summary>#1634 — the Gaussian primes (lattice scale / tail window /
        /// interior hits set by the Gaussian* fields). Appended: saved values keep their meaning.</summary>
        GaussianPrime
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
    /// What the cycling parameter is measured in (#1590). <c>Iterations</c>
    /// (historical default): <c>smooth·CycleSpeed</c>, so the palette repeats on
    /// a fixed iteration rhythm. <c>FitToRange</c>: <c>smooth / MaxIterations</c>,
    /// so density 1 spans the palette exactly once over 0..MaxIterations — for
    /// calculators that normalise their value onto that range (Lyapunov,
    /// PrecisionField, dual-orbit fields, Slit Cascade, Talbot Carpet). Offset,
    /// density and wrap then work in range units: Clamp for intensities, Repeat
    /// for phases (one turn per range).
    /// </summary>
    public enum ColorScaleMode
    {
        /// <summary>Cycle on the iteration axis (historical default).</summary>
        Iterations,
        /// <summary>Spread the palette over 0..MaxIterations.</summary>
        FitToRange,
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

        // ── Orbit Trap (F13, Kind == OrbitTrap) ───────────────────────────────

        /// <summary>Trap shape the orbit distance is measured against. Default
        /// <see cref="OrbitTrapShape.Point"/> (distance to origin).</summary>
        public OrbitTrapShape TrapShape { get; set; } = OrbitTrapShape.Point;

        /// <summary>Trap distances above this clamp to the gradient end. Smaller
        /// pulls more pixels toward the bright end. Default 2 (matches the
        /// built-in <c>OrbitTrapPowerBaseMap</c> default).</summary>
        public float TrapScale { get; set; } = 2f;

        /// <summary>Exponent of the trap-distance response curve; smaller expands
        /// small trap values into the gradient body. Default 0.35 (matches the
        /// built-in <c>OrbitTrapPowerBaseMap</c> default).</summary>
        public float TrapPower { get; set; } = 0.35f;

        /// <summary>#590 (F14) — colour in-set (non-escaping) pixels by the
        /// accumulated orbit instead of a flat interior fill, on calculator
        /// paths that support it (the User-Equation path today). Default false
        /// ⇒ flat interior (byte-identical). Only meaningful for the OrbitTrap
        /// kind.</summary>
        public bool ColorInterior { get; set; } = false;

        /// <summary>#1634 — GaussianPrime shape: the orbit is scaled by this before
        /// measuring to the Gaussian primes (0.5..16; default 6 as the built-in).</summary>
        public float GaussianLatticeScale { get; set; } = 6f;
        /// <summary>#1634 — GaussianPrime shape: tail-window block length; the window is
        /// the last block..2·block−1 iterations before escape. 0 = the whole orbit
        /// (goes flat past shallow zoom). Default 10 as the built-in.</summary>
        public int GaussianTailBlock { get; set; } = 10;
        /// <summary>#1634 — GaussianPrime shape with <see cref="ColorInterior"/>: colour the
        /// interior by the share of the attracting cycle's points that land on a Gaussian
        /// prime (the built-in Gaussian Prime Hits) instead of by trap distance.</summary>
        public bool GaussianInteriorHits { get; set; } = false;

        // ── Prime bands (#1634, Kind == PrimeBands) ───────────────────────────

        /// <summary>Colour of class-A primes; null = gold #FFCC00 (the built-in).</summary>
        public InSetColorData? PrimeColorA { get; set; }
        /// <summary>Colour of class-B primes; null = cyan (26, 153, 255) (the built-in).</summary>
        public InSetColorData? PrimeColorB { get; set; }
        /// <summary>Colour of the special primes (2, and 3 under Mod6); null = white.</summary>
        public InSetColorData? PrimeColorSpecial { get; set; }
        /// <summary>Brightness of the gradient on non-prime counts, 0..1. Default 0.25.</summary>
        public float PrimeCompositeDim { get; set; } = 0.25f;
        /// <summary>How primes split into classes. Default Mod4.</summary>
        public PrimeBandModulus PrimeModulus { get; set; } = PrimeBandModulus.Mod4;

        // ── Categorical / Billiard (#630) ─────────────────────────────────────
        /// <summary>#630 — secondary modulation for a categorical billiard theme
        /// (Kind == Categorical). Stops are the per-gate palette (index = gate),
        /// InSetColor is the trapped colour. Default Flat. Ignored by every other
        /// kind, so it round-trips harmlessly.</summary>
        public BilliardDrive BilliardDrive { get; set; } = BilliardDrive.Flat;

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

        /// <summary>What the cycling parameter is measured in (#1590). Default
        /// <see cref="ColorScaleMode.Iterations"/> (historical).</summary>
        public ColorScaleMode ScaleMode { get; set; } = ColorScaleMode.Iterations;

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

        // ── Out-of-bounds surround override (#615 Phase 1) ────────────────────

        /// <summary>
        /// Optional dedicated colour for the beyond-escape-radius surround — the
        /// flat disk that fills the screen when a 2D escape-time fractal is zoomed
        /// out far enough that the set shrinks to a dot. Null (default) = paint the
        /// escape-time gradient as before (byte-identical). Reuses the R/G/B/A
        /// carrier so JSON stays human-editable. Surfaced at runtime as
        /// IColorMap.OutOfBoundsColor.
        /// </summary>
        public InSetColorData? OutOfBoundsColor { get; set; }

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