// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/FractalRegion.cs
// Defines FractalRegion (a named, typed coordinate bookmark) and
// FractalRegionLibrary which owns both the 12 built-in regions and an
// unlimited number of user-defined regions persisted to JSON in
// %APPDATA%\FracturingFog\regions.json.
//
// Design decisions:
//   • Built-in regions are read-only; only user regions can be deleted.
//   • Coordinates are stored as double for maximum zoom precision.
//   • The library is a singleton (FractalRegionLibrary.Instance).
//   • JSON serialisation uses System.Text.Json with indented formatting
//     for human-readability — no third-party dependency required.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using FracturingFog.Abstractions;
using FracturingFog.FFMath;

namespace FracturingFog.Models
{
    // ── Data model ────────────────────────────────────────────────────────────

    /// <summary>
    /// RegionType distinguishes built-in regions (read-only, defined in code) from
    /// user-defined regions (modifiable and persisted to JSON).
    /// </summary>
    public enum RegionType
    {
        /// <summary>Built-In</summary>
        BuiltIn,
        /// <summary>User-Defined</summary>
        UserDefined
    }

    /// <summary>
    /// A named Mandelbrot coordinate bookmark.
    /// </summary>
    public sealed class FractalRegion : IJsonOnDeserialized, IJsonOnDeserializing
    {
        /// <summary>#1832 — after any JSON load (the region store, an import, a server
        /// request): a valid exact centre rewrites the limbs from itself, so every reader of
        /// the limbs sees the same centre; a damaged pair (not decimal text, or only one
        /// part) is dropped and the limbs rule (tolerant load, #964 / #1212).
        /// #1974 — then a zoom saved in the old pixel-pitch convention converts once.</summary>
        void IJsonOnDeserialized.OnDeserialized() { NormaliseCentre(); NormaliseZoom(); MigrateZoomConvention(); }

        /// <summary>#1974 — JSON without <see cref="ZoomConvention"/> was written before it
        /// existed (the legacy convention); one that has it overwrites this.</summary>
        void IJsonOnDeserializing.OnDeserializing() => ZoomConvention = 0;

        /// <summary>#1974 — the pixel-pitch convention <see cref="Zoom"/> was saved in. 0: before
        /// #1974, when <see cref="FracturingFog.ViewState.ViewCamera.UsedLegacyPitch"/> families
        /// rendered with pitch (4 / width) / zoom; 1 (current): the camera's
        /// 3.5 / (max(width, height) x zoom). Written on every save.</summary>
        public int ZoomConvention { get; set; } = CurrentZoomConvention;
        public const int CurrentZoomConvention = 1;

        /// <summary>#1974 — a legacy-convention zoom of an affected family becomes the zoom that
        /// frames the same landscape view (x 3.5/4); then the region is current, so a load,
        /// save and load converts once.</summary>
        public void MigrateZoomConvention()
        {
            if (ZoomConvention >= CurrentZoomConvention) return;
            if (FracturingFog.ViewState.ViewCamera.UsedLegacyPitch(FractalType) && Zoom > 0 && ZoomLog10 == null)
                Zoom *= FracturingFog.ViewState.ViewCamera.LegacyZoomFactor;
            ZoomConvention = CurrentZoomConvention;
        }

        /// <summary>#1832 — see <see cref="IJsonOnDeserialized.OnDeserialized"/>.</summary>
        public void NormaliseCentre()
        {
            if (CenterExactX == null && CenterExactY == null) return;
            if (CentreExact is { } e) Centre = e.ToDeep();
            else ClearCentreExact();
        }

        /// <summary>Display name shown in the UI.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Real part of the complex-plane view centre (Hi word of a double-double).</summary>
        public double CenterX { get; set; }

        /// <summary>Imaginary part of the complex-plane view centre (Hi word of a double-double).</summary>
        public double CenterY { get; set; }

        /// <summary>
        /// Low (round-off) word of the real centre.  Captures the bits that fall
        /// below ulp(CenterX) — essential at zoom ≳ 1e15 where pixel size is
        /// smaller than what a single double can address.  Defaults to 0 for
        /// backwards compatibility with regions saved before DD precision.
        /// </summary>
        public double CenterXLo { get; set; }

        /// <summary>Low (round-off) word of the imaginary centre.  See <see cref="CenterXLo"/>.</summary>
        public double CenterYLo { get; set; }
        /// <summary>QD limb 2 of real centre — used at zoom > 1e25 (~62-digit precision).
        /// Defaults to 0 for backwards compatibility with DD-only regions.</summary>
        public double CenterX2 { get; set; }

        /// <summary>QD limb 3 of real centre.  See <see cref="CenterX2"/>.</summary>
        public double CenterX3 { get; set; }

        /// <summary>QD limb 2 of imaginary centre.  See <see cref="CenterX2"/>.</summary>
        public double CenterY2 { get; set; }

        /// <summary>QD limb 3 of imaginary centre.  See <see cref="CenterX2"/>.</summary>
        public double CenterY3 { get; set; }

        // #1320 — OD limbs 4..7 (~124 digits), needed past zoom ~1e50: there the
        // QD limbs alone leave the centre up to a view-width off (and far more
        // deeper). Written only when non-zero, so shallower regions (and the
        // built-in store) serialise exactly as before; older files load them as 0.
        /// <summary>OD limb 4 of the real centre (zoom &gt; ~1e50).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CenterX4 { get; set; }
        /// <summary>OD limb 5 of the real centre.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CenterX5 { get; set; }
        /// <summary>OD limb 6 of the real centre.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CenterX6 { get; set; }
        /// <summary>OD limb 7 of the real centre.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CenterX7 { get; set; }
        /// <summary>OD limb 4 of the imaginary centre.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CenterY4 { get; set; }
        /// <summary>OD limb 5 of the imaginary centre.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CenterY5 { get; set; }
        /// <summary>OD limb 6 of the imaginary centre.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CenterY6 { get; set; }
        /// <summary>OD limb 7 of the imaginary centre.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public double CenterY7 { get; set; }

        /// <summary>#1832 (epic #1428) — the exact real centre as decimal text, when the view
        /// had a centre past OD's precision. Null (not written) otherwise, so files for
        /// views up to 1e100 are byte-identical. Where valid it wins over the limbs.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CenterExactX { get; set; }

        /// <summary>#1832 — the exact imaginary centre as decimal text (see <see cref="CenterExactX"/>).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CenterExactY { get; set; }

        /// <summary>#1832 — the precision (bits) <see cref="CenterExactX"/> / <see cref="CenterExactY"/>
        /// were written at: their text round-trips at exactly that precision. 0 (not written)
        /// for a hand-written pair, which is then read at its digit count.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int CenterExactBits { get; set; }

        /// <summary>#1832 — the exact centre from <see cref="CenterExactX"/> / <see cref="CenterExactY"/>,
        /// or null when absent or not a decimal pair (a hand-edited or damaged file falls back
        /// to the limbs).</summary>
        [JsonIgnore]
        public ExactCentre? CentreExact
            => CenterExactBits is >= 0 and <= 4 * ExactCentre.MaxDigits
               && ExactCentre.TryParse(CenterExactX, CenterExactY, out var c, CenterExactBits) ? c : null;

        /// <summary>#1832 — store an exact centre: the decimal pair, its precision and its OD limbs.</summary>
        public void SetCentreExact(ExactCentre c)
        {
            Centre = c.ToDeep();
            (CenterExactX, CenterExactY) = c.ToStrings();
            CenterExactBits = c.Precision;
        }

        /// <summary>#1832 — clear the exact centre (the limbs rule).</summary>
        public void ClearCentreExact() { CenterExactX = null; CenterExactY = null; CenterExactBits = 0; }

        /// <summary>#1320 — the full eight-limb centre.</summary>
        [JsonIgnore]
        public DeepComplex Centre
        {
            get => DeepComplex.FromLimbs(
                CenterX, CenterXLo, CenterX2, CenterX3, CenterX4, CenterX5, CenterX6, CenterX7,
                CenterY, CenterYLo, CenterY2, CenterY3, CenterY4, CenterY5, CenterY6, CenterY7);
            set
            {
                CenterX = value.Re.X0; CenterXLo = value.Re.X1; CenterX2 = value.Re.X2; CenterX3 = value.Re.X3;
                CenterX4 = value.Re.X4; CenterX5 = value.Re.X5; CenterX6 = value.Re.X6; CenterX7 = value.Re.X7;
                CenterY = value.Im.X0; CenterYLo = value.Im.X1; CenterY2 = value.Im.X2; CenterY3 = value.Im.X3;
                CenterY4 = value.Im.X4; CenterY5 = value.Im.X5; CenterY6 = value.Im.X6; CenterY7 = value.Im.X7;
            }
        }

        /// <summary>#1320 — copy all sixteen centre limbs into a view state.</summary>
        public void CopyCentreTo(FracturingFog.ViewState.FractalViewState s)
        {
            // #1832 — a valid exact centre wins: it sets the limbs from itself.
            if (CentreExact is { } e) { s.SetCenterExact(e); return; }
            s.CenterX = CenterX; s.CenterXLo = CenterXLo; s.CenterX2 = CenterX2; s.CenterX3 = CenterX3;
            s.CenterX4 = CenterX4; s.CenterX5 = CenterX5; s.CenterX6 = CenterX6; s.CenterX7 = CenterX7;
            s.CenterY = CenterY; s.CenterYLo = CenterYLo; s.CenterY2 = CenterY2; s.CenterY3 = CenterY3;
            s.CenterY4 = CenterY4; s.CenterY5 = CenterY5; s.CenterY6 = CenterY6; s.CenterY7 = CenterY7;
        }

        /// <summary>#1320 — copy all sixteen centre limbs from a view state.</summary>
        public void CopyCentreFrom(FracturingFog.ViewState.FractalViewState s)
        {
            CenterX = s.CenterX; CenterXLo = s.CenterXLo; CenterX2 = s.CenterX2; CenterX3 = s.CenterX3;
            CenterX4 = s.CenterX4; CenterX5 = s.CenterX5; CenterX6 = s.CenterX6; CenterX7 = s.CenterX7;
            CenterY = s.CenterY; CenterYLo = s.CenterYLo; CenterY2 = s.CenterY2; CenterY3 = s.CenterY3;
            CenterY4 = s.CenterY4; CenterY5 = s.CenterY5; CenterY6 = s.CenterY6; CenterY7 = s.CenterY7;
            // #1832 — the exact centre when the view has one, else none (so the limbs rule).
            if (s.CenterExact is { } e) SetCentreExactStrings(e);
            else ClearCentreExact();
        }

        private void SetCentreExactStrings(ExactCentre e)
        {
            (CenterExactX, CenterExactY) = e.ToStrings();
            CenterExactBits = e.Precision;
        }

        /// <summary>#1320 — copy all sixteen centre limbs into a Mandelbrot calculator.</summary>
        public void CopyCentreTo(MandelbrotCalculator c)
        {
            c.CenterX = CenterX; c.CenterXLo = CenterXLo; c.CenterX2 = CenterX2; c.CenterX3 = CenterX3;
            c.CenterX4 = CenterX4; c.CenterX5 = CenterX5; c.CenterX6 = CenterX6; c.CenterX7 = CenterX7;
            c.CenterY = CenterY; c.CenterYLo = CenterYLo; c.CenterY2 = CenterY2; c.CenterY3 = CenterY3;
            c.CenterY4 = CenterY4; c.CenterY5 = CenterY5; c.CenterY6 = CenterY6; c.CenterY7 = CenterY7;
            c.CenterExact = CentreExact;   // #1860: and the exact centre where the region has one
        }

        /// <summary>#27 Phase 0 — true when this region was loaded from an
        /// external (cross-user) file via import. Runtime-only ([JsonIgnore]) so
        /// it cannot be forged by the file itself. When set, applying the
        /// region's UserEquationSource / UserBulbSource stamps
        /// <see cref="UserCodeOrigin.ExternalFile"/> onto the render params so a
        /// hostile raw-C# equation is refused under the default policy.</summary>
        [JsonIgnore]
        public bool ExternalOrigin { get; set; }

        /// <summary>Full double-double real centre, assembled from CenterX (Hi) + CenterXLo (Lo).</summary>
        [JsonIgnore]
        public DD CenterDDX
        {
            get => new DD(CenterX, CenterXLo);
            set { CenterX = value.Hi; CenterXLo = value.Lo; }
        }

        /// <summary>Full double-double imaginary centre.</summary>
        [JsonIgnore]
        public DD CenterDDY
        {
            get => new DD(CenterY, CenterYLo);
            set { CenterY = value.Hi; CenterYLo = value.Lo; }
        }

        /// <summary>
        /// Zoom factor: 1.0 = full set visible, higher = zoomed in.
        /// Stored as scale width (smaller = more zoomed in) for direct use with
        /// <see cref="MandelbrotCalculator.Zoom"/>.
        /// </summary>
        public double Zoom { get; set; }

        /// <summary>#1836 (epic #1428) — log10 zoom, written only past double's range
        /// (~1.8e308), where <see cref="Zoom"/> holds double.MaxValue. Omitted otherwise, so
        /// region files up to there are unchanged.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ZoomLog10 { get; set; }

        /// <summary>#1836 — the region's zoom as log10 at any depth.</summary>
        [JsonIgnore]
        public double ZoomLog10Value => ZoomLog10 is double l && ValidLog(l) ? l : Math.Log10(Zoom);

        private static bool ValidLog(double l) => double.IsFinite(l) && FracturingFog.ViewState.ZoomLog.BeyondDouble(l);

        /// <summary>#1836 — after a JSON load: a valid log zoom sets <see cref="Zoom"/> from
        /// itself (saturated); one that isn't past double (or isn't finite) is dropped and
        /// <see cref="Zoom"/> rules.</summary>
        public void NormaliseZoom()
        {
            if (ZoomLog10 is not double l) return;
            if (ValidLog(l)) Zoom = FracturingFog.ViewState.ZoomLog.FromLog10(l);
            else ZoomLog10 = null;
        }

        /// <summary>#1836 — the region's zoom into a view state (a non-positive
        /// <see cref="Zoom"/> leaves the view's zoom, as region apply always did).</summary>
        public void CopyZoomTo(FracturingFog.ViewState.FractalViewState s)
        {
            if (ZoomLog10 is double l && ValidLog(l)) s.SetZoomLog10(l);
            else if (Zoom > 0) s.Zoom = Zoom;
        }

        /// <summary>#1836 — the view's zoom into this region (the log only past double).</summary>
        public void CopyZoomFrom(FracturingFog.ViewState.FractalViewState s)
        {
            Zoom = s.Zoom;
            ZoomLog10 = LogFieldFor(s);
        }

        /// <summary>#1836 — the <see cref="ZoomLog10"/> field for a view: its log past
        /// double, else null.</summary>
        public static double? LogFieldFor(FracturingFog.ViewState.FractalViewState s)
            => s.ZoomBeyondDouble ? s.ZoomLog10 : null;

        /// <summary>Suggested maximum iteration count, or 0 to use auto.</summary>
        public int Iterations { get; set; }

        /// <summary>
        /// Quality tier to use when rendering this region.
        /// </summary>
        [JsonIgnore]
        public QualityPreset QualityPreset { get; set; } = QualityPreset.Standard;

        /// <summary>
        /// Quality Preset Name for JSON serialization.  This is a string property that maps to the QualityPreset object.
        /// </summary>
        public string QualityPresetName
        {
            get { return QualityPreset.Name; }
            set { QualityPreset = QualityPreset.FromName(value); }
        }

        /// <summary>One-line description for the UI tooltip.</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Optional custom watermark embedded into this region's JSON.
        /// Set when the user ticks "Include watermark" in the Save Region
        /// dialog while a custom watermark is active. On recall the shell
        /// pushes this into MainViewModel.RegionEmbeddedWatermark which the
        /// precedence resolver then routes onto every render surface. Null on
        /// legacy regions; omitted from JSON when null thanks to
        /// JsonIgnoreCondition.WhenWritingNull on the library writer.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WatermarkDef? EmbeddedWatermark { get; set; }

        /// <summary>
        /// Fractal type this region targets. Serialized as the enum name (e.g. "Mandelbrot") so
        /// the JSON stays human-readable and survives enum value reordering. Defaults to
        /// <see cref="FractalType.Mandelbrot"/> for backwards compatibility with regions saved
        /// before fractal-type-aware bookmarks existed.
        /// </summary>
        [JsonConverter(typeof(FractalTypeJsonConverter))]   // #1154/#1155 — reads renamed type names
        public FractalType FractalType { get; set; } = FractalType.Mandelbrot;

        /// <summary>
        /// Name of the saved <see cref="UserEquationEntry"/> this region depends on
        /// when <see cref="FractalType"/> is <see cref="FractalType.UserEquation"/>.
        /// On recall the source is looked up by name in <see cref="UserEquationStore"/>,
        /// so editing the saved equation later updates every region that references it.
        /// Null/empty for non-UserEquation regions, or for ad-hoc equations the user
        /// never saved.
        /// </summary>
        public string? UserEquationName { get; set; }

        /// <summary>
        /// Name of the saved <see cref="SandboxEquationEntry"/> this region depends on
        /// when <see cref="FractalType"/> is <see cref="FractalType.Sandbox"/>.
        /// On recall the source is looked up by name in <see cref="SandboxEquationStore"/>.
        /// Null/empty for non-Sandbox regions, or for ad-hoc sources the user never saved.
        /// </summary>
        public string? SandboxName { get; set; }

        /// <summary>
        /// Optional friendly name for the UserBulb (3D) source captured by this region.
        /// UserBulb has no shared library yet, so the source itself is embedded in
        /// <see cref="UserBulbSource"/>. The name is informational.
        /// </summary>
        public string? UserBulbName { get; set; }

        /// <summary>
        /// Full UserBulb (3D) Step-function source recorded when the region was saved.
        /// Restored verbatim and recompiled on recall so the saved view renders the
        /// same fractal even if the user has edited the live source since. Null/empty
        /// for non-UserBulb regions.
        /// </summary>
        public string? UserBulbSource { get; set; }

        /// <summary>#1100 — bulb-language version of <see cref="UserBulbSource"/>.
        /// Null on regions saved before #1100 (version 1: -x^y meant (-x)^y).</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? UserBulbLanguageVersion { get; set; }

        /// <summary>#1100 — <see cref="UserBulbSource"/> under the current rules:
        /// upgraded from version 1 when the region predates #1100. Use this, not
        /// the raw property, when recalling the source.</summary>
        public string? EffectiveUserBulbSource()
            => string.IsNullOrWhiteSpace(UserBulbSource) || (UserBulbLanguageVersion ?? 1) >= BulbLanguageMigration.CurrentLanguageVersion
                ? UserBulbSource
                : BulbLanguageMigration.UpgradeFromVersion1(UserBulbSource).Source;

        /// <summary>UserBulb camera distance (radial). 0 = use parameter default on recall.</summary>
        public double UserBulbCameraDistance { get; set; }
        /// <summary>UserBulb camera theta (azimuth, radians).</summary>
        public double UserBulbCameraTheta { get; set; }
        /// <summary>UserBulb camera phi (polar, radians).</summary>
        public double UserBulbCameraPhi { get; set; }
        /// <summary>UserBulb light theta (radians).</summary>
        public double UserBulbLightTheta { get; set; }
        /// <summary>UserBulb light phi (radians).</summary>
        public double UserBulbLightPhi { get; set; }

        // ── Lighting & FX override (Phase 10, optional) ──────────────────────
        //
        // Region snapshot of the user's tuned "Lighting & FX" state. Null =
        // region has no opinion; recall preserves whatever lighting the user
        // currently has dialled in. Non-null = recall snaps
        // FractalParameters.Lighting to the saved values so the dramatic
        // shadow angle / volumetric fog / bloom that defined the saved view
        // come back exactly as captured.
        //
        // Uses the same DTO as ColorThemeData.LightingPreset so theme presets
        // and region overrides can round-trip through one serializer.

        /// <summary>
        /// Optional snapshot of the active <see cref="FractalParameters.Lighting"/>
        /// at the time the region was saved. Null = recall leaves user lighting
        /// alone (legacy behaviour; pre-Phase-10 regions still load cleanly).
        /// </summary>
        public LightingFxPresetData? LightingOverride { get; set; }

        /// <summary>
        /// VLAO audit #295 — when true, recalling this region applies its
        /// lighting <em>authoritatively</em>: a non-null <see cref="LightingOverride"/>
        /// is restored as before, but a null override resets lighting to stock
        /// defaults instead of inheriting whatever the installer state happens
        /// to be. This makes the region portable — it looks the same on any
        /// install. Default false = legacy "leave user lighting alone on null"
        /// so existing regions don't change behaviour. Omitted from JSON when
        /// false so legacy regions stay clean. Mirrors the authoritative apply
        /// that <see cref="Relief3D"/> already has.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LightingIsAuthoritative { get; set; }

        /// <summary>
        /// P6: optional hand-picked colour-theme names this region looks best
        /// with. When non-null+non-empty the slideshow / video slideshow draw
        /// theme picks from this pool first; unknown names are dropped, and if
        /// the curated pool produces zero valid entries the picker falls back
        /// to the compat-filtered list and then the unfiltered list (three-tier
        /// chain, never empty). Omitted from JSON when null thanks to
        /// <c>JsonIgnoreCondition.WhenWritingNull</c> so legacy regions stay
        /// clean.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? CuratedThemes { get; set; }

        /// <summary>
        /// When true, recalling this region applies its first valid
        /// <see cref="CuratedThemes"/> entry as the active colour theme, so the
        /// saved look comes back on jump. Default false = recall leaves the
        /// active theme untouched (legacy behaviour; no regression for regions
        /// that already rely on whatever theme is live). Omitted from JSON when
        /// false so legacy regions stay clean.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool UseCuratedThemesOnly { get; set; }

        /// <summary>
        /// Per-region palette-cycling (LUT rotation) preference. On recall the
        /// shell sets <c>MainViewModel.PaletteCycleEnabled</c> to this value,
        /// honouring the region's saved toggle *over* the toolbar Cycle button —
        /// some Acid Fog looks (e.g. a Flow-morph animation) read better with
        /// cycling off. Null = "no opinion" (legacy regions): recall falls back to
        /// the type default (cycle on for <see cref="FractalType.AcidWarp"/>, off
        /// otherwise). Omitted from JSON when null so legacy regions stay clean.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? PaletteCycleEnabled { get; set; }

        /// <summary>
        /// Animation Roadmap Phase 3 — optional name of a saved
        /// <c>AnimationData</c> entry in <c>AnimationLibrary</c>. On region
        /// recall the shell loads the animation onto the shared
        /// <c>AnimationBusHost</c> bus and starts playback. Null = no
        /// attached animation (default for legacy regions). Omitted from
        /// JSON when null.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AnimationName { get; set; }

        /// <summary>
        /// Video-slideshow multi-type roadmap P1 (#91) — optional snapshot of the
        /// core per-family parameters needed to faithfully reconstruct a
        /// zoomable-2D non-Mandelbrot region (Julia constant, Multibrot power,
        /// Phoenix/Glynn constants, Spider decay, Newton exponent/relaxation,
        /// Secant offset, Apollonian knobs). Null for Mandelbrot and for families
        /// whose default parameters already render correctly (Tricorn, BurningShip,
        /// Magnet, TearDrop, generated). Omitted from JSON when null so legacy
        /// regions stay clean. 3D-camera + non-spatial params are deferred to
        /// P3 (#93) / P4 (#94).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RegionFractalParams? Params { get; set; }

        /// <summary>
        /// #268 / Audio-Reactive Phase 4 fast-follow — optional snapshot of the
        /// region's audio→param modulation bindings (signal / curve / gain / bias /
        /// invert / out-range + enabled), keyed by parameter name. Hydrated into the
        /// app-scoped <c>AudioModulationManager</c> on region jump so a saved
        /// region's audio reactivity comes back on recall. Null for regions with no
        /// audio drive; omitted from JSON when null so legacy regions stay clean.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<FracturingFog.Audio.AudioParamBinding>? AudioBindings { get; set; }

        /// <summary>
        /// Optional snapshot of the Relief 3D (2D heightfield / Oblique raymarch)
        /// settings active when the region was saved. Null = the region captured
        /// no relief view (either relief was off, or it predates relief-aware
        /// bookmarks); recall then leaves the user's current relief state alone.
        /// Non-null = recall restores the full relief look — camera, tone curve,
        /// isolation cull, and mesh-export knobs — so a saved 3D relief view
        /// comes back exactly as captured. Omitted from JSON when null.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Relief3DSettings? Relief3D { get; set; }

        /// <summary>
        /// Apply this region's lighting override (if any) to the given params.
        /// No-op when the override is null. Pair with a host-side
        /// "Lock lighting on recall" toggle to let the user opt out of the
        /// override per recall.
        /// </summary>
        public void ApplyLightingTo(FractalParameters parameters)
            => LightingOverride?.ApplyTo(parameters);

        /// <summary>VLAO audit #295 — authoritatively set lighting from this
        /// region: restore the saved <see cref="LightingOverride"/> when present,
        /// or reset to <see cref="LightingFxData.CreateDefault"/> when the region
        /// has none. Use on region-to-region recall so a region with no captured
        /// lighting renders identically on every install instead of inheriting
        /// the ambient app state. Contrast with <see cref="ApplyLightingTo"/>,
        /// which leaves lighting untouched on null. Mirrors
        /// <see cref="ApplyRelief3DAuthoritative"/>.</summary>
        public void ApplyLightingAuthoritative(FractalParameters parameters)
        {
            if (parameters is null) return;
            if (LightingOverride != null)
                LightingOverride.ApplyTo(parameters);
            else
                parameters.Lighting = FracturingFog.Rendering.Lighting.LightingFxData.CreateDefault();
        }

        /// <summary>
        /// Populate the source-compiled equation slots (UserEquation / Sandbox /
        /// UserBulb, looked up by name in the user's local stores) + lighting +
        /// Relief 3D snapshot into <paramref name="p"/> for an offline render — no
        /// live host needed; the calculators lazily compile from the source
        /// strings. Mirrors the equation / lighting half of
        /// HostColorThemeService.LoadRegionFractalParams. Shared by the scene
        /// exporter and batch video (#947). The equation stores must be loaded.
        /// </summary>
        public void ApplyHeadlessParams(FractalParameters p)
        {
            // #27 Phase 0 — inline raw-C# source from a cross-user imported
            // region is refused by the gate; local-store sources re-mark trusted.
            p.UserCodeOrigin = ExternalOrigin
                ? FracturingFog.Security.UserCodeOrigin.ExternalFile
                : FracturingFog.Security.UserCodeOrigin.Interactive;

            if (FractalType == FractalType.UserEquation
                && !string.IsNullOrWhiteSpace(UserEquationName))
            {
                var entry = UserEquationStore.Instance.GetByName(UserEquationName);
                if (entry != null) { entry.ApplyTo(p); p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive; }
            }
            if (FractalType == FractalType.Sandbox
                && !string.IsNullOrWhiteSpace(SandboxName))
            {
                var entry = SandboxEquationStore.Instance.GetByName(SandboxName);
                if (entry != null) { p.SandboxSource = entry.Source; p.SandboxName = entry.Name; }
            }
            if (FractalType == FractalType.UserBulb)
            {
                var entry = !string.IsNullOrWhiteSpace(UserBulbName)
                    ? UserBulbStore.Instance.GetByName(UserBulbName)
                    : null;
                if (entry != null) { p.UserBulbSource = entry.Source; p.UserBulbName = entry.Name; p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive; }
                else if (!string.IsNullOrWhiteSpace(UserBulbSource))
                {
                    p.UserBulbSource = EffectiveUserBulbSource();   // #1100
                    p.UserBulbName = UserBulbName;
                }
                if (UserBulbCameraDistance > 0)
                {
                    p.UserBulbCameraDistance = UserBulbCameraDistance;
                    p.UserBulbCameraTheta = UserBulbCameraTheta;
                    p.UserBulbCameraPhi = UserBulbCameraPhi;
                    p.UserBulbLightTheta = UserBulbLightTheta;
                    p.UserBulbLightPhi = UserBulbLightPhi;
                }
            }
            // Region lighting override snapshot (Phase 10) — no-op when null,
            // unless the region opted into authoritative lighting (#295), in
            // which case a null override resets to stock defaults so the
            // rendered scene matches the region's portable look.
            if (LightingIsAuthoritative)
                ApplyLightingAuthoritative(p);
            else
                ApplyLightingTo(p);
            // Region Relief 3D snapshot — no-op when null.
            ApplyRelief3DTo(p);
        }

        /// <summary>#960 — authoritative per-family params: reset everything this region's
        /// family snapshot can carry to defaults, then overlay the saved snapshot (a legacy
        /// region with null <see cref="Params"/> gets the family defaults). Every region-recall
        /// path uses this instead of a bare <c>Params?.ApplyTo</c>, so a region renders the same
        /// whatever was live before (an animation, a previous region).</summary>
        public void ApplyFamilyParams(FractalParameters p)
        {
            if (p == null) return;
            RegionFractalParams.ResetFamilyToDefaults(FractalType, p);
            Params?.ApplyTo(p);
        }

        /// <summary>Apply this region's Relief 3D snapshot (if any) to the given
        /// params. No-op when null (leaves the current relief state alone).</summary>
        public void ApplyRelief3DTo(FractalParameters parameters)
            => Relief3D?.ApplyTo(parameters);

        /// <summary>Authoritatively set the relief state from this region: restore
        /// the saved relief when present, or turn relief OFF when the region has
        /// none. Use on region-to-region recall so relief toggles with the
        /// selection (a plain region clears a relief view). Contrast with
        /// <see cref="ApplyRelief3DTo"/> which leaves relief untouched on null.</summary>
        public void ApplyRelief3DAuthoritative(FractalParameters parameters)
            => Relief3DSettings.ApplyOrDisable(Relief3D, parameters);

        /// <summary>
        /// Region type (built-in or user-defined).  This is not serialized to JSON; instead, all loaded regions are
        /// assumed to be user-defined unless explicitly marked as built-in.
        /// </summary>
        [JsonIgnore]
        public RegionType RegionType { get; set; } = RegionType.UserDefined;

        /// <summary>
        /// Is Built In region (read-only, defined in code) vs User-Defined (modifiable and persisted to JSON).
        /// </summary>
        [JsonIgnore]
        public bool IsBuiltIn => RegionType == RegionType.BuiltIn;
    }

    // ── Per-family parameter snapshot (multi-type video roadmap P1, #91) ───────

    /// <summary>
    /// Minimal, JSON-lean snapshot of the core per-family parameters a
    /// zoomable-2D region needs to reconstruct its exact look for an unattended
    /// video-slideshow zoom leg. Only the fields relevant to the region's
    /// <see cref="FractalRegion.FractalType"/> are populated; the rest stay null
    /// and are omitted from JSON. <see cref="ApplyTo"/> overlays the captured
    /// fields onto a live <see cref="FractalParameters"/> at recall time.
    ///
    /// Deliberately does NOT snapshot user-code source (UserEquation / Sandbox /
    /// UserBulb — round-tripped by name/embed elsewhere), 3D camera state
    /// (P3, #93), or non-spatial knobs (P4, #94).
    /// </summary>
    public sealed class RegionFractalParams
    {
        // Every field is nullable and omitted from JSON when null so a snapshot
        // for one family (e.g. Julia) writes only its two constant fields — the
        // rest never bloat regions.json.
        private const JsonIgnoreCondition OmitNull = JsonIgnoreCondition.WhenWritingNull;

        [JsonIgnore(Condition = OmitNull)] public double? JuliaCRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? JuliaCIm { get; set; }
        // #920 faithful implosion (user-picked p/q)
        [JsonIgnore(Condition = OmitNull)] public bool?   FaithfulImplosion { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int?    FaithfulImplosionP { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int?    FaithfulImplosionQ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? FaithfulImplosionApproach { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool?   FaithfulImplosionSatellite { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int?    FaithfulImplosionParentP { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int?    FaithfulImplosionParentQ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? FaithfulImplosionParentPath { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? MultibrotExponent { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? PhoenixPRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? PhoenixPIm { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? GlynnCRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? GlynnCIm { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpiderCDecay { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? NewtonExponent { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? NewtonRelaxation { get; set; }
        // #1294 — Nova: Julia-plane switch, parameter-plane z0, Julia-plane c.
        [JsonIgnore(Condition = OmitNull)] public bool? NovaJuliaMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? NovaZ0Re { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? NovaZ0Im { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? NovaJuliaCRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? NovaJuliaCIm { get; set; }
        // #1980 — the opt-in chaos early-out (Nova, Newton); only when on / off its default threshold.
        [JsonIgnore(Condition = OmitNull)] public bool? ChaosEarlyOut { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ChaosEarlyOutLog10 { get; set; }

        // #1981 — rotation-domain colouring rides along (Julia, Phoenix, Magnet 1 / 2, Newton, Nova)
        // when a field differs from its default; ApplyTo sets only the carried ones.
        [JsonIgnore(Condition = OmitNull)] public bool? RotationDomainColoring { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? RotationDomainEpsilon { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? RotationDomainBound { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? RotationDomainShadeWidth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public uint? RotationDomainColor { get; set; }
        [JsonIgnore(Condition = OmitNull)] public uint? RotationDomainEdgeColor { get; set; }
        [JsonIgnore(Condition = OmitNull)] public uint? RotationDomainChaosColor { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SecantOffsetRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SecantOffsetIm { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? ApollonianDepth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ApollonianMinPixelRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? ApollonianColorByDepth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? AcidWarpPattern { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AcidWarpFrequency { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AcidWarpWarpStrength { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? AcidWarpMorph { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AcidWarpFlow { get; set; }
        // Chaotic billiard (#627). Stored so a saved region reproduces the exact
        // obstacle field and launch/outcome tuning. Geometry is the int cast of
        // BilliardGeometry (matches the AcidWarpPattern int-enum precedent above).
        [JsonIgnore(Condition = OmitNull)] public int? BilliardGeometry { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardDiskCount { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BilliardDiskRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BilliardSeparation { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardMaxBounces { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardGateCount { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardSeed { get; set; }
        // #1445 — launch / gate mode (int casts of BilliardLaunchMode / BilliardGateMode)
        // and miss muting. A region saved before these existed carries all seven #627
        // fields and none of these; ApplyTo then restores the #627 look.
        [JsonIgnore(Condition = OmitNull)] public int? BilliardLaunchMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardGateMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BilliardMuteMisses { get; set; }
        // #1504 — Custom geometry disk list ("x,y,r;..."); captured only when Custom is
        // the active geometry and has disks, so other billiard regions stay unchanged.
        [JsonIgnore(Condition = OmitNull)] public string? BilliardCustomDisks { get; set; }
        // #1548 — the Math Plane scene text (always captured for MathPlane, even when
        // empty: an empty scene is a real choice, not "unset").
        [JsonIgnore(Condition = OmitNull)] public string? MathPlaneScene { get; set; }
        // #1552 — the map (int casts of MathPlaneMapMode / MathPlaneInterpolation).
        [JsonIgnore(Condition = OmitNull)] public int? MathPlaneMapMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlaneM11 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlaneM12 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlaneM21 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlaneM22 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlaneT { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? MathPlaneInterpolation { get; set; }
        // #1553 — the c of a complex map.
        [JsonIgnore(Condition = OmitNull)] public double? MathPlaneCRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlaneCIm { get; set; }
        // #1554 — the fractal backdrop (int cast of MathPlaneBackdrop); its family's own
        // params are merged into the MathPlane block (see Snapshot).
        [JsonIgnore(Condition = OmitNull)] public int? MathPlaneBackdrop { get; set; }
        // #1695 — the scene script's clock (seconds).
        [JsonIgnore(Condition = OmitNull)] public double? MathPlaneScriptTime { get; set; }
        // #1713 — Math Plane 3D: the scene text and the camera target (the camera triple
        // rides the Cam3D* block).
        [JsonIgnore(Condition = OmitNull)] public string? MathPlane3DScene { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DTargetX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DTargetY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DTargetZ { get; set; }
        // #1715 — the 3×3 map (int cast of MathPlaneInterpolation).
        [JsonIgnore(Condition = OmitNull)] public bool? MathPlane3DMapOn { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM11 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM12 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM13 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM21 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM22 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM23 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM31 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM32 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DM33 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MathPlane3DT { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? MathPlane3DInterpolation { get; set; }
        // #1538 — Polygon sides; captured only when Polygon is the active geometry.
        [JsonIgnore(Condition = OmitNull)] public int? BilliardPolygonSides { get; set; }
        // #1539 — Polygon fill (captured with Polygon) and lattice rotation (captured with a lattice).
        [JsonIgnore(Condition = OmitNull)] public bool? BilliardPolygonFill { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BilliardLatticeRotation { get; set; }
        // #1506 — Billiard Rays sweep (BilliardRaysSweep = int cast of BilliardRaySweep).
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysSweep { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysCount { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysAngleCount { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BilliardRaysAngle { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BilliardRaysSpread { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BilliardRaysSourceX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BilliardRaysSourceY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysMinBounces { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BilliardRaysShowDisks { get; set; }
        // #1923 — black hole shadows (BlackHoleLayout = int cast of FracturingFog.BlackHoleLayout).
        [JsonIgnore(Condition = OmitNull)] public int? BlackHoleLayout { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleMass1 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleMass2 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleSeparation { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? BlackHoleCustom { get; set; }
        // #1995 — User Lens (only with the UserLens layout).
        [JsonIgnore(Condition = OmitNull)] public string? BlackHoleLensSource { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleLensCapture { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleCameraDistance { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleInclination { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleAzimuth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleCaptureFraction { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleTolerance { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BlackHoleMaxSteps { get; set; }
        // #1924 — colouring channel (int cast of FracturingFog.BlackHoleChannel).
        [JsonIgnore(Condition = OmitNull)] public int? BlackHoleChannel { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BlackHoleShadeFates { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleChannelScale { get; set; }
        // #1925 — projection and sky (int casts of BlackHoleProjection / BlackHoleSky).
        [JsonIgnore(Condition = OmitNull)] public int? BlackHoleProjection { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BlackHoleSky { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BlackHoleSkyDivisions { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BlackHoleSkySeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BlackHoleSkyRotation { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BlackHoleSolidBlocks { get; set; }   // #1970
        [JsonIgnore(Condition = OmitNull)] public int? BlackHoleGpuPrecision { get; set; }   // #1926
        [JsonIgnore(Condition = OmitNull)] public bool? BlackHoleDdEdges { get; set; }   // #1927
        // #1942 — Hénon-Heiles exit basins (enums as int casts).
        [JsonIgnore(Condition = OmitNull)] public double? HenonHeilesEnergy { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? HenonHeilesView { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? HenonHeilesLaunch { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? HenonHeilesLaunchAngle { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? HenonHeilesChannel { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? HenonHeilesChannelScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? HenonHeilesMaxTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? HenonHeilesTolerance { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? HenonHeilesGpuPrecision { get; set; }   // #2049
        [JsonIgnore(Condition = OmitNull)] public bool? HenonHeilesShadeExits { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? HenonHeilesSolidBlocks { get; set; }
        // #1943 — restricted three-body basins (enums as int casts).
        [JsonIgnore(Condition = OmitNull)] public int? ThreeBodyView { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ThreeBodyMassRatio { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ThreeBodyJacobi { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? ThreeBodyLaunch { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ThreeBodyLaunchAngle { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ThreeBodyCrashRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ThreeBodyEscapeRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ThreeBodyMaxTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? ThreeBodyTolerance { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? ThreeBodyNewtonIterations { get; set; }
        // #1944 — magnetised black hole (enums as int casts).
        [JsonIgnore(Condition = OmitNull)] public int? MagBhView { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MagBhField { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? MagBhBranch { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MagBhEnergy { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MagBhAngularMomentum { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MagBhLaunchAngle { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MagBhEscapeHeight { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MagBhMaxTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MagBhTolerance { get; set; }
        // #1945 — spinning compact binaries (enum as int cast).
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinMassRatio { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinChi1 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinChi2 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinPhi1 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinPhi2 { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinSeparation { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinVelocity { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? SpinBinRadiationReaction { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SpinBinOutcome { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinMergeRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinSeparateRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinMaxOrbits { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SpinBinTolerance { get; set; }
        // #1946 — Mixmaster (enum as int cast).
        [JsonIgnore(Condition = OmitNull)] public int? MixView { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MixUExit { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MixOmegaDot { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MixOmega { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MixAlphaDot { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MixBetaDot { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MixMaxTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MixTolerance { get; set; }
        // #1947 — black hole + ring (enums as int casts).
        [JsonIgnore(Condition = OmitNull)] public int? BhRingView { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BhRingMass { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BhRingRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BhRingEnergy { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BhRingAngularMomentum { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BhRingVelocityScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BhRingOrbits { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BhRingOrbitTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BhRingFateTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BhRingColour { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BhRingTolerance { get; set; }
        // #1994 — User Potential.
        [JsonIgnore(Condition = OmitNull)] public string? UserPotentialSource { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserPotentialEnergy { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? UserPotentialView { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? UserPotentialLaunch { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserPotentialLaunchAngle { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserPotentialExitRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? UserPotentialSectors { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserPotentialSectorOffset { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? UserPotentialExitIf { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? UserPotentialRotating { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserPotentialMaxTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserPotentialTolerance { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserPotentialMaxStep { get; set; }
        // #1997 — User Hamiltonian.
        [JsonIgnore(Condition = OmitNull)] public string? UserHamSource { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? UserHamDimension { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? UserHamAxisA { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? UserHamAxisB { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? UserHamFixed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? UserHamSolveFor { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? UserHamSolveSign { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserHamEnergy { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserHamExitRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? UserHamSectors { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserHamSectorOffset { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? UserHamExitIf { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserHamMaxTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserHamTolerance { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserHamMaxStep { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? UserHamProjectEvery { get; set; }
        // #1948 — microlensing.
        [JsonIgnore(Condition = OmitNull)] public int? MicroLayout { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroKappaStars { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroKappaSmooth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroShear { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroFieldRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? MicroSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? MicroMassFunction { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroMassMin { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroRaysPerPixel { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroPad { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroColourScale { get; set; }
        // #2007 — light-curve inset.
        [JsonIgnore(Condition = OmitNull)] public bool? MicroLightCurve { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroTrackX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroTrackY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MicroTrackAngle { get; set; }
        // #2021 — Cosmic Web (enums as int casts; the camera rides the generic Cam3D block).
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebGenerator { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebBoxSize { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebClumps { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebPlacement { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebEta { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebLambda { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebFirstRatio { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebTopSize { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebLevelMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebLevels { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebPointCap { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebPointSize { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebDepthFade { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebColour { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebGamma { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebGrid { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebSpectralIndex { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebSmoothing { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebGrowth { get; set; }
        // #2023 — D(r) measurement.
        [JsonIgnore(Condition = OmitNull)] public bool? CosmicWebMeasure { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebMeasureSample { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? CosmicWebDimensionOverlay { get; set; }
        // #2024 — volume render.
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebRender { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebVolumeGrid { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebVolumeSmoothing { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebVolumeOpacity { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebVolumeSteps { get; set; }
        // #2025 — Voronoi model.
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebVoronoiNuclei { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? CosmicWebVoronoiPoints { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebVoronoiField { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebVoronoiWalls { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebVoronoiFilaments { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebVoronoiClusters { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebVoronoiThickness { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? CosmicWebVolumeBrightness { get; set; }
        // #1507 — int cast of BilliardRayColorSource.
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysColorSource { get; set; }
        // #1508 — crossings (int casts of BilliardRayMode / BilliardCrossingValue).
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysCrossingWindow { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysCrossingValue { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BilliardRaysSelfCrossings { get; set; }
        // #1509 — int cast of BilliardDivergenceValue.
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysDivergenceValue { get; set; }
        // #1510 — int cast of BilliardEscapedColour.
        [JsonIgnore(Condition = OmitNull)] public int? BilliardRaysEscapedColour { get; set; }
        // #1511 — fraction of the sweep emitted.
        [JsonIgnore(Condition = OmitNull)] public double? BilliardRaysProgress { get; set; }
        // Precision-sensitivity field (#628). Tiers/metric stored as int casts of
        // PrecisionTier / PrecisionDiffMetric.
        [JsonIgnore(Condition = OmitNull)] public int? PrecisionLowTier { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? PrecisionHighTier { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? PrecisionDiffMetric { get; set; }

        // #253 — cross-fractal domain warp. Carried for the escape-time family
        // (Julia, Burning Ship, Tricorn, Multibrot, Magnet 1/2, Glynn, Phoenix,
        // Spider) whenever it's enabled, even on types whose base block is null.
        // Only recorded when on; recall resets the warp off for any region that
        // doesn't carry it (authoritative, like Relief 3D).
        [JsonIgnore(Condition = OmitNull)] public bool? DomainWarpEnabled { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DomainWarpStrength { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DomainWarpFrequency { get; set; }

        // #93 (P3) — raymarched-3D camera baseline. One generic camera triple
        // (orbit distance + azimuth/elevation) plus an optional 4D-slice offset,
        // tagged with the family it belongs to (Cam3DFamily = the int FractalType)
        // so ApplyTo can route it back to the right per-family fields without
        // needing the region's type. Captured for Mandelbulb / Mandelbox / KIFS /
        // Quaternion Julia+Mandelbrot / Kleinian / Bicomplex so a saved region —
        // and each video-slideshow camera-fly leg (#93) — reproduces the authored
        // framing. UserBulb keeps its own dedicated camera fields (user code, out
        // of the slideshow pool). Light angles are left at their live values.
        [JsonIgnore(Condition = OmitNull)] public int? Cam3DFamily { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? Cam3DDistance { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? Cam3DTheta { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? Cam3DPhi { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? Cam3DSliceW { get; set; }
        // #1440 — Wada mirror spheres (camera rides the Cam3D block above).
        [JsonIgnore(Condition = OmitNull)] public double? MirrorSpheresRadiusScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? MirrorSpheresMaxBounces { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MirrorSpheresReflectance { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? MirrorSpheresMuteMisses { get; set; }
        // #1441 — photo mode (mode = int cast of MirrorSpheresMode).
        [JsonIgnore(Condition = OmitNull)] public int? MirrorSpheresMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? MirrorSpheresOpeningColors { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? MirrorSpheresViewerBlack { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? MirrorSpheresFresnel { get; set; }
        // #1444 — orientation (int cast of MirrorSpheresOrientation) + room colour.
        [JsonIgnore(Condition = OmitNull)] public int? MirrorSpheresOrientation { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? MirrorSpheresRoomColor { get; set; }

        // #94 (P4) — non-spatial family params. These families ignore plane
        // zoom, so the video slideshow renders them as a static-hold leg; the
        // generated image is defined by a seed / preset / roughness rather than a
        // viewport, so snapshot those here to reproduce the authored look.
        [JsonIgnore(Condition = OmitNull)] public int? PlasmaSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? PlasmaRoughness { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? FlamePresetName { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? FlameGamma { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DlaParticles { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DlaSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? LogisticSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? LogisticBurnIn { get; set; }
        // #1570 — Talbot carpet (profile / view = int casts of their enums).
        [JsonIgnore(Condition = OmitNull)] public int? TalbotProfile { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? TalbotDuty { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? TalbotModes { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? TalbotView { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? TalbotDynamicRange { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? TalbotDomainContours { get; set; }   // #1598
        [JsonIgnore(Condition = OmitNull)] public int? TalbotContourCount { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? TalbotSliceOverlay { get; set; }   // #1601
        [JsonIgnore(Condition = OmitNull)] public double? TalbotSliceZeta { get; set; }
        // #1569 — slit cascade (layout / view = int casts of their enums).
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeChildren { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SlitCascadeRatio { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeDepth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SlitCascadeSeparation { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SlitCascadeWidth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SlitCascadeWavelength { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeLayout { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeView { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SlitCascadeDynamicRange { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? SlitCascadeQ { get; set; }   // #1571
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeCharge { get; set; }   // #1572
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeChargePattern { get; set; }
        // #1600 — per-level which-path (pattern = int cast of SlitCascadeWhichPathPattern).
        [JsonIgnore(Condition = OmitNull)] public double? SlitCascadeWhichPath { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeWhichPathPattern { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeWhichPathLevel { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeWhichPathMask { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeDomainContours { get; set; }   // #1598
        // #1597 — white light (int casts of WhiteLightMode).
        [JsonIgnore(Condition = OmitNull)] public int? WhiteLightSamples { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeWhiteLight { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? TalbotWhiteLight { get; set; }
        // #1599 — detection build-up, shared by SlitCascade / TalbotCarpet / JulibrotPair.
        [JsonIgnore(Condition = OmitNull)] public int? DetectionCount { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DetectionSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DetectionDotRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DetectionGain { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? SlitCascadeContourCount { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? LyapunovSequence { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? LyapunovWarmup { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? TranscendentalMap { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? TranscendentalLambdaRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? TranscendentalLambdaIm { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? TranscendentalBailout { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? MagnetConvergence { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MagnetConvergenceEpsilon { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? RandomTileSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? IFSPresetName { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? LSystemPresetName { get; set; }
        // #875 — Kleinian group preset + necklace ring size (omitted at defaults).
        [JsonIgnore(Condition = OmitNull)] public int? KleinianPreset { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? KleinianNecklaceCount { get; set; }
        // #876 — user-authored custom inversion sphere list (omitted when empty).
        [JsonIgnore(Condition = OmitNull)] public List<KleinianSphereDef>? KleinianCustomSpheres { get; set; }
        // #877 — rotation-fold angle (deg) + axis (omitted when angle is 0).
        [JsonIgnore(Condition = OmitNull)] public double? KleinianRotationAngle { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? KleinianRotationAxisX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? KleinianRotationAxisY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? KleinianRotationAxisZ { get; set; }
        // #878 — surface colour source (omitted at the Smooth default).
        [JsonIgnore(Condition = OmitNull)] public int? KleinianColorSource { get; set; }
        // #881 — sphere-trace under-relaxation factor (omitted at the 1.0 default).
        [JsonIgnore(Condition = OmitNull)] public double? KleinianDeFactor { get; set; }
        // #1335 — over-relaxed sphere tracing ω for the DE raymarchers
        // (UsesMarchRelaxation; omitted at the 1.0 plain-march default).
        [JsonIgnore(Condition = OmitNull)] public double? MarchRelaxation { get; set; }
        // #1173-C — Bicomplex slice axis (BicomplexSliceAxis; omitted at the K default).
        [JsonIgnore(Condition = OmitNull)] public int? BicomplexSliceAxis { get; set; }
        // #893 — Indra's Pearls 2D group: family + μ / traces / c + depth + mode
        // (each omitted at its default). The generator matrices derive from these.
        [JsonIgnore(Condition = OmitNull)] public int? IndrasFamily { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? IndrasMaskitMuRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? IndrasMaskitMuIm { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? IndrasGrandmaTaRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? IndrasGrandmaTaIm { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? IndrasGrandmaTbRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? IndrasGrandmaTbIm { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? IndrasGrandmaSecondSolution { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? IndrasRileyCRe { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? IndrasRileyCIm { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? IndrasMaxWordDepth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? IndrasRenderMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? IndrasColorSource { get; set; }
        // #864 — dual-orbit escape-geometry: field selector + decoupled c-seed.
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitField { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitCSeedX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitCSeedY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? DualOrbitCEqualsS { get; set; }
        // #866 — quaternion map variant: map selector + c-seed Z + s_z dial.
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitMap { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitCSeedZ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitSZ { get; set; }
        // #970 — bailout radius + GreenRatio span.
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitBailout { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitRatioSpan { get; set; }
        // #1124 — Dual Buddhabrot: the shared Buddhabrot sampler settings (first
        // family to persist them — the classic four still do not) + dual knobs.
        [JsonIgnore(Condition = OmitNull)] public int? BuddhaSamples { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BuddhaIterHigh { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BuddhaIterLow { get; set; }    // #1125 — outcome Nebulabrot bands
        [JsonIgnore(Condition = OmitNull)] public int? BuddhaIterMid { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BuddhaMinIter { get; set; }    // #1218 — classic escape types
        [JsonIgnore(Condition = OmitNull)] public int? DualBuddhaDeposit { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualBuddhaNebula { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? DualBuddhaAnti { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BuddhaQualityMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BuddhaMetropolis { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BuddhaProgressive { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BuddhaSeed { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BuddhaZoomCompensation { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? BuddhaScaleSamplesWithWindow { get; set; }   // #1224
        [JsonIgnore(Condition = OmitNull)] public double? DualBuddhaCSeedX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualBuddhaCSeedY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualBuddhaMinIter { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualBuddhaComposite { get; set; }
        [JsonIgnore(Condition = OmitNull)] public uint? DualBuddhaColorZ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public uint? DualBuddhaColorCB { get; set; }
        [JsonIgnore(Condition = OmitNull)] public uint? DualBuddhaColorCE { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualBuddhaGainZ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualBuddhaGainCB { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualBuddhaGainCE { get; set; }
        // #1116 — pair-native field scales.
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitLyapunovSpan { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitDivergenceRatio { get; set; }
        // #1120 — Böttcher-ratio domain colouring.
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitContourDensity { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? DualOrbitDomainGrid { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitDomainPalette { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? DualOrbitDomainMarkCuts { get; set; }
        // #1121 — interior phase-lag / period colouring.
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitLagColors { get; set; }
        // #1117 — per-orbit traps / stripes.
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitTrapShape { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitTrapScale { get; set; }
        // #1119 — co-moving trap frame.
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitTrapFrame { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? DualOrbitTrapRotate { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? DualOrbitTrapScaleByOrbit { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitTrapAngle { get; set; }
        // #1123 — Relief split.
        [JsonIgnore(Condition = OmitNull)] public bool? DualOrbitSplitHeight { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitHeightField { get; set; }
        // #1126 — path interference.
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitInterferenceK { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitInterferenceGamma { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitWhichPath { get; set; }   // #1567
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitAngularMomentum { get; set; }   // #1568
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitWindingMode { get; set; }   // #1568
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitInterferenceContours { get; set; }   // #1598
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitInterferenceContourCount { get; set; }
        // #1602 — N-path interference (layout = int cast of DualOrbitPathLayout).
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitInterferencePaths { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitPathLayout { get; set; }
        // #1127 — ensemble fields.
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitEnsembleN { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitEnsembleRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitEnsembleSectors { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitUncertaintyLevels { get; set; }
        // #1128 — FTLE / anisotropy / LIC.
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitFtleSpan { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitAnisotropyScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitLicSource { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitLicLength { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitLicStrength { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? DualOrbitLyapunovSmooth { get; set; }   // #1144
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitStripeDensity { get; set; }
        // #1118 — distance estimates / dual outline.
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitDEScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitOutlineWidth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public uint? DualOrbitOutlineColorZ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public uint? DualOrbitOutlineColorC { get; set; }
        // #1122 — bivariate modes.
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitPalette2D { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitBivariateScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitPhaseK { get; set; }
        // #971 — slice axes + fixed s when s is not an image axis.
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitSliceAxes { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitSX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitSY { get; set; }
        // #980 — per-orbit layer colouring (#979).
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitColorMode { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? DualOrbitThemeZ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? DualOrbitThemeC { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitLayerBlend { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitOpacityZ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitOpacityC { get; set; }
        // #972 — dual-orbit volume (camera + s.y ride the Cam3D block; s.y as the slice).
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitVolumeSXCenter { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DualOrbitVolumeHalfHeight { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? DualOrbitVolumeColor { get; set; }
        // #909 — quaternion Mandelbrot dual-orbit surface colouring.
        [JsonIgnore(Condition = OmitNull)] public bool? QMandelDualOrbitColor { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? QMandelDualSeedX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? QMandelDualSeedY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? QMandelDualSeedZ { get; set; }

        // #961 — every animatable param of a family is captured, so an animation's
        // leftover value can't leak into the next region and a saved region can
        // reproduce it. Each is omitted at its FractalParameters default (recall
        // resets the family to defaults first, #960).
        [JsonIgnore(Condition = OmitNull)] public double? EscapeIterationScale { get; set; }
        // #1275 — Mandelbrot BLA tolerance; omitted at the 1e-6 default.
        [JsonIgnore(Condition = OmitNull)] public double? BlaTolerance { get; set; }
        // #1344 — Mandelbrot adaptive anti-aliasing; each omitted at its default.
        [JsonIgnore(Condition = OmitNull)] public bool? AdaptiveAa { get; set; }
        // #1343 — Mandelbrot solid-region skipping; omitted at its default (off).
        [JsonIgnore(Condition = OmitNull)] public bool? SolidRegionSkip { get; set; }
        // #1394 — its per-frame Auto decision; omitted at its default (on).
        [JsonIgnore(Condition = OmitNull)] public bool? SolidRegionAuto { get; set; }
        // #1629 — Mandelbrot deep-zoom evaluator (DeepLaMode: 0 Off, 1 Auto, 2 On); omitted at Auto.
        [JsonIgnore(Condition = OmitNull)] public int? DeepLa { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AdaptiveAaThreshold { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AdaptiveAaTolerance { get; set; }
        // #1981 part 2 — the distance-estimate refinement of the adaptive AA mask (families with a
        // distance source, AdaptiveAaDistanceCapable), carried when not at its defaults.
        [JsonIgnore(Condition = OmitNull)] public bool? AdaptiveAaDistanceSkip { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AdaptiveAaDistanceMinPx { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? IFSIterations { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? LSystemDepth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? AttractorPresetName { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AttractorA { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AttractorB { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AttractorC { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AttractorD { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserEquationRotationDegrees { get; set; }
        /// <summary>#1242 — the User Equation's z₀ seed expression (e.g. a
        /// critical point picked in Explore ▸ Find). Null when blank (z₀ = 0),
        /// so the saved equation's own seed stands.</summary>
        [JsonIgnore(Condition = OmitNull)] public string? UserEquationSeed { get; set; }
        /// <summary>#1249 — User Equation / Sandbox render settings (escape radius
        /// override, convergence bailout, interior-orbit colouring). Captured only
        /// when they differ from the defaults.</summary>
        [JsonIgnore(Condition = OmitNull)] public double? UserEquationEscapeRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? UserEquationBailoutCondition { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? UserEquationBailoutReplacesModulus { get; set; }
        [JsonIgnore(Condition = OmitNull)] public bool? UserEquationColorInterior { get; set; }
        /// <summary>#1989 — the real-Newton potential and its root list (null when off / discovered).</summary>
        [JsonIgnore(Condition = OmitNull)] public string? UserEquationPotential { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? UserEquationNewtonRoots { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? BulbPower { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? BulbIterations { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? UserBulbIterations { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserBulbTime { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MandelboxScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MandelboxFixedRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? MandelboxMinRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? MandelboxIterations { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? KifsFold { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? KifsScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? KifsOffsetX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? KifsOffsetY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? KifsOffsetZ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? KifsIterations { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? QJuliaCX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? QJuliaCY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? QJuliaCZ { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? QJuliaCW { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AcidWarpCenterX { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AcidWarpCenterY { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? FlameVibrancy { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? KleinianSphereScale { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? KleinianIterations { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? RandomTileCount { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? RandomTileRelief { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? RandomTileSizeExponent { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? RandomTileGap { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? RandomTileMinPixelRadius { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? RandomTileShape { get; set; }

        // #961 — omit-at-default helpers against one stock FractalParameters, so the
        // default is never restated as a literal here.
        static readonly FractalParameters D = new();
        static readonly JsonSerializerOptions s_omitNull = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        static double? Nd(double v, double d) => v != d ? v : null;
        static int? Ni(int v, int d) => v != d ? v : null;

        /// <summary>
        /// Capture the P1-relevant parameters for <paramref name="type"/> from a
        /// live <paramref name="p"/>. Returns null when the family needs nothing
        /// (its defaults already reproduce the look) or when <paramref name="p"/>
        /// is null — so a Mandelbrot region never carries an empty block.
        /// </summary>
        /// <summary>Fractal types whose renderer (EscapeTimeCalculator) honours
        /// the #253 cross-fractal domain warp.</summary>
        internal static bool SupportsDomainWarp(FractalType t) =>
            t is FractalType.Julia or FractalType.BurningShip or FractalType.Tricorn
              or FractalType.Multibrot or FractalType.Magnet1 or FractalType.Magnet2
              or FractalType.Glynn or FractalType.Phoenix or FractalType.Spider;

        /// <summary>#1981 — the families with rotation-domain colouring (EscapeTimeCalculator's and
        /// NewtonCalculator's passes).</summary>
        internal static bool RotationDomainCapable(FractalType t) =>
            t is FractalType.Julia or FractalType.Phoenix or FractalType.Magnet1 or FractalType.Magnet2
              or FractalType.Newton or FractalType.Nova or FractalType.UserEquation;   // #1984

        /// <summary>#1981 part 2 — the families whose adaptive AA mask can use a distance estimate:
        /// the exterior DE (Mandelbrot, Julia, Multibrot) or the rotation-domain distance.</summary>
        internal static bool AdaptiveAaDistanceCapable(FractalType t) =>
            t is FractalType.Mandelbrot or FractalType.Multibrot || RotationDomainCapable(t);

        /// <summary>#1738 — the family's current orbit camera (distance, theta, phi), the values a
        /// partial Cam3D* set keeps for its missing keys. Null for a type without one; the switch
        /// in <see cref="ApplyTo"/> covers the same families.</summary>
        internal static (double Distance, double Theta, double Phi)? CameraOf(FractalType type, FractalParameters p) => type switch
        {
            FractalType.Mandelbulb => (p.BulbCameraDistance, p.BulbCameraTheta, p.BulbCameraPhi),
            FractalType.Mandelbox => (p.MandelboxCameraDistance, p.MandelboxCameraTheta, p.MandelboxCameraPhi),
            FractalType.Kifs => (p.KifsCameraDistance, p.KifsCameraTheta, p.KifsCameraPhi),
            FractalType.QuaternionJulia => (p.QJuliaCameraDistance, p.QJuliaCameraTheta, p.QJuliaCameraPhi),
            FractalType.QuaternionMandelbrot => (p.QMandelCameraDistance, p.QMandelCameraTheta, p.QMandelCameraPhi),
            FractalType.Kleinian => (p.KleinianCameraDistance, p.KleinianCameraTheta, p.KleinianCameraPhi),
            FractalType.MirrorSpheres => (p.MirrorSpheresCameraDistance, p.MirrorSpheresCameraTheta, p.MirrorSpheresCameraPhi),
            FractalType.MathPlane3D => (p.MathPlane3DCameraDistance, p.MathPlane3DCameraTheta, p.MathPlane3DCameraPhi),
            FractalType.CosmicWeb => (p.CosmicWebCameraDistance, p.CosmicWebCameraTheta, p.CosmicWebCameraPhi),   // #2021
            FractalType.BicomplexMandelbrot => (p.BicomplexCameraDistance, p.BicomplexCameraTheta, p.BicomplexCameraPhi),
            FractalType.Coquaternion => (p.CoquaternionCameraDistance, p.CoquaternionCameraTheta, p.CoquaternionCameraPhi),
            FractalType.Julibrot => (p.DualOrbitVolumeCameraDistance, p.DualOrbitVolumeCameraTheta, p.DualOrbitVolumeCameraPhi),
            _ => null,
        };

        /// <summary>#954 — just the generic 3D camera block (family + distance /
        /// azimuth / elevation / slice) of <see cref="Snapshot"/>, or null when the
        /// type has none. <see cref="ApplyTo"/> is null-guarded per field, so
        /// re-applying this touches ONLY the camera — an orbit can drive the
        /// azimuth every frame without resetting other (possibly animated) params.</summary>
        public static RegionFractalParams? CameraSnapshot(FractalType type, FractalParameters? p)
        {
            var full = Snapshot(type, p);
            if (full?.Cam3DFamily is null || full.Cam3DTheta is null) return null;
            return new RegionFractalParams
            {
                Cam3DFamily = full.Cam3DFamily,
                Cam3DDistance = full.Cam3DDistance,
                Cam3DTheta = full.Cam3DTheta,
                Cam3DPhi = full.Cam3DPhi,
                Cam3DSliceW = full.Cam3DSliceW,
            };
        }

        public static RegionFractalParams? Snapshot(FractalType type, FractalParameters? p)
        {
            if (p == null) return null;
            var rp = type switch
            {
                FractalType.Julia => new RegionFractalParams
                {
                    JuliaCRe = p.JuliaC.Real,
                    JuliaCIm = p.JuliaC.Imaginary,
                    // #920 faithful implosion — only when engaged, so plain Julia regions stay clean.
                    FaithfulImplosion         = p.FaithfulImplosion ? true : null,
                    FaithfulImplosionP        = p.FaithfulImplosion ? p.FaithfulImplosionP : null,
                    FaithfulImplosionQ        = p.FaithfulImplosion ? p.FaithfulImplosionQ : null,
                    FaithfulImplosionApproach = p.FaithfulImplosion ? p.FaithfulImplosionApproach : null,
                    FaithfulImplosionSatellite = (p.FaithfulImplosion && p.FaithfulImplosionSatellite) ? true : null,
                    FaithfulImplosionParentP = (p.FaithfulImplosion && p.FaithfulImplosionParentQ > 1) ? p.FaithfulImplosionParentP : null,
                    FaithfulImplosionParentQ = (p.FaithfulImplosion && p.FaithfulImplosionParentQ > 1) ? p.FaithfulImplosionParentQ : null,
                    FaithfulImplosionParentPath = (p.FaithfulImplosion && !string.IsNullOrWhiteSpace(p.FaithfulImplosionParentPath)) ? p.FaithfulImplosionParentPath : null,
                    EscapeIterationScale = Nd(p.EscapeIterationScale, D.EscapeIterationScale),   // #961
                    SolidRegionSkip = p.SolidRegionSkip != D.SolidRegionSkip ? p.SolidRegionSkip : null,   // #1394 part 2
                    SolidRegionAuto = p.SolidRegionAuto != D.SolidRegionAuto ? p.SolidRegionAuto : null,
                },
                FractalType.Multibrot => new RegionFractalParams
                {
                    MultibrotExponent = p.MultibrotExponent,
                    SolidRegionSkip = p.SolidRegionSkip != D.SolidRegionSkip ? p.SolidRegionSkip : null,   // #1394 part 2
                    SolidRegionAuto = p.SolidRegionAuto != D.SolidRegionAuto ? p.SolidRegionAuto : null,
                },
                FractalType.Phoenix => new RegionFractalParams
                {
                    PhoenixPRe = p.PhoenixP.Real,
                    PhoenixPIm = p.PhoenixP.Imaginary,
                },
                FractalType.Glynn => new RegionFractalParams
                {
                    GlynnCRe = p.GlynnC.Real,
                    GlynnCIm = p.GlynnC.Imaginary,
                },
                FractalType.Spider => new RegionFractalParams
                {
                    SpiderCDecay = p.SpiderCDecay,
                },
                // Magnet 1/2 (#852): persist the convergence-colouring toggle +
                // epsilon only when they differ from the defaults (on / 1e-4),
                // so a default-look region still snapshots to null (base block
                // stays lean; the #253 warp can still ride along below).
                FractalType.Magnet1 or FractalType.Magnet2
                    when !p.MagnetConvergence || p.MagnetConvergenceEpsilon != 1e-4
                    => new RegionFractalParams
                    {
                        MagnetConvergence = p.MagnetConvergence ? null : (bool?)false,
                        MagnetConvergenceEpsilon = p.MagnetConvergenceEpsilon != 1e-4
                            ? p.MagnetConvergenceEpsilon : (double?)null,
                    },
                // Newton-family basins share NewtonExponent + NewtonRelaxation.
                FractalType.Newton or FractalType.Halley => new RegionFractalParams
                {
                    NewtonExponent = p.NewtonExponent,
                    NewtonRelaxation = p.NewtonRelaxation,
                    ChaosEarlyOut = type == FractalType.Newton && p.ChaosEarlyOut ? true : null,   // #1980
                    ChaosEarlyOutLog10 = type == FractalType.Newton && p.ChaosEarlyOutLog10 != 200 ? p.ChaosEarlyOutLog10 : null,
                },
                // #1294 — Nova adds the +c term: its plane switch, z0 and Julia c.
                FractalType.Nova => new RegionFractalParams
                {
                    NewtonExponent = p.NewtonExponent,
                    NewtonRelaxation = p.NewtonRelaxation,
                    NovaJuliaMode = p.NovaJuliaMode,
                    NovaZ0Re = p.NovaZ0.Real,
                    NovaZ0Im = p.NovaZ0.Imaginary,
                    NovaJuliaCRe = p.NovaJuliaC.Real,
                    NovaJuliaCIm = p.NovaJuliaC.Imaginary,
                    ChaosEarlyOut = p.ChaosEarlyOut ? true : null,                                 // #1980
                    ChaosEarlyOutLog10 = p.ChaosEarlyOutLog10 != 200 ? p.ChaosEarlyOutLog10 : null,
                },
                FractalType.Secant => new RegionFractalParams
                {
                    NewtonExponent = p.NewtonExponent,
                    SecantOffsetRe = p.SecantInitialOffset.Real,
                    SecantOffsetIm = p.SecantInitialOffset.Imaginary,
                    NewtonRelaxation = Nd(p.NewtonRelaxation, D.NewtonRelaxation),   // #961
                },
                FractalType.Apollonian => new RegionFractalParams
                {
                    ApollonianDepth = p.ApollonianDepth,
                    ApollonianMinPixelRadius = p.ApollonianMinPixelRadius,
                    ApollonianColorByDepth = p.ApollonianColorByDepth,
                },
                FractalType.ChaoticBilliard => new RegionFractalParams
                {
                    BilliardGeometry = (int)p.BilliardGeometry,
                    BilliardDiskCount = p.BilliardDiskCount,
                    BilliardDiskRadius = p.BilliardDiskRadius,
                    BilliardSeparation = p.BilliardSeparation,
                    BilliardMaxBounces = p.BilliardMaxBounces,
                    BilliardGateCount = p.BilliardGateCount,
                    BilliardSeed = p.BilliardSeed,
                    BilliardLaunchMode = (int)p.BilliardLaunchMode,
                    BilliardGateMode = (int)p.BilliardGateMode,
                    BilliardMuteMisses = p.BilliardMuteMisses,
                    BilliardCustomDisks = p.BilliardGeometry == FracturingFog.BilliardGeometry.Custom
                                          && !string.IsNullOrWhiteSpace(p.BilliardCustomDisks)
                        ? p.BilliardCustomDisks : null,
                    BilliardPolygonSides = p.BilliardGeometry == FracturingFog.BilliardGeometry.Polygon ? p.BilliardPolygonSides : null,
                    BilliardPolygonFill = p.BilliardGeometry == FracturingFog.BilliardGeometry.Polygon ? p.BilliardPolygonFill : null,
                    BilliardLatticeRotation = FracturingFog.Models.BilliardLayout.IsLattice(p.BilliardGeometry) ? p.BilliardLatticeRotation : null,
                },
                // #1548 — the whole Math Plane scene is one text field.
                FractalType.MathPlane => new RegionFractalParams
                {
                    // Canonical one-line form when it parses (a multi-line edit box
                    // value would break a --param on the command line); else verbatim.
                    MathPlaneScene = FracturingFog.Models.MathPlaneScene.TryParse(p.MathPlaneScene, out var mpScene, out _)
                        ? mpScene.Serialize() : p.MathPlaneScene ?? string.Empty,
                    MathPlaneMapMode = (int)p.MathPlaneMapMode,
                    MathPlaneM11 = p.MathPlaneM11,
                    MathPlaneM12 = p.MathPlaneM12,
                    MathPlaneM21 = p.MathPlaneM21,
                    MathPlaneM22 = p.MathPlaneM22,
                    MathPlaneT = p.MathPlaneT,
                    MathPlaneInterpolation = (int)p.MathPlaneInterpolation,
                    MathPlaneCRe = p.MathPlaneCRe,
                    MathPlaneCIm = p.MathPlaneCIm,
                    MathPlaneBackdrop = (int)p.MathPlaneBackdrop,
                    MathPlaneScriptTime = p.MathPlaneScriptTime,
                },
                // #1506 — the shared billiard scene (no launch / gate / mute: those
                // colour ChaoticBilliard only) plus the sweep.
                // #1923 — black hole shadows: every setting (Custom holes only when used).
                FractalType.BlackHoleShadow => new RegionFractalParams
                {
                    BlackHoleLayout = (int)p.BlackHoleLayout,
                    BlackHoleMass1 = p.BlackHoleMass1,
                    BlackHoleMass2 = p.BlackHoleMass2,
                    BlackHoleSeparation = p.BlackHoleSeparation,
                    BlackHoleCustom = p.BlackHoleLayout is FracturingFog.BlackHoleLayout.Custom or FracturingFog.BlackHoleLayout.UserLens
                                      && !string.IsNullOrWhiteSpace(p.BlackHoleCustom) ? p.BlackHoleCustom : null,
                    BlackHoleLensSource = p.BlackHoleLayout == FracturingFog.BlackHoleLayout.UserLens ? p.BlackHoleLensSource ?? "" : null,
                    BlackHoleLensCapture = p.BlackHoleLayout == FracturingFog.BlackHoleLayout.UserLens ? p.BlackHoleLensCapture : null,
                    BlackHoleCameraDistance = p.BlackHoleCameraDistance,
                    BlackHoleInclination = p.BlackHoleInclination,
                    BlackHoleAzimuth = p.BlackHoleAzimuth,
                    BlackHoleCaptureFraction = p.BlackHoleCaptureFraction,
                    BlackHoleTolerance = p.BlackHoleTolerance,
                    BlackHoleMaxSteps = p.BlackHoleMaxSteps,
                    BlackHoleChannel = (int)p.BlackHoleChannel,
                    BlackHoleShadeFates = p.BlackHoleShadeFates,
                    BlackHoleChannelScale = p.BlackHoleChannelScale,
                    BlackHoleProjection = (int)p.BlackHoleProjection,
                    BlackHoleSky = (int)p.BlackHoleSky,
                    BlackHoleSkyDivisions = p.BlackHoleSkyDivisions,
                    BlackHoleSkySeed = p.BlackHoleSkySeed,
                    BlackHoleSkyRotation = p.BlackHoleSkyRotation,
                    BlackHoleSolidBlocks = p.BlackHoleSolidBlocks,
                    BlackHoleGpuPrecision = (int)p.BlackHoleGpuPrecision,
                    BlackHoleDdEdges = p.BlackHoleDdEdges,
                },
                // #1942 — Hénon-Heiles exit basins: every setting.
                FractalType.HenonHeilesBasins => new RegionFractalParams
                {
                    HenonHeilesEnergy = p.HenonHeilesEnergy,
                    HenonHeilesView = (int)p.HenonHeilesView,
                    HenonHeilesLaunch = (int)p.HenonHeilesLaunch,
                    HenonHeilesLaunchAngle = p.HenonHeilesLaunchAngle,
                    HenonHeilesChannel = (int)p.HenonHeilesChannel,
                    HenonHeilesChannelScale = p.HenonHeilesChannelScale,
                    HenonHeilesMaxTime = p.HenonHeilesMaxTime,
                    HenonHeilesTolerance = p.HenonHeilesTolerance,
                    HenonHeilesGpuPrecision = (int)p.HenonHeilesGpuPrecision,
                    HenonHeilesShadeExits = p.HenonHeilesShadeExits,
                    HenonHeilesSolidBlocks = p.HenonHeilesSolidBlocks,
                },
                // #1943 — restricted three-body basins: every setting.
                FractalType.ThreeBodyBasins => new RegionFractalParams
                {
                    ThreeBodyView = (int)p.ThreeBodyView,
                    ThreeBodyMassRatio = p.ThreeBodyMassRatio,
                    ThreeBodyJacobi = p.ThreeBodyJacobi,
                    ThreeBodyLaunch = (int)p.ThreeBodyLaunch,
                    ThreeBodyLaunchAngle = p.ThreeBodyLaunchAngle,
                    ThreeBodyCrashRadius = p.ThreeBodyCrashRadius,
                    ThreeBodyEscapeRadius = p.ThreeBodyEscapeRadius,
                    ThreeBodyMaxTime = p.ThreeBodyMaxTime,
                    ThreeBodyTolerance = p.ThreeBodyTolerance,
                    ThreeBodyNewtonIterations = p.ThreeBodyNewtonIterations,
                },
                // #1944 — magnetised black hole: every setting.
                FractalType.MagnetisedBlackHole => new RegionFractalParams
                {
                    MagBhView = (int)p.MagBhView,
                    MagBhField = p.MagBhField,
                    MagBhBranch = (int)p.MagBhBranch,
                    MagBhEnergy = p.MagBhEnergy,
                    MagBhAngularMomentum = p.MagBhAngularMomentum,
                    MagBhLaunchAngle = p.MagBhLaunchAngle,
                    MagBhEscapeHeight = p.MagBhEscapeHeight,
                    MagBhMaxTime = p.MagBhMaxTime,
                    MagBhTolerance = p.MagBhTolerance,
                },
                // #1945 — spinning compact binaries: every setting.
                FractalType.SpinningBinaryBasins => new RegionFractalParams
                {
                    SpinBinMassRatio = p.SpinBinMassRatio,
                    SpinBinChi1 = p.SpinBinChi1,
                    SpinBinChi2 = p.SpinBinChi2,
                    SpinBinPhi1 = p.SpinBinPhi1,
                    SpinBinPhi2 = p.SpinBinPhi2,
                    SpinBinSeparation = p.SpinBinSeparation,
                    SpinBinVelocity = p.SpinBinVelocity,
                    SpinBinRadiationReaction = p.SpinBinRadiationReaction,
                    SpinBinOutcome = (int)p.SpinBinOutcome,
                    SpinBinMergeRadius = p.SpinBinMergeRadius,
                    SpinBinSeparateRadius = p.SpinBinSeparateRadius,
                    SpinBinMaxOrbits = p.SpinBinMaxOrbits,
                    SpinBinTolerance = p.SpinBinTolerance,
                },
                // #1946 — Mixmaster: every setting.
                FractalType.MixmasterBasins => new RegionFractalParams
                {
                    MixView = (int)p.MixView,
                    MixUExit = p.MixUExit,
                    MixOmegaDot = p.MixOmegaDot,
                    MixOmega = p.MixOmega,
                    MixAlphaDot = p.MixAlphaDot,
                    MixBetaDot = p.MixBetaDot,
                    MixMaxTime = p.MixMaxTime,
                    MixTolerance = p.MixTolerance,
                },
                // #1947 — black hole + ring: every setting.
                FractalType.BlackHoleRingOrbits => new RegionFractalParams
                {
                    BhRingView = (int)p.BhRingView,
                    BhRingMass = p.BhRingMass,
                    BhRingRadius = p.BhRingRadius,
                    BhRingEnergy = p.BhRingEnergy,
                    BhRingAngularMomentum = p.BhRingAngularMomentum,
                    BhRingVelocityScale = p.BhRingVelocityScale,
                    BhRingOrbits = p.BhRingOrbits,
                    BhRingOrbitTime = p.BhRingOrbitTime,
                    BhRingFateTime = p.BhRingFateTime,
                    BhRingColour = (int)p.BhRingColour,
                    BhRingTolerance = p.BhRingTolerance,
                },
                // #1994 — User Potential: every setting, the texts included.
                FractalType.UserPotentialBasins => new RegionFractalParams
                {
                    UserPotentialSource = p.UserPotentialSource ?? "",
                    UserPotentialEnergy = p.UserPotentialEnergy,
                    UserPotentialView = (int)p.UserPotentialView,
                    UserPotentialLaunch = (int)p.UserPotentialLaunch,
                    UserPotentialLaunchAngle = p.UserPotentialLaunchAngle,
                    UserPotentialExitRadius = p.UserPotentialExitRadius,
                    UserPotentialSectors = p.UserPotentialSectors,
                    UserPotentialSectorOffset = p.UserPotentialSectorOffset,
                    UserPotentialExitIf = p.UserPotentialExitIf ?? "",
                    UserPotentialRotating = p.UserPotentialRotating,
                    UserPotentialMaxTime = p.UserPotentialMaxTime,
                    UserPotentialTolerance = p.UserPotentialTolerance,
                    UserPotentialMaxStep = p.UserPotentialMaxStep,
                },
                // #1997 — User Hamiltonian: every setting, the texts included.
                FractalType.UserHamiltonianBasins => new RegionFractalParams
                {
                    UserHamSource = p.UserHamSource ?? "",
                    UserHamDimension = p.UserHamDimension,
                    UserHamAxisA = p.UserHamAxisA ?? "",
                    UserHamAxisB = p.UserHamAxisB ?? "",
                    UserHamFixed = p.UserHamFixed ?? "",
                    UserHamSolveFor = p.UserHamSolveFor ?? "",
                    UserHamSolveSign = p.UserHamSolveSign,
                    UserHamEnergy = p.UserHamEnergy,
                    UserHamExitRadius = p.UserHamExitRadius,
                    UserHamSectors = p.UserHamSectors,
                    UserHamSectorOffset = p.UserHamSectorOffset,
                    UserHamExitIf = p.UserHamExitIf ?? "",
                    UserHamMaxTime = p.UserHamMaxTime,
                    UserHamTolerance = p.UserHamTolerance,
                    UserHamMaxStep = p.UserHamMaxStep,
                    UserHamProjectEvery = p.UserHamProjectEvery,
                },
                // #2021 — Cosmic Web: every setting; the orbit camera in the generic Cam3D block.
                FractalType.CosmicWeb => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.CosmicWebCameraDistance,
                    Cam3DTheta = p.CosmicWebCameraTheta,
                    Cam3DPhi = p.CosmicWebCameraPhi,
                    CosmicWebGenerator = (int)p.CosmicWebGenerator,
                    CosmicWebSeed = p.CosmicWebSeed,
                    CosmicWebBoxSize = p.CosmicWebBoxSize,
                    CosmicWebClumps = p.CosmicWebClumps,
                    CosmicWebPlacement = (int)p.CosmicWebPlacement,
                    CosmicWebEta = p.CosmicWebEta,
                    CosmicWebLambda = p.CosmicWebLambda,
                    CosmicWebFirstRatio = p.CosmicWebFirstRatio,
                    CosmicWebTopSize = p.CosmicWebTopSize,
                    CosmicWebLevelMode = (int)p.CosmicWebLevelMode,
                    CosmicWebLevels = p.CosmicWebLevels,
                    CosmicWebPointCap = p.CosmicWebPointCap,
                    CosmicWebPointSize = p.CosmicWebPointSize,
                    CosmicWebDepthFade = p.CosmicWebDepthFade,
                    CosmicWebColour = (int)p.CosmicWebColour,
                    CosmicWebGamma = p.CosmicWebGamma,
                    CosmicWebGrid = p.CosmicWebGrid,
                    CosmicWebSpectralIndex = p.CosmicWebSpectralIndex,
                    CosmicWebSmoothing = p.CosmicWebSmoothing,
                    CosmicWebGrowth = p.CosmicWebGrowth,
                    CosmicWebMeasure = p.CosmicWebMeasure,
                    CosmicWebMeasureSample = p.CosmicWebMeasureSample,
                    CosmicWebDimensionOverlay = p.CosmicWebDimensionOverlay,
                    CosmicWebRender = (int)p.CosmicWebRender,
                    CosmicWebVolumeGrid = p.CosmicWebVolumeGrid,
                    CosmicWebVolumeSmoothing = p.CosmicWebVolumeSmoothing,
                    CosmicWebVolumeOpacity = p.CosmicWebVolumeOpacity,
                    CosmicWebVolumeSteps = p.CosmicWebVolumeSteps,
                    CosmicWebVoronoiNuclei = p.CosmicWebVoronoiNuclei,
                    CosmicWebVoronoiPoints = p.CosmicWebVoronoiPoints,
                    CosmicWebVoronoiField = p.CosmicWebVoronoiField,
                    CosmicWebVoronoiWalls = p.CosmicWebVoronoiWalls,
                    CosmicWebVoronoiFilaments = p.CosmicWebVoronoiFilaments,
                    CosmicWebVoronoiClusters = p.CosmicWebVoronoiClusters,
                    CosmicWebVoronoiThickness = p.CosmicWebVoronoiThickness,
                    CosmicWebVolumeBrightness = p.CosmicWebVolumeBrightness,
                },
                // #1948 — microlensing: every setting.
                FractalType.MicrolensingMap => new RegionFractalParams
                {
                    MicroLayout = (int)p.MicroLayout,
                    MicroKappaStars = p.MicroKappaStars,
                    MicroKappaSmooth = p.MicroKappaSmooth,
                    MicroShear = p.MicroShear,
                    MicroFieldRadius = p.MicroFieldRadius,
                    MicroSeed = p.MicroSeed,
                    MicroMassFunction = (int)p.MicroMassFunction,
                    MicroMassMin = p.MicroMassMin,
                    MicroRaysPerPixel = p.MicroRaysPerPixel,
                    MicroPad = p.MicroPad,
                    MicroColourScale = p.MicroColourScale,
                    MicroLightCurve = p.MicroLightCurve,
                    MicroTrackX = p.MicroTrackX,
                    MicroTrackY = p.MicroTrackY,
                    MicroTrackAngle = p.MicroTrackAngle,
                },
                FractalType.BilliardRays => new RegionFractalParams
                {
                    BilliardGeometry = (int)p.BilliardGeometry,
                    BilliardDiskCount = p.BilliardDiskCount,
                    BilliardDiskRadius = p.BilliardDiskRadius,
                    BilliardSeparation = p.BilliardSeparation,
                    BilliardMaxBounces = p.BilliardMaxBounces,
                    BilliardSeed = p.BilliardSeed,
                    BilliardCustomDisks = p.BilliardGeometry == FracturingFog.BilliardGeometry.Custom
                                          && !string.IsNullOrWhiteSpace(p.BilliardCustomDisks)
                        ? p.BilliardCustomDisks : null,
                    BilliardPolygonSides = p.BilliardGeometry == FracturingFog.BilliardGeometry.Polygon ? p.BilliardPolygonSides : null,
                    BilliardPolygonFill = p.BilliardGeometry == FracturingFog.BilliardGeometry.Polygon ? p.BilliardPolygonFill : null,
                    BilliardLatticeRotation = FracturingFog.Models.BilliardLayout.IsLattice(p.BilliardGeometry) ? p.BilliardLatticeRotation : null,
                    BilliardRaysSweep = (int)p.BilliardRaysSweep,
                    BilliardRaysCount = p.BilliardRaysCount,
                    BilliardRaysAngleCount = p.BilliardRaysAngleCount,
                    BilliardRaysAngle = p.BilliardRaysAngle,
                    BilliardRaysSpread = p.BilliardRaysSpread,
                    BilliardRaysSourceX = p.BilliardRaysSourceX,
                    BilliardRaysSourceY = p.BilliardRaysSourceY,
                    BilliardRaysSeed = p.BilliardRaysSeed,
                    BilliardRaysMinBounces = p.BilliardRaysMinBounces,
                    BilliardRaysShowDisks = p.BilliardRaysShowDisks,
                    // #1507 — gates now colour Billiard Rays too.
                    BilliardGateMode = (int)p.BilliardGateMode,
                    BilliardGateCount = p.BilliardGateCount,
                    BilliardRaysColorSource = (int)p.BilliardRaysColorSource,
                    BilliardRaysMode = (int)p.BilliardRaysMode,
                    BilliardRaysCrossingWindow = p.BilliardRaysCrossingWindow,
                    BilliardRaysCrossingValue = (int)p.BilliardRaysCrossingValue,
                    BilliardRaysSelfCrossings = p.BilliardRaysSelfCrossings,
                    BilliardRaysDivergenceValue = (int)p.BilliardRaysDivergenceValue,
                    BilliardRaysEscapedColour = (int)p.BilliardRaysEscapedColour,
                    BilliardRaysProgress = p.BilliardRaysProgress,
                },
                FractalType.PrecisionField => new RegionFractalParams
                {
                    PrecisionLowTier = (int)p.PrecisionLowTier,
                    PrecisionHighTier = (int)p.PrecisionHighTier,
                    PrecisionDiffMetric = (int)p.PrecisionDiffMetric,
                },
                FractalType.AcidWarp => new RegionFractalParams
                {
                    AcidWarpPattern = p.AcidWarpPattern,
                    AcidWarpFrequency = p.AcidWarpFrequency,
                    AcidWarpWarpStrength = p.AcidWarpWarpStrength,
                    AcidWarpMorph = p.AcidWarpMorph ? true : null,
                    AcidWarpFlow = p.AcidWarpMorph ? p.AcidWarpFlow : null,
                    AcidWarpCenterX = Nd(p.AcidWarpCenterX, D.AcidWarpCenterX),   // #961
                    AcidWarpCenterY = Nd(p.AcidWarpCenterY, D.AcidWarpCenterY),
                },
                // #93 (P3) — raymarched-3D camera baseline. Each family stores its
                // orbit camera under its own fields; capture into the generic
                // Cam3D* block tagged with the family.
                FractalType.Mandelbulb => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.BulbCameraDistance,
                    Cam3DTheta = p.BulbCameraTheta,
                    Cam3DPhi = p.BulbCameraPhi,
                    BulbPower = Nd(p.BulbPower, D.BulbPower),   // #961
                    BulbIterations = Ni(p.BulbIterations, D.BulbIterations),
                },
                FractalType.Mandelbox => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.MandelboxCameraDistance,
                    Cam3DTheta = p.MandelboxCameraTheta,
                    Cam3DPhi = p.MandelboxCameraPhi,
                    MandelboxScale = Nd(p.MandelboxScale, D.MandelboxScale),   // #961
                    MandelboxFixedRadius = Nd(p.MandelboxFixedRadius, D.MandelboxFixedRadius),
                    MandelboxMinRadius = Nd(p.MandelboxMinRadius, D.MandelboxMinRadius),
                    MandelboxIterations = Ni(p.MandelboxIterations, D.MandelboxIterations),
                },
                FractalType.Kifs => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.KifsCameraDistance,
                    Cam3DTheta = p.KifsCameraTheta,
                    Cam3DPhi = p.KifsCameraPhi,
                    KifsFold = p.KifsFold != D.KifsFold ? (int)p.KifsFold : null,   // #961
                    KifsScale = Nd(p.KifsScale, D.KifsScale),
                    KifsOffsetX = Nd(p.KifsOffsetX, D.KifsOffsetX),
                    KifsOffsetY = Nd(p.KifsOffsetY, D.KifsOffsetY),
                    KifsOffsetZ = Nd(p.KifsOffsetZ, D.KifsOffsetZ),
                    KifsIterations = Ni(p.KifsIterations, D.KifsIterations),
                },
                FractalType.QuaternionJulia => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.QJuliaCameraDistance,
                    Cam3DTheta = p.QJuliaCameraTheta,
                    Cam3DPhi = p.QJuliaCameraPhi,
                    Cam3DSliceW = p.QJuliaSliceW,
                    QJuliaCX = Nd(p.QJuliaCX, D.QJuliaCX),   // #961
                    QJuliaCY = Nd(p.QJuliaCY, D.QJuliaCY),
                    QJuliaCZ = Nd(p.QJuliaCZ, D.QJuliaCZ),
                    QJuliaCW = Nd(p.QJuliaCW, D.QJuliaCW),
                },
                FractalType.QuaternionMandelbrot => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.QMandelCameraDistance,
                    Cam3DTheta = p.QMandelCameraTheta,
                    Cam3DPhi = p.QMandelCameraPhi,
                    Cam3DSliceW = p.QMandelSliceW,
                    // #909 — dual-orbit surface colouring (omitted when off).
                    QMandelDualOrbitColor = p.QMandelDualOrbitColor ? true : (bool?)null,
                    QMandelDualSeedX = p.QMandelDualOrbitColor ? p.QMandelDualSeedX : (double?)null,
                    QMandelDualSeedY = p.QMandelDualOrbitColor ? p.QMandelDualSeedY : (double?)null,
                    QMandelDualSeedZ = p.QMandelDualOrbitColor ? p.QMandelDualSeedZ : (double?)null,
                },
                FractalType.Kleinian => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.KleinianCameraDistance,
                    Cam3DTheta = p.KleinianCameraTheta,
                    Cam3DPhi = p.KleinianCameraPhi,
                    KleinianPreset = p.KleinianPreset != FracturingFog.KleinianPreset.Tetrahedral
                        ? (int)p.KleinianPreset : null,
                    KleinianNecklaceCount = p.KleinianNecklaceCount != 6
                        ? p.KleinianNecklaceCount : (int?)null,
                    KleinianCustomSpheres = p.KleinianCustomSpheres.Count > 0
                        ? p.KleinianCustomSpheres.ConvertAll(s => s.Clone()) : null,
                    KleinianRotationAngle = p.KleinianRotationAngle != 0.0 ? p.KleinianRotationAngle : (double?)null,
                    KleinianRotationAxisX = p.KleinianRotationAngle != 0.0 ? p.KleinianRotationAxisX : (double?)null,
                    KleinianRotationAxisY = p.KleinianRotationAngle != 0.0 ? p.KleinianRotationAxisY : (double?)null,
                    KleinianRotationAxisZ = p.KleinianRotationAngle != 0.0 ? p.KleinianRotationAxisZ : (double?)null,
                    KleinianColorSource = p.KleinianColorSource != FracturingFog.KleinianColorSource.Smooth
                        ? (int)p.KleinianColorSource : (int?)null,
                    KleinianDeFactor = p.KleinianDeFactor != 1.0 ? p.KleinianDeFactor : (double?)null,
                    KleinianSphereScale = Nd(p.KleinianSphereScale, D.KleinianSphereScale),   // #961
                    KleinianIterations = Ni(p.KleinianIterations, D.KleinianIterations),
                },
                // #1713 — Math Plane 3D: scene (canonical one-line form when it parses),
                // target, and the orbit camera in the generic Cam3D block.
                FractalType.MathPlane3D => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.MathPlane3DCameraDistance,
                    Cam3DTheta = p.MathPlane3DCameraTheta,
                    Cam3DPhi = p.MathPlane3DCameraPhi,
                    MathPlane3DScene = FracturingFog.Models.MathPlane3DScene.TryParse(p.MathPlane3DScene, out var mp3Scene, out _)
                        ? mp3Scene.Serialize() : p.MathPlane3DScene ?? string.Empty,
                    MathPlane3DTargetX = p.MathPlane3DTargetX,
                    MathPlane3DTargetY = p.MathPlane3DTargetY,
                    MathPlane3DTargetZ = p.MathPlane3DTargetZ,
                    MathPlane3DMapOn = p.MathPlane3DMapOn,
                    MathPlane3DM11 = p.MathPlane3DM11,
                    MathPlane3DM12 = p.MathPlane3DM12,
                    MathPlane3DM13 = p.MathPlane3DM13,
                    MathPlane3DM21 = p.MathPlane3DM21,
                    MathPlane3DM22 = p.MathPlane3DM22,
                    MathPlane3DM23 = p.MathPlane3DM23,
                    MathPlane3DM31 = p.MathPlane3DM31,
                    MathPlane3DM32 = p.MathPlane3DM32,
                    MathPlane3DM33 = p.MathPlane3DM33,
                    MathPlane3DT = p.MathPlane3DT,
                    MathPlane3DInterpolation = (int)p.MathPlane3DInterpolation,
                },
                FractalType.MirrorSpheres => new RegionFractalParams   // #1440
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.MirrorSpheresCameraDistance,
                    Cam3DTheta = p.MirrorSpheresCameraTheta,
                    Cam3DPhi = p.MirrorSpheresCameraPhi,
                    MirrorSpheresRadiusScale = Nd(p.MirrorSpheresRadiusScale, D.MirrorSpheresRadiusScale),
                    MirrorSpheresMaxBounces = Ni(p.MirrorSpheresMaxBounces, D.MirrorSpheresMaxBounces),
                    MirrorSpheresReflectance = Nd(p.MirrorSpheresReflectance, D.MirrorSpheresReflectance),
                    MirrorSpheresMuteMisses = p.MirrorSpheresMuteMisses != D.MirrorSpheresMuteMisses
                        ? p.MirrorSpheresMuteMisses : (bool?)null,
                    // #1441 — photo mode.
                    MirrorSpheresMode = p.MirrorSpheresMode != D.MirrorSpheresMode ? (int)p.MirrorSpheresMode : (int?)null,
                    MirrorSpheresOpeningColors = p.MirrorSpheresOpeningColors != D.MirrorSpheresOpeningColors
                        ? p.MirrorSpheresOpeningColors : null,
                    MirrorSpheresViewerBlack = p.MirrorSpheresViewerBlack != D.MirrorSpheresViewerBlack
                        ? p.MirrorSpheresViewerBlack : (bool?)null,
                    MirrorSpheresFresnel = p.MirrorSpheresFresnel != D.MirrorSpheresFresnel
                        ? p.MirrorSpheresFresnel : (bool?)null,
                    // #1444 — orientation + room.
                    MirrorSpheresOrientation = p.MirrorSpheresOrientation != D.MirrorSpheresOrientation
                        ? (int)p.MirrorSpheresOrientation : (int?)null,
                    MirrorSpheresRoomColor = p.MirrorSpheresRoomColor != D.MirrorSpheresRoomColor
                        ? p.MirrorSpheresRoomColor : null,
                },
                FractalType.BicomplexMandelbrot => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.BicomplexCameraDistance,
                    Cam3DTheta = p.BicomplexCameraTheta,
                    Cam3DPhi = p.BicomplexCameraPhi,
                    Cam3DSliceW = p.BicomplexSliceW,
                    BicomplexSliceAxis = p.BicomplexSliceAxis != FracturingFog.Models.BicomplexSliceAxis.K
                        ? (int)p.BicomplexSliceAxis : (int?)null,
                },
                FractalType.Coquaternion => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.CoquaternionCameraDistance,
                    Cam3DTheta = p.CoquaternionCameraTheta,
                    Cam3DPhi = p.CoquaternionCameraPhi,
                    Cam3DSliceW = p.CoquaternionSliceW,
                },
                // #972 — dual-orbit volume: camera + s.y (the slice of the 4D (z0, s)
                // space) in the Cam3D block; sweep window + colour source omitted at default.
                FractalType.Julibrot => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.DualOrbitVolumeCameraDistance,
                    Cam3DTheta = p.DualOrbitVolumeCameraTheta,
                    Cam3DPhi = p.DualOrbitVolumeCameraPhi,
                    Cam3DSliceW = p.DualOrbitVolumeSY,
                    DualOrbitVolumeSXCenter = p.DualOrbitVolumeSXCenter != -0.75 ? p.DualOrbitVolumeSXCenter : (double?)null,
                    DualOrbitVolumeHalfHeight = p.DualOrbitVolumeHalfHeight != 1.4 ? p.DualOrbitVolumeHalfHeight : (double?)null,
                    DualOrbitVolumeColor = p.DualOrbitVolumeColor != FracturingFog.DualOrbitVolumeColor.ExternalAngle ? (int)p.DualOrbitVolumeColor : (int?)null,
                },
                // #94 (P4) — non-spatial families. Static-hold legs reproduce the
                // authored generated image from its seed / preset / roughness.
                FractalType.Plasma => new RegionFractalParams
                {
                    PlasmaSeed = p.PlasmaSeed,
                    PlasmaRoughness = p.PlasmaRoughness,
                },
                FractalType.Flame => new RegionFractalParams
                {
                    FlamePresetName = p.FlamePresetName,
                    FlameGamma = p.FlameGamma,
                    FlameVibrancy = Nd(p.FlameVibrancy, D.FlameVibrancy),   // #961
                },
                FractalType.Dla => new RegionFractalParams
                {
                    DlaParticles = p.DlaParticles,
                    DlaSeed = p.DlaSeed,
                },
                FractalType.Logistic => new RegionFractalParams
                {
                    LogisticSeed = p.LogisticSeed,
                    LogisticBurnIn = p.LogisticBurnIn,
                },
                // #1570 — the SlitCascade profile reads the cascade's own settings, so they
                // ride along only then.
                FractalType.TalbotCarpet => new RegionFractalParams
                {
                    TalbotProfile = (int)p.TalbotProfile,
                    TalbotDuty = p.TalbotDuty,
                    TalbotModes = p.TalbotModes,
                    TalbotView = (int)p.TalbotView,
                    TalbotDynamicRange = p.TalbotDynamicRange,
                    TalbotDomainContours = (int)p.TalbotDomainContours,
                    TalbotContourCount = p.TalbotContourCount,
                    TalbotSliceOverlay = p.TalbotSliceOverlay,
                    TalbotSliceZeta = p.TalbotSliceZeta,
                    TalbotWhiteLight = (int)p.TalbotWhiteLight,
                    WhiteLightSamples = p.WhiteLightSamples,
                    DetectionCount = p.DetectionCount,
                    DetectionSeed = p.DetectionSeed,
                    DetectionDotRadius = p.DetectionDotRadius,
                    DetectionGain = p.DetectionGain,
                    SlitCascadeChildren = p.TalbotProfile == FracturingFog.TalbotProfile.SlitCascade ? p.SlitCascadeChildren : null,
                    SlitCascadeRatio = p.TalbotProfile == FracturingFog.TalbotProfile.SlitCascade ? p.SlitCascadeRatio : null,
                    SlitCascadeDepth = p.TalbotProfile == FracturingFog.TalbotProfile.SlitCascade ? p.SlitCascadeDepth : null,
                    SlitCascadeSeparation = p.TalbotProfile == FracturingFog.TalbotProfile.SlitCascade ? p.SlitCascadeSeparation : null,
                    SlitCascadeWidth = p.TalbotProfile == FracturingFog.TalbotProfile.SlitCascade ? p.SlitCascadeWidth : null,
                    SlitCascadeWavelength = p.TalbotProfile == FracturingFog.TalbotProfile.SlitCascade ? p.SlitCascadeWavelength : null,
                },
                FractalType.SlitCascade => new RegionFractalParams
                {
                    SlitCascadeChildren = p.SlitCascadeChildren,
                    SlitCascadeRatio = p.SlitCascadeRatio,
                    SlitCascadeDepth = p.SlitCascadeDepth,
                    SlitCascadeSeparation = p.SlitCascadeSeparation,
                    SlitCascadeWidth = p.SlitCascadeWidth,
                    SlitCascadeWavelength = p.SlitCascadeWavelength,
                    SlitCascadeLayout = (int)p.SlitCascadeLayout,
                    SlitCascadeView = (int)p.SlitCascadeView,
                    SlitCascadeDynamicRange = p.SlitCascadeDynamicRange,
                    SlitCascadeQ = p.SlitCascadeQ,
                    SlitCascadeCharge = p.SlitCascadeCharge,
                    SlitCascadeChargePattern = (int)p.SlitCascadeChargePattern,
                    SlitCascadeWhichPath = p.SlitCascadeWhichPath,
                    SlitCascadeWhichPathPattern = (int)p.SlitCascadeWhichPathPattern,
                    SlitCascadeWhichPathLevel = p.SlitCascadeWhichPathLevel,
                    SlitCascadeWhichPathMask = p.SlitCascadeWhichPathMask,
                    SlitCascadeDomainContours = (int)p.SlitCascadeDomainContours,
                    SlitCascadeContourCount = p.SlitCascadeContourCount,
                    SlitCascadeWhiteLight = (int)p.SlitCascadeWhiteLight,
                    WhiteLightSamples = p.WhiteLightSamples,
                    DetectionCount = p.DetectionCount,
                    DetectionSeed = p.DetectionSeed,
                    DetectionDotRadius = p.DetectionDotRadius,
                    DetectionGain = p.DetectionGain,
                },
                FractalType.Lyapunov => new RegionFractalParams
                {
                    LyapunovSequence = p.LyapunovSequence,
                    LyapunovWarmup = p.LyapunovWarmup,
                },
                FractalType.TranscendentalJulia => new RegionFractalParams
                {
                    TranscendentalMap = (int)p.TranscendentalMap,
                    TranscendentalLambdaRe = p.TranscendentalLambdaRe,
                    TranscendentalLambdaIm = p.TranscendentalLambdaIm,
                    TranscendentalBailout = p.TranscendentalBailout,
                },
                FractalType.RandomTile => new RegionFractalParams
                {
                    RandomTileSeed = p.RandomTileSeed,
                    RandomTileCount = Ni(p.RandomTileCount, D.RandomTileCount),   // #961
                    RandomTileRelief = Nd(p.RandomTileRelief, D.RandomTileRelief),
                    RandomTileSizeExponent = Nd(p.RandomTileSizeExponent, D.RandomTileSizeExponent),
                    RandomTileGap = Nd(p.RandomTileGap, D.RandomTileGap),
                    RandomTileMinPixelRadius = Nd(p.RandomTileMinPixelRadius, D.RandomTileMinPixelRadius),
                    RandomTileShape = p.RandomTileShape != D.RandomTileShape ? (int)p.RandomTileShape : null,
                },
                FractalType.IFS => new RegionFractalParams
                {
                    IFSPresetName = p.IFSPresetName,
                    IFSIterations = Ni(p.IFSIterations, D.IFSIterations),   // #961
                },
                FractalType.LSystem => new RegionFractalParams
                {
                    LSystemPresetName = p.LSystemPresetName,
                    LSystemDepth = Ni(p.LSystemDepth, D.LSystemDepth),   // #961
                },
                // #864 — dual-orbit escape-geometry: field + decoupled c-seed +
                // control flag, each omitted at its default.
                FractalType.JulibrotPair => new RegionFractalParams
                {
                    DualOrbitMap = p.DualOrbitMap != FracturingFog.DualOrbitMap.ComplexPlane ? (int)p.DualOrbitMap : (int?)null,
                    DualOrbitField = p.DualOrbitField != FracturingFog.DualOrbitField.EscapeSeparation ? (int)p.DualOrbitField : (int?)null,
                    DualOrbitCSeedX = p.DualOrbitCSeedX != 0.5 ? p.DualOrbitCSeedX : (double?)null,
                    DualOrbitCSeedY = p.DualOrbitCSeedY != 0.0 ? p.DualOrbitCSeedY : (double?)null,
                    DualOrbitCSeedZ = p.DualOrbitCSeedZ != 0.3 ? p.DualOrbitCSeedZ : (double?)null,
                    DualOrbitSZ = p.DualOrbitSZ != 0.0 ? p.DualOrbitSZ : (double?)null,
                    DualOrbitCEqualsS = p.DualOrbitCEqualsS ? true : (bool?)null,
                    DualOrbitBailout = p.DualOrbitBailout != 128.0 ? p.DualOrbitBailout : (double?)null,
                    DualOrbitRatioSpan = p.DualOrbitRatioSpan != 8.0 ? p.DualOrbitRatioSpan : (double?)null,
                    DualOrbitLyapunovSpan = p.DualOrbitLyapunovSpan != 2.0 ? p.DualOrbitLyapunovSpan : (double?)null,
                    DualOrbitDivergenceRatio = p.DualOrbitDivergenceRatio != 4.0 ? p.DualOrbitDivergenceRatio : (double?)null,
                    DualOrbitContourDensity = p.DualOrbitContourDensity != D.DualOrbitContourDensity ? p.DualOrbitContourDensity : (double?)null,
                    DualOrbitDomainGrid = p.DualOrbitDomainGrid ? true : (bool?)null,
                    DualOrbitDomainPalette = p.DualOrbitDomainPalette != D.DualOrbitDomainPalette ? (int)p.DualOrbitDomainPalette : (int?)null,
                    DualOrbitDomainMarkCuts = p.DualOrbitDomainMarkCuts ? (bool?)null : false,
                    DualOrbitLagColors = p.DualOrbitLagColors != D.DualOrbitLagColors ? (int)p.DualOrbitLagColors : (int?)null,
                    DualOrbitTrapShape = p.DualOrbitTrapShape != D.DualOrbitTrapShape ? (int)p.DualOrbitTrapShape : (int?)null,
                    DualOrbitTrapScale = p.DualOrbitTrapScale != D.DualOrbitTrapScale ? p.DualOrbitTrapScale : (double?)null,
                    DualOrbitTrapFrame = p.DualOrbitTrapFrame != D.DualOrbitTrapFrame ? (int)p.DualOrbitTrapFrame : (int?)null,
                    DualOrbitTrapRotate = p.DualOrbitTrapRotate ? (bool?)null : false,
                    DualOrbitTrapScaleByOrbit = p.DualOrbitTrapScaleByOrbit ? true : (bool?)null,
                    DualOrbitTrapAngle = p.DualOrbitTrapAngle != D.DualOrbitTrapAngle ? p.DualOrbitTrapAngle : (double?)null,
                    DualOrbitSplitHeight = p.DualOrbitSplitHeight ? true : (bool?)null,
                    DualOrbitHeightField = p.DualOrbitHeightField != D.DualOrbitHeightField ? (int)p.DualOrbitHeightField : (int?)null,
                    DualOrbitInterferenceK = p.DualOrbitInterferenceK != D.DualOrbitInterferenceK ? p.DualOrbitInterferenceK : (double?)null,
                    DualOrbitInterferenceGamma = p.DualOrbitInterferenceGamma != D.DualOrbitInterferenceGamma ? p.DualOrbitInterferenceGamma : (double?)null,
                    DualOrbitWhichPath = p.DualOrbitWhichPath != D.DualOrbitWhichPath ? p.DualOrbitWhichPath : (double?)null,
                    DualOrbitAngularMomentum = p.DualOrbitAngularMomentum != D.DualOrbitAngularMomentum ? p.DualOrbitAngularMomentum : (int?)null,
                    DualOrbitWindingMode = p.DualOrbitWindingMode != D.DualOrbitWindingMode ? (int)p.DualOrbitWindingMode : (int?)null,
                    DualOrbitInterferenceContours = p.DualOrbitInterferenceContours != D.DualOrbitInterferenceContours ? (int)p.DualOrbitInterferenceContours : (int?)null,
                    DualOrbitInterferenceContourCount = p.DualOrbitInterferenceContourCount != D.DualOrbitInterferenceContourCount ? p.DualOrbitInterferenceContourCount : (int?)null,
                    DualOrbitInterferencePaths = p.DualOrbitInterferencePaths != D.DualOrbitInterferencePaths ? p.DualOrbitInterferencePaths : (int?)null,   // #1602
                    DualOrbitPathLayout = p.DualOrbitPathLayout != D.DualOrbitPathLayout ? (int)p.DualOrbitPathLayout : (int?)null,
                    DetectionCount = p.DetectionCount != D.DetectionCount ? p.DetectionCount : (int?)null,   // #1599
                    DetectionSeed = p.DetectionSeed != D.DetectionSeed ? p.DetectionSeed : (int?)null,
                    DetectionDotRadius = p.DetectionDotRadius != D.DetectionDotRadius ? p.DetectionDotRadius : (int?)null,
                    DetectionGain = p.DetectionGain != D.DetectionGain ? p.DetectionGain : (double?)null,
                    DualOrbitEnsembleN = p.DualOrbitEnsembleN != D.DualOrbitEnsembleN ? p.DualOrbitEnsembleN : (int?)null,
                    DualOrbitEnsembleRadius = p.DualOrbitEnsembleRadius != D.DualOrbitEnsembleRadius ? p.DualOrbitEnsembleRadius : (double?)null,
                    DualOrbitEnsembleSectors = p.DualOrbitEnsembleSectors != D.DualOrbitEnsembleSectors ? p.DualOrbitEnsembleSectors : (int?)null,
                    DualOrbitUncertaintyLevels = p.DualOrbitUncertaintyLevels != D.DualOrbitUncertaintyLevels ? p.DualOrbitUncertaintyLevels : (int?)null,
                    DualOrbitFtleSpan = p.DualOrbitFtleSpan != D.DualOrbitFtleSpan ? p.DualOrbitFtleSpan : (double?)null,
                    DualOrbitAnisotropyScale = p.DualOrbitAnisotropyScale != D.DualOrbitAnisotropyScale ? p.DualOrbitAnisotropyScale : (double?)null,
                    DualOrbitLicSource = p.DualOrbitLicSource != D.DualOrbitLicSource ? (int)p.DualOrbitLicSource : (int?)null,
                    DualOrbitLicLength = p.DualOrbitLicLength != D.DualOrbitLicLength ? p.DualOrbitLicLength : (int?)null,
                    DualOrbitLicStrength = p.DualOrbitLicStrength != D.DualOrbitLicStrength ? p.DualOrbitLicStrength : (double?)null,
                    DualOrbitLyapunovSmooth = p.DualOrbitLyapunovSmooth ? true : (bool?)null,
                    DualOrbitStripeDensity = p.DualOrbitStripeDensity != D.DualOrbitStripeDensity ? p.DualOrbitStripeDensity : (double?)null,
                    DualOrbitDEScale = p.DualOrbitDEScale != D.DualOrbitDEScale ? p.DualOrbitDEScale : (double?)null,
                    DualOrbitOutlineWidth = p.DualOrbitOutlineWidth != D.DualOrbitOutlineWidth ? p.DualOrbitOutlineWidth : (double?)null,
                    DualOrbitOutlineColorZ = p.DualOrbitOutlineColorZ != D.DualOrbitOutlineColorZ ? p.DualOrbitOutlineColorZ : (uint?)null,
                    DualOrbitOutlineColorC = p.DualOrbitOutlineColorC != D.DualOrbitOutlineColorC ? p.DualOrbitOutlineColorC : (uint?)null,
                    DualOrbitPalette2D = p.DualOrbitPalette2D != D.DualOrbitPalette2D ? (int)p.DualOrbitPalette2D : (int?)null,
                    DualOrbitBivariateScale = p.DualOrbitBivariateScale != D.DualOrbitBivariateScale ? p.DualOrbitBivariateScale : (double?)null,
                    DualOrbitPhaseK = p.DualOrbitPhaseK != D.DualOrbitPhaseK ? p.DualOrbitPhaseK : (double?)null,
                    DualOrbitSliceAxes = p.DualOrbitSliceAxes != FracturingFog.DualOrbitSliceAxes.SxSy ? (int)p.DualOrbitSliceAxes : (int?)null,
                    DualOrbitSX = p.DualOrbitSX != -0.78 ? p.DualOrbitSX : (double?)null,
                    DualOrbitSY = p.DualOrbitSY != 0.15 ? p.DualOrbitSY : (double?)null,
                    DualOrbitColorMode = p.DualOrbitColorMode != FracturingFog.DualOrbitColorMode.Field ? (int)p.DualOrbitColorMode : (int?)null,
                    DualOrbitThemeZ = p.DualOrbitThemeZ != FractalParameters.DualOrbitDefaultThemeZ ? p.DualOrbitThemeZ : null,
                    DualOrbitThemeC = p.DualOrbitThemeC != FractalParameters.DualOrbitDefaultThemeC ? p.DualOrbitThemeC : null,
                    DualOrbitLayerBlend = p.DualOrbitLayerBlend != FracturingFog.DualOrbitLayerBlend.COverZ ? (int)p.DualOrbitLayerBlend : (int?)null,
                    DualOrbitOpacityZ = p.DualOrbitOpacityZ != 1.0 ? p.DualOrbitOpacityZ : (double?)null,
                    DualOrbitOpacityC = p.DualOrbitOpacityC != 0.6 ? p.DualOrbitOpacityC : (double?)null,
                },
                // #1134 — the classic Buddhabrot family: the shared Monte Carlo sampler
                // settings (seed, sample budget, HD / Metropolis / progressive, zoom
                // compensation, iteration windows), each omitted at default. Iter low /
                // mid band the 3-channel types; harmless (ignored) on the single-channel
                // ones. BuddhaColorMode is not carried: every classic type forces its
                // composition (ForcedColorMode), so it has no effect here.
                FractalType.BuddhaBrot or FractalType.Nebulabrot
                    or FractalType.AntiBuddhabrot or FractalType.AntiNebulabrot => new RegionFractalParams
                {
                    BuddhaSamples = p.BuddhaSamples != D.BuddhaSamples ? p.BuddhaSamples : (int?)null,
                    BuddhaIterHigh = p.BuddhaIterHigh != D.BuddhaIterHigh ? p.BuddhaIterHigh : (int?)null,
                    BuddhaIterLow = p.BuddhaIterLow != D.BuddhaIterLow ? p.BuddhaIterLow : (int?)null,
                    BuddhaIterMid = p.BuddhaIterMid != D.BuddhaIterMid ? p.BuddhaIterMid : (int?)null,
                    BuddhaMinIter = p.BuddhaMinIter != D.BuddhaMinIter ? p.BuddhaMinIter : (int?)null,   // #1218
                    BuddhaQualityMode = p.BuddhaQualityMode != D.BuddhaQualityMode ? (int)p.BuddhaQualityMode : (int?)null,
                    BuddhaMetropolis = p.BuddhaMetropolis != D.BuddhaMetropolis ? p.BuddhaMetropolis : (bool?)null,
                    BuddhaProgressive = p.BuddhaProgressive != D.BuddhaProgressive ? p.BuddhaProgressive : (bool?)null,
                    BuddhaSeed = p.BuddhaSeed != D.BuddhaSeed ? p.BuddhaSeed : (int?)null,
                    BuddhaZoomCompensation = p.BuddhaZoomCompensation != D.BuddhaZoomCompensation ? p.BuddhaZoomCompensation : (bool?)null,
                    BuddhaScaleSamplesWithWindow = p.BuddhaScaleSamplesWithWindow != D.BuddhaScaleSamplesWithWindow ? p.BuddhaScaleSamplesWithWindow : (bool?)null,   // #1224
                },
                // #1124 — Dual Buddhabrot: sampler + dual knobs, each omitted at default.
                FractalType.DualBuddhabrot => new RegionFractalParams
                {
                    BuddhaSamples = p.BuddhaSamples != D.BuddhaSamples ? p.BuddhaSamples : (int?)null,
                    BuddhaIterHigh = p.BuddhaIterHigh != D.BuddhaIterHigh ? p.BuddhaIterHigh : (int?)null,
                    BuddhaIterLow = p.BuddhaIterLow != D.BuddhaIterLow ? p.BuddhaIterLow : (int?)null,
                    BuddhaIterMid = p.BuddhaIterMid != D.BuddhaIterMid ? p.BuddhaIterMid : (int?)null,
                    DualBuddhaDeposit = p.DualBuddhaDeposit != D.DualBuddhaDeposit ? (int)p.DualBuddhaDeposit : (int?)null,
                    DualBuddhaNebula = p.DualBuddhaNebula != D.DualBuddhaNebula ? (int)p.DualBuddhaNebula : (int?)null,
                    DualBuddhaAnti = p.DualBuddhaAnti ? true : (bool?)null,
                    BuddhaQualityMode = p.BuddhaQualityMode != D.BuddhaQualityMode ? (int)p.BuddhaQualityMode : (int?)null,
                    BuddhaMetropolis = p.BuddhaMetropolis != D.BuddhaMetropolis ? p.BuddhaMetropolis : (bool?)null,
                    BuddhaProgressive = p.BuddhaProgressive != D.BuddhaProgressive ? p.BuddhaProgressive : (bool?)null,
                    BuddhaSeed = p.BuddhaSeed != D.BuddhaSeed ? p.BuddhaSeed : (int?)null,
                    BuddhaZoomCompensation = p.BuddhaZoomCompensation != D.BuddhaZoomCompensation ? p.BuddhaZoomCompensation : (bool?)null,
                    BuddhaScaleSamplesWithWindow = p.BuddhaScaleSamplesWithWindow != D.BuddhaScaleSamplesWithWindow ? p.BuddhaScaleSamplesWithWindow : (bool?)null,   // #1224
                    DualBuddhaCSeedX = p.DualBuddhaCSeedX != D.DualBuddhaCSeedX ? p.DualBuddhaCSeedX : (double?)null,
                    DualBuddhaCSeedY = p.DualBuddhaCSeedY != D.DualBuddhaCSeedY ? p.DualBuddhaCSeedY : (double?)null,
                    DualBuddhaMinIter = p.DualBuddhaMinIter != D.DualBuddhaMinIter ? p.DualBuddhaMinIter : (int?)null,
                    DualBuddhaComposite = p.DualBuddhaComposite != D.DualBuddhaComposite ? (int)p.DualBuddhaComposite : (int?)null,
                    DualBuddhaColorZ = p.DualBuddhaColorZ != D.DualBuddhaColorZ ? p.DualBuddhaColorZ : (uint?)null,
                    DualBuddhaColorCB = p.DualBuddhaColorCB != D.DualBuddhaColorCB ? p.DualBuddhaColorCB : (uint?)null,
                    DualBuddhaColorCE = p.DualBuddhaColorCE != D.DualBuddhaColorCE ? p.DualBuddhaColorCE : (uint?)null,
                    DualBuddhaGainZ = p.DualBuddhaGainZ != D.DualBuddhaGainZ ? p.DualBuddhaGainZ : (double?)null,
                    DualBuddhaGainCB = p.DualBuddhaGainCB != D.DualBuddhaGainCB ? p.DualBuddhaGainCB : (double?)null,
                    DualBuddhaGainCE = p.DualBuddhaGainCE != D.DualBuddhaGainCE ? p.DualBuddhaGainCE : (double?)null,
                },
                // #893 — Indra's Pearls 2D group. Family + the family's parameter
                // (μ / traces / c) + depth + mode, each omitted at its default.
                FractalType.IndrasPearls => new RegionFractalParams
                {
                    IndrasFamily = p.IndrasFamily != IndrasGroupFamily.Maskit ? (int)p.IndrasFamily : (int?)null,
                    IndrasMaskitMuRe = p.IndrasMaskitMuRe != 0.0 ? p.IndrasMaskitMuRe : (double?)null,
                    IndrasMaskitMuIm = p.IndrasMaskitMuIm != 2.0 ? p.IndrasMaskitMuIm : (double?)null,
                    IndrasGrandmaTaRe = p.IndrasGrandmaTaRe != 1.87 ? p.IndrasGrandmaTaRe : (double?)null,
                    IndrasGrandmaTaIm = p.IndrasGrandmaTaIm != 0.1 ? p.IndrasGrandmaTaIm : (double?)null,
                    IndrasGrandmaTbRe = p.IndrasGrandmaTbRe != 1.87 ? p.IndrasGrandmaTbRe : (double?)null,
                    IndrasGrandmaTbIm = p.IndrasGrandmaTbIm != -0.1 ? p.IndrasGrandmaTbIm : (double?)null,
                    IndrasGrandmaSecondSolution = p.IndrasGrandmaSecondSolution ? true : (bool?)null,
                    IndrasRileyCRe = p.IndrasRileyCRe != 0.1 ? p.IndrasRileyCRe : (double?)null,
                    IndrasRileyCIm = p.IndrasRileyCIm != 0.93 ? p.IndrasRileyCIm : (double?)null,
                    IndrasMaxWordDepth = p.IndrasMaxWordDepth != 12 ? p.IndrasMaxWordDepth : (int?)null,
                    IndrasRenderMode = p.IndrasRenderMode != FracturingFog.Models.IndrasRenderMode.PointCloud ? (int)p.IndrasRenderMode : (int?)null,
                    IndrasColorSource = p.IndrasColorSource != FracturingFog.Models.IndrasColorSource.Density ? (int)p.IndrasColorSource : (int?)null,
                },
                // #961 — families whose only region-relevant params are animatable.
                FractalType.StrangeAttractor => new RegionFractalParams
                {
                    AttractorPresetName = p.AttractorPresetName != D.AttractorPresetName ? p.AttractorPresetName : null,
                    AttractorA = Nd(p.AttractorA, D.AttractorA),
                    AttractorB = Nd(p.AttractorB, D.AttractorB),
                    AttractorC = Nd(p.AttractorC, D.AttractorC),
                    AttractorD = Nd(p.AttractorD, D.AttractorD),
                },
                FractalType.UserEquation => new RegionFractalParams
                {
                    UserEquationRotationDegrees = Nd(p.UserEquationRotationDegrees, D.UserEquationRotationDegrees),
                    UserEquationSeed = string.IsNullOrWhiteSpace(p.UserEquationSeed) ? null : p.UserEquationSeed.Trim(),
                    UserEquationEscapeRadius = Nd(p.EscapeRadius, D.EscapeRadius),
                    UserEquationBailoutCondition = string.IsNullOrWhiteSpace(p.UserEquationBailoutCondition) ? null : p.UserEquationBailoutCondition.Trim(),
                    UserEquationBailoutReplacesModulus = p.UserEquationBailoutReplacesModulus ? true : null,
                    UserEquationColorInterior = p.UserEquationColorInterior ? true : null,
                    UserEquationPotential = string.IsNullOrWhiteSpace(p.UserEquationPotential) ? null : p.UserEquationPotential.Trim(),
                    UserEquationNewtonRoots = string.IsNullOrWhiteSpace(p.UserEquationNewtonRoots) ? null : p.UserEquationNewtonRoots.Trim(),
                },
                // #1249 — Sandbox shares the escape radius and bailout knobs.
                FractalType.Sandbox => new RegionFractalParams
                {
                    UserEquationEscapeRadius = Nd(p.EscapeRadius, D.EscapeRadius),
                    UserEquationBailoutCondition = string.IsNullOrWhiteSpace(p.UserEquationBailoutCondition) ? null : p.UserEquationBailoutCondition.Trim(),
                    UserEquationBailoutReplacesModulus = p.UserEquationBailoutReplacesModulus ? true : null,
                },
                // UserBulb keeps its source + dedicated camera on the region itself;
                // the animatable power / iterations / time ride here.
                FractalType.UserBulb => new RegionFractalParams
                {
                    BulbPower = Nd(p.BulbPower, D.BulbPower),
                    UserBulbIterations = Ni(p.UserBulbIterations, D.UserBulbIterations),
                    UserBulbTime = Nd(p.UserBulbTime, D.UserBulbTime),
                },
                // #1275 — Mandelbrot carries its BLA tolerance when it is not the
                // default (null otherwise, so a stock region stays block-free).
                FractalType.Mandelbrot => new RegionFractalParams
                {
                    BlaTolerance = Nd(p.BlaTolerance, D.BlaTolerance),
                    AdaptiveAa = p.AdaptiveAa != D.AdaptiveAa ? p.AdaptiveAa : null,                      // #1344
                    SolidRegionSkip = p.SolidRegionSkip != D.SolidRegionSkip ? p.SolidRegionSkip : null,   // #1343
                    SolidRegionAuto = p.SolidRegionAuto != D.SolidRegionAuto ? p.SolidRegionAuto : null,   // #1394
                    DeepLa = p.DeepLa != D.DeepLa ? (int)p.DeepLa : null,                                  // #1629
                    AdaptiveAaThreshold = Nd(p.AdaptiveAaThreshold, D.AdaptiveAaThreshold),
                    AdaptiveAaTolerance = Nd(p.AdaptiveAaTolerance, D.AdaptiveAaTolerance),
                },
                // Tricorn, BurningShip, Magnet1/2, TearDrop and the generated
                // families need no extra params — defaults suffice.
                _ => null,
            };

            // #253 — cross-fractal domain warp rides along for the escape-time
            // family whenever it's on, even on types whose base block is null
            // (Burning Ship, Tricorn, Magnet). Captured only when enabled; recall
            // resets it off for regions that don't carry it.
            if (SupportsDomainWarp(type) && p.DomainWarpEnabled)
            {
                rp ??= new RegionFractalParams();
                rp.DomainWarpEnabled = true;
                rp.DomainWarpStrength = p.DomainWarpStrength;
                rp.DomainWarpFrequency = p.DomainWarpFrequency;
            }

            // #1335 — the DE raymarchers' over-relaxation rides along when it is not
            // the plain march (1.0, the default).
            if (MarchRelaxationCapabilities.UsesMarchRelaxation(type) && p.MarchRelaxation != 1.0)
            {
                rp ??= new RegionFractalParams();
                rp.MarchRelaxation = p.MarchRelaxation;
            }

            // #1382 — the 3D raymarchers' and the 2D alt families' adaptive anti-aliasing
            // rides along when not at its defaults (off, threshold 12, tolerance 1.5), as
            // Mandelbrot's does.
            if (AdaptiveAaCapabilities.RidesAlong(type))
            {
                bool? aa = p.AdaptiveAa != D.AdaptiveAa ? p.AdaptiveAa : null;
                double? thr = Nd(p.AdaptiveAaThreshold, D.AdaptiveAaThreshold);
                double? tol = Nd(p.AdaptiveAaTolerance, D.AdaptiveAaTolerance);
                if (aa != null || thr != null || tol != null)
                {
                    rp ??= new RegionFractalParams();
                    rp.AdaptiveAa = aa;
                    rp.AdaptiveAaThreshold = thr;
                    rp.AdaptiveAaTolerance = tol;
                }
            }

            // #1981 part 2 — the AA mask's distance skip rides along for the families with a distance source.
            if (AdaptiveAaDistanceCapable(type))
            {
                bool? skip = p.AdaptiveAaDistanceSkip != D.AdaptiveAaDistanceSkip ? p.AdaptiveAaDistanceSkip : null;
                double? minPx = Nd(p.AdaptiveAaDistanceMinPx, D.AdaptiveAaDistanceMinPx);
                if (skip != null || minPx != null)
                {
                    rp ??= new RegionFractalParams();
                    rp.AdaptiveAaDistanceSkip = skip;
                    rp.AdaptiveAaDistanceMinPx = minPx;
                }
            }

            // #1981 — rotation-domain colouring rides along for its families when not at its defaults.
            if (RotationDomainCapable(type))
            {
                bool? on = p.RotationDomainColoring != D.RotationDomainColoring ? p.RotationDomainColoring : null;
                double? eps = Nd(p.RotationDomainEpsilon, D.RotationDomainEpsilon);
                double? bound = Nd(p.RotationDomainBound, D.RotationDomainBound);
                double? width = Nd(p.RotationDomainShadeWidth, D.RotationDomainShadeWidth);
                uint? col = p.RotationDomainColor != D.RotationDomainColor ? p.RotationDomainColor : null;
                uint? edge = p.RotationDomainEdgeColor != D.RotationDomainEdgeColor ? p.RotationDomainEdgeColor : null;
                uint? chaos = p.RotationDomainChaosColor != D.RotationDomainChaosColor ? p.RotationDomainChaosColor : null;
                if (on != null || eps != null || bound != null || width != null || col != null || edge != null || chaos != null)
                {
                    rp ??= new RegionFractalParams();
                    rp.RotationDomainColoring = on;
                    rp.RotationDomainEpsilon = eps;
                    rp.RotationDomainBound = bound;
                    rp.RotationDomainShadeWidth = width;
                    rp.RotationDomainColor = col;
                    rp.RotationDomainEdgeColor = edge;
                    rp.RotationDomainChaosColor = chaos;
                }
            }

            // #1554 — a Math Plane backdrop's own family params (Multibrot exponent,
            // Phoenix p, a domain warp ...) ride in the MathPlane block, so a region or
            // --param reproduces the backdrop. Julia's c is the plane's c, not JuliaC.
            if (type == FractalType.MathPlane && rp != null
                && MathPlaneBackdropFamily(p.MathPlaneBackdrop) is { } bt && bt != FractalType.Julia)
                MergeMissing(rp, Snapshot(bt, p));

            // #961 — a family whose params are all at their defaults (every field
            // omitted) carries no block, so a stock region never saves an empty {}.
            if (rp != null && JsonSerializer.Serialize(rp, s_omitNull) == "{}") rp = null;

            return rp;
        }

        /// <summary>#1994 — the "exit if" rule of the Copenhagen preset: gate 0 / 1 for a crash into the
        /// mass at (-1/2, 0) / (1/2, 0) (radius 0.01, as ThreeBodySystem's default), gate 2 past r = 10.</summary>
        public const string UserPotentialCopenhagenExit =
            "sqrt((x + 0.5)^2 + y^2) < 0.01 ? 0 : (sqrt((x - 0.5)^2 + y^2) < 0.01 ? 1 : (x^2 + y^2 > 100 ? 2 : -1))";

        /// <summary>#1554 — the family a Math Plane backdrop draws, or null (escape-time families;
        /// #2000 adds the scattering families with a physical plane).</summary>
        public static FractalType? MathPlaneBackdropFamily(MathPlaneBackdrop b) => b switch
        {
            FracturingFog.MathPlaneBackdrop.Mandelbrot => FractalType.Mandelbrot,
            FracturingFog.MathPlaneBackdrop.Julia => FractalType.Julia,
            FracturingFog.MathPlaneBackdrop.BurningShip => FractalType.BurningShip,
            FracturingFog.MathPlaneBackdrop.Tricorn => FractalType.Tricorn,
            FracturingFog.MathPlaneBackdrop.Multibrot => FractalType.Multibrot,
            FracturingFog.MathPlaneBackdrop.Phoenix => FractalType.Phoenix,
            FracturingFog.MathPlaneBackdrop.Magnet1 => FractalType.Magnet1,
            FracturingFog.MathPlaneBackdrop.Magnet2 => FractalType.Magnet2,
            FracturingFog.MathPlaneBackdrop.Glynn => FractalType.Glynn,
            FracturingFog.MathPlaneBackdrop.Spider => FractalType.Spider,
            // #2000 — scattering families (drawn by their own calculators, physical views).
            FracturingFog.MathPlaneBackdrop.HenonHeiles => FractalType.HenonHeilesBasins,
            FracturingFog.MathPlaneBackdrop.ThreeBody => FractalType.ThreeBodyBasins,
            FracturingFog.MathPlaneBackdrop.MagnetisedBlackHole => FractalType.MagnetisedBlackHole,
            _ => null,
        };

        /// <summary>Copy every field set in <paramref name="from"/> that is unset in <paramref name="into"/>.</summary>
        private static void MergeMissing(RegionFractalParams into, RegionFractalParams? from)
        {
            if (from == null) return;
            foreach (var pi in typeof(RegionFractalParams).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                var v = pi.GetValue(from);
                if (v != null && pi.GetValue(into) == null) pi.SetValue(into, v);
            }
        }

        // ── #960 authoritative family recall ─────────────────────────────────
        //
        // ApplyTo is an overlay (null = leave current), and Snapshot omits many
        // fields at their default or while a mode is off. On its own, recall
        // therefore cannot put a default back: a plain Julia region after a
        // faithful-implosion region kept faithful mode on, a default-seed
        // dual-orbit region kept an animated seed. Region recall first resets
        // every param the region's family owns, then overlays.
        //
        // "Owns" = every FractalParameters property Snapshot(type, …) can carry.
        // It is discovered by probing Snapshot itself (bump one property, see
        // whether the snapshot changes), on two bases: stock defaults, and a
        // base with every bool on and every value moved off its default, so
        // fields captured only while a mode is engaged (FaithfulImplosion*,
        // DomainWarp*, Kleinian rotation axis) are found too. New Snapshot
        // fields are picked up automatically — no second list to maintain.

        static readonly System.Collections.Concurrent.ConcurrentDictionary<FractalType, System.Reflection.PropertyInfo[]>
            s_familyProps = new();

        /// <summary>#960 — reset every parameter the <paramref name="type"/> family's region
        /// snapshot can carry to its <see cref="FractalParameters"/> default, leaving other
        /// families, lighting, relief and colour untouched. Call before <see cref="ApplyTo"/> on
        /// region recall so a region always renders the same regardless of what was live.</summary>
        public static void ResetFamilyToDefaults(FractalType type, FractalParameters p)
        {
            if (p == null) return;
            var props = FamilyProperties(type);
            if (props.Count == 0) return;
            var defaults = new FractalParameters();
            foreach (var pi in props)
            {
                try { pi.SetValue(p, pi.GetValue(defaults)); }
                catch { /* a throwing setter keeps its current value */ }
            }
        }

        /// <summary>#960 — the <see cref="FractalParameters"/> properties the
        /// <paramref name="type"/> family's region snapshot can carry (cached per type).</summary>
        public static IReadOnlyList<System.Reflection.PropertyInfo> FamilyProperties(FractalType type)
            => s_familyProps.GetOrAdd(type, DiscoverFamilyProperties);

        static System.Reflection.PropertyInfo[] DiscoverFamilyProperties(FractalType type)
        {
            var candidates = new List<System.Reflection.PropertyInfo>();
            foreach (var pi in typeof(FractalParameters).GetProperties(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (pi.CanRead && pi.CanWrite && pi.GetIndexParameters().Length == 0
                    && pi.GetSetMethod() != null && IsProbeable(pi.PropertyType))
                    candidates.Add(pi);
            }

            var stock = new FractalParameters();
            var engaged = new FractalParameters();
            foreach (var pi in candidates)
            {
                try
                {
                    object? v = pi.GetValue(engaged);
                    pi.SetValue(engaged, pi.PropertyType == typeof(bool) ? true : Bump(v, pi.PropertyType));
                }
                catch { }
            }

            var owned = new List<System.Reflection.PropertyInfo>();
            foreach (var pi in candidates)
                if (Captures(type, stock, pi) || Captures(type, engaged, pi))
                    owned.Add(pi);
            return owned.ToArray();
        }

        // Does Snapshot(type, b) change when only pi changes? Mutates b, then restores it.
        static bool Captures(FractalType type, FractalParameters b, System.Reflection.PropertyInfo pi)
        {
            object? original;
            try { original = pi.GetValue(b); } catch { return false; }
            try
            {
                string before = SnapshotJson(type, b);
                pi.SetValue(b, Bump(original, pi.PropertyType));
                return !string.Equals(before, SnapshotJson(type, b), StringComparison.Ordinal);
            }
            catch { return false; }
            finally
            {
                try { pi.SetValue(b, original); } catch { }
            }
        }

        static string SnapshotJson(FractalType type, FractalParameters b)
            => JsonSerializer.Serialize(Snapshot(type, b));

        static bool IsProbeable(Type t)
            => t == typeof(double) || t == typeof(float) || t == typeof(int) || t == typeof(long)
               || t == typeof(bool) || t == typeof(string) || t == typeof(Complex) || t.IsEnum
               || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>));

        // A value guaranteed to differ from v (same type).
        static object? Bump(object? v, Type t)
        {
            if (t == typeof(double)) return (double)v! + 0.123;
            if (t == typeof(float)) return (float)v! + 0.123f;
            if (t == typeof(int)) return (int)v! + 1;
            if (t == typeof(long)) return (long)v! + 1;
            if (t == typeof(bool)) return !(bool)v!;
            if (t == typeof(string)) return (v as string ?? string.Empty) + "x";
            if (t == typeof(Complex)) return (Complex)v! + new Complex(0.11, -0.07);
            if (t.IsEnum)
            {
                var values = Enum.GetValues(t);
                int i = Array.IndexOf(values, v);
                return values.GetValue((i + 1) % values.Length);
            }
            // List<T>: one more default element (e.g. Kleinian custom spheres).
            var list = (System.Collections.IList)Activator.CreateInstance(t)!;
            if (v is System.Collections.IEnumerable src) foreach (var e in src) list.Add(e);
            list.Add(Activator.CreateInstance(t.GetGenericArguments()[0]));
            return list;
        }

        // ── --param Key=Value (#997) ─────────────────────────────────────────
        // The batch CLI carries any per-family setting (Julia constant, dual-orbit
        // knobs, the 3D camera, ...) as repeatable `--param Key=Value`, keyed on
        // this class's property names. Reusing the region snapshot means the CLI
        // covers exactly what a saved region does, with the same apply path.
        //
        // #1576 — `--param Key=@path` reads a text value from a UTF-8 file. Values with
        // double quotes (a Math Plane scene's expressions) can't be passed through cmd
        // or Windows PowerShell 5.1; a file sidesteps shell quoting entirely. Only
        // text-typed keys read files (a number never starts with '@'); "@@" is a
        // literal leading '@'.

        /// <summary>#1576 — resolve a text value's <c>@path</c> form. Returns false with
        /// <paramref name="error"/> when the file can't be read.</summary>
        public static bool TryResolveFileValue(string key, string value, out string resolved, out string? error)
        {
            resolved = value; error = null;
            if (value.StartsWith("@@", StringComparison.Ordinal)) { resolved = value[1..]; return true; }
            if (!value.StartsWith('@')) return true;
            string path = value[1..].Trim().Trim('"');
            if (path.Length == 0) { error = $"--param {key}: '@' needs a file path (use @@ for a literal '@')."; return false; }
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                resolved = System.IO.File.ReadAllText(full, System.Text.Encoding.UTF8).TrimEnd('\r', '\n');
                return true;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                error = $"--param {key}: can't read '{path}': {ex.Message}";
                return false;
            }
        }

        /// <summary>The snapshot's non-null fields as <c>Key=Value</c> pairs, in
        /// declaration order: strings raw, numbers / booleans as their JSON text
        /// (round-trippable), lists / objects as compact JSON.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> ToKeyValues()
        {
            var obj = JsonSerializer.SerializeToNode(this, s_omitNull) as System.Text.Json.Nodes.JsonObject;
            var list = new List<KeyValuePair<string, string>>();
            if (obj == null) return list;
            foreach (var (key, node) in obj)
            {
                if (node == null) continue;
                string text = node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? str)
                    ? str
                    : node.ToJsonString();
                list.Add(new KeyValuePair<string, string>(key, text));
            }
            return list;
        }

        /// <summary>The settable keys (property names), for help and suggestions.</summary>
        public static IReadOnlyList<string> KeyNames { get; } =
            typeof(RegionFractalParams).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(pi => pi.CanWrite && pi.GetIndexParameters().Length == 0)
                .Select(pi => pi.Name).ToArray();

        /// <summary>#1741 — the enum behind an enum-valued key: a whole-number key whose
        /// <see cref="FractalParameters"/> property of the same name is an enum (KifsFold,
        /// KleinianPreset, BicomplexSliceAxis, BilliardGeometry, ...). Null for other keys.</summary>
        public static Type? EnumTypeOf(string key)
        {
            var pi = typeof(RegionFractalParams).GetProperty(key ?? "",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (pi == null || (Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType) != typeof(int)) return null;
            var fp = typeof(FractalParameters).GetProperty(pi.Name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var t = fp == null ? null : Nullable.GetUnderlyingType(fp.PropertyType) ?? fp.PropertyType;
            return t is { IsEnum: true } ? t : null;
        }

        /// <summary>#1741 — an enum key's value given by member name (case-insensitive) as its
        /// number; false with the valid names when the name is not one of them.</summary>
        private static bool TryEnumName(Type enumType, string value, out int number, out string? error)
        {
            number = 0; error = null;
            string v = value.Trim();
            if (v.Length > 0 && !v.Contains(',') && Enum.TryParse(enumType, v, ignoreCase: true, out object? e)
                && e != null && Enum.IsDefined(enumType, e))
            { number = Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture); return true; }
            error = $"'{value}' is not one of {string.Join(", ", Enum.GetNames(enumType))} (or its number).";
            return false;
        }

        /// <summary>Build a snapshot from <c>Key=Value</c> pairs (keys are
        /// case-insensitive; later pairs win). #1741: an enum-valued key also takes the
        /// member's name (KifsFold=Dodecahedron as well as KifsFold=3). <c>Cam3DFamily</c> also accepts a
        /// fractal-type name, and defaults to <paramref name="cameraFamily"/> when
        /// camera fields are given without it. Returns null with a message on an
        /// unknown key or a value of the wrong type.</summary>
        public static RegionFractalParams? FromKeyValues(
            IEnumerable<KeyValuePair<string, string>> pairs, FractalType? cameraFamily, out string? error)
        {
            error = null;
            var obj = new System.Text.Json.Nodes.JsonObject();
            foreach (var (rawKey, rawValue) in pairs)
            {
                var pi = typeof(RegionFractalParams).GetProperty(rawKey?.Trim() ?? "",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (pi == null || !pi.CanWrite)
                {
                    var near = KeyNames.Where(k => k.Contains(rawKey?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
                    error = $"Unknown --param key '{rawKey}'." + (near.Count > 0 ? " Did you mean: " + string.Join(", ", near) + "?" : "");
                    return null;
                }
                string value = rawValue ?? "";
                Type t = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
                System.Text.Json.Nodes.JsonNode? node;
                if (t == typeof(string))
                {
                    if (!TryResolveFileValue(pi.Name, value, out value, out string? fileError)) { error = fileError; return null; }
                    node = System.Text.Json.Nodes.JsonValue.Create(value);
                }
                else if (pi.Name == nameof(Cam3DFamily) && FractalTypeNames.TryParse(value.Trim(), true, out var fam) && !int.TryParse(value, out _))
                    node = System.Text.Json.Nodes.JsonValue.Create((int)fam);
                else if (t == typeof(int) && !int.TryParse(value.Trim(), out _) && EnumTypeOf(pi.Name) is { } enumType)   // #1741
                {
                    if (!TryEnumName(enumType, value, out int number, out string? enumError))
                    { error = $"--param {pi.Name}: {enumError}"; return null; }
                    node = System.Text.Json.Nodes.JsonValue.Create(number);
                }
                else
                {
                    try { node = System.Text.Json.Nodes.JsonNode.Parse(value.Trim()); }
                    catch (JsonException) { node = null; }
                    if (node == null) { error = $"--param {pi.Name}: '{value}' is not a valid {TypeLabel(t)}."; return null; }
                }
                obj[pi.Name] = node;
            }

            RegionFractalParams? rp;
            try { rp = obj.Deserialize<RegionFractalParams>(s_omitNull); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                error = "--param: " + ex.Message;
                return null;
            }
            if (rp == null) { error = "--param: no values."; return null; }

            bool anyCamera = rp.Cam3DDistance.HasValue || rp.Cam3DTheta.HasValue || rp.Cam3DPhi.HasValue || rp.Cam3DSliceW.HasValue;
            if (anyCamera && !rp.Cam3DFamily.HasValue && cameraFamily.HasValue)
                rp.Cam3DFamily = (int)cameraFamily.Value;
            return rp;
        }

        static string TypeLabel(Type t)
            => t == typeof(double) || t == typeof(float) ? "number"
             : t == typeof(int) || t == typeof(long) ? "whole number"
             : t == typeof(bool) ? "true/false"
             : "JSON value";

        // #1445 — a billiard snapshot written before the launch / gate / mute fields
        // existed. Snapshot always writes all seven #627 fields, so a hand-typed
        // `--param` with a few billiard keys is not mistaken for one.
        private bool IsLegacyBilliardSnapshot =>
            BilliardGeometry.HasValue && BilliardDiskCount.HasValue && BilliardDiskRadius.HasValue
            && BilliardSeparation.HasValue && BilliardMaxBounces.HasValue && BilliardGateCount.HasValue
            && BilliardSeed.HasValue
            && !BilliardLaunchMode.HasValue && !BilliardGateMode.HasValue && !BilliardMuteMisses.HasValue;

        /// <summary>
        /// Overlay every captured (non-null) field onto <paramref name="p"/>.
        /// No-op for fields left null, so applying a Julia snapshot never
        /// disturbs unrelated parameters.
        /// </summary>
        public void ApplyTo(FractalParameters p)
        {
            if (p == null) return;
            if (JuliaCRe.HasValue && JuliaCIm.HasValue)
                p.JuliaC = new Complex(JuliaCRe.Value, JuliaCIm.Value);
            // #920 faithful implosion (user-picked p/q)
            if (FaithfulImplosion.HasValue)        p.FaithfulImplosion = FaithfulImplosion.Value;
            if (FaithfulImplosionP.HasValue)       p.FaithfulImplosionP = FaithfulImplosionP.Value;
            if (FaithfulImplosionQ.HasValue)       p.FaithfulImplosionQ = FaithfulImplosionQ.Value;
            if (FaithfulImplosionApproach.HasValue) p.FaithfulImplosionApproach = FaithfulImplosionApproach.Value;
            if (FaithfulImplosionSatellite.HasValue) p.FaithfulImplosionSatellite = FaithfulImplosionSatellite.Value;
            if (FaithfulImplosionParentP.HasValue) p.FaithfulImplosionParentP = FaithfulImplosionParentP.Value;
            if (FaithfulImplosionParentQ.HasValue) p.FaithfulImplosionParentQ = FaithfulImplosionParentQ.Value;
            if (FaithfulImplosionParentPath != null) p.FaithfulImplosionParentPath = FaithfulImplosionParentPath;
            if (MultibrotExponent.HasValue)
                p.MultibrotExponent = MultibrotExponent.Value;
            if (PhoenixPRe.HasValue && PhoenixPIm.HasValue)
                p.PhoenixP = new Complex(PhoenixPRe.Value, PhoenixPIm.Value);
            if (GlynnCRe.HasValue && GlynnCIm.HasValue)
                p.GlynnC = new Complex(GlynnCRe.Value, GlynnCIm.Value);
            if (SpiderCDecay.HasValue)
                p.SpiderCDecay = SpiderCDecay.Value;
            if (NewtonExponent.HasValue)
                p.NewtonExponent = NewtonExponent.Value;
            if (NewtonRelaxation.HasValue)
                p.NewtonRelaxation = NewtonRelaxation.Value;
            if (NovaJuliaMode.HasValue)
                p.NovaJuliaMode = NovaJuliaMode.Value;
            if (NovaZ0Re.HasValue || NovaZ0Im.HasValue)   // #1294 — either half alone keeps the other
                p.NovaZ0 = new Complex(NovaZ0Re ?? p.NovaZ0.Real, NovaZ0Im ?? p.NovaZ0.Imaginary);
            if (NovaJuliaCRe.HasValue || NovaJuliaCIm.HasValue)
                p.NovaJuliaC = new Complex(NovaJuliaCRe ?? p.NovaJuliaC.Real, NovaJuliaCIm ?? p.NovaJuliaC.Imaginary);
            if (ChaosEarlyOut.HasValue)                                   // #1980
                p.ChaosEarlyOut = ChaosEarlyOut.Value;
            if (ChaosEarlyOutLog10.HasValue)
                p.ChaosEarlyOutLog10 = ChaosEarlyOutLog10.Value;
            if (RotationDomainColoring.HasValue) p.RotationDomainColoring = RotationDomainColoring.Value;   // #1981
            if (RotationDomainEpsilon.HasValue) p.RotationDomainEpsilon = RotationDomainEpsilon.Value;
            if (RotationDomainBound.HasValue) p.RotationDomainBound = RotationDomainBound.Value;
            if (RotationDomainShadeWidth.HasValue) p.RotationDomainShadeWidth = RotationDomainShadeWidth.Value;
            if (RotationDomainColor.HasValue) p.RotationDomainColor = RotationDomainColor.Value;
            if (RotationDomainEdgeColor.HasValue) p.RotationDomainEdgeColor = RotationDomainEdgeColor.Value;
            if (RotationDomainChaosColor.HasValue) p.RotationDomainChaosColor = RotationDomainChaosColor.Value;
            if (SecantOffsetRe.HasValue && SecantOffsetIm.HasValue)
                p.SecantInitialOffset = new Complex(SecantOffsetRe.Value, SecantOffsetIm.Value);
            if (ApollonianDepth.HasValue)
                p.ApollonianDepth = ApollonianDepth.Value;
            if (ApollonianMinPixelRadius.HasValue)
                p.ApollonianMinPixelRadius = ApollonianMinPixelRadius.Value;
            if (ApollonianColorByDepth.HasValue)
                p.ApollonianColorByDepth = ApollonianColorByDepth.Value;
            if (BilliardGeometry.HasValue)
                p.BilliardGeometry = (FracturingFog.BilliardGeometry)BilliardGeometry.Value;
            if (BilliardDiskCount.HasValue)
                p.BilliardDiskCount = BilliardDiskCount.Value;
            if (BilliardDiskRadius.HasValue)
                p.BilliardDiskRadius = BilliardDiskRadius.Value;
            if (BilliardSeparation.HasValue)
                p.BilliardSeparation = BilliardSeparation.Value;
            if (BilliardMaxBounces.HasValue)
                p.BilliardMaxBounces = BilliardMaxBounces.Value;
            if (BilliardGateCount.HasValue)
                p.BilliardGateCount = BilliardGateCount.Value;
            if (BilliardSeed.HasValue)
                p.BilliardSeed = BilliardSeed.Value;
            if (BilliardLaunchMode.HasValue)
                p.BilliardLaunchMode = (FracturingFog.BilliardLaunchMode)BilliardLaunchMode.Value;
            if (BilliardGateMode.HasValue)
                p.BilliardGateMode = (FracturingFog.BilliardGateMode)BilliardGateMode.Value;
            if (BilliardMuteMisses.HasValue)
                p.BilliardMuteMisses = BilliardMuteMisses.Value;
            if (BilliardCustomDisks != null)
                p.BilliardCustomDisks = BilliardCustomDisks;
            if (MathPlaneScene != null)
                p.MathPlaneScene = MathPlaneScene;
            if (MathPlaneMapMode.HasValue) p.MathPlaneMapMode = (FracturingFog.MathPlaneMapMode)MathPlaneMapMode.Value;
            if (MathPlaneM11.HasValue) p.MathPlaneM11 = MathPlaneM11.Value;
            if (MathPlaneM12.HasValue) p.MathPlaneM12 = MathPlaneM12.Value;
            if (MathPlaneM21.HasValue) p.MathPlaneM21 = MathPlaneM21.Value;
            if (MathPlaneM22.HasValue) p.MathPlaneM22 = MathPlaneM22.Value;
            if (MathPlaneT.HasValue) p.MathPlaneT = MathPlaneT.Value;
            if (MathPlaneInterpolation.HasValue) p.MathPlaneInterpolation = (FracturingFog.MathPlaneInterpolation)MathPlaneInterpolation.Value;
            if (MathPlaneCRe.HasValue) p.MathPlaneCRe = MathPlaneCRe.Value;
            if (MathPlaneCIm.HasValue) p.MathPlaneCIm = MathPlaneCIm.Value;
            if (MathPlaneBackdrop.HasValue) p.MathPlaneBackdrop = (FracturingFog.MathPlaneBackdrop)MathPlaneBackdrop.Value;
            if (MathPlaneScriptTime.HasValue) p.MathPlaneScriptTime = MathPlaneScriptTime.Value;
            if (MathPlane3DScene != null) p.MathPlane3DScene = MathPlane3DScene;
            if (MathPlane3DTargetX.HasValue) p.MathPlane3DTargetX = MathPlane3DTargetX.Value;
            if (MathPlane3DTargetY.HasValue) p.MathPlane3DTargetY = MathPlane3DTargetY.Value;
            if (MathPlane3DTargetZ.HasValue) p.MathPlane3DTargetZ = MathPlane3DTargetZ.Value;
            if (MathPlane3DMapOn.HasValue) p.MathPlane3DMapOn = MathPlane3DMapOn.Value;
            if (MathPlane3DM11.HasValue) p.MathPlane3DM11 = MathPlane3DM11.Value;
            if (MathPlane3DM12.HasValue) p.MathPlane3DM12 = MathPlane3DM12.Value;
            if (MathPlane3DM13.HasValue) p.MathPlane3DM13 = MathPlane3DM13.Value;
            if (MathPlane3DM21.HasValue) p.MathPlane3DM21 = MathPlane3DM21.Value;
            if (MathPlane3DM22.HasValue) p.MathPlane3DM22 = MathPlane3DM22.Value;
            if (MathPlane3DM23.HasValue) p.MathPlane3DM23 = MathPlane3DM23.Value;
            if (MathPlane3DM31.HasValue) p.MathPlane3DM31 = MathPlane3DM31.Value;
            if (MathPlane3DM32.HasValue) p.MathPlane3DM32 = MathPlane3DM32.Value;
            if (MathPlane3DM33.HasValue) p.MathPlane3DM33 = MathPlane3DM33.Value;
            if (MathPlane3DT.HasValue) p.MathPlane3DT = MathPlane3DT.Value;
            if (MathPlane3DInterpolation.HasValue) p.MathPlane3DInterpolation = (FracturingFog.MathPlaneInterpolation)MathPlane3DInterpolation.Value;
            if (BilliardPolygonSides.HasValue)
                p.BilliardPolygonSides = BilliardPolygonSides.Value;
            if (BilliardPolygonFill.HasValue)
                p.BilliardPolygonFill = BilliardPolygonFill.Value;
            if (BilliardLatticeRotation.HasValue)
                p.BilliardLatticeRotation = BilliardLatticeRotation.Value;
            if (BilliardRaysSweep.HasValue)      p.BilliardRaysSweep = (FracturingFog.BilliardRaySweep)BilliardRaysSweep.Value;
            if (BilliardRaysCount.HasValue)      p.BilliardRaysCount = BilliardRaysCount.Value;
            if (BilliardRaysAngleCount.HasValue) p.BilliardRaysAngleCount = BilliardRaysAngleCount.Value;
            if (BilliardRaysAngle.HasValue)      p.BilliardRaysAngle = BilliardRaysAngle.Value;
            if (BilliardRaysSpread.HasValue)     p.BilliardRaysSpread = BilliardRaysSpread.Value;
            if (BilliardRaysSourceX.HasValue)    p.BilliardRaysSourceX = BilliardRaysSourceX.Value;
            if (BilliardRaysSourceY.HasValue)    p.BilliardRaysSourceY = BilliardRaysSourceY.Value;
            if (BilliardRaysSeed.HasValue)       p.BilliardRaysSeed = BilliardRaysSeed.Value;
            if (BilliardRaysMinBounces.HasValue) p.BilliardRaysMinBounces = BilliardRaysMinBounces.Value;
            if (BilliardRaysShowDisks.HasValue)  p.BilliardRaysShowDisks = BilliardRaysShowDisks.Value;
            if (BlackHoleLayout.HasValue)         p.BlackHoleLayout = (FracturingFog.BlackHoleLayout)BlackHoleLayout.Value;
            if (BlackHoleMass1.HasValue)          p.BlackHoleMass1 = BlackHoleMass1.Value;
            if (BlackHoleMass2.HasValue)          p.BlackHoleMass2 = BlackHoleMass2.Value;
            if (BlackHoleSeparation.HasValue)     p.BlackHoleSeparation = BlackHoleSeparation.Value;
            if (BlackHoleCustom != null)          p.BlackHoleCustom = BlackHoleCustom;
            if (BlackHoleLensSource != null)      p.BlackHoleLensSource = BlackHoleLensSource;   // #1995
            if (BlackHoleLensCapture.HasValue)    p.BlackHoleLensCapture = BlackHoleLensCapture.Value;
            if (BlackHoleCameraDistance.HasValue) p.BlackHoleCameraDistance = BlackHoleCameraDistance.Value;
            if (BlackHoleInclination.HasValue)    p.BlackHoleInclination = BlackHoleInclination.Value;
            if (BlackHoleAzimuth.HasValue)        p.BlackHoleAzimuth = BlackHoleAzimuth.Value;
            if (BlackHoleCaptureFraction.HasValue) p.BlackHoleCaptureFraction = BlackHoleCaptureFraction.Value;
            if (BlackHoleTolerance.HasValue)      p.BlackHoleTolerance = BlackHoleTolerance.Value;
            if (BlackHoleMaxSteps.HasValue)       p.BlackHoleMaxSteps = BlackHoleMaxSteps.Value;
            if (BlackHoleChannel.HasValue)        p.BlackHoleChannel = (FracturingFog.BlackHoleChannel)BlackHoleChannel.Value;
            if (BlackHoleShadeFates.HasValue)     p.BlackHoleShadeFates = BlackHoleShadeFates.Value;
            if (BlackHoleChannelScale.HasValue)   p.BlackHoleChannelScale = BlackHoleChannelScale.Value;
            if (BlackHoleProjection.HasValue)     p.BlackHoleProjection = (FracturingFog.BlackHoleProjection)BlackHoleProjection.Value;
            if (BlackHoleSky.HasValue)            p.BlackHoleSky = (FracturingFog.BlackHoleSky)BlackHoleSky.Value;
            if (BlackHoleSkyDivisions.HasValue)   p.BlackHoleSkyDivisions = BlackHoleSkyDivisions.Value;
            if (BlackHoleSkySeed.HasValue)        p.BlackHoleSkySeed = BlackHoleSkySeed.Value;
            if (BlackHoleSkyRotation.HasValue)    p.BlackHoleSkyRotation = BlackHoleSkyRotation.Value;
            if (BlackHoleSolidBlocks.HasValue)    p.BlackHoleSolidBlocks = BlackHoleSolidBlocks.Value;
            if (BlackHoleGpuPrecision.HasValue)   p.BlackHoleGpuPrecision = (FracturingFog.BlackHoleGpuPrecision)BlackHoleGpuPrecision.Value;
            if (BlackHoleDdEdges.HasValue)        p.BlackHoleDdEdges = BlackHoleDdEdges.Value;
            if (HenonHeilesEnergy.HasValue)       p.HenonHeilesEnergy = HenonHeilesEnergy.Value;   // #1942
            if (HenonHeilesView is { } hhv && Enum.IsDefined(typeof(FracturingFog.HenonHeilesView), hhv)) p.HenonHeilesView = (FracturingFog.HenonHeilesView)hhv;
            if (HenonHeilesLaunch is { } hhl && Enum.IsDefined(typeof(FracturingFog.HenonHeilesLaunch), hhl)) p.HenonHeilesLaunch = (FracturingFog.HenonHeilesLaunch)hhl;
            if (HenonHeilesLaunchAngle.HasValue)  p.HenonHeilesLaunchAngle = HenonHeilesLaunchAngle.Value;
            if (HenonHeilesChannel is { } hhc && Enum.IsDefined(typeof(FracturingFog.HenonHeilesChannel), hhc)) p.HenonHeilesChannel = (FracturingFog.HenonHeilesChannel)hhc;
            if (HenonHeilesChannelScale.HasValue) p.HenonHeilesChannelScale = HenonHeilesChannelScale.Value;
            if (HenonHeilesMaxTime.HasValue)      p.HenonHeilesMaxTime = HenonHeilesMaxTime.Value;
            if (HenonHeilesTolerance.HasValue)    p.HenonHeilesTolerance = HenonHeilesTolerance.Value;
            if (HenonHeilesGpuPrecision is { } hgp && Enum.IsDefined(typeof(FracturingFog.BlackHoleGpuPrecision), hgp)) p.HenonHeilesGpuPrecision = (FracturingFog.BlackHoleGpuPrecision)hgp;   // #2049
            if (HenonHeilesShadeExits.HasValue)   p.HenonHeilesShadeExits = HenonHeilesShadeExits.Value;
            if (HenonHeilesSolidBlocks.HasValue)  p.HenonHeilesSolidBlocks = HenonHeilesSolidBlocks.Value;
            if (ThreeBodyView is { } tbv && Enum.IsDefined(typeof(FracturingFog.ThreeBodyView), tbv)) p.ThreeBodyView = (FracturingFog.ThreeBodyView)tbv;   // #1943
            if (ThreeBodyMassRatio.HasValue)      p.ThreeBodyMassRatio = ThreeBodyMassRatio.Value;
            if (ThreeBodyJacobi.HasValue)         p.ThreeBodyJacobi = ThreeBodyJacobi.Value;
            if (ThreeBodyLaunch is { } tbl && Enum.IsDefined(typeof(FracturingFog.ThreeBodyLaunch), tbl)) p.ThreeBodyLaunch = (FracturingFog.ThreeBodyLaunch)tbl;
            if (ThreeBodyLaunchAngle.HasValue)    p.ThreeBodyLaunchAngle = ThreeBodyLaunchAngle.Value;
            if (ThreeBodyCrashRadius.HasValue)    p.ThreeBodyCrashRadius = ThreeBodyCrashRadius.Value;
            if (ThreeBodyEscapeRadius.HasValue)   p.ThreeBodyEscapeRadius = ThreeBodyEscapeRadius.Value;
            if (ThreeBodyMaxTime.HasValue)        p.ThreeBodyMaxTime = ThreeBodyMaxTime.Value;
            if (ThreeBodyTolerance.HasValue)      p.ThreeBodyTolerance = ThreeBodyTolerance.Value;
            if (ThreeBodyNewtonIterations.HasValue) p.ThreeBodyNewtonIterations = ThreeBodyNewtonIterations.Value;
            if (MagBhView is { } mbv && Enum.IsDefined(typeof(FracturingFog.MagnetisedBhView), mbv)) p.MagBhView = (FracturingFog.MagnetisedBhView)mbv;   // #1944
            if (MagBhField.HasValue)              p.MagBhField = MagBhField.Value;
            if (MagBhBranch is { } mbb && Enum.IsDefined(typeof(FracturingFog.MagnetisedBhBranch), mbb)) p.MagBhBranch = (FracturingFog.MagnetisedBhBranch)mbb;
            if (MagBhEnergy.HasValue)             p.MagBhEnergy = MagBhEnergy.Value;
            if (MagBhAngularMomentum.HasValue)    p.MagBhAngularMomentum = MagBhAngularMomentum.Value;
            if (MagBhLaunchAngle.HasValue)        p.MagBhLaunchAngle = MagBhLaunchAngle.Value;
            if (MagBhEscapeHeight.HasValue)       p.MagBhEscapeHeight = MagBhEscapeHeight.Value;
            if (MagBhMaxTime.HasValue)            p.MagBhMaxTime = MagBhMaxTime.Value;
            if (MagBhTolerance.HasValue)          p.MagBhTolerance = MagBhTolerance.Value;
            if (SpinBinMassRatio.HasValue)        p.SpinBinMassRatio = SpinBinMassRatio.Value;   // #1945
            if (SpinBinChi1.HasValue)             p.SpinBinChi1 = SpinBinChi1.Value;
            if (SpinBinChi2.HasValue)             p.SpinBinChi2 = SpinBinChi2.Value;
            if (SpinBinPhi1.HasValue)             p.SpinBinPhi1 = SpinBinPhi1.Value;
            if (SpinBinPhi2.HasValue)             p.SpinBinPhi2 = SpinBinPhi2.Value;
            if (SpinBinSeparation.HasValue)       p.SpinBinSeparation = SpinBinSeparation.Value;
            if (SpinBinVelocity.HasValue)         p.SpinBinVelocity = SpinBinVelocity.Value;
            if (SpinBinRadiationReaction.HasValue) p.SpinBinRadiationReaction = SpinBinRadiationReaction.Value;
            if (SpinBinOutcome is { } sbo && Enum.IsDefined(typeof(FracturingFog.SpinningBinaryOutcomeMode), sbo)) p.SpinBinOutcome = (FracturingFog.SpinningBinaryOutcomeMode)sbo;
            if (SpinBinMergeRadius.HasValue)      p.SpinBinMergeRadius = SpinBinMergeRadius.Value;
            if (SpinBinSeparateRadius.HasValue)   p.SpinBinSeparateRadius = SpinBinSeparateRadius.Value;
            if (SpinBinMaxOrbits.HasValue)        p.SpinBinMaxOrbits = SpinBinMaxOrbits.Value;
            if (SpinBinTolerance.HasValue)        p.SpinBinTolerance = SpinBinTolerance.Value;
            if (MixView is { } mxv && Enum.IsDefined(typeof(FracturingFog.MixmasterView), mxv)) p.MixView = (FracturingFog.MixmasterView)mxv;   // #1946
            if (MixUExit.HasValue)                p.MixUExit = MixUExit.Value;
            if (MixOmegaDot.HasValue)             p.MixOmegaDot = MixOmegaDot.Value;
            if (MixOmega.HasValue)                p.MixOmega = MixOmega.Value;
            if (MixAlphaDot.HasValue)             p.MixAlphaDot = MixAlphaDot.Value;
            if (MixBetaDot.HasValue)              p.MixBetaDot = MixBetaDot.Value;
            if (MixMaxTime.HasValue)              p.MixMaxTime = MixMaxTime.Value;
            if (MixTolerance.HasValue)            p.MixTolerance = MixTolerance.Value;
            if (BhRingView is { } brv && Enum.IsDefined(typeof(FracturingFog.BlackHoleRingView), brv)) p.BhRingView = (FracturingFog.BlackHoleRingView)brv;   // #1947
            if (BhRingMass.HasValue)              p.BhRingMass = BhRingMass.Value;
            if (BhRingRadius.HasValue)            p.BhRingRadius = BhRingRadius.Value;
            if (BhRingEnergy.HasValue)            p.BhRingEnergy = BhRingEnergy.Value;
            if (BhRingAngularMomentum.HasValue)   p.BhRingAngularMomentum = BhRingAngularMomentum.Value;
            if (BhRingVelocityScale.HasValue)     p.BhRingVelocityScale = BhRingVelocityScale.Value;
            if (BhRingOrbits.HasValue)            p.BhRingOrbits = BhRingOrbits.Value;
            if (BhRingOrbitTime.HasValue)         p.BhRingOrbitTime = BhRingOrbitTime.Value;
            if (BhRingFateTime.HasValue)          p.BhRingFateTime = BhRingFateTime.Value;
            if (BhRingColour is { } brc && Enum.IsDefined(typeof(FracturingFog.BlackHoleRingColour), brc)) p.BhRingColour = (FracturingFog.BlackHoleRingColour)brc;
            if (BhRingTolerance.HasValue)         p.BhRingTolerance = BhRingTolerance.Value;
            if (UserPotentialSource != null)      p.UserPotentialSource = UserPotentialSource;   // #1994
            if (UserPotentialEnergy.HasValue)     p.UserPotentialEnergy = UserPotentialEnergy.Value;
            if (UserPotentialView is { } upv && Enum.IsDefined(typeof(FracturingFog.UserPotentialView), upv)) p.UserPotentialView = (FracturingFog.UserPotentialView)upv;
            if (UserPotentialLaunch is { } upl && Enum.IsDefined(typeof(FracturingFog.HenonHeilesLaunch), upl)) p.UserPotentialLaunch = (FracturingFog.HenonHeilesLaunch)upl;
            if (UserPotentialLaunchAngle.HasValue) p.UserPotentialLaunchAngle = UserPotentialLaunchAngle.Value;
            if (UserPotentialExitRadius.HasValue) p.UserPotentialExitRadius = UserPotentialExitRadius.Value;
            if (UserPotentialSectors.HasValue)    p.UserPotentialSectors = UserPotentialSectors.Value;
            if (UserPotentialSectorOffset.HasValue) p.UserPotentialSectorOffset = UserPotentialSectorOffset.Value;
            if (UserPotentialExitIf != null)      p.UserPotentialExitIf = UserPotentialExitIf;
            if (UserPotentialRotating.HasValue)   p.UserPotentialRotating = UserPotentialRotating.Value;
            if (UserPotentialMaxTime.HasValue)    p.UserPotentialMaxTime = UserPotentialMaxTime.Value;
            if (UserPotentialTolerance.HasValue)  p.UserPotentialTolerance = UserPotentialTolerance.Value;
            if (UserPotentialMaxStep.HasValue)    p.UserPotentialMaxStep = UserPotentialMaxStep.Value;
            if (UserHamSource != null) p.UserHamSource = UserHamSource;
            if (UserHamDimension.HasValue) p.UserHamDimension = UserHamDimension.Value;
            if (UserHamAxisA != null) p.UserHamAxisA = UserHamAxisA;
            if (UserHamAxisB != null) p.UserHamAxisB = UserHamAxisB;
            if (UserHamFixed != null) p.UserHamFixed = UserHamFixed;
            if (UserHamSolveFor != null) p.UserHamSolveFor = UserHamSolveFor;
            if (UserHamSolveSign.HasValue) p.UserHamSolveSign = UserHamSolveSign.Value;
            if (UserHamEnergy.HasValue) p.UserHamEnergy = UserHamEnergy.Value;
            if (UserHamExitRadius.HasValue) p.UserHamExitRadius = UserHamExitRadius.Value;
            if (UserHamSectors.HasValue) p.UserHamSectors = UserHamSectors.Value;
            if (UserHamSectorOffset.HasValue) p.UserHamSectorOffset = UserHamSectorOffset.Value;
            if (UserHamExitIf != null) p.UserHamExitIf = UserHamExitIf;
            if (UserHamMaxTime.HasValue) p.UserHamMaxTime = UserHamMaxTime.Value;
            if (UserHamTolerance.HasValue) p.UserHamTolerance = UserHamTolerance.Value;
            if (UserHamMaxStep.HasValue) p.UserHamMaxStep = UserHamMaxStep.Value;
            if (UserHamProjectEvery.HasValue) p.UserHamProjectEvery = UserHamProjectEvery.Value;
            if (MicroLayout is { } vMicroLayout && Enum.IsDefined(typeof(FracturingFog.MicrolensLayout), vMicroLayout)) p.MicroLayout = (FracturingFog.MicrolensLayout)vMicroLayout;
            if (MicroKappaStars.HasValue) p.MicroKappaStars = MicroKappaStars.Value;
            if (MicroKappaSmooth.HasValue) p.MicroKappaSmooth = MicroKappaSmooth.Value;
            if (MicroShear.HasValue) p.MicroShear = MicroShear.Value;
            if (MicroFieldRadius.HasValue) p.MicroFieldRadius = MicroFieldRadius.Value;
            if (MicroSeed.HasValue) p.MicroSeed = MicroSeed.Value;
            if (MicroMassFunction is { } vMicroMassFunction && Enum.IsDefined(typeof(FracturingFog.MicrolensMassFunction), vMicroMassFunction)) p.MicroMassFunction = (FracturingFog.MicrolensMassFunction)vMicroMassFunction;
            if (MicroMassMin.HasValue) p.MicroMassMin = MicroMassMin.Value;
            if (MicroRaysPerPixel.HasValue) p.MicroRaysPerPixel = MicroRaysPerPixel.Value;
            if (MicroPad.HasValue) p.MicroPad = MicroPad.Value;
            if (MicroColourScale.HasValue) p.MicroColourScale = MicroColourScale.Value;
            if (MicroLightCurve.HasValue) p.MicroLightCurve = MicroLightCurve.Value;   // #2007
            if (MicroTrackX.HasValue) p.MicroTrackX = MicroTrackX.Value;
            if (MicroTrackY.HasValue) p.MicroTrackY = MicroTrackY.Value;
            if (MicroTrackAngle.HasValue) p.MicroTrackAngle = MicroTrackAngle.Value;
            if (CosmicWebGenerator is { } cwg && Enum.IsDefined(typeof(FracturingFog.CosmicWebGenerator), cwg)) p.CosmicWebGenerator = (FracturingFog.CosmicWebGenerator)cwg;   // #2021
            if (CosmicWebSeed.HasValue)       p.CosmicWebSeed = CosmicWebSeed.Value;
            if (CosmicWebBoxSize.HasValue)    p.CosmicWebBoxSize = CosmicWebBoxSize.Value;
            if (CosmicWebClumps.HasValue)     p.CosmicWebClumps = CosmicWebClumps.Value;
            if (CosmicWebPlacement is { } cwp && Enum.IsDefined(typeof(FracturingFog.CosmicWebPlacement), cwp)) p.CosmicWebPlacement = (FracturingFog.CosmicWebPlacement)cwp;
            if (CosmicWebEta.HasValue)        p.CosmicWebEta = CosmicWebEta.Value;
            if (CosmicWebLambda.HasValue)     p.CosmicWebLambda = CosmicWebLambda.Value;
            if (CosmicWebFirstRatio.HasValue) p.CosmicWebFirstRatio = CosmicWebFirstRatio.Value;
            if (CosmicWebTopSize.HasValue)    p.CosmicWebTopSize = CosmicWebTopSize.Value;
            if (CosmicWebLevelMode is { } cwl && Enum.IsDefined(typeof(FracturingFog.CosmicWebLevelMode), cwl)) p.CosmicWebLevelMode = (FracturingFog.CosmicWebLevelMode)cwl;
            if (CosmicWebLevels.HasValue)     p.CosmicWebLevels = CosmicWebLevels.Value;
            if (CosmicWebPointCap.HasValue)   p.CosmicWebPointCap = CosmicWebPointCap.Value;
            if (CosmicWebPointSize.HasValue)  p.CosmicWebPointSize = CosmicWebPointSize.Value;
            if (CosmicWebDepthFade.HasValue)  p.CosmicWebDepthFade = CosmicWebDepthFade.Value;
            if (CosmicWebColour is { } cwc && Enum.IsDefined(typeof(FracturingFog.CosmicWebColour), cwc)) p.CosmicWebColour = (FracturingFog.CosmicWebColour)cwc;
            if (CosmicWebGamma.HasValue)      p.CosmicWebGamma = CosmicWebGamma.Value;
            if (CosmicWebGrid.HasValue)       p.CosmicWebGrid = CosmicWebGrid.Value;   // #2022
            if (CosmicWebSpectralIndex.HasValue) p.CosmicWebSpectralIndex = CosmicWebSpectralIndex.Value;
            if (CosmicWebSmoothing.HasValue)  p.CosmicWebSmoothing = CosmicWebSmoothing.Value;
            if (CosmicWebGrowth.HasValue)     p.CosmicWebGrowth = CosmicWebGrowth.Value;
            if (CosmicWebMeasure.HasValue)    p.CosmicWebMeasure = CosmicWebMeasure.Value;   // #2023
            if (CosmicWebMeasureSample.HasValue) p.CosmicWebMeasureSample = CosmicWebMeasureSample.Value;
            if (CosmicWebDimensionOverlay.HasValue) p.CosmicWebDimensionOverlay = CosmicWebDimensionOverlay.Value;
            if (CosmicWebRender is { } cwr && Enum.IsDefined(typeof(FracturingFog.CosmicWebRender), cwr)) p.CosmicWebRender = (FracturingFog.CosmicWebRender)cwr;   // #2024
            if (CosmicWebVolumeGrid.HasValue) p.CosmicWebVolumeGrid = CosmicWebVolumeGrid.Value;
            if (CosmicWebVolumeSmoothing.HasValue) p.CosmicWebVolumeSmoothing = CosmicWebVolumeSmoothing.Value;
            if (CosmicWebVolumeOpacity.HasValue) p.CosmicWebVolumeOpacity = CosmicWebVolumeOpacity.Value;
            if (CosmicWebVolumeSteps.HasValue) p.CosmicWebVolumeSteps = CosmicWebVolumeSteps.Value;
            if (CosmicWebVoronoiNuclei.HasValue) p.CosmicWebVoronoiNuclei = CosmicWebVoronoiNuclei.Value;   // #2025
            if (CosmicWebVoronoiPoints.HasValue) p.CosmicWebVoronoiPoints = CosmicWebVoronoiPoints.Value;   // #2025
            if (CosmicWebVoronoiField.HasValue) p.CosmicWebVoronoiField = CosmicWebVoronoiField.Value;   // #2025
            if (CosmicWebVoronoiWalls.HasValue) p.CosmicWebVoronoiWalls = CosmicWebVoronoiWalls.Value;   // #2025
            if (CosmicWebVoronoiFilaments.HasValue) p.CosmicWebVoronoiFilaments = CosmicWebVoronoiFilaments.Value;   // #2025
            if (CosmicWebVoronoiClusters.HasValue) p.CosmicWebVoronoiClusters = CosmicWebVoronoiClusters.Value;   // #2025
            if (CosmicWebVoronoiThickness.HasValue) p.CosmicWebVoronoiThickness = CosmicWebVoronoiThickness.Value;   // #2025
            if (CosmicWebVolumeBrightness.HasValue) p.CosmicWebVolumeBrightness = CosmicWebVolumeBrightness.Value;
            if (BilliardRaysColorSource.HasValue) p.BilliardRaysColorSource = (FracturingFog.BilliardRayColorSource)BilliardRaysColorSource.Value;
            if (BilliardRaysMode.HasValue)       p.BilliardRaysMode = (FracturingFog.BilliardRayMode)BilliardRaysMode.Value;
            if (BilliardRaysCrossingWindow.HasValue) p.BilliardRaysCrossingWindow = BilliardRaysCrossingWindow.Value;
            if (BilliardRaysCrossingValue.HasValue) p.BilliardRaysCrossingValue = (FracturingFog.BilliardCrossingValue)BilliardRaysCrossingValue.Value;
            if (BilliardRaysSelfCrossings.HasValue) p.BilliardRaysSelfCrossings = BilliardRaysSelfCrossings.Value;
            if (BilliardRaysDivergenceValue.HasValue) p.BilliardRaysDivergenceValue = (FracturingFog.BilliardDivergenceValue)BilliardRaysDivergenceValue.Value;
            if (BilliardRaysEscapedColour.HasValue) p.BilliardRaysEscapedColour = (FracturingFog.BilliardEscapedColour)BilliardRaysEscapedColour.Value;
            if (BilliardRaysProgress.HasValue)   p.BilliardRaysProgress = BilliardRaysProgress.Value;
            // #1445 — a full pre-#1445 billiard snapshot (all seven #627 fields, none of
            // the new ones) keeps the look it was saved with: compass sectors, no muting.
            if (IsLegacyBilliardSnapshot)
            {
                p.BilliardLaunchMode = FracturingFog.BilliardLaunchMode.FarField;
                p.BilliardGateMode = FracturingFog.BilliardGateMode.Sector;
                p.BilliardMuteMisses = false;
            }
            if (PrecisionLowTier.HasValue)
                p.PrecisionLowTier = (FracturingFog.PrecisionTier)PrecisionLowTier.Value;
            if (PrecisionHighTier.HasValue)
                p.PrecisionHighTier = (FracturingFog.PrecisionTier)PrecisionHighTier.Value;
            if (PrecisionDiffMetric.HasValue)
                p.PrecisionDiffMetric = (FracturingFog.PrecisionDiffMetric)PrecisionDiffMetric.Value;
            if (AcidWarpPattern.HasValue)
                p.AcidWarpPattern = AcidWarpPattern.Value;
            if (AcidWarpFrequency.HasValue)
                p.AcidWarpFrequency = AcidWarpFrequency.Value;
            if (AcidWarpWarpStrength.HasValue)
                p.AcidWarpWarpStrength = AcidWarpWarpStrength.Value;
            if (AcidWarpMorph.HasValue)
                p.AcidWarpMorph = AcidWarpMorph.Value;
            if (AcidWarpFlow.HasValue)
                p.AcidWarpFlow = AcidWarpFlow.Value;
            // #253 — domain warp overlay. Only sets the enable flag when carried;
            // the recall path (LoadRegionFractalParams) resets it off first so a
            // region without a warp block turns the warp off.
            if (DomainWarpEnabled.HasValue)
                p.DomainWarpEnabled = DomainWarpEnabled.Value;
            if (DomainWarpStrength.HasValue)
                p.DomainWarpStrength = DomainWarpStrength.Value;
            if (DomainWarpFrequency.HasValue)
                p.DomainWarpFrequency = DomainWarpFrequency.Value;

            // #93 (P3) — route the generic 3D camera baseline back to the family
            // it was captured from. No-op when Cam3DFamily is absent (2D regions).
            // #1738 — each key applies on its own: a missing distance / theta / phi keeps
            // the family's current value (a lone --param Cam3DDistance=30 used to be
            // dropped silently, because the block needed all three).
            if (Cam3DFamily.HasValue && CameraOf((FractalType)Cam3DFamily.Value, p) is var (d0, th0, ph0)
                && (Cam3DDistance.HasValue || Cam3DTheta.HasValue || Cam3DPhi.HasValue || Cam3DSliceW.HasValue))
            {
                double d = Cam3DDistance ?? d0, th = Cam3DTheta ?? th0, ph = Cam3DPhi ?? ph0;
                switch ((FractalType)Cam3DFamily.Value)
                {
                    case FractalType.Mandelbulb:
                        p.BulbCameraDistance = d; p.BulbCameraTheta = th; p.BulbCameraPhi = ph;
                        break;
                    case FractalType.Mandelbox:
                        p.MandelboxCameraDistance = d; p.MandelboxCameraTheta = th; p.MandelboxCameraPhi = ph;
                        break;
                    case FractalType.Kifs:
                        p.KifsCameraDistance = d; p.KifsCameraTheta = th; p.KifsCameraPhi = ph;
                        break;
                    case FractalType.QuaternionJulia:
                        p.QJuliaCameraDistance = d; p.QJuliaCameraTheta = th; p.QJuliaCameraPhi = ph;
                        if (Cam3DSliceW.HasValue) p.QJuliaSliceW = Cam3DSliceW.Value;
                        break;
                    case FractalType.QuaternionMandelbrot:
                        p.QMandelCameraDistance = d; p.QMandelCameraTheta = th; p.QMandelCameraPhi = ph;
                        if (Cam3DSliceW.HasValue) p.QMandelSliceW = Cam3DSliceW.Value;
                        break;
                    case FractalType.Kleinian:
                        p.KleinianCameraDistance = d; p.KleinianCameraTheta = th; p.KleinianCameraPhi = ph;
                        break;
                    case FractalType.MirrorSpheres:
                        p.MirrorSpheresCameraDistance = d; p.MirrorSpheresCameraTheta = th; p.MirrorSpheresCameraPhi = ph;
                        break;
                    case FractalType.MathPlane3D:   // #1713
                        p.MathPlane3DCameraDistance = d; p.MathPlane3DCameraTheta = th; p.MathPlane3DCameraPhi = ph;
                        break;
                    case FractalType.CosmicWeb:   // #2021
                        p.CosmicWebCameraDistance = d; p.CosmicWebCameraTheta = th; p.CosmicWebCameraPhi = ph;
                        break;
                    case FractalType.BicomplexMandelbrot:
                        p.BicomplexCameraDistance = d; p.BicomplexCameraTheta = th; p.BicomplexCameraPhi = ph;
                        if (Cam3DSliceW.HasValue) p.BicomplexSliceW = Cam3DSliceW.Value;
                        break;
                    case FractalType.Coquaternion:
                        p.CoquaternionCameraDistance = d; p.CoquaternionCameraTheta = th; p.CoquaternionCameraPhi = ph;
                        if (Cam3DSliceW.HasValue) p.CoquaternionSliceW = Cam3DSliceW.Value;
                        break;
                    case FractalType.Julibrot:
                        p.DualOrbitVolumeCameraDistance = d; p.DualOrbitVolumeCameraTheta = th; p.DualOrbitVolumeCameraPhi = ph;
                        if (Cam3DSliceW.HasValue) p.DualOrbitVolumeSY = Cam3DSliceW.Value;
                        break;
                }
            }

            // #1173-C — 3D family settings whose keys are unique to one family apply on
            // their own, outside the camera block above: a lone --param KleinianDeFactor=0.5
            // (or BicomplexSliceAxis=1) carries no Cam3D* keys and was silently dropped.
            if (QMandelDualOrbitColor.HasValue) p.QMandelDualOrbitColor = QMandelDualOrbitColor.Value;
            if (QMandelDualSeedX.HasValue) p.QMandelDualSeedX = QMandelDualSeedX.Value;
            if (QMandelDualSeedY.HasValue) p.QMandelDualSeedY = QMandelDualSeedY.Value;
            if (QMandelDualSeedZ.HasValue) p.QMandelDualSeedZ = QMandelDualSeedZ.Value;
            if (KleinianPreset.HasValue) p.KleinianPreset = (FracturingFog.KleinianPreset)KleinianPreset.Value;
            if (MirrorSpheresRadiusScale.HasValue) p.MirrorSpheresRadiusScale = MirrorSpheresRadiusScale.Value;   // #1440
            if (MirrorSpheresMaxBounces.HasValue) p.MirrorSpheresMaxBounces = MirrorSpheresMaxBounces.Value;
            if (MirrorSpheresReflectance.HasValue) p.MirrorSpheresReflectance = MirrorSpheresReflectance.Value;
            if (MirrorSpheresMuteMisses.HasValue) p.MirrorSpheresMuteMisses = MirrorSpheresMuteMisses.Value;
            if (MirrorSpheresMode.HasValue) p.MirrorSpheresMode = (FracturingFog.MirrorSpheresMode)MirrorSpheresMode.Value;   // #1441
            if (MirrorSpheresOpeningColors != null) p.MirrorSpheresOpeningColors = MirrorSpheresOpeningColors;
            if (MirrorSpheresViewerBlack.HasValue) p.MirrorSpheresViewerBlack = MirrorSpheresViewerBlack.Value;
            if (MirrorSpheresFresnel.HasValue) p.MirrorSpheresFresnel = MirrorSpheresFresnel.Value;
            if (MirrorSpheresOrientation.HasValue) p.MirrorSpheresOrientation = (FracturingFog.MirrorSpheresOrientation)MirrorSpheresOrientation.Value;   // #1444
            if (MirrorSpheresRoomColor != null) p.MirrorSpheresRoomColor = MirrorSpheresRoomColor;
            if (KleinianNecklaceCount.HasValue) p.KleinianNecklaceCount = KleinianNecklaceCount.Value;
            if (KleinianCustomSpheres != null) p.KleinianCustomSpheres = KleinianCustomSpheres.ConvertAll(s => s.Clone());
            if (KleinianRotationAngle.HasValue) p.KleinianRotationAngle = KleinianRotationAngle.Value;
            if (KleinianRotationAxisX.HasValue) p.KleinianRotationAxisX = KleinianRotationAxisX.Value;
            if (KleinianRotationAxisY.HasValue) p.KleinianRotationAxisY = KleinianRotationAxisY.Value;
            if (KleinianRotationAxisZ.HasValue) p.KleinianRotationAxisZ = KleinianRotationAxisZ.Value;
            if (KleinianColorSource.HasValue) p.KleinianColorSource = (FracturingFog.KleinianColorSource)KleinianColorSource.Value;
            if (KleinianDeFactor.HasValue) p.KleinianDeFactor = KleinianDeFactor.Value;
            if (MarchRelaxation.HasValue) p.MarchRelaxation = MarchRelaxation.Value;   // #1335
            if (BicomplexSliceAxis.HasValue) p.BicomplexSliceAxis = (FracturingFog.Models.BicomplexSliceAxis)BicomplexSliceAxis.Value;

            // #94 (P4) — non-spatial family params (static-hold legs).
            if (PlasmaSeed.HasValue) p.PlasmaSeed = PlasmaSeed.Value;
            if (PlasmaRoughness.HasValue) p.PlasmaRoughness = PlasmaRoughness.Value;
            if (!string.IsNullOrEmpty(FlamePresetName)) p.FlamePresetName = FlamePresetName;
            if (FlameGamma.HasValue) p.FlameGamma = FlameGamma.Value;
            if (DlaParticles.HasValue) p.DlaParticles = DlaParticles.Value;
            if (DlaSeed.HasValue) p.DlaSeed = DlaSeed.Value;
            if (LogisticSeed.HasValue) p.LogisticSeed = LogisticSeed.Value;
            if (LogisticBurnIn.HasValue) p.LogisticBurnIn = LogisticBurnIn.Value;
            if (this.TalbotProfile.HasValue) p.TalbotProfile = (FracturingFog.TalbotProfile)this.TalbotProfile.Value;   // #1570
            if (TalbotDuty.HasValue) p.TalbotDuty = TalbotDuty.Value;
            if (TalbotModes.HasValue) p.TalbotModes = TalbotModes.Value;
            if (this.TalbotView.HasValue) p.TalbotView = (FracturingFog.TalbotView)this.TalbotView.Value;
            if (TalbotDynamicRange.HasValue) p.TalbotDynamicRange = TalbotDynamicRange.Value;
            if (this.TalbotDomainContours.HasValue) p.TalbotDomainContours = (FracturingFog.DomainContourMode)this.TalbotDomainContours.Value;   // #1598
            if (TalbotContourCount.HasValue) p.TalbotContourCount = TalbotContourCount.Value;
            if (TalbotSliceOverlay.HasValue) p.TalbotSliceOverlay = TalbotSliceOverlay.Value;   // #1601
            if (TalbotSliceZeta.HasValue) p.TalbotSliceZeta = TalbotSliceZeta.Value;
            if (SlitCascadeChildren.HasValue) p.SlitCascadeChildren = SlitCascadeChildren.Value;   // #1569
            if (SlitCascadeRatio.HasValue) p.SlitCascadeRatio = SlitCascadeRatio.Value;
            if (SlitCascadeDepth.HasValue) p.SlitCascadeDepth = SlitCascadeDepth.Value;
            if (SlitCascadeSeparation.HasValue) p.SlitCascadeSeparation = SlitCascadeSeparation.Value;
            if (SlitCascadeWidth.HasValue) p.SlitCascadeWidth = SlitCascadeWidth.Value;
            if (SlitCascadeWavelength.HasValue) p.SlitCascadeWavelength = SlitCascadeWavelength.Value;
            if (this.SlitCascadeLayout.HasValue) p.SlitCascadeLayout = (FracturingFog.SlitCascadeLayout)this.SlitCascadeLayout.Value;
            if (this.SlitCascadeView.HasValue) p.SlitCascadeView = (FracturingFog.SlitCascadeView)this.SlitCascadeView.Value;
            if (SlitCascadeDynamicRange.HasValue) p.SlitCascadeDynamicRange = SlitCascadeDynamicRange.Value;
            if (SlitCascadeQ.HasValue) p.SlitCascadeQ = SlitCascadeQ.Value;
            if (SlitCascadeCharge.HasValue) p.SlitCascadeCharge = SlitCascadeCharge.Value;
            if (this.SlitCascadeChargePattern.HasValue) p.SlitCascadeChargePattern = (FracturingFog.SlitCascadeChargePattern)this.SlitCascadeChargePattern.Value;
            if (SlitCascadeWhichPath.HasValue) p.SlitCascadeWhichPath = SlitCascadeWhichPath.Value;   // #1600
            if (this.SlitCascadeWhichPathPattern.HasValue) p.SlitCascadeWhichPathPattern = (FracturingFog.SlitCascadeWhichPathPattern)this.SlitCascadeWhichPathPattern.Value;
            if (SlitCascadeWhichPathLevel.HasValue) p.SlitCascadeWhichPathLevel = SlitCascadeWhichPathLevel.Value;
            if (SlitCascadeWhichPathMask.HasValue) p.SlitCascadeWhichPathMask = SlitCascadeWhichPathMask.Value;
            if (this.SlitCascadeDomainContours.HasValue) p.SlitCascadeDomainContours = (FracturingFog.DomainContourMode)this.SlitCascadeDomainContours.Value;   // #1598
            if (SlitCascadeContourCount.HasValue) p.SlitCascadeContourCount = SlitCascadeContourCount.Value;
            if (DetectionCount.HasValue) p.DetectionCount = DetectionCount.Value;   // #1599
            if (WhiteLightSamples.HasValue) p.WhiteLightSamples = WhiteLightSamples.Value;   // #1597
            if (this.SlitCascadeWhiteLight.HasValue) p.SlitCascadeWhiteLight = (FracturingFog.WhiteLightMode)this.SlitCascadeWhiteLight.Value;
            if (this.TalbotWhiteLight.HasValue) p.TalbotWhiteLight = (FracturingFog.WhiteLightMode)this.TalbotWhiteLight.Value;
            if (DetectionSeed.HasValue) p.DetectionSeed = DetectionSeed.Value;
            if (DetectionDotRadius.HasValue) p.DetectionDotRadius = DetectionDotRadius.Value;
            if (DetectionGain.HasValue) p.DetectionGain = DetectionGain.Value;
            if (!string.IsNullOrEmpty(LyapunovSequence)) p.LyapunovSequence = LyapunovSequence;
            if (LyapunovWarmup.HasValue) p.LyapunovWarmup = LyapunovWarmup.Value;
            if (TranscendentalMap.HasValue) p.TranscendentalMap = (FracturingFog.TranscendentalMap)TranscendentalMap.Value;
            if (TranscendentalLambdaRe.HasValue) p.TranscendentalLambdaRe = TranscendentalLambdaRe.Value;
            if (TranscendentalLambdaIm.HasValue) p.TranscendentalLambdaIm = TranscendentalLambdaIm.Value;
            if (TranscendentalBailout.HasValue) p.TranscendentalBailout = TranscendentalBailout.Value;
            if (MagnetConvergence.HasValue) p.MagnetConvergence = MagnetConvergence.Value;
            if (MagnetConvergenceEpsilon.HasValue) p.MagnetConvergenceEpsilon = MagnetConvergenceEpsilon.Value;
            if (RandomTileSeed.HasValue) p.RandomTileSeed = RandomTileSeed.Value;
            if (!string.IsNullOrEmpty(IFSPresetName)) p.IFSPresetName = IFSPresetName;
            if (!string.IsNullOrEmpty(LSystemPresetName)) p.LSystemPresetName = LSystemPresetName;
            // #864 / #866 — dual-orbit escape-geometry.
            if (this.DualOrbitMap.HasValue) p.DualOrbitMap = (FracturingFog.DualOrbitMap)this.DualOrbitMap.Value;
            if (this.DualOrbitField.HasValue) p.DualOrbitField = (FracturingFog.DualOrbitField)this.DualOrbitField.Value;
            if (DualOrbitCSeedX.HasValue) p.DualOrbitCSeedX = DualOrbitCSeedX.Value;
            if (DualOrbitCSeedY.HasValue) p.DualOrbitCSeedY = DualOrbitCSeedY.Value;
            if (DualOrbitCSeedZ.HasValue) p.DualOrbitCSeedZ = DualOrbitCSeedZ.Value;
            if (DualOrbitSZ.HasValue) p.DualOrbitSZ = DualOrbitSZ.Value;
            if (DualOrbitCEqualsS.HasValue) p.DualOrbitCEqualsS = DualOrbitCEqualsS.Value;
            if (DualOrbitBailout.HasValue) p.DualOrbitBailout = DualOrbitBailout.Value;
            if (DualOrbitRatioSpan.HasValue) p.DualOrbitRatioSpan = DualOrbitRatioSpan.Value;
            if (DualOrbitLyapunovSpan.HasValue) p.DualOrbitLyapunovSpan = DualOrbitLyapunovSpan.Value;
            // #1124 — Dual Buddhabrot; #1134 — the sampler keys are shared with the classic family.
            if (BuddhaSamples.HasValue) p.BuddhaSamples = BuddhaSamples.Value;
            if (BuddhaIterHigh.HasValue) p.BuddhaIterHigh = BuddhaIterHigh.Value;
            if (BuddhaIterLow.HasValue) p.BuddhaIterLow = BuddhaIterLow.Value;
            if (BuddhaIterMid.HasValue) p.BuddhaIterMid = BuddhaIterMid.Value;
            if (BuddhaMinIter.HasValue) p.BuddhaMinIter = BuddhaMinIter.Value;   // #1218
            if (this.DualBuddhaDeposit.HasValue) p.DualBuddhaDeposit = (FracturingFog.DualBuddhaDeposit)this.DualBuddhaDeposit.Value;
            if (this.DualBuddhaNebula.HasValue) p.DualBuddhaNebula = (FracturingFog.DualBuddhaNebula)this.DualBuddhaNebula.Value;
            if (DualBuddhaAnti.HasValue) p.DualBuddhaAnti = DualBuddhaAnti.Value;
            if (this.BuddhaQualityMode.HasValue) p.BuddhaQualityMode = (FracturingFog.Models.BuddhaQualityMode)this.BuddhaQualityMode.Value;
            if (BuddhaMetropolis.HasValue) p.BuddhaMetropolis = BuddhaMetropolis.Value;
            if (BuddhaProgressive.HasValue) p.BuddhaProgressive = BuddhaProgressive.Value;
            if (BuddhaSeed.HasValue) p.BuddhaSeed = BuddhaSeed.Value;
            if (BuddhaZoomCompensation.HasValue) p.BuddhaZoomCompensation = BuddhaZoomCompensation.Value;
            if (BuddhaScaleSamplesWithWindow.HasValue) p.BuddhaScaleSamplesWithWindow = BuddhaScaleSamplesWithWindow.Value;   // #1224
            if (DualBuddhaCSeedX.HasValue) p.DualBuddhaCSeedX = DualBuddhaCSeedX.Value;
            if (DualBuddhaCSeedY.HasValue) p.DualBuddhaCSeedY = DualBuddhaCSeedY.Value;
            if (DualBuddhaMinIter.HasValue) p.DualBuddhaMinIter = DualBuddhaMinIter.Value;
            if (this.DualBuddhaComposite.HasValue) p.DualBuddhaComposite = (FracturingFog.DualBuddhaComposite)this.DualBuddhaComposite.Value;
            if (DualBuddhaColorZ.HasValue) p.DualBuddhaColorZ = DualBuddhaColorZ.Value;
            if (DualBuddhaColorCB.HasValue) p.DualBuddhaColorCB = DualBuddhaColorCB.Value;
            if (DualBuddhaColorCE.HasValue) p.DualBuddhaColorCE = DualBuddhaColorCE.Value;
            if (DualBuddhaGainZ.HasValue) p.DualBuddhaGainZ = DualBuddhaGainZ.Value;
            if (DualBuddhaGainCB.HasValue) p.DualBuddhaGainCB = DualBuddhaGainCB.Value;
            if (DualBuddhaGainCE.HasValue) p.DualBuddhaGainCE = DualBuddhaGainCE.Value;
            if (DualOrbitDivergenceRatio.HasValue) p.DualOrbitDivergenceRatio = DualOrbitDivergenceRatio.Value;
            if (DualOrbitContourDensity.HasValue) p.DualOrbitContourDensity = DualOrbitContourDensity.Value;
            if (DualOrbitDomainGrid.HasValue) p.DualOrbitDomainGrid = DualOrbitDomainGrid.Value;
            if (this.DualOrbitDomainPalette.HasValue) p.DualOrbitDomainPalette = (FracturingFog.DualOrbitDomainPalette)this.DualOrbitDomainPalette.Value;
            if (DualOrbitDomainMarkCuts.HasValue) p.DualOrbitDomainMarkCuts = DualOrbitDomainMarkCuts.Value;
            if (this.DualOrbitLagColors.HasValue) p.DualOrbitLagColors = (FracturingFog.DualOrbitCategoricalColors)this.DualOrbitLagColors.Value;
            if (this.DualOrbitTrapShape.HasValue) p.DualOrbitTrapShape = (FracturingFog.Models.OrbitTrapShapeDef)this.DualOrbitTrapShape.Value;
            if (DualOrbitTrapScale.HasValue) p.DualOrbitTrapScale = DualOrbitTrapScale.Value;
            if (this.DualOrbitTrapFrame.HasValue) p.DualOrbitTrapFrame = (FracturingFog.DualOrbitTrapFrame)this.DualOrbitTrapFrame.Value;
            if (DualOrbitTrapRotate.HasValue) p.DualOrbitTrapRotate = DualOrbitTrapRotate.Value;
            if (DualOrbitTrapScaleByOrbit.HasValue) p.DualOrbitTrapScaleByOrbit = DualOrbitTrapScaleByOrbit.Value;
            if (DualOrbitTrapAngle.HasValue) p.DualOrbitTrapAngle = DualOrbitTrapAngle.Value;
            if (DualOrbitSplitHeight.HasValue) p.DualOrbitSplitHeight = DualOrbitSplitHeight.Value;
            if (this.DualOrbitHeightField.HasValue) p.DualOrbitHeightField = (FracturingFog.DualOrbitField)this.DualOrbitHeightField.Value;
            if (DualOrbitInterferenceK.HasValue) p.DualOrbitInterferenceK = DualOrbitInterferenceK.Value;
            if (DualOrbitInterferenceGamma.HasValue) p.DualOrbitInterferenceGamma = DualOrbitInterferenceGamma.Value;
            if (DualOrbitWhichPath.HasValue) p.DualOrbitWhichPath = DualOrbitWhichPath.Value;
            if (DualOrbitAngularMomentum.HasValue) p.DualOrbitAngularMomentum = DualOrbitAngularMomentum.Value;
            if (this.DualOrbitWindingMode.HasValue) p.DualOrbitWindingMode = (FracturingFog.DualOrbitWindingMode)this.DualOrbitWindingMode.Value;
            if (this.DualOrbitInterferenceContours.HasValue) p.DualOrbitInterferenceContours = (FracturingFog.DomainContourMode)this.DualOrbitInterferenceContours.Value;   // #1598
            if (DualOrbitInterferenceContourCount.HasValue) p.DualOrbitInterferenceContourCount = DualOrbitInterferenceContourCount.Value;
            if (DualOrbitInterferencePaths.HasValue) p.DualOrbitInterferencePaths = DualOrbitInterferencePaths.Value;   // #1602
            if (this.DualOrbitPathLayout.HasValue) p.DualOrbitPathLayout = (FracturingFog.DualOrbitPathLayout)this.DualOrbitPathLayout.Value;
            if (DualOrbitEnsembleN.HasValue) p.DualOrbitEnsembleN = DualOrbitEnsembleN.Value;
            if (DualOrbitEnsembleRadius.HasValue) p.DualOrbitEnsembleRadius = DualOrbitEnsembleRadius.Value;
            if (DualOrbitEnsembleSectors.HasValue) p.DualOrbitEnsembleSectors = DualOrbitEnsembleSectors.Value;
            if (DualOrbitUncertaintyLevels.HasValue) p.DualOrbitUncertaintyLevels = DualOrbitUncertaintyLevels.Value;
            if (DualOrbitFtleSpan.HasValue) p.DualOrbitFtleSpan = DualOrbitFtleSpan.Value;
            if (DualOrbitAnisotropyScale.HasValue) p.DualOrbitAnisotropyScale = DualOrbitAnisotropyScale.Value;
            if (this.DualOrbitLicSource.HasValue) p.DualOrbitLicSource = (FracturingFog.DualOrbitLicSource)this.DualOrbitLicSource.Value;
            if (DualOrbitLicLength.HasValue) p.DualOrbitLicLength = DualOrbitLicLength.Value;
            if (DualOrbitLicStrength.HasValue) p.DualOrbitLicStrength = DualOrbitLicStrength.Value;
            if (DualOrbitLyapunovSmooth.HasValue) p.DualOrbitLyapunovSmooth = DualOrbitLyapunovSmooth.Value;
            if (DualOrbitStripeDensity.HasValue) p.DualOrbitStripeDensity = DualOrbitStripeDensity.Value;
            if (DualOrbitDEScale.HasValue) p.DualOrbitDEScale = DualOrbitDEScale.Value;
            if (DualOrbitOutlineWidth.HasValue) p.DualOrbitOutlineWidth = DualOrbitOutlineWidth.Value;
            if (DualOrbitOutlineColorZ.HasValue) p.DualOrbitOutlineColorZ = DualOrbitOutlineColorZ.Value;
            if (DualOrbitOutlineColorC.HasValue) p.DualOrbitOutlineColorC = DualOrbitOutlineColorC.Value;
            if (this.DualOrbitPalette2D.HasValue) p.DualOrbitPalette2D = (FracturingFog.DualOrbitPalette2D)this.DualOrbitPalette2D.Value;
            if (DualOrbitBivariateScale.HasValue) p.DualOrbitBivariateScale = DualOrbitBivariateScale.Value;
            if (DualOrbitPhaseK.HasValue) p.DualOrbitPhaseK = DualOrbitPhaseK.Value;
            if (this.DualOrbitSliceAxes.HasValue) p.DualOrbitSliceAxes = (FracturingFog.DualOrbitSliceAxes)this.DualOrbitSliceAxes.Value;
            if (DualOrbitSX.HasValue) p.DualOrbitSX = DualOrbitSX.Value;
            if (DualOrbitSY.HasValue) p.DualOrbitSY = DualOrbitSY.Value;
            // #980 — per-orbit layer colouring.
            if (this.DualOrbitColorMode.HasValue) p.DualOrbitColorMode = (FracturingFog.DualOrbitColorMode)this.DualOrbitColorMode.Value;
            if (DualOrbitThemeZ != null) p.DualOrbitThemeZ = DualOrbitThemeZ;
            if (DualOrbitThemeC != null) p.DualOrbitThemeC = DualOrbitThemeC;
            if (this.DualOrbitLayerBlend.HasValue) p.DualOrbitLayerBlend = (FracturingFog.DualOrbitLayerBlend)this.DualOrbitLayerBlend.Value;
            if (DualOrbitOpacityZ.HasValue) p.DualOrbitOpacityZ = DualOrbitOpacityZ.Value;
            if (DualOrbitOpacityC.HasValue) p.DualOrbitOpacityC = DualOrbitOpacityC.Value;
            // #972 — dual-orbit volume.
            if (DualOrbitVolumeSXCenter.HasValue) p.DualOrbitVolumeSXCenter = DualOrbitVolumeSXCenter.Value;
            if (DualOrbitVolumeHalfHeight.HasValue) p.DualOrbitVolumeHalfHeight = DualOrbitVolumeHalfHeight.Value;
            if (this.DualOrbitVolumeColor.HasValue) p.DualOrbitVolumeColor = (FracturingFog.DualOrbitVolumeColor)this.DualOrbitVolumeColor.Value;
            // #893 — Indra's Pearls 2D group.
            if (IndrasFamily.HasValue) p.IndrasFamily = (IndrasGroupFamily)IndrasFamily.Value;
            if (IndrasMaskitMuRe.HasValue) p.IndrasMaskitMuRe = IndrasMaskitMuRe.Value;
            if (IndrasMaskitMuIm.HasValue) p.IndrasMaskitMuIm = IndrasMaskitMuIm.Value;
            if (IndrasGrandmaTaRe.HasValue) p.IndrasGrandmaTaRe = IndrasGrandmaTaRe.Value;
            if (IndrasGrandmaTaIm.HasValue) p.IndrasGrandmaTaIm = IndrasGrandmaTaIm.Value;
            if (IndrasGrandmaTbRe.HasValue) p.IndrasGrandmaTbRe = IndrasGrandmaTbRe.Value;
            if (IndrasGrandmaTbIm.HasValue) p.IndrasGrandmaTbIm = IndrasGrandmaTbIm.Value;
            if (IndrasGrandmaSecondSolution.HasValue) p.IndrasGrandmaSecondSolution = IndrasGrandmaSecondSolution.Value;
            if (IndrasRileyCRe.HasValue) p.IndrasRileyCRe = IndrasRileyCRe.Value;
            if (IndrasRileyCIm.HasValue) p.IndrasRileyCIm = IndrasRileyCIm.Value;
            if (IndrasMaxWordDepth.HasValue) p.IndrasMaxWordDepth = IndrasMaxWordDepth.Value;
            if (this.IndrasRenderMode.HasValue)
                p.IndrasRenderMode = (FracturingFog.Models.IndrasRenderMode)this.IndrasRenderMode.Value;
            if (this.IndrasColorSource.HasValue)
                p.IndrasColorSource = (FracturingFog.Models.IndrasColorSource)this.IndrasColorSource.Value;

            // #961 — full animatable coverage.
            if (EscapeIterationScale.HasValue) p.EscapeIterationScale = EscapeIterationScale.Value;
            if (BlaTolerance.HasValue) p.BlaTolerance = BlaTolerance.Value;   // #1275
            if (AdaptiveAa.HasValue) p.AdaptiveAa = AdaptiveAa.Value;                       // #1344
            if (SolidRegionSkip.HasValue) p.SolidRegionSkip = SolidRegionSkip.Value;         // #1343
            if (SolidRegionAuto.HasValue) p.SolidRegionAuto = SolidRegionAuto.Value;         // #1394
            if (DeepLa is >= (int)DeepLaMode.Off and <= (int)DeepLaMode.On) p.DeepLa = (DeepLaMode)DeepLa.Value;   // #1629
            if (AdaptiveAaThreshold.HasValue) p.AdaptiveAaThreshold = AdaptiveAaThreshold.Value;
            if (AdaptiveAaTolerance.HasValue) p.AdaptiveAaTolerance = AdaptiveAaTolerance.Value;
            if (AdaptiveAaDistanceSkip.HasValue) p.AdaptiveAaDistanceSkip = AdaptiveAaDistanceSkip.Value;   // #1981 part 2
            if (AdaptiveAaDistanceMinPx.HasValue) p.AdaptiveAaDistanceMinPx = AdaptiveAaDistanceMinPx.Value;
            if (IFSIterations.HasValue) p.IFSIterations = IFSIterations.Value;
            if (LSystemDepth.HasValue) p.LSystemDepth = LSystemDepth.Value;
            if (!string.IsNullOrEmpty(AttractorPresetName)) p.AttractorPresetName = AttractorPresetName;
            if (AttractorA.HasValue) p.AttractorA = AttractorA.Value;
            if (AttractorB.HasValue) p.AttractorB = AttractorB.Value;
            if (AttractorC.HasValue) p.AttractorC = AttractorC.Value;
            if (AttractorD.HasValue) p.AttractorD = AttractorD.Value;
            if (UserEquationRotationDegrees.HasValue) p.UserEquationRotationDegrees = UserEquationRotationDegrees.Value;
            if (!string.IsNullOrWhiteSpace(UserEquationSeed)) p.UserEquationSeed = UserEquationSeed;
            if (UserEquationEscapeRadius.HasValue) p.EscapeRadius = UserEquationEscapeRadius.Value;
            if (!string.IsNullOrWhiteSpace(UserEquationBailoutCondition)) p.UserEquationBailoutCondition = UserEquationBailoutCondition;
            if (UserEquationBailoutReplacesModulus.HasValue) p.UserEquationBailoutReplacesModulus = UserEquationBailoutReplacesModulus.Value;
            if (UserEquationColorInterior.HasValue) p.UserEquationColorInterior = UserEquationColorInterior.Value;
            if (!string.IsNullOrWhiteSpace(UserEquationPotential)) p.UserEquationPotential = UserEquationPotential;   // #1989
            if (!string.IsNullOrWhiteSpace(UserEquationNewtonRoots)) p.UserEquationNewtonRoots = UserEquationNewtonRoots;
            if (BulbPower.HasValue) p.BulbPower = BulbPower.Value;
            if (BulbIterations.HasValue) p.BulbIterations = BulbIterations.Value;
            if (UserBulbIterations.HasValue) p.UserBulbIterations = UserBulbIterations.Value;
            if (UserBulbTime.HasValue) p.UserBulbTime = UserBulbTime.Value;
            if (MandelboxScale.HasValue) p.MandelboxScale = MandelboxScale.Value;
            if (MandelboxFixedRadius.HasValue) p.MandelboxFixedRadius = MandelboxFixedRadius.Value;
            if (MandelboxMinRadius.HasValue) p.MandelboxMinRadius = MandelboxMinRadius.Value;
            if (MandelboxIterations.HasValue) p.MandelboxIterations = MandelboxIterations.Value;
            if (KifsFold.HasValue) p.KifsFold = (KifsFoldKind)KifsFold.Value;
            if (KifsScale.HasValue) p.KifsScale = KifsScale.Value;
            if (KifsOffsetX.HasValue) p.KifsOffsetX = KifsOffsetX.Value;
            if (KifsOffsetY.HasValue) p.KifsOffsetY = KifsOffsetY.Value;
            if (KifsOffsetZ.HasValue) p.KifsOffsetZ = KifsOffsetZ.Value;
            if (KifsIterations.HasValue) p.KifsIterations = KifsIterations.Value;
            if (QJuliaCX.HasValue) p.QJuliaCX = QJuliaCX.Value;
            if (QJuliaCY.HasValue) p.QJuliaCY = QJuliaCY.Value;
            if (QJuliaCZ.HasValue) p.QJuliaCZ = QJuliaCZ.Value;
            if (QJuliaCW.HasValue) p.QJuliaCW = QJuliaCW.Value;
            if (AcidWarpCenterX.HasValue) p.AcidWarpCenterX = AcidWarpCenterX.Value;
            if (AcidWarpCenterY.HasValue) p.AcidWarpCenterY = AcidWarpCenterY.Value;
            if (FlameVibrancy.HasValue) p.FlameVibrancy = FlameVibrancy.Value;
            if (KleinianSphereScale.HasValue) p.KleinianSphereScale = KleinianSphereScale.Value;
            if (KleinianIterations.HasValue) p.KleinianIterations = KleinianIterations.Value;
            if (RandomTileCount.HasValue) p.RandomTileCount = RandomTileCount.Value;
            if (RandomTileRelief.HasValue) p.RandomTileRelief = RandomTileRelief.Value;
            if (RandomTileSizeExponent.HasValue) p.RandomTileSizeExponent = RandomTileSizeExponent.Value;
            if (RandomTileGap.HasValue) p.RandomTileGap = RandomTileGap.Value;
            if (RandomTileMinPixelRadius.HasValue) p.RandomTileMinPixelRadius = RandomTileMinPixelRadius.Value;
            if (RandomTileShape.HasValue) p.RandomTileShape = (FracturingFog.RandomTileShape)RandomTileShape.Value;
        }
    }

    // ── Relief 3D (2D heightfield / Oblique raymarch) snapshot ─────────────────

    /// <summary>
    /// Full snapshot of the <c>FractalParameters.Relief2D*</c> family — the
    /// Relief 3D (2D heightfield hillshade + Oblique 3D raymarch) settings.
    /// Unlike <see cref="RegionFractalParams"/> (per-family spatial params), the
    /// whole block is captured as one unit so a saved 3D relief view round-trips
    /// exactly: camera, tone curve, edge fade, isolation cull, and the mesh-export
    /// knobs. Only recorded when relief is enabled, so plain 2D bookmarks carry
    /// nothing (the property is omitted from JSON when null).
    /// </summary>
    public sealed class Relief3DSettings
    {
        // Relief master + hillshade (Phase 1)
        public bool Enabled { get; set; } = true;
        public double HeightScale { get; set; } = 1.0;
        public double LightAzimuthDeg { get; set; } = 135.0;
        public double LightElevationDeg { get; set; } = 30.0;
        public double ShadowStrength { get; set; } = 0.6;
        public double Strength { get; set; } = 1.0;
        public bool Absolute { get; set; } = false;   // #127

        // Oblique 3D raymarch (Phase 2)
        public bool Raymarch { get; set; } = false;
        public double CameraAzimuthDeg { get; set; } = 0.0;
        public double CameraElevationDeg { get; set; } = 45.0;
        public double CameraFovDeg { get; set; } = 50.0;
        public double CameraZoom { get; set; } = 1.0;
        public bool CameraOrthographic { get; set; } = false;
        public int Supersample { get; set; } = 2;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public HeightCurve2D HeightCurve { get; set; } = HeightCurve2D.Log;
        public bool BicubicHeight { get; set; } = false;
        public bool GroundPlane { get; set; } = true;
        public bool AutoShade { get; set; } = true;
        public double EdgeFade { get; set; } = 0.04;
        public bool HiResField { get; set; } = true;   // #143
        public int FieldFloor { get; set; } = 1080;     // #143

        // Height normalisation (#1026). Defaults = Peak / measured, so a region
        // saved before these existed recalls exactly as before.
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ReliefHeightMode HeightMode { get; set; } = ReliefHeightMode.Peak;
        public double HeightRef { get; set; } = 0.0;
        public double HeightBaseline { get; set; } = -1.0;

        // Tolerance anchor (#1027). Default Camera = the original march.
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ReliefDetailAnchor DetailAnchor { get; set; } = ReliefDetailAnchor.Camera;
        public bool TrueHeight { get; set; } = false;   // #1027 follow-up
        public double TerrainSmoothing { get; set; } = 0.02;   // #1035
        public bool CanonicalField { get; set; } = true;       // #1028

        // Isolation cull (#135)
        public bool Isolate { get; set; } = false;
        public bool IsolateByDetail { get; set; } = true;
        public double DetailThreshold { get; set; } = 0.6;
        public bool IsolateByColor { get; set; } = false;
        public string DropColorsCsv { get; set; } = "";
        public double ColorTolerance { get; set; } = 0.12;

        // Mesh export knobs (#138)
        public double MeshHeight { get; set; } = 0.15;
        public double MeshSmoothing { get; set; } = 0.5;
        public int MeshGrid { get; set; } = 512;
        public double MeshMaxMB { get; set; } = 0.0;
        public double MeshUnderside { get; set; } = 0.6;

        // Froxel volumetrics (#408, S6). Carried on the region so a scene / batch /
        // slideshow render sourced from this region can turn froxel fog — and its
        // cross-frame temporal reprojection — ON without a live UI. All default to
        // the froxel-off single-frame path, so a region without these stays
        // byte-identical. FroxelTemporal only bites when FroxelVolumetrics is on.
        public bool FroxelVolumetrics { get; set; } = false;
        public bool FroxelTemporal { get; set; } = false;
        public double FroxelTemporalFeedback { get; set; } = 0.9;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public FroxelQuality FroxelQuality { get; set; } = FroxelQuality.Balanced;

        // Guided À-Trous denoise + SVGF temporal (#402, S4). Carried on the region so a
        // scene / batch / slideshow render sourced from it can enable the denoise — and
        // its cross-frame SVGF temporal accumulation + variance guiding — without a live
        // UI. All default off / neutral, so a region without these is byte-identical.
        // DenoiseTemporal only bites when DenoiseIterations > 0.
        public int DenoiseIterations { get; set; } = 0;
        public double DenoiseColorSigma { get; set; } = 0.10;
        public double DenoiseNormalSigma { get; set; } = 0.30;
        public double DenoiseDepthSigma { get; set; } = 0.20;
        public bool DenoiseAdaptiveSupersample { get; set; } = false;
        public bool DenoiseSimd { get; set; } = false;   // #1285
        public bool DenoiseTemporal { get; set; } = false;
        public double DenoiseTemporalFeedback { get; set; } = 0.8;
        public double DenoiseVarianceScale { get; set; } = 4.0;

        // Height-field shaping (#518 detail/gamma, #592 height source, #520 far /
        // settle detail) + auto-shade zero handling (#296). Defaults mirror
        // FractalParameters, so a region saved before these were captured (the
        // keys are absent from its JSON) recalls exactly as it did before.
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ReliefHeightSource HeightSource { get; set; } = ReliefHeightSource.Smooth;
        public double HeightBlend { get; set; } = 0.5;
        public double DistanceFalloff { get; set; } = 0.02;   // #1029
        public double TerraceRamp { get; set; } = 1.0;        // #1489
        public double DetailGain { get; set; } = 1.0;
        public int DetailRadius { get; set; } = 0;
        public double HeightGamma { get; set; } = 1.0;
        public double FarDetail { get; set; } = 1.0;
        public bool SettleDetail { get; set; } = false;
        public bool AutoShadeKeepExplicitZeros { get; set; } = false;

        // Cinematic camera (S3 #389/#400): thin-lens DOF + in-camera exposure.
        public double DofApertureRadius { get; set; } = 0.0;
        public double DofFocusDistance { get; set; } = 0.0;
        public double CameraExposureEv { get; set; } = 0.0;

        // Vector motion blur (S1 #398) — only bites on sequence renders.
        public double MotionBlurStrength { get; set; } = 0.0;
        public int MotionBlurSamples { get; set; } = 8;

        // Froxel sub-cell temporal reprojection (S6 #408).
        public bool FroxelReproject { get; set; } = false;

        // Relight in post (S1 #398). Tints are 0xAARRGGBB, white = none.
        public bool Relight { get; set; } = false;
        public double RelightDiffuseGain { get; set; } = 1.0;
        public double RelightSpecularGain { get; set; } = 1.0;
        public double RelightAoStrength { get; set; } = 1.0;
        public double RelightAmbient { get; set; } = 0.0;
        public uint RelightDiffuseTint { get; set; } = 0xFFFFFFFFu;
        public uint RelightSpecularTint { get; set; } = 0xFFFFFFFFu;

        // Deliberately NOT captured (see RegionRelief3DTests guard):
        //   Relief2DGpuRaymarch — CPU vs GPU dispatch preference (Ctrl+Shift+G), a
        //     host/backend choice at CPU-twin parity, not part of the view.
        //   Relief2DEmptySkip   — conservative step-count acceleration over the
        //     SAME surface; changes speed, never the image.

        /// <summary>Apply <paramref name="s"/> when non-null, otherwise turn
        /// relief OFF on <paramref name="p"/>. The authoritative recall path so a
        /// plain (no-relief) region clears a relief view instead of leaving it on.</summary>
        public static void ApplyOrDisable(Relief3DSettings? s, FractalParameters p)
        {
            if (p == null) return;
            if (s != null) { s.ApplyTo(p); return; }
            p.Relief2DEnabled = false;
            p.Relief2DRaymarch = false;
            // Froxel is a relief-raymarch feature; clear it too so an authoritative
            // recall of a plain region can't leave stale froxel fog armed.
            p.Relief2DFroxelVolumetrics = false;
            p.Relief2DFroxelTemporal = false;
            // Denoise (S4) is likewise a relief feature — clear it so a plain-region
            // recall can't leave the À-Trous / SVGF pass armed on a flat view.
            p.Relief2DDenoiseIterations = 0;
            p.Relief2DDenoiseTemporal = false;
        }

        /// <summary>Capture the relief block from a live params, or null when
        /// relief is off (so plain regions stay clean).</summary>
        public static Relief3DSettings? Snapshot(FractalParameters? p)
        {
            if (p == null || !p.Relief2DEnabled) return null;
            return new Relief3DSettings
            {
                Enabled            = p.Relief2DEnabled,
                HeightScale        = p.Relief2DHeightScale,
                LightAzimuthDeg    = p.Relief2DLightAzimuthDeg,
                LightElevationDeg  = p.Relief2DLightElevationDeg,
                ShadowStrength     = p.Relief2DShadowStrength,
                Strength           = p.Relief2DStrength,
                Absolute           = p.Relief2DAbsolute,
                Raymarch           = p.Relief2DRaymarch,
                CameraAzimuthDeg   = p.Relief2DCameraAzimuthDeg,
                CameraElevationDeg = p.Relief2DCameraElevationDeg,
                CameraFovDeg       = p.Relief2DCameraFovDeg,
                CameraZoom         = p.Relief2DCameraZoom,
                CameraOrthographic = p.Relief2DCameraOrthographic,
                Supersample        = p.Relief2DSupersample,
                HeightCurve        = p.Relief2DHeightCurve,
                BicubicHeight      = p.Relief2DBicubicHeight,
                GroundPlane        = p.Relief2DGroundPlane,
                AutoShade          = p.Relief2DAutoShade,
                EdgeFade           = p.Relief2DEdgeFade,
                HiResField         = p.Relief2DHiResField,
                FieldFloor         = p.Relief2DFieldFloor,
                HeightMode         = p.Relief2DHeightMode,
                HeightRef          = p.Relief2DHeightRef,
                HeightBaseline     = p.Relief2DHeightBaseline,
                DetailAnchor       = p.Relief2DDetailAnchor,
                TrueHeight         = p.Relief2DTrueHeight,
                TerrainSmoothing   = p.Relief2DTerrainSmoothing,
                CanonicalField     = p.Relief2DCanonicalField,
                Isolate            = p.Relief2DIsolate,
                IsolateByDetail    = p.Relief2DIsolateByDetail,
                DetailThreshold    = p.Relief2DDetailThreshold,
                IsolateByColor     = p.Relief2DIsolateByColor,
                DropColorsCsv      = p.Relief2DDropColorsCsv,
                ColorTolerance     = p.Relief2DColorTolerance,
                MeshHeight         = p.Relief2DMeshHeight,
                MeshSmoothing      = p.Relief2DMeshSmoothing,
                MeshGrid           = p.Relief2DMeshGrid,
                MeshMaxMB          = p.Relief2DMeshMaxMB,
                MeshUnderside      = p.Relief2DMeshUnderside,
                FroxelVolumetrics  = p.Relief2DFroxelVolumetrics,
                FroxelTemporal     = p.Relief2DFroxelTemporal,
                FroxelTemporalFeedback = p.Relief2DFroxelTemporalFeedback,
                FroxelQuality      = p.Relief2DFroxelQuality,
                DenoiseIterations  = p.Relief2DDenoiseIterations,
                DenoiseColorSigma  = p.Relief2DDenoiseColorSigma,
                DenoiseNormalSigma = p.Relief2DDenoiseNormalSigma,
                DenoiseDepthSigma  = p.Relief2DDenoiseDepthSigma,
                DenoiseAdaptiveSupersample = p.Relief2DDenoiseAdaptiveSupersample,
                DenoiseSimd        = p.Relief2DDenoiseSimd,
                DenoiseTemporal    = p.Relief2DDenoiseTemporal,
                DenoiseTemporalFeedback = p.Relief2DDenoiseTemporalFeedback,
                DenoiseVarianceScale = p.Relief2DDenoiseVarianceScale,
                HeightSource       = p.Relief2DHeightSource,
                HeightBlend        = p.Relief2DHeightBlend,
                DistanceFalloff    = p.Relief2DDistanceFalloff,
                TerraceRamp        = p.Relief2DTerraceRamp,
                DetailGain         = p.Relief2DDetailGain,
                DetailRadius       = p.Relief2DDetailRadius,
                HeightGamma        = p.Relief2DHeightGamma,
                FarDetail          = p.Relief2DFarDetail,
                SettleDetail       = p.Relief2DSettleDetail,
                AutoShadeKeepExplicitZeros = p.Relief2DAutoShadeKeepExplicitZeros,
                DofApertureRadius  = p.Relief2DDofApertureRadius,
                DofFocusDistance   = p.Relief2DDofFocusDistance,
                CameraExposureEv   = p.Relief2DCameraExposureEv,
                MotionBlurStrength = p.Relief2DMotionBlurStrength,
                MotionBlurSamples  = p.Relief2DMotionBlurSamples,
                FroxelReproject    = p.Relief2DFroxelReproject,
                Relight            = p.Relief2DRelight,
                RelightDiffuseGain = p.Relief2DRelightDiffuseGain,
                RelightSpecularGain = p.Relief2DRelightSpecularGain,
                RelightAoStrength  = p.Relief2DRelightAoStrength,
                RelightAmbient     = p.Relief2DRelightAmbient,
                RelightDiffuseTint = p.Relief2DRelightDiffuseTint,
                RelightSpecularTint = p.Relief2DRelightSpecularTint,
            };
        }

        /// <summary>Restore every captured field onto a live params.</summary>
        public void ApplyTo(FractalParameters p)
        {
            if (p == null) return;
            p.Relief2DEnabled            = Enabled;
            p.Relief2DHeightScale        = HeightScale;
            p.Relief2DLightAzimuthDeg    = LightAzimuthDeg;
            p.Relief2DLightElevationDeg  = LightElevationDeg;
            p.Relief2DShadowStrength     = ShadowStrength;
            p.Relief2DStrength           = Strength;
            p.Relief2DAbsolute           = Absolute;
            p.Relief2DRaymarch           = Raymarch;
            p.Relief2DCameraAzimuthDeg   = CameraAzimuthDeg;
            p.Relief2DCameraElevationDeg = CameraElevationDeg;
            p.Relief2DCameraFovDeg       = CameraFovDeg;
            p.Relief2DCameraZoom         = CameraZoom;
            p.Relief2DCameraOrthographic = CameraOrthographic;
            p.Relief2DSupersample        = Supersample;
            p.Relief2DHeightCurve        = HeightCurve;
            p.Relief2DBicubicHeight      = BicubicHeight;
            p.Relief2DGroundPlane        = GroundPlane;
            p.Relief2DAutoShade          = AutoShade;
            p.Relief2DEdgeFade           = EdgeFade;
            p.Relief2DHiResField         = HiResField;
            p.Relief2DFieldFloor         = FieldFloor;
            p.Relief2DHeightMode         = HeightMode;
            p.Relief2DHeightRef          = HeightRef;
            p.Relief2DHeightBaseline     = HeightBaseline;
            p.Relief2DDetailAnchor       = DetailAnchor;
            p.Relief2DTrueHeight         = TrueHeight;
            p.Relief2DTerrainSmoothing   = TerrainSmoothing;
            p.Relief2DCanonicalField     = CanonicalField;
            p.Relief2DIsolate            = Isolate;
            p.Relief2DIsolateByDetail    = IsolateByDetail;
            p.Relief2DDetailThreshold    = DetailThreshold;
            p.Relief2DIsolateByColor     = IsolateByColor;
            p.Relief2DDropColorsCsv      = DropColorsCsv ?? "";
            p.Relief2DColorTolerance     = ColorTolerance;
            p.Relief2DMeshHeight         = MeshHeight;
            p.Relief2DMeshSmoothing      = MeshSmoothing;
            p.Relief2DMeshGrid           = MeshGrid;
            p.Relief2DMeshMaxMB          = MeshMaxMB;
            p.Relief2DMeshUnderside      = MeshUnderside;
            p.Relief2DFroxelVolumetrics      = FroxelVolumetrics;
            p.Relief2DFroxelTemporal         = FroxelTemporal;
            p.Relief2DFroxelTemporalFeedback = FroxelTemporalFeedback;
            p.Relief2DFroxelQuality          = FroxelQuality;
            p.Relief2DDenoiseIterations      = DenoiseIterations;
            p.Relief2DDenoiseColorSigma      = DenoiseColorSigma;
            p.Relief2DDenoiseNormalSigma     = DenoiseNormalSigma;
            p.Relief2DDenoiseDepthSigma      = DenoiseDepthSigma;
            p.Relief2DDenoiseAdaptiveSupersample = DenoiseAdaptiveSupersample;
            p.Relief2DDenoiseSimd            = DenoiseSimd;
            p.Relief2DDenoiseTemporal        = DenoiseTemporal;
            p.Relief2DDenoiseTemporalFeedback = DenoiseTemporalFeedback;
            p.Relief2DDenoiseVarianceScale   = DenoiseVarianceScale;
            p.Relief2DHeightSource           = HeightSource;
            p.Relief2DHeightBlend            = HeightBlend;
            p.Relief2DDistanceFalloff        = DistanceFalloff;
            p.Relief2DTerraceRamp            = TerraceRamp;
            p.Relief2DDetailGain             = DetailGain;
            p.Relief2DDetailRadius           = DetailRadius;
            p.Relief2DHeightGamma            = HeightGamma;
            p.Relief2DFarDetail              = FarDetail;
            p.Relief2DSettleDetail           = SettleDetail;
            p.Relief2DAutoShadeKeepExplicitZeros = AutoShadeKeepExplicitZeros;
            p.Relief2DDofApertureRadius      = DofApertureRadius;
            p.Relief2DDofFocusDistance       = DofFocusDistance;
            p.Relief2DCameraExposureEv       = CameraExposureEv;
            p.Relief2DMotionBlurStrength     = MotionBlurStrength;
            p.Relief2DMotionBlurSamples      = MotionBlurSamples;
            p.Relief2DFroxelReproject        = FroxelReproject;
            p.Relief2DRelight                = Relight;
            p.Relief2DRelightDiffuseGain     = RelightDiffuseGain;
            p.Relief2DRelightSpecularGain    = RelightSpecularGain;
            p.Relief2DRelightAoStrength      = RelightAoStrength;
            p.Relief2DRelightAmbient         = RelightAmbient;
            p.Relief2DRelightDiffuseTint     = RelightDiffuseTint;
            p.Relief2DRelightSpecularTint    = RelightSpecularTint;
        }
    }

    // ── Library ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Singleton library of <see cref="FractalRegion"/> bookmarks.
    /// Call <see cref="Load"/> once at startup; <see cref="Save"/> whenever
    /// the user list changes.
    /// </summary>
    public sealed class FractalRegionLibrary
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        private static FractalRegionLibrary? _instance;

        /// <summary>
        /// Instance of the library.  Lazy-initialized on first access.
        /// </summary>
        public static FractalRegionLibrary Instance
            => _instance ??= new FractalRegionLibrary();

        private FractalRegionLibrary() { }

        // ── Storage ───────────────────────────────────────────────────────────

        private static string SettingsDir => AppDataPaths.Root;

        private static string RegionsFile =>
            Path.Combine(SettingsDir, "regions.json");

        // ── Built-in regions ──────────────────────────────────────────────────

        // #1603 — the vortex-well presets' Relief 3D: an oblique raymarch from 50° up, the
        // light high (55°) with soft shadows so the pit walls stay lit, and +3 EV because the
        // Domain view's brightness is the log intensity, dim round the vortices. A new
        // instance per preset (regions must not share a mutable settings object).
        // #1604 — one enabled audio→param binding (#268), for the music-driven presets.
        private static List<FracturingFog.Audio.AudioParamBinding> AudioDrive(string param,
            FracturingFog.Audio.AudioSignalKind source, double outMin, double outMax,
            FracturingFog.Audio.AudioResponseCurve curve = FracturingFog.Audio.AudioResponseCurve.Linear,
            double attack = 0.0, double release = 0.0) =>
        [
            new()
            {
                ParamName = param, Enabled = true,
                Binding = new FracturingFog.Audio.AudioModulationBinding
                {
                    Source = source, Curve = curve, OutMin = outMin, OutMax = outMax,
                    AttackSeconds = attack, ReleaseSeconds = release,
                },
            },
        ];

        private static Relief3DSettings VortexWellRelief() => new()
        {
            Enabled = true, Raymarch = true, CameraElevationDeg = 50.0,
            LightElevationDeg = 55.0, ShadowStrength = 0.3, CameraExposureEv = 3.0,
        };

        /// <summary>#1989 — the rotating-frame effective potential Ω (ThreeBodySystem.Omega) for equal
        /// masses, and its equilibria L1..L5 in ThreeBodyScene's gate order (L2 by bisection of ∂Ω/∂x).</summary>
        internal const string LagrangeBasinsPotential =
            "let mu = 0.5 in 0.5*(x^2 + y^2) + (1 - mu)/sqrt((x + mu)^2 + y^2) + mu/sqrt((x - 1 + mu)^2 + y^2)";
        internal const string LagrangeBasinsRoots =
            "0, 0; 1.19840614455492, 0; -1.19840614455492, 0; 0, 0.8660254037844386; 0, -0.8660254037844386";

        private static readonly FractalRegion[] _builtIns =
        [
            new()
            {
                Name        = "Classic Full View",
                CenterX     = -0.5,
                CenterY     =  0.0,
                Zoom        =  0.5,
                Iterations  =  256,
                Description = "The default overview showing the complete Mandelbrot set.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Seahorse Valley",
                CenterX     = -0.7435669,
                CenterY     =  0.1314023,
                Zoom        =  400.0,
                Iterations  =  800,
                Description = "Classic seahorse-shaped spirals near the main cardioid neck.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Elephant Valley",
                CenterX     =  0.3245046,
                CenterY     =  0.0483453,
                Zoom        =  300.0,
                Iterations  =  700,
                Description = "Elephant-trunk filaments branching from the period-2 bulb.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Double Spiral",
                CenterX     = -0.7269,
                CenterY     =  0.1889,
                Zoom        =  2500.0,
                Iterations  = 1200,
                Description = "Interleaved double spiral arms deep in Seahorse Valley.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Lightning Storm",
                CenterX     = -0.7746806,
                CenterY     =  0.1245250,
                Zoom        =  1200.0,
                Iterations  = 1400,
                Description = "Jagged lightning-bolt filaments near the top of the main bulb.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Galaxy Spiral",
                CenterX     = -0.5622951,
                CenterY     =  0.6427316,
                Zoom        =  3000.0,
                Iterations  = 1500,
                Description = "Spiral arms resembling a barred galaxy in the upper limb.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Mini Mandelbrot",
                CenterX     = -1.7497388,
                CenterY     =  0.0,
                Zoom        =  6000.0,
                Iterations  = 2000,
                Description = "A miniature copy of the whole set — self-similarity at depth.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Feigenbaum Point",
                CenterX     = -1.4011552,
                CenterY     =  0.0,
                Zoom        =  2000.0,
                Iterations  = 1800,
                Description = "The Feigenbaum accumulation point where period doublings converge.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Star Cluster",
                CenterX     = -0.5443,
                CenterY     =  0.6070,
                Zoom        =  800.0,
                Iterations  = 1200,
                Description = "Dense star-like radiating filaments above the main cardioid.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Needle Tip",
                CenterX     = -1.9999118,
                CenterY     =  0.0,
                Zoom        =  8000.0,
                Iterations  = 2500,
                Description = "Extreme zoom at the tip of the real-axis needle.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Ultra
            },
            new()
            {
                Name        = "Parabolic Bifurcation",
                CenterX     = -0.1552,
                CenterY     =  1.0300,
                Zoom        =  600.0,
                Iterations  = 1100,
                Description = "Parabolic bifurcation site — two buds splitting from one.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Triple Spiral",
                CenterX     = -0.0886,
                CenterY     =  0.6544,
                Zoom        =  5000.0,
                Iterations  = 2000,
                Description = "Three interlocked spiral arms deep in the upper filament zone.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbrot,
                QualityPreset = QualityPreset.High
            },
            new()
            {
                Name        = "Magnet 1 - Main Body",
                CenterX     =  1.5,
                CenterY     =  0.0,
                Zoom        =  0.6,
                Iterations  =  512,
                Description = "Heart-shaped main body of the Magnet 1 rational map.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Magnet1,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Magnet 2 - Triple Lobe",
                CenterX     =  1.5,
                CenterY     =  0.0,
                Zoom        =  0.5,
                Iterations  =  512,
                Description = "Three-lobed main body of the cubic Magnet 2 variant.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Magnet2,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Glynn - Canonical",
                CenterX     = -0.2,
                CenterY     =  0.0,
                Zoom        =  0.7,
                Iterations  =  512,
                Description = "Canonical Glynn Julia dendrite at c = -0.2.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Glynn,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Logistic - Full Cascade",
                CenterX     =  3.5,
                CenterY     =  0.5,
                Zoom        =  2.0,
                Iterations  = 4000,
                Description = "Period-doubling cascade through chaos: r ∈ ~[2.6, 4.4].",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Logistic,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Halley - z3 - 1 basins",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  =  64,
                Description = "Halley basins of z³ − 1 — three roots, fine filaments.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Halley,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Secant - z3 - 1 basins",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  =  64,
                Description = "Secant-method basins of z³ − 1 — chord-step pattern through Wada lakes.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Secant,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Spider - Canonical",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.2,
                Iterations  = 512,
                Description = "Canonical Spider at decay = 0.5. Spider-leg filaments around the origin.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Spider,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Mandelbox - Canonical (scale 2)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Canonical Mandelbox at scale = 2.0. Vault-and-corridor structure with the classic box footprint.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbox,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Mandelbox - Inverse (scale -1.5)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Juliabox-like inversive Mandelbox at scale = −1.5. Set MandelboxScale before recall.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbox,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Mandelbox - Open Pore (scale 3)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Open-pore Mandelbox at scale = 3.0. Inner spiral structure visible. Set MandelboxScale before recall.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbox,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "KIFS - Menger sponge",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Canonical Menger sponge — sort-3 fold + scale-3 from (1,1,1). Set KifsFold = Menger before recall.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Kifs,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "KIFS - Sierpinski tetra",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Sierpinski tetrahedron gasket — 3 vertex reflections + scale-2 from (1,1,1). Set KifsFold = Sierpinski before recall.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Kifs,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Quat Julia - Classic Norton (-0.2, 0.4, -0.4, -0.4)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Canonical 4D quaternion Julia slice — Hart 1989 reference c. Filaments and bulbs visible from default camera angle.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.QuaternionJulia,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Quat Julia - Dendrite (0.0, 1.0, 0.0, 0.0)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Pure-imaginary c — open dendritic structure. Set QJuliaC to (0, 1, 0, 0) before recall.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.QuaternionJulia,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Quat Julia - Spheroid (-1.0, 0.2, 0.0, 0.0)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Compact spheroid-like quaternion Julia. Set QJuliaC to (−1, 0.2, 0, 0) before recall.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.QuaternionJulia,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Quat Mandelbrot - Slice W = 0",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Quaternion Mandelbrot at the W=0 slice — q=0 orbit, c varies per pixel. The familiar Mandelbrot silhouette extruded into the Z axis.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.QuaternionMandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Quat Mandelbrot - Slice W = 0.5",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Off-axis 4D slice of the quaternion Mandelbrot. Set QMandelSliceW = 0.5 before recall — exposes thin filaments not present in the W=0 plane.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.QuaternionMandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Apollonian - (-1, 2, 2, 3) Gasket",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.75,
                Iterations  = 12,
                Description = "Integral Apollonian packing built from the seed curvature quadruple (−1, 2, 2, 3). Outer unit disk, two half-radius circles on the diameter, third-radius circles above and below. Recurse via Vieta jumping until sub-pixel.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Apollonian,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "DLA - Default Brownian Tree",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 1,
                Description = "Witten–Sander diffusion-limited aggregation seeded at the canvas centre. Default 8000 particles produce a recognisable dendrite at 512² in well under a second. Bump DlaParticles for denser growth.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Dla,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Random Tiling - Bourke Fill",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.875,
                Iterations  = 1,
                Description = "Paul Bourke's random space filling of the plane: shapes of power-law-decreasing size dropped at random, non-overlapping positions until the plane fills. Seed-deterministic; each shape domes for Relief3D / volumetric. Tune RandomTileCount / RandomTileSizeExponent / RandomTileSeed.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.RandomTile,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Bicomplex Mandelbrot - Slice k = 0",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Bicomplex (tessarine) Mandelbrot at the k = 0 slice. With sliceW = 0 the 3D slab collapses onto the standard 2D Mandelbrot extruded along the j axis.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.BicomplexMandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Bicomplex Mandelbrot - Slice k = 0.4",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Off-axis bicomplex slice. Set BicomplexSliceW = 0.4 before recall to expose the zero-divisor seam slabs unique to the tessarine algebra.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.BicomplexMandelbrot,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Kleinian - Tetrahedral 4-Sphere",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 16,
                Description = "Schottky-style Kleinian limit set generated by inversion in four mutually tangent spheres at the (±1, ±1, ±1) even-parity corners. The limit set is the 3D cocoon between the spheres where their inversions meet.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Kleinian,
                QualityPreset = QualityPreset.Standard
            },
            // #1444 — the Sweet, Ott & Yorke 1999 Nature photograph (Fig. 2): four touching
            // mirrored balls stacked like cannonballs, seen through one of the upper openings.
            // Red and blue boards cover the other two upper openings, white light comes in
            // through the bottom opening, and the viewing opening and the room are dark.
            // Opening k is the face opposite ball k: 0 faces the table (white), 1 and 2 are
            // the upper boards, 3 is the viewing opening. Vermillion stands in for the red
            // board (colour-blind safe). Which upper opening held which board isn't stated.
            new()
            {
                Name        = "Wada Mirror Spheres - 1999 Photograph",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  2.2,   // the opening fills the frame, as in Fig. 2
                Iterations  = 256,
                Description = "The Sweet, Ott & Yorke (Nature, 1999) four-mirror-ball photograph: looking in through an upper opening of the touching stack, with red and blue boards over the other upper openings, white light from below and a dark room. Every boundary between the colours has all four colours near it at every zoom (the Wada property).",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.MirrorSpheres,
                QualityPreset = QualityPreset.Standard,
                Params      = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.MirrorSpheres,
                    Cam3DDistance = 3.8,
                    Cam3DTheta = 0.0,
                    Cam3DPhi = 1.2309594173407747,   // acos(1/3): the upper opening's outward normal
                    MirrorSpheresMode = (int)FracturingFog.MirrorSpheresMode.Photo,
                    MirrorSpheresOrientation = (int)FracturingFog.MirrorSpheresOrientation.VertexUp,
                    MirrorSpheresOpeningColors = "#F2F2F2,#D55E00,#0072B2,#000000",
                    MirrorSpheresRoomColor = "#000000",
                    MirrorSpheresMaxBounces = 256,
                },
            },
            // #1512 — Billiard Rays presets (tracker #1505): one per kind of picture.
            new()
            {
                Name        = "Billiard Rays - Three-Disk Gate Flow",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.525,
                Iterations  = 256,
                Description = "The classic three-disk scatterer seen from above, lit by three parallel beams. Each ray is drawn in the colour of the gap it finally leaves through, so the flow into each exit basin shows in its own colour, the incoming beams included.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.BilliardRays,
                QualityPreset = QualityPreset.Standard,
                Params      = new RegionFractalParams
                {
                    BilliardGeometry = (int)FracturingFog.BilliardGeometry.ThreeDisk,
                    BilliardDiskRadius = 0.8, BilliardSeparation = 1.0, BilliardMaxBounces = 256,
                    BilliardGateMode = (int)FracturingFog.BilliardGateMode.ExitGap,
                    BilliardRaysSweep = (int)FracturingFog.BilliardRaySweep.BSweep,
                    BilliardRaysCount = 200_000, BilliardRaysAngleCount = 3, BilliardRaysAngle = 90.0,
                    BilliardRaysMinBounces = 2,
                    BilliardRaysColorSource = (int)FracturingFog.BilliardRayColorSource.Gate,
                    BilliardRaysMode = (int)FracturingFog.BilliardRayMode.Paths,
                },
            },
            new()
            {
                Name        = "Billiard Rays - Ring Crossings",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.525,
                Iterations  = 256,
                Description = "Six mirrors on a ring, rays fired inward from all round it. Only the points where a ray crosses one fired just before it are drawn, coloured by the difference in their bounce counts there, with relief turning that difference into height.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.BilliardRays,
                QualityPreset = QualityPreset.Standard,
                Relief3D    = new Relief3DSettings { Enabled = true },
                Params      = new RegionFractalParams
                {
                    BilliardGeometry = (int)FracturingFog.BilliardGeometry.Ring,
                    BilliardDiskCount = 6, BilliardDiskRadius = 0.45, BilliardSeparation = 1.3, BilliardMaxBounces = 256,
                    BilliardGateMode = (int)FracturingFog.BilliardGateMode.ExitGap,
                    BilliardRaysSweep = (int)FracturingFog.BilliardRaySweep.AngleSweep,
                    BilliardRaysCount = 60_000, BilliardRaysSpread = 40.0, BilliardRaysSeed = 1,
                    BilliardRaysMinBounces = 2,
                    BilliardRaysMode = (int)FracturingFog.BilliardRayMode.Crossings,
                    BilliardRaysCrossingWindow = 100,
                    BilliardRaysCrossingValue = (int)FracturingFog.BilliardCrossingValue.DeltaN,
                },
            },
            new()
            {
                Name        = "Billiard Rays - Custom Five-Disk Divergence",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.525,
                Iterations  = 256,
                Description = "An irregular five-mirror layout (edit it by dragging disks in Ray paths). Each line is where a ray parts from the ray launched next to it; the ones that then leave through a different gap trace the boundary between the exit basins, drawn over the faint ray paths.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.BilliardRays,
                QualityPreset = QualityPreset.Standard,
                Params      = new RegionFractalParams
                {
                    BilliardGeometry = (int)FracturingFog.BilliardGeometry.Custom,
                    BilliardCustomDisks = "0,1.1,0.55;1.05,0.2,0.5;0.55,-1,0.6;-0.75,-0.85,0.45;-1.1,0.35,0.5",
                    BilliardDiskRadius = 0.5, BilliardMaxBounces = 256,
                    BilliardGateMode = (int)FracturingFog.BilliardGateMode.ExitGap,
                    BilliardRaysSweep = (int)FracturingFog.BilliardRaySweep.BSweep,
                    BilliardRaysCount = 200_000, BilliardRaysAngleCount = 2, BilliardRaysAngle = 20.0,
                    BilliardRaysMinBounces = 2,
                    BilliardRaysMode = (int)FracturingFog.BilliardRayMode.DivergenceOverPaths,
                    BilliardRaysDivergenceValue = (int)FracturingFog.BilliardDivergenceValue.GateChange,
                },
            },
            // #1539 — a finite patch of the periodic Lorentz gas (Sinai 1970; Bunimovich & Sinai 1981).
            new()
            {
                Name        = "Billiard Rays - Hexagonal Lorentz Gas",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.21875,
                Iterations  = 256,
                Description = "Sixty-one mirrors on a hexagonal grid, a patch of the periodic Lorentz gas, lit by one parallel beam from the left. Each ray is coloured by how many times it bounced, so the colour shifts from the side the beam comes in on, where rays leave after a few bounces, to the middle of the patch, where they bounce many times.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.BilliardRays,
                QualityPreset = QualityPreset.Standard,
                Params      = new RegionFractalParams
                {
                    BilliardGeometry = (int)FracturingFog.BilliardGeometry.HexLattice,
                    BilliardDiskCount = 61, BilliardDiskRadius = 0.3, BilliardSeparation = 1.0, BilliardMaxBounces = 256,
                    BilliardLatticeRotation = 0.0,
                    BilliardRaysSweep = (int)FracturingFog.BilliardRaySweep.BSweep,
                    BilliardRaysCount = 200_000, BilliardRaysAngleCount = 1, BilliardRaysAngle = 0.0,
                    BilliardRaysColorSource = (int)FracturingFog.BilliardRayColorSource.Bounces,
                    BilliardRaysMode = (int)FracturingFog.BilliardRayMode.Paths,
                },
            },
            // #1928 — black hole shadow presets (epic #1922). The first is the setup of Shipley &
            // Dolan 2016 Fig. 19 (equal masses, separation a = 2 M, viewed side on from 50 M); the
            // close-up is the upper neck of their a = 1 panel. The rest show unequal masses, three
            // holes, an inclined camera and the lensed sky.
            new()
            {
                Name          = "Black Hole Shadow - Equal-Mass Pair",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.14,
                Iterations    =  256,
                Description   = "Two equal, extremally charged black holes a = 2 apart, seen side on from 50 M: the setup of Shipley & Dolan's binary shadow figure. Besides the two main shadows there are thin 'eyebrow' shadows, each holding smaller copies of the pair. The bound 'Black hole: closing pair' animation moves the holes between 0.5 and 3 apart.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Black hole: closing pair",
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.Binary,
                    BlackHoleMass1 = 1.0, BlackHoleMass2 = 1.0, BlackHoleSeparation = 2.0,
                    BlackHoleCameraDistance = 50.0, BlackHoleInclination = 90.0, BlackHoleAzimuth = 0.0,
                },
            },
            new()
            {
                Name          = "Black Hole Shadow - Eyebrow Hierarchy",
                CenterX       =  0.0,
                CenterY       = -7.0,
                Zoom          =  2.2,
                Iterations    =  256,
                Description   = "Close-up of the neck above the gap between two equal holes a = 1 apart. Eyebrows sit inside eyebrows, alternating between the two holes, down to the pixel size: light that circles one hole, then the other, many times before falling in. Zoom on any eyebrow edge for more.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.Binary,
                    BlackHoleMass1 = 1.0, BlackHoleMass2 = 1.0, BlackHoleSeparation = 1.0,
                    BlackHoleCameraDistance = 50.0, BlackHoleInclination = 90.0, BlackHoleAzimuth = 0.0,
                },
            },
            new()
            {
                Name          = "Black Hole Shadow - Unequal Masses",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.14,
                Iterations    =  256,
                Description   = "A heavy hole and one of 0.4 its mass, 2 apart. The small hole's shadow sits inside a crescent of the big one's, and the eyebrows on the two sides no longer match.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.Binary,
                    BlackHoleMass1 = 1.0, BlackHoleMass2 = 0.4, BlackHoleSeparation = 2.0,
                    BlackHoleCameraDistance = 50.0, BlackHoleInclination = 90.0, BlackHoleAzimuth = 0.0,
                },
            },
            new()
            {
                Name          = "Black Hole Shadow - Triangle of Three",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.1,
                Iterations    =  256,
                Description   = "Three equal holes at the corners of a triangle of side 2, seen face on. Light that weaves between them before falling in draws eyebrows of all three holes, nested inside one another.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.Triangle,
                    BlackHoleMass1 = 1.0, BlackHoleSeparation = 2.0,
                    BlackHoleCameraDistance = 50.0, BlackHoleInclination = 90.0, BlackHoleAzimuth = 90.0,
                },
            },
            new()
            {
                Name          = "Black Hole Shadow - Inclined Camera",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.14,
                Iterations    =  256,
                Description   = "Two equal holes 1.5 apart, seen 60 degrees from their axis: the near hole's shadow sits inside the far one's and the eyebrows become rings. The bound 'Black hole: camera swing' animation swings the camera from nearly end-on to the other end.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Black hole: camera swing",
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.Binary,
                    BlackHoleMass1 = 1.0, BlackHoleMass2 = 1.0, BlackHoleSeparation = 1.5,
                    BlackHoleCameraDistance = 50.0, BlackHoleInclination = 60.0, BlackHoleAzimuth = 0.0,
                },
            },
            new()
            {
                Name          = "Black Hole Shadow - Lensed Starfield",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.11,
                Iterations    =  256,
                Description   = "The equal pair in front of a star field, through a pinhole camera 30 M away. Stars near the shadows are stretched into arcs and repeated. The bound 'Black hole: turning sky' animation turns the sky, so the lensed copies stream round the holes.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Black hole: turning sky",
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.Binary,
                    BlackHoleMass1 = 1.0, BlackHoleMass2 = 1.0, BlackHoleSeparation = 2.0,
                    BlackHoleCameraDistance = 30.0, BlackHoleInclination = 90.0, BlackHoleAzimuth = 0.0,
                    BlackHoleProjection = (int)FracturingFog.BlackHoleProjection.Pinhole,
                    BlackHoleSky = (int)FracturingFog.BlackHoleSky.Stars,
                    BlackHoleSkySeed = 1, BlackHoleSkyRotation = 0.0,
                },
            },
            // #1995 — User Lens: the built-in pair written as U (parity), a ring of eight holes, and a
            // smooth (non-harmonic) lens.
            new()
            {
                Name          = "Black Hole Shadow - User Lens Pair",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.14,
                Iterations    =  256,
                Description   = "The equal-mass pair written as a user lens, U = 1 + 1/|x - (0,0,1)| + 1/|x - (0,0,-1)|, with the two holes listed as capture spheres. FF traces the rays with this U and its exact gradient, so the picture is the built-in Equal-Mass Pair. Edit U to make your own spacetime or lens.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.UserLens,
                    BlackHoleLensSource = FractalParameters.BlackHoleLensDefaultSource, BlackHoleLensCapture = 0.0,
                    BlackHoleCustom = "0,0,1,1;0,0,-1,1",
                    BlackHoleCameraDistance = 50.0, BlackHoleInclination = 90.0, BlackHoleAzimuth = 0.0,
                },
            },
            new()
            {
                Name          = "Black Hole Shadow - User Lens Ring",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.14,
                Iterations    =  256,
                Description   = "Eight small extremal holes (mass 0.25 each) evenly spaced on a circle of radius 2, written as one U (a sum of point sources, so still a vacuum Majumdar-Papapetrou spacetime), seen 60 degrees from the axis. Rays are captured where U passes 6. A discrete stand-in for a ring of mass; compare the Black Hole + Ring family.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.UserLens,
                    BlackHoleLensSource = "1 + 0.25*(1/sqrt((x - 2.0)^2 + y^2 + z^2) + 1/sqrt((x - 1.414213562373)^2 + (y - 1.414213562373)^2 + z^2) + 1/sqrt(x^2 + (y - 2.0)^2 + z^2) + 1/sqrt((x + 1.414213562373)^2 + (y - 1.414213562373)^2 + z^2) + 1/sqrt((x + 2.0)^2 + y^2 + z^2) + 1/sqrt((x + 1.414213562373)^2 + (y + 1.414213562373)^2 + z^2) + 1/sqrt(x^2 + (y + 2.0)^2 + z^2) + 1/sqrt((x - 1.414213562373)^2 + (y + 1.414213562373)^2 + z^2))", BlackHoleLensCapture = 6.0,
                    BlackHoleCameraDistance = 50.0, BlackHoleInclination = 60.0, BlackHoleAzimuth = 0.0,
                },
            },
            new()
            {
                Name          = "Black Hole Shadow - Smooth Lens",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.3,
                Iterations    =  256,
                Description   = "A smooth bump, U = 1 + 1.5 exp(-|x|^2 / 2), in front of a checker sky. It has no horizon, so nothing is captured: there is no shadow, only lensing, with the sky folded over itself along caustics. This U is not harmonic, so it is an optical medium for the rays, not a black hole spacetime.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleShadow,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BlackHoleLayout = (int)FracturingFog.BlackHoleLayout.UserLens,
                    BlackHoleLensSource = "1 + 1.5*exp(-(x^2 + y^2 + z^2)/2)", BlackHoleLensCapture = 0.0,
                    BlackHoleCameraDistance = 30.0, BlackHoleInclination = 90.0, BlackHoleAzimuth = 0.0,
                    BlackHoleProjection = (int)FracturingFog.BlackHoleProjection.Pinhole,
                    BlackHoleSky = (int)FracturingFog.BlackHoleSky.Checker, BlackHoleSkyDivisions = 16,
                },
            },
            // #1942 — Hénon-Heiles exit basins (Aguirre, Vallejo & Sanjuán 2001).
            new()
            {
                Name          = "Henon-Heiles - Exit Basins",
                CenterX       =  0.0,
                CenterY       =  -0.25,
                Zoom          =  0.85,
                Iterations    =  256,
                Description   = "Launches across the Henon-Heiles well at energy 0.25, each moving anticlockwise. The colour is the channel the particle leaves through. The three basins wind into each other, and every boundary touches all three (Wada basins). The bound 'Henon-Heiles: rising energy' animation sweeps the energy from just above escape to 0.5.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.HenonHeilesBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Henon-Heiles: rising energy",
                Params        = new RegionFractalParams
                {
                    HenonHeilesEnergy = 0.25, HenonHeilesView = (int)FracturingFog.HenonHeilesView.Plane,
                    HenonHeilesLaunch = (int)FracturingFog.HenonHeilesLaunch.Tangential,
                },
            },
            new()
            {
                Name          = "Henon-Heiles - Poincare Section",
                CenterX       =  0.2,
                CenterY       =  0.0,
                Zoom          =  1.6,
                Iterations    =  256,
                Description   = "Launches on the line x = 0 at energy 0.25: horizontal is y, vertical is the momentum py, px from the energy. This is the section in which Aguirre, Vallejo and Sanjuan showed the exit basins are Wada. The bands fold into each other towards the bottom-right boundary.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.HenonHeilesBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    HenonHeilesEnergy = 0.25, HenonHeilesView = (int)FracturingFog.HenonHeilesView.Section,
                },
            },
            new()
            {
                Name          = "Henon-Heiles - Near Escape Energy",
                CenterX       =  0.0,
                CenterY       =  -0.25,
                Zoom          =  0.85,
                Iterations    =  256,
                Description   = "Energy 0.18, just above the escape energy 1/6: the exits are narrow and most launches wander for a long time before finding one, so the basins break into fine grain. The dark islands are regular orbits that never escape (undecided at the time limit 1000).",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.HenonHeilesBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    HenonHeilesEnergy = 0.18, HenonHeilesView = (int)FracturingFog.HenonHeilesView.Plane,
                    HenonHeilesLaunch = (int)FracturingFog.HenonHeilesLaunch.Tangential, HenonHeilesMaxTime = 1000.0,
                },
            },
            new()
            {
                Name          = "Henon-Heiles - High Energy",
                CenterX       =  0.0,
                CenterY       =  -0.25,
                Zoom          =  0.85,
                Iterations    =  256,
                Description   = "Energy 0.5: the channels are wide, most launches leave quickly, and the fractal boundaries thin to narrow ribbons between large smooth basins.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.HenonHeilesBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    HenonHeilesEnergy = 0.5, HenonHeilesView = (int)FracturingFog.HenonHeilesView.Plane,
                    HenonHeilesLaunch = (int)FracturingFog.HenonHeilesLaunch.Tangential,
                },
            },
            new()
            {
                Name          = "Henon-Heiles - Escape Time",
                CenterX       =  0.0,
                CenterY       =  -0.25,
                Zoom          =  0.85,
                Iterations    =  256,
                Description   = "The exit basins at energy 0.25 coloured by how long each particle stays in the well (log escape time through the palette). The long stays line the basin boundaries.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.HenonHeilesBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    HenonHeilesEnergy = 0.25, HenonHeilesView = (int)FracturingFog.HenonHeilesView.Plane,
                    HenonHeilesLaunch = (int)FracturingFog.HenonHeilesLaunch.Tangential,
                    HenonHeilesChannel = (int)FracturingFog.HenonHeilesChannel.EscapeTime,
                },
            },
            new()
            {
                Name          = "Henon-Heiles - Parallel Launch",
                CenterX       =  0.0,
                CenterY       =  -0.25,
                Zoom          =  0.85,
                Iterations    =  256,
                Description   = "Every particle starts moving straight up (fixed launch angle 90 degrees) at energy 0.25. The basins keep the mirror symmetry about the vertical axis but lose the threefold one. The bound 'Henon-Heiles: turning launch' animation turns the launch direction once round.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.HenonHeilesBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Henon-Heiles: turning launch",
                Params        = new RegionFractalParams
                {
                    HenonHeilesEnergy = 0.25, HenonHeilesView = (int)FracturingFog.HenonHeilesView.Plane,
                    HenonHeilesLaunch = (int)FracturingFog.HenonHeilesLaunch.FixedAngle, HenonHeilesLaunchAngle = 90.0,
                },
            },
            // #1943 — restricted three-body basins (Nagler 2004 / 2005 fates; Zotos 2016 Newton-Raphson basins).
            new()
            {
                Name          = "Three-Body - Copenhagen Fates",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "Two equal masses (the Copenhagen problem) in the rotating frame. Every launch has Jacobi constant 3, moving prograde: it crashes into the left or right primary, escapes, or is still bounded (black) after 100 time units. Grey is forbidden at this energy. The bound 'Three-body: falling Jacobi' animation sweeps C between 2.8 and 3.6.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.ThreeBodyBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Three-body: falling Jacobi",
                Params        = new RegionFractalParams
                {
                    ThreeBodyView = (int)FracturingFog.ThreeBodyView.Fates, ThreeBodyMassRatio = 0.5, ThreeBodyJacobi = 3.0,
                    ThreeBodyLaunch = (int)FracturingFog.ThreeBodyLaunch.Prograde,
                },
            },
            new()
            {
                Name          = "Three-Body - Copenhagen Trapped Zone",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "Jacobi constant 3.6: above the value at L2 and L3 (about 3.457) but below L1's (4). The neck between the primaries is open, the way out is not, so launches near the primaries only crash or stay bounded, and launches outside only escape.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.ThreeBodyBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    ThreeBodyView = (int)FracturingFog.ThreeBodyView.Fates, ThreeBodyMassRatio = 0.5, ThreeBodyJacobi = 3.6,
                    ThreeBodyLaunch = (int)FracturingFog.ThreeBodyLaunch.Prograde,
                },
            },
            new()
            {
                Name          = "Three-Body - Retrograde Launch",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The Copenhagen fates at Jacobi constant 3 with every launch moving against the primaries' rotation. Large black regions are bounded retrograde orbits round each primary; the fractal mixing of the fates sits between them.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.ThreeBodyBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    ThreeBodyView = (int)FracturingFog.ThreeBodyView.Fates, ThreeBodyMassRatio = 0.5, ThreeBodyJacobi = 3.0,
                    ThreeBodyLaunch = (int)FracturingFog.ThreeBodyLaunch.Retrograde,
                },
            },
            new()
            {
                Name          = "Three-Body - Unequal Masses",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "Mass ratio 0.1: the heavy primary sits near the centre of mass, the light one at x = 0.9. At Jacobi constant 3.2 the exit past L2 (beside the light primary) is open, the one past L3 is not, so a particle from round the primaries can only get out past the light one.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.ThreeBodyBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    ThreeBodyView = (int)FracturingFog.ThreeBodyView.Fates, ThreeBodyMassRatio = 0.1, ThreeBodyJacobi = 3.2,
                    ThreeBodyLaunch = (int)FracturingFog.ThreeBodyLaunch.Prograde,
                },
            },
            new()
            {
                Name          = "Three-Body - Lagrange Basins",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "Newton-Raphson on the gradient of the effective potential for two equal masses, each start coloured by which equilibrium it reaches: L1 between the primaries, L2 and L3 outside them, L4 and L5 above and below. The basin boundaries are fractal. The bound 'Three-body: mass ratio sweep' animation moves the mass ratio between 0.05 and 0.5.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.ThreeBodyBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Three-body: mass ratio sweep",
                Params        = new RegionFractalParams
                {
                    ThreeBodyView = (int)FracturingFog.ThreeBodyView.LagrangeBasins, ThreeBodyMassRatio = 0.5, ThreeBodyNewtonIterations = 60,
                },
            },
            new()
            {
                Name          = "Three-Body - Lagrange Basins, Unequal",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The Newton-Raphson basins of L1-L5 for mass ratio 0.1. L1 and L2 crowd towards the light primary at x = 0.9 and the symmetry about the vertical axis is gone.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.ThreeBodyBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    ThreeBodyView = (int)FracturingFog.ThreeBodyView.LagrangeBasins, ThreeBodyMassRatio = 0.1, ThreeBodyNewtonIterations = 60,
                },
            },
            // #1989 — the equal-mass Lagrange basins rebuilt as a User Equation (real 2D Newton).
            new()
            {
                Name          = "User Equation - Lagrange Basins (Newton on a potential)",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  60,
                Description   = "The Three-Body Lagrange Basins written as a User Equation: the Newton field is the rotating-frame potential for two equal masses, and the five roots are L1, L2, L3, L4 and L5 in the Three-Body order, so the basin map and colours match that preset. Edit the potential to explore other fields.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.UserEquation,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    UserEquationPotential = LagrangeBasinsPotential,
                    UserEquationNewtonRoots = LagrangeBasinsRoots,
                },
            },
            // #1944 — magnetised black hole (Wald field; Kick view after Bautista & Vega 2021).
            new()
            {
                Name          = "Magnetised Black Hole - Kick Basins",
                CenterX       =  3.2,
                CenterY       =  -1.5,
                Zoom          =  0.7,
                Iterations    =  256,
                Description   = "Charged particles on circular orbits round a black hole in a uniform magnetic field (interaction strength b = 0.3), each kicked straight up the field. Across: the orbit radius (horizon radii); up: the energy after the kick. Below energy 1 nothing escapes; above it particles escape up the field, fall in, or after a chaotic dance escape down it. The bound 'Magnetised black hole: field sweep' animation moves b between 0.1 and 0.6.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MagnetisedBlackHole,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Magnetised black hole: field sweep",
                Params        = new RegionFractalParams
                {
                    MagBhView = (int)FracturingFog.MagnetisedBhView.Kick, MagBhField = 0.3, MagBhBranch = (int)FracturingFog.MagnetisedBhBranch.Plus,
                },
            },
            new()
            {
                Name          = "Magnetised Black Hole - Kick Close-Up",
                CenterX       =  2.0,
                CenterY       =  -1.45,
                Zoom          =  2.9,
                Iterations    =  256,
                Description   = "The inner orbits of the kick basins at b = 0.3: orbit radius 1.4 to 2.6, energy 0.9 to 2. Capture, upstream and downstream escape interleave in ever finer bands.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MagnetisedBlackHole,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MagBhView = (int)FracturingFog.MagnetisedBhView.Kick, MagBhField = 0.3, MagBhBranch = (int)FracturingFog.MagnetisedBhBranch.Plus,
                },
            },
            new()
            {
                Name          = "Magnetised Black Hole - Minus Branch",
                CenterX       =  3.3,
                CenterY       =  -3.5,
                Zoom          =  0.55,
                Iterations    =  256,
                Description   = "The kick basins from the other family of circular orbits (the minus root of the circular-orbit condition, negative angular momentum at b = 0.3). Escape needs more energy here, so the frame reaches energy 5.5.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MagnetisedBlackHole,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MagBhView = (int)FracturingFog.MagnetisedBhView.Kick, MagBhField = 0.3, MagBhBranch = (int)FracturingFog.MagnetisedBhBranch.Minus,
                },
            },
            new()
            {
                Name          = "Magnetised Black Hole - Strong Field",
                CenterX       =  3.2,
                CenterY       =  -1.5,
                Zoom          =  0.7,
                Iterations    =  256,
                Description   = "The kick basins at interaction strength b = 0.5: the circular orbits and the bands where the three fates interleave shift with the stronger field.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MagnetisedBlackHole,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MagBhView = (int)FracturingFog.MagnetisedBhView.Kick, MagBhField = 0.5, MagBhBranch = (int)FracturingFog.MagnetisedBhBranch.Plus,
                },
            },
            new()
            {
                Name          = "Magnetised Black Hole - Meridional Upward Launch",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.3,
                Iterations    =  256,
                Description   = "Every point of a plane through the field axis is a launch at energy 1.5 and angular momentum 1, moving straight up the field (b = 0.3); the plane is drawn mirrored about the axis. Grey: no launch at this energy. The bound 'Magnetised black hole: turning launch' animation turns the launch direction once round.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MagnetisedBlackHole,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Magnetised black hole: turning launch",
                Params        = new RegionFractalParams
                {
                    MagBhView = (int)FracturingFog.MagnetisedBhView.Meridional, MagBhField = 0.3, MagBhEnergy = 1.5, MagBhAngularMomentum = 1.0, MagBhLaunchAngle = 90.0,
                },
            },
            new()
            {
                Name          = "Magnetised Black Hole - Meridional, High Angular Momentum",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.3,
                Iterations    =  256,
                Description   = "Launches across the plane through the axis at energy 2 and angular momentum 3, moving outward across the field (b = 0.3). Horizontal launches give a map symmetric about the equator with the two escape directions swapped.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MagnetisedBlackHole,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MagBhView = (int)FracturingFog.MagnetisedBhView.Meridional, MagBhField = 0.3, MagBhEnergy = 2.0, MagBhAngularMomentum = 3.0, MagBhLaunchAngle = 0.0,
                },
            },
            // #1945 — spinning compact binaries (Kidder 1995 equations; Levin 2000 set-ups; fractality disputed).
            new()
            {
                Name          = "Spinning Binary - Levin Conservative",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.557,
                Iterations    =  256,
                Description   = "Levin's conservative set-up: mass ratio 1/3, both spins 0.6, separation 5 (total masses), speed 0.45, no radiation reaction. Across and up: the two initial spin angles (radians from the orbital axis). Each binary merges, flies apart (past 1000) or is still orbiting after 50 orbits. Levin reported fractal boundaries here; later papers dispute chaos in this system, so read the edges as fractal-looking at finite resolution. The bound 'Spinning binary: spin-up' animation varies spin 1.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SpinningBinaryBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Spinning binary: spin-up",
                Params        = new RegionFractalParams
                {
                    SpinBinMassRatio = 1.0 / 3.0, SpinBinChi1 = 0.6, SpinBinChi2 = 0.6, SpinBinSeparation = 5.0, SpinBinVelocity = 0.45,
                    SpinBinMaxOrbits = 50.0, SpinBinSeparateRadius = 1000.0, SpinBinRadiationReaction = false, SpinBinOutcome = (int)FracturingFog.SpinningBinaryOutcomeMode.Fates,
                },
            },
            new()
            {
                Name          = "Spinning Binary - Conservative Close-Up",
                CenterX       =  -1.18,
                CenterY       =  -1.11,
                Zoom          =  4.0,
                Iterations    =  256,
                Description   = "A close-up of thin tongues of separating binaries reaching into the still-orbiting region of Levin's conservative set-up. At this zoom the edges look smooth: zoom further to see whether new structure keeps appearing at your resolution, which is the question the published dispute turns on. The bound 'Spinning binary: speed sweep' animation varies the initial speed.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SpinningBinaryBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Spinning binary: speed sweep",
                Params        = new RegionFractalParams
                {
                    SpinBinMassRatio = 1.0 / 3.0, SpinBinChi1 = 0.6, SpinBinChi2 = 0.6, SpinBinSeparation = 5.0, SpinBinVelocity = 0.45,
                    SpinBinMaxOrbits = 50.0, SpinBinSeparateRadius = 1000.0, SpinBinRadiationReaction = false, SpinBinOutcome = (int)FracturingFog.SpinningBinaryOutcomeMode.Fates,
                },
            },
            new()
            {
                Name          = "Spinning Binary - Radiation Reaction On",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.557,
                Iterations    =  256,
                Description   = "The same binaries with 2.5PN radiation reaction switched on: from this close they all merge, and the merge / separate / orbit structure of the conservative map is gone. This dissipative term is at the heart of the published dispute.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SpinningBinaryBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    SpinBinMassRatio = 1.0 / 3.0, SpinBinChi1 = 0.6, SpinBinChi2 = 0.6, SpinBinSeparation = 5.0, SpinBinVelocity = 0.45,
                    SpinBinMaxOrbits = 50.0, SpinBinSeparateRadius = 1000.0, SpinBinRadiationReaction = true, SpinBinOutcome = (int)FracturingFog.SpinningBinaryOutcomeMode.Fates,
                },
            },
            new()
            {
                Name          = "Spinning Binary - Merger Side (Dissipative)",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.557,
                Iterations    =  256,
                Description   = "After Levin's dissipative figure: a maximally spinning black hole with a neutron star (mass ratio 1.4/10, spins 1 and 0.7), separation 26, speed 0.15, radiation reaction on. Every pair merges; the colour is whether the separation vector points above or below the initial orbital plane at merger.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SpinningBinaryBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    SpinBinMassRatio = 0.14, SpinBinChi1 = 1.0, SpinBinChi2 = 0.7, SpinBinSeparation = 26.0, SpinBinVelocity = 0.15,
                    SpinBinMaxOrbits = 200.0, SpinBinRadiationReaction = true, SpinBinOutcome = (int)FracturingFog.SpinningBinaryOutcomeMode.MergerSide,
                },
            },
            new()
            {
                Name          = "Spinning Binary - No Spins",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.557,
                Iterations    =  256,
                Description   = "Levin's conservative set-up with both spins zero. Without spins the post-Newtonian two-body motion stays in one plane and is integrable, so the spin angles change nothing and the map is a single colour: the control for the other presets.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SpinningBinaryBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    SpinBinMassRatio = 1.0 / 3.0, SpinBinChi1 = 0.0, SpinBinChi2 = 0.0, SpinBinSeparation = 5.0, SpinBinVelocity = 0.45,
                    SpinBinMaxOrbits = 50.0, SpinBinRadiationReaction = false, SpinBinOutcome = (int)FracturingFog.SpinningBinaryOutcomeMode.Fates,
                },
            },
            // #1946 — Mixmaster (Bianchi IX) universe, Cornish & Levin 1997 launches and outcome.
            new()
            {
                Name          = "Mixmaster - Kasner Basins",
                CenterX       =  1.5,
                CenterY       =  -0.5,
                Zoom          =  3.5,
                Iterations    =  256,
                Description   = "Cornish and Levin's picture of Mixmaster chaos: each pixel starts the universe in a Kasner epoch with parameters u (across, 1 to 2) and v (up), runs it towards the big bang through its bounces, and stops once an epoch has u above 8. The colour is the axis collapsing fastest then. The three basins interleave in nested bands. The bound 'Mixmaster: exit threshold' animation moves u_exit between 4 and 16.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MixmasterBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Mixmaster: exit threshold",
                Params        = new RegionFractalParams
                {
                    MixView = (int)FracturingFog.MixmasterView.KasnerUV, MixOmegaDot = 1.0 / 3.0, MixUExit = 8.0,
                },
            },
            new()
            {
                Name          = "Mixmaster - Kasner Close-Up",
                CenterX       =  1.75,
                CenterY       =  -0.55,
                Zoom          =  175,
                Iterations    =  256,
                Description   = "The Kasner basins magnified 50 times (as in Cornish and Levin's own close-up): the interleaved bands keep appearing inside each band.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MixmasterBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MixView = (int)FracturingFog.MixmasterView.KasnerUV, MixOmegaDot = 1.0 / 3.0, MixUExit = 8.0,
                },
            },
            new()
            {
                Name          = "Mixmaster - Late Exit",
                CenterX       =  1.5,
                CenterY       =  -0.5,
                Zoom          =  3.5,
                Iterations    =  256,
                Description   = "The Kasner basins with the exit at u above 16 instead of 8: each universe bounces longer before it is classified, and the basins mix more finely.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MixmasterBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MixView = (int)FracturingFog.MixmasterView.KasnerUV, MixOmegaDot = 1.0 / 3.0, MixUExit = 16.0,
                },
            },
            new()
            {
                Name          = "Mixmaster - Anisotropy Plane",
                CenterX       =  -2.6,
                CenterY       =  0.0,
                Zoom          =  1.5,
                Iterations    =  256,
                Description   = "Cornish and Levin's other launch: the universe starts at volume variable -1 with the expansion rates of axes a and b both 10 (c from the constraint), and the pixel is the shape of the anisotropy (beta+ across, beta- up). In this window the outcome depends mostly on beta+, in fine stripes. The bound 'Mixmaster: volume sweep' animation moves the starting volume.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MixmasterBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Mixmaster: volume sweep",
                Params        = new RegionFractalParams
                {
                    MixView = (int)FracturingFog.MixmasterView.Anisotropy, MixOmega = -1.0, MixAlphaDot = 10.0, MixBetaDot = 10.0, MixUExit = 8.0,
                },
            },
            // #1948 — microlensing magnification maps (inverse ray shooting, Kayser, Refsdal & Stabell 1986).
            new()
            {
                Name          = "Microlensing - Caustic Network",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.875,
                Iterations    =  256,
                Description   = "A field of equal stars (convergence 0.4 in stars) with an external shear of 0.3, the kind of magnification map a quasar image lensed by a galaxy's stars sees. Each pixel is a source position; bright curves are caustics, where the lens map folds and the magnification jumps. Not a fractal: the curves are smooth between their cusps. The bound 'Microlensing: shear' animation turns the shear.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MicrolensingMap,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Microlensing: shear",
                Params        = new RegionFractalParams
                {
                    MicroLayout = (int)FracturingFog.MicrolensLayout.Field, MicroKappaStars = 0.4, MicroKappaSmooth = 0.0, MicroShear = 0.3, MicroFieldRadius = 14.0, MicroSeed = 1, MicroRaysPerPixel = 4.0,
                },
            },
            new()
            {
                Name          = "Microlensing - Single Lens",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.875,
                Iterations    =  256,
                Description   = "One point-mass lens: the magnification falls off with distance u from the centre as Paczynski's A(u) = (u^2 + 2) / (u sqrt(u^2 + 4)), the curve of every microlensing light curve. The centre is a single caustic point.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MicrolensingMap,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MicroLayout = (int)FracturingFog.MicrolensLayout.Single, MicroKappaSmooth = 0.0, MicroShear = 0.0, MicroRaysPerPixel = 16.0,
                },
            },
            new()
            {
                Name          = "Microlensing - Single Lens with Shear",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  2.0,
                Iterations    =  256,
                Description   = "One point mass with an external shear of 0.2: the caustic point opens into a four-cusped curve.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MicrolensingMap,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MicroLayout = (int)FracturingFog.MicrolensLayout.Single, MicroKappaSmooth = 0.0, MicroShear = 0.2, MicroRaysPerPixel = 16.0,
                },
            },
            new()
            {
                Name          = "Microlensing - Mixed Masses",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.875,
                Iterations    =  256,
                Description   = "Stars with masses spread evenly between 0.1 and 1 at convergence 0.6 and no shear: caustics of many sizes overlap into a dense web.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MicrolensingMap,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MicroLayout = (int)FracturingFog.MicrolensLayout.Field, MicroKappaStars = 0.6, MicroShear = 0.0, MicroFieldRadius = 14.0, MicroMassFunction = (int)FracturingFog.MicrolensMassFunction.UniformRange, MicroMassMin = 0.1, MicroSeed = 3, MicroRaysPerPixel = 4.0,
                },
            },
            // #2007 — light-curve inset presets.
            new()
            {
                Name          = "Microlensing - Single Lens Light Curve",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.875,
                Iterations    =  256,
                Description   = "One point lens with a source passing 0.3 Einstein radii from it: the orange curve is the magnification counted along the track, the dashed blue one Paczynski's formula for the same pass. They lie on top of each other: the classic symmetric microlensing bump.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MicrolensingMap,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MicroLayout = (int)FracturingFog.MicrolensLayout.Single, MicroKappaSmooth = 0.0, MicroShear = 0.0, MicroRaysPerPixel = 16.0, MicroLightCurve = true, MicroTrackX = 0.0, MicroTrackY = 0.3, MicroTrackAngle = 0.0,
                },
            },
            new()
            {
                Name          = "Microlensing - Caustic Crossing",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.875,
                Iterations    =  256,
                Description   = "The caustic network with a source track across it: each time the track crosses a caustic the light curve jumps sharply and then falls away again: the spikes of a source passing behind a field of stars.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.MicrolensingMap,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    MicroLayout = (int)FracturingFog.MicrolensLayout.Field, MicroKappaStars = 0.4, MicroKappaSmooth = 0.0, MicroShear = 0.3, MicroFieldRadius = 14.0, MicroSeed = 1, MicroRaysPerPixel = 4.0, MicroLightCurve = true, MicroTrackX = 0.0, MicroTrackY = 0.2, MicroTrackAngle = 20.0,
                },
            },
            // #2021 — Cosmic Web: Soneira & Peebles (AJ 83, 845, 1978) hierarchical clustering.
            new()
            {
                Name          = "Cosmic Web - Soneira-Peebles Final Model",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The 1978 computer model universe of Soneira and Peebles in a 100 Mpc/h periodic box: clumps of galaxies at their clump density, each built by splitting into two sub-clumps at a fixed separation in a random direction (11.35 Mpc/h, then 1.10 and 1.76 times smaller per level), with the number of levels drawn from their Table I. The clustering is self-similar with D = log 2 / log 1.76 = 1.23 between the smallest and largest clump sizes. Right-drag to orbit.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.6, Cam3DPhi = 1.1,
                    CosmicWebPlacement = (int)FracturingFog.CosmicWebPlacement.Dumbbell, CosmicWebLevelMode = (int)FracturingFog.CosmicWebLevelMode.TableI,
                    CosmicWebBoxSize = 100.0, CosmicWebClumps = 310, CosmicWebTopSize = 11.35, CosmicWebFirstRatio = 1.10, CosmicWebLambda = 1.76,
                    CosmicWebSeed = 1978, CosmicWebPointSize = 1.0, CosmicWebDepthFade = 0.5,
                },
            },
            new()
            {
                Name          = "Cosmic Web - Rich Clumps",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "Forty of the richest clumps of Soneira and Peebles (12 levels, 4096 galaxies each) in a 100 Mpc/h box: the dumbbell hierarchy is easy to follow from the 11 Mpc/h pairs down to 37 kpc/h, and the random orientations make chains and filaments the model never built in.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.0, Cam3DTheta = 0.9, Cam3DPhi = 1.2,
                    CosmicWebPlacement = (int)FracturingFog.CosmicWebPlacement.Dumbbell, CosmicWebLevelMode = (int)FracturingFog.CosmicWebLevelMode.Fixed, CosmicWebLevels = 12,
                    CosmicWebBoxSize = 100.0, CosmicWebClumps = 40, CosmicWebTopSize = 11.35, CosmicWebFirstRatio = 1.10, CosmicWebLambda = 1.76,
                    CosmicWebSeed = 7, CosmicWebPointSize = 1.0, CosmicWebDepthFade = 0.6,
                },
            },
            new()
            {
                Name          = "Cosmic Web - Uniform Sphere Clumps (D = 2)",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The earlier placement of Soneira and Peebles: four sub-clumps uniformly at random inside each sphere, each half its radius, seven levels. With eta = 4 and lambda = 2 the hierarchy has D = log 4 / log 2 = 2, so the clumps read as crumpled sheets rather than chains.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.6, Cam3DPhi = 1.1,
                    CosmicWebPlacement = (int)FracturingFog.CosmicWebPlacement.UniformSphere, CosmicWebEta = 4, CosmicWebLevelMode = (int)FracturingFog.CosmicWebLevelMode.Fixed, CosmicWebLevels = 7,
                    CosmicWebBoxSize = 200.0, CosmicWebClumps = 120, CosmicWebTopSize = 15.0, CosmicWebFirstRatio = 2.0, CosmicWebLambda = 2.0,
                    CosmicWebSeed = 1977, CosmicWebPointSize = 1.0, CosmicWebDepthFade = 0.5,
                },
            },
            // #2022 — Cosmic Web: the Zel'dovich approximation on a power-law Gaussian random field.
            new()
            {
                Name          = "Cosmic Web - Zeldovich Web Forming",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "128^3 particles start on a grid and move in straight lines set by a Gaussian random density field (P(k) ~ 1/k^2, smoothed over two grid cells): the Zel'dovich approximation. At this growth (D = 1, the rms linear density contrast) sheets are folding and the first filaments join them. The bound 'Cosmic web: forming' animation runs D from 0 (the bare grid) to 2.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Cosmic web: forming",
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.6, Cam3DPhi = 1.1,
                    CosmicWebGenerator = (int)FracturingFog.CosmicWebGenerator.Zeldovich, CosmicWebGrid = 128,
                    CosmicWebSpectralIndex = -2.0, CosmicWebSmoothing = 2.0, CosmicWebGrowth = 1.0,
                    CosmicWebBoxSize = 100.0, CosmicWebSeed = 1970, CosmicWebPointSize = 0.0, CosmicWebDepthFade = 0.5, CosmicWebGamma = 0.6,
                },
            },
            new()
            {
                Name          = "Cosmic Web - Zeldovich Mature Web",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The same approximation well past shell crossing (D = 2): particles stream through each other, so walls and filaments thicken and the voids between them empty. The Zel'dovich approximation does not hold particles in the structures they reach, so they would keep streaming apart as D grows.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.9, Cam3DPhi = 1.2,
                    CosmicWebGenerator = (int)FracturingFog.CosmicWebGenerator.Zeldovich, CosmicWebGrid = 128,
                    CosmicWebSpectralIndex = -2.0, CosmicWebSmoothing = 2.0, CosmicWebGrowth = 2.0,
                    CosmicWebBoxSize = 100.0, CosmicWebSeed = 70, CosmicWebPointSize = 0.0, CosmicWebDepthFade = 0.5, CosmicWebGamma = 0.6,
                },
            },
            // #2023 — the D(r) measurement (Einasto 2026, Eqs. 2-4) on the Soneira-Peebles final model.
            new()
            {
                Name          = "Cosmic Web - Measured D(r)",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The Soneira-Peebles final model with its fractal dimension function measured from the points: D(r) = 3 + d log(1 + xi) / d log r, plotted along the bottom (orange), from every point. It scatters about the model's D = 1.23 (dashed green) on the smallest scales, where the pairs inside clumps dominate, and climbs towards the homogeneous 3 (dashed sky blue) as the uncorrelated pairs between clumps take over. The status bar gives D at 0.3, 1, 3 and 10 Mpc/h.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.6, Cam3DPhi = 1.1,
                    CosmicWebPlacement = (int)FracturingFog.CosmicWebPlacement.Dumbbell, CosmicWebLevelMode = (int)FracturingFog.CosmicWebLevelMode.TableI,
                    CosmicWebBoxSize = 100.0, CosmicWebClumps = 310, CosmicWebTopSize = 11.35, CosmicWebFirstRatio = 1.10, CosmicWebLambda = 1.76,
                    CosmicWebSeed = 1978, CosmicWebPointSize = 1.0, CosmicWebDepthFade = 0.5,
                    CosmicWebMeasure = true, CosmicWebMeasureSample = 100000, CosmicWebDimensionOverlay = true,
                },
            },
            // #2024 — the Zel'dovich web as a raymarched CIC density volume.
            new()
            {
                Name          = "Cosmic Web - Zeldovich Volume",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The forming Zel'dovich web (D = 1) as glowing gas: 2.1 million particles deposited on a 128^3 density grid by cloud-in-cell assignment and raymarched with emission and absorption, coloured by depth. Runs on the GPU (D3D11 or Vulkan) when there is one. Right-drag to orbit.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.6, Cam3DPhi = 1.1,
                    CosmicWebGenerator = (int)FracturingFog.CosmicWebGenerator.Zeldovich, CosmicWebGrid = 128,
                    CosmicWebSpectralIndex = -2.0, CosmicWebSmoothing = 2.0, CosmicWebGrowth = 1.0,
                    CosmicWebBoxSize = 100.0, CosmicWebSeed = 1970, CosmicWebGamma = 0.4,
                    CosmicWebRender = (int)FracturingFog.CosmicWebRender.Volume, CosmicWebVolumeGrid = 128, CosmicWebVolumeSmoothing = 0.75,
                    CosmicWebVolumeOpacity = 12.0, CosmicWebVolumeBrightness = 40.0, CosmicWebVolumeSteps = 384,
                },
            },
            // #2025 — the compared set: one per generator at 262,144 points each, with D(r) measured.
            new()
            {
                Name          = "Cosmic Web - Compare: Soneira-Peebles",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The compared set (1 of 3), 262,144 galaxies each, D(r) measured. the dumbbell hierarchy of Soneira and Peebles with 64 clumps of 12 fixed levels (pairs down to 0.04 Mpc/h): D(r) near the model's 1.23 while the pairs inside clumps dominate, rising towards 3 as the uncorrelated pairs between clumps take over.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.6, Cam3DPhi = 1.1,
                    CosmicWebGenerator = (int)FracturingFog.CosmicWebGenerator.SoneiraPeebles,
                    CosmicWebPlacement = (int)FracturingFog.CosmicWebPlacement.Dumbbell, CosmicWebLevelMode = (int)FracturingFog.CosmicWebLevelMode.Fixed, CosmicWebLevels = 12,
                    CosmicWebBoxSize = 100.0, CosmicWebClumps = 64, CosmicWebTopSize = 11.35, CosmicWebFirstRatio = 1.10, CosmicWebLambda = 1.76,
                    CosmicWebPointCap = 262144, CosmicWebSeed = 1978, CosmicWebPointSize = 1.0, CosmicWebDepthFade = 0.5,
                    CosmicWebMeasure = true, CosmicWebMeasureSample = 100000, CosmicWebDimensionOverlay = true,
                },
            },
            new()
            {
                Name          = "Cosmic Web - Compare: Zeldovich",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The compared set (2 of 3), 262,144 galaxies each, D(r) measured. The Zel'dovich approximation on a 64^3 particle grid at D = 1: below 3 at a few Mpc/h, where sheets and filaments have formed, and climbing slowly on large scales.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.6, Cam3DPhi = 1.1,
                    CosmicWebGenerator = (int)FracturingFog.CosmicWebGenerator.Zeldovich, CosmicWebGrid = 64,
                    CosmicWebSpectralIndex = -2.0, CosmicWebSmoothing = 1.0, CosmicWebGrowth = 1.0,
                    CosmicWebBoxSize = 100.0, CosmicWebSeed = 1970, CosmicWebPointSize = 1.0, CosmicWebDepthFade = 0.5, CosmicWebGamma = 0.6,
                    CosmicWebMeasure = true, CosmicWebMeasureSample = 100000, CosmicWebDimensionOverlay = true,
                },
            },
            new()
            {
                Name          = "Cosmic Web - Compare: Voronoi",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "The compared set (3 of 3), 262,144 galaxies each, D(r) measured. Van de Weygaert's Voronoi clustering model: galaxies on the walls, edges and vertices of the tessellation of 64 random nuclei, 0.5 Mpc/h wide. D(r) is near 3 inside the width, drops towards 1 where filaments and clusters dominate, and climbs back to 3 at the cell size.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.6, Cam3DPhi = 1.1,
                    CosmicWebGenerator = (int)FracturingFog.CosmicWebGenerator.Voronoi, CosmicWebVoronoiNuclei = 64, CosmicWebVoronoiPoints = 262144,
                    CosmicWebVoronoiField = 0.1, CosmicWebVoronoiWalls = 0.35, CosmicWebVoronoiFilaments = 0.35, CosmicWebVoronoiClusters = 0.2, CosmicWebVoronoiThickness = 0.5,
                    CosmicWebBoxSize = 100.0, CosmicWebSeed = 2007, CosmicWebPointSize = 1.0, CosmicWebDepthFade = 0.5,
                    CosmicWebMeasure = true, CosmicWebMeasureSample = 100000, CosmicWebDimensionOverlay = true,
                },
            },
            // #2025 — a D(r) curve with two regimes and a break (cf. Einasto 2026).
            new()
            {
                Name          = "Cosmic Web - Voronoi Two Regimes",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.0,
                Iterations    =  256,
                Description   = "A Voronoi web whose D(r) has two regimes with a break between them, as Einasto (2026) describes for the real web (there near 3 Mpc/h): inside the 0.5 Mpc/h width of the walls, filaments and clusters the galaxies fill space (D near 3); above it the filaments and clusters set it (D falls towards 1 near 2 Mpc/h); past the 25 Mpc/h cells D climbs back to 3. A model with a chosen width, not a fit to the sky.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.CosmicWeb,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    Cam3DFamily = (int)FractalType.CosmicWeb, Cam3DDistance = 2.2, Cam3DTheta = 0.9, Cam3DPhi = 1.2,
                    CosmicWebGenerator = (int)FracturingFog.CosmicWebGenerator.Voronoi, CosmicWebVoronoiNuclei = 64, CosmicWebVoronoiPoints = 400000,
                    CosmicWebVoronoiField = 0.1, CosmicWebVoronoiWalls = 0.35, CosmicWebVoronoiFilaments = 0.35, CosmicWebVoronoiClusters = 0.2, CosmicWebVoronoiThickness = 0.5,
                    CosmicWebBoxSize = 100.0, CosmicWebSeed = 11, CosmicWebPointSize = 1.0, CosmicWebDepthFade = 0.5,
                    CosmicWebMeasure = true, CosmicWebMeasureSample = 60000, CosmicWebDimensionOverlay = true,
                },
            },
            // #1997 — User Hamiltonian: the Hénon–Heiles section (parity with that view), FF's own
            // three-degree-of-freedom extension, and the Copenhagen problem in canonical variables.
            new()
            {
                Name          = "User Hamiltonian - Henon-Heiles Section",
                CenterX       =  0.2,
                CenterY       =  0.0,
                Zoom          =  1.6,
                Iterations    =  256,
                Description   = "The Henon-Heiles Hamiltonian written out, H = (px^2 + py^2)/2 + V(x, y), launched on the line x = 0 with the pixel as (y, py) and px solved from H = 0.25. FF derives Hamilton's equations from the formula, so this is the built-in Henon-Heiles Section view. The bound 'User Hamiltonian: energy' animation sweeps the energy.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.UserHamiltonianBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "User Hamiltonian: energy",
                Params        = new RegionFractalParams
                {
                    UserHamSource = FractalParameters.UserHamDefaultSource, UserHamDimension = 2,
                    UserHamAxisA = "y", UserHamAxisB = "py", UserHamFixed = "x=0", UserHamSolveFor = "px", UserHamSolveSign = 1,
                    UserHamEnergy = 0.25, UserHamExitRadius = 2.0, UserHamSectors = 3, UserHamSectorOffset = 90.0,
                },
            },
            new()
            {
                Name          = "User Hamiltonian - Three Degrees of Freedom",
                CenterX       =  0.2,
                CenterY       =  0.0,
                Zoom          =  1.6,
                Iterations    =  256,
                Description   = "Henon-Heiles with a third coordinate z coupled in, H = (px^2 + py^2 + pz^2)/2 + (x^2 + y^2 + z^2)/2 + x^2*y - y^3/3 + 0.5*x*z^2, launched from z = 0.15 at rest in z. The extra motion in z blurs and shifts the three exit basins of the section. The coupling is FF's own example, not taken from a paper.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.UserHamiltonianBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    UserHamSource = "(px^2 + py^2 + pz^2)/2 + (x^2 + y^2 + z^2)/2 + x^2*y - y^3/3 + 0.5*x*z^2", UserHamDimension = 3,
                    UserHamAxisA = "y", UserHamAxisB = "py", UserHamFixed = "x=0; z=0.15; pz=0", UserHamSolveFor = "px", UserHamSolveSign = 1,
                    UserHamEnergy = 0.25, UserHamExitRadius = 2.0, UserHamSectors = 3, UserHamSectorOffset = 90.0,
                },
            },
            new()
            {
                Name          = "User Hamiltonian - Copenhagen Canonical",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.7,
                Iterations    =  256,
                Description   = "The restricted three-body problem with two equal masses in canonical rotating-frame variables, H = (px^2 + py^2)/2 + y px - x py - Omega-terms, at H = -1.5 (Jacobi constant 3). Each pixel is a position (x, y) launched with no x-velocity (px = -y) and py solved from H; an 'exit if' rule catches crashes into either mass and escapes. The launch rule is FF's own.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.UserHamiltonianBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    UserHamSource = "(px^2 + py^2)/2 + y*px - x*py - 0.5/sqrt((x + 0.5)^2 + y^2) - 0.5/sqrt((x - 0.5)^2 + y^2)",
                    UserHamDimension = 2, UserHamAxisA = "x", UserHamAxisB = "y", UserHamFixed = "px=-y", UserHamSolveFor = "py",
                    UserHamSolveSign = 1, UserHamEnergy = -1.5, UserHamExitRadius = 1000.0,
                    UserHamExitIf = RegionFractalParams.UserPotentialCopenhagenExit, UserHamMaxTime = 100.0,
                    UserHamTolerance = 1e-9, UserHamMaxStep = 0.1,
                },
            },
            // #1994 — User Potential: Hénon–Heiles written as V (parity), FF's own four-exit
            // potential, and the Copenhagen problem in the rotating frame.
            new()
            {
                Name          = "User Potential - Henon-Heiles",
                CenterX       =  0.0,
                CenterY       = -0.25,
                Zoom          =  0.85,
                Iterations    =  256,
                Description   = "The Henon-Heiles potential written as a user formula, V = (x^2 + y^2)/2 + x^2*y - y^3/3, with three exit sectors at E = 0.25. FF works out the forces from the formula and traces each launch exactly as the built-in Henon-Heiles family does, so the picture is the same. Edit V to explore other potentials. The bound 'User potential: energy' animation sweeps E.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.UserPotentialBasins,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "User potential: energy",
                Params        = new RegionFractalParams
                {
                    UserPotentialSource = FractalParameters.UserPotentialDefaultSource, UserPotentialEnergy = 0.25,
                    UserPotentialSectors = 3, UserPotentialSectorOffset = 90.0, UserPotentialExitRadius = 2.0,
                },
            },
            new()
            {
                Name          = "User Potential - Four Exits",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.85,
                Iterations    =  256,
                Description   = "A four-fold well, V = (x^2 + y^2)/2 - (x^4 + y^4)/4, with saddles at distance 1 along the axes (height 1/4). Above that energy a particle leaves through one of four channels; the basins interleave like the three of Henon-Heiles. The potential is FF's own example, not taken from a paper.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.UserPotentialBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    UserPotentialSource = "(x^2 + y^2)/2 - (x^4 + y^4)/4", UserPotentialEnergy = 0.3,
                    UserPotentialSectors = 4, UserPotentialSectorOffset = 0.0, UserPotentialExitRadius = 2.0,
                },
            },
            new()
            {
                Name          = "User Potential - Copenhagen Problem",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.7,
                Iterations    =  256,
                Description   = "The restricted three-body problem with two equal masses, written as a user potential: V = -Omega in the rotating frame (Coriolis on), E = -C/2 with C = 3, and an 'exit if' rule for crashing into either mass or escaping. It reproduces the built-in Three-Body Fates view up to the step control near the masses.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.UserPotentialBasins,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    UserPotentialSource = "-(x^2 + y^2)/2 - 0.5/sqrt((x + 0.5)^2 + y^2) - 0.5/sqrt((x - 0.5)^2 + y^2)",
                    UserPotentialEnergy = -1.5, UserPotentialRotating = true, UserPotentialExitRadius = 1000.0,
                    UserPotentialExitIf = RegionFractalParams.UserPotentialCopenhagenExit, UserPotentialMaxTime = 100.0,
                    UserPotentialTolerance = 1e-9, UserPotentialMaxStep = 0.1,
                },
            },
            // #1947 — black hole + Bach-Weyl ring (Semerák & Suková 2010 set-ups).
            new()
            {
                Name          = "Black Hole + Ring - Poincare Section",
                CenterX       =  16.0,
                CenterY       =  0.0,
                Zoom          =  0.12,
                Iterations    =  256,
                Description   = "A Schwarzschild black hole with a ring of half its mass at radius 20M; free particles with energy 0.977 and angular momentum 3.75M, as in Semerak and Sukova's ring figures. Each colour is one orbit, dotted where it crosses the equatorial plane (r across, radial velocity up). Closed curves are regular orbits, the scatter between them is chaos the ring creates. The bound 'Black hole + ring: ring mass' animation grows the ring from 0 to the black hole's mass.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleRingOrbits,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Black hole + ring: ring mass",
                Params        = new RegionFractalParams
                {
                    BhRingView = (int)FracturingFog.BlackHoleRingView.Section, BhRingMass = 0.5, BhRingRadius = 20.0,
                    BhRingEnergy = 0.977, BhRingAngularMomentum = 3.75, BhRingColour = (int)FracturingFog.BlackHoleRingColour.ByOrbit,
                },
            },
            new()
            {
                Name          = "Black Hole + Ring - Without the Ring",
                CenterX       =  16.0,
                CenterY       =  0.0,
                Zoom          =  0.12,
                Iterations    =  256,
                Description   = "The same particles around the bare black hole (ring mass 0): Schwarzschild motion is integrable, so every orbit draws a closed curve and there is no scatter.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleRingOrbits,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BhRingView = (int)FracturingFog.BlackHoleRingView.Section, BhRingMass = 0.0, BhRingRadius = 20.0,
                    BhRingEnergy = 0.977, BhRingAngularMomentum = 3.75, BhRingColour = (int)FracturingFog.BlackHoleRingColour.ByOrbit,
                },
            },
            new()
            {
                Name          = "Black Hole + Ring - Heavy Ring",
                CenterX       =  16.0,
                CenterY       =  0.0,
                Zoom          =  0.12,
                Iterations    =  256,
                Description   = "A ring as heavy as the black hole: the central island shrinks to a diamond with an island chain round it, and a chaotic sea fills much of the section.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleRingOrbits,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BhRingView = (int)FracturingFog.BlackHoleRingView.Section, BhRingMass = 1.0, BhRingRadius = 20.0,
                    BhRingEnergy = 0.977, BhRingAngularMomentum = 3.75, BhRingColour = (int)FracturingFog.BlackHoleRingColour.ByOrbit,
                },
            },
            new()
            {
                Name          = "Black Hole + Ring - Chaotic Sea",
                CenterX       =  14.5,
                CenterY       =  0.0,
                Zoom          =  0.2,
                Iterations    =  256,
                Description   = "Lower energy (0.94) with the half-mass ring: a regular island ringed by a chain of small islands, inside a wide chaotic sea. The bound 'Black hole + ring: energy' animation sweeps the energy between 0.94 and 0.98.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleRingOrbits,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Black hole + ring: energy",
                Params        = new RegionFractalParams
                {
                    BhRingView = (int)FracturingFog.BlackHoleRingView.Section, BhRingMass = 0.5, BhRingRadius = 20.0,
                    BhRingEnergy = 0.94, BhRingAngularMomentum = 3.75, BhRingColour = (int)FracturingFog.BlackHoleRingColour.ByOrbit,
                },
            },
            new()
            {
                Name          = "Black Hole + Ring - Section Density",
                CenterX       =  16.0,
                CenterY       =  0.0,
                Zoom          =  0.12,
                Iterations    =  256,
                Description   = "The default section coloured by how many crossings land in each pixel (through the palette) instead of by orbit.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleRingOrbits,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BhRingView = (int)FracturingFog.BlackHoleRingView.Section, BhRingMass = 0.5, BhRingRadius = 20.0,
                    BhRingEnergy = 0.977, BhRingAngularMomentum = 3.75, BhRingColour = (int)FracturingFog.BlackHoleRingColour.Density,
                },
            },
            new()
            {
                Name          = "Black Hole + Ring - Fates",
                CenterX       =  16.0,
                CenterY       =  0.0,
                Zoom          =  0.12,
                Iterations    =  256,
                Description   = "Every section point launched upward: captured by the black hole, hit the ring, or still bound after 5000M of proper time (the theme's interior colour). Slow: every bound orbit runs to the limit.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.BlackHoleRingOrbits,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    BhRingView = (int)FracturingFog.BlackHoleRingView.Fates, BhRingMass = 0.5, BhRingRadius = 20.0,
                    BhRingEnergy = 0.977, BhRingAngularMomentum = 3.75, BhRingColour = (int)FracturingFog.BlackHoleRingColour.ByOrbit,
                },
            },
            new()
            {
                Name        = "Apollonian - L/R Kissing Cusp",
                CenterX     =  0.0,
                CenterY     =  0.4,
                Zoom        =  5.25,
                Iterations  = 14,
                Description = "Off-axis zoom into the curvilinear triangle bounded by L, R, T. The self-similar Vieta-jump chain produces tight clusters of progressively smaller circles approaching each tangency point.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Apollonian,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Plasma - Default",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 64,
                Description = "Diamond-square midpoint-displacement noise field at the default seed and roughness. Pan/zoom is a no-op — the generated field IS the image; switch PlasmaSeed for variety.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Plasma,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Acid Fog - Rings",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 64,
                Description = "Clean-room homage to Noah Spurrier's 1992 Acid Warp. Concentric-ring procedural pattern mapped through the active colour theme; pan/zoom is a no-op. Switch the pattern for spokes, spirals, interference, plaid and more; pair with animated palette cycling.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.AcidWarp,
                CuratedThemes = new List<string> { "Acid Fog Spectrum" },
                UseCuratedThemesOnly = true,
                PaletteCycleEnabled = true,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Acid Fog - Classic",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 64,
                Description = "The classic palette-cycling look/feel (homage to Noah Spurrier's 1992 Acid Warp): the multi-centre 'peacock' interference field. Pick the 'Acid Fog Spectrum' theme and turn on the Cycle toolbar toggle for the continuously-flowing psychedelic animation.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.AcidWarp,
                Params      = new RegionFractalParams { AcidWarpPattern = 9, AcidWarpFrequency = 1.0, AcidWarpWarpStrength = 0.0 },
                CuratedThemes = new List<string> { "Acid Fog Spectrum" },
                UseCuratedThemesOnly = true,
                PaletteCycleEnabled = true,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Flame - Default Chaos",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Apophysis-style chaos-game flame at the default variation table. The renderer auto-fits the attractor; CX/CY/Zoom are advisory only.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Flame,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Logistic - r in [2.9, 4.0]",
                CenterX     =  3.45,
                CenterY     =  0.5,
                Zoom        =  1.8,
                Iterations  = 512,
                Description = "Classic bifurcation diagram framing — the period-doubling cascade from r ≈ 2.9 through the Feigenbaum point at r ≈ 3.5699 into the chaotic regime past r = 4.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Logistic,
                QualityPreset = QualityPreset.High
            },
            new()
            {
                Name        = "TearDrop - Default",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.6,
                Iterations  = 256,
                Description = "Tear Drop fractal at default framing. The asymmetric drop shape sits centred on the origin.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.TearDrop,
                QualityPreset = QualityPreset.Standard
            },
            new()
            {
                Name        = "Mandelbulb - Power 8",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  1.0,
                Iterations  = 128,
                Description = "Canonical power-8 Mandelbulb at default camera. Triplex algebra (spherical-coord exponent map) renders the bulb with raymarched DE and Phong shading.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Mandelbulb,
                QualityPreset = QualityPreset.Standard
            },
            // #911 S1 — parabolic-parameter Julia presets. Each sits at a
            // parabolic c₀ (fixed-point multiplier a root of unity), where the
            // Julia set is discontinuous under perturbation. Pair with the
            // matching "Parabolic implosion (…)" animation to circle c₀ and watch
            // the naïve explosion (Docs/Technical/Parabolic-Implosion-DesignPlan.md).
            new()
            {
                Name        = "Parabolic c = 1/4 (cardioid root)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.65,
                Iterations  =  500,
                Description = "Julia set at the parabolic parameter c = 1/4 (the cardioid cusp; fixed-point multiplier +1). The 'cauliflower'. Enable 'Parabolic implosion (c=1/4)' to circle c and watch the set explode.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                Params      = new RegionFractalParams { JuliaCRe = 0.25, JuliaCIm = 0.0 },
            },
            new()
            {
                Name        = "Parabolic c = -3/4 (period-2 root)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.6,
                Iterations  =  500,
                Description = "Julia set at the parabolic parameter c = -3/4 (root of the period-2 bulb; multiplier -1). Pair with 'Parabolic implosion (c=-3/4)'.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                Params      = new RegionFractalParams { JuliaCRe = -0.75, JuliaCIm = 0.0 },
            },
            new()
            {
                Name        = "Parabolic c = -0.125+0.6495i (1/3 bulb root)",
                CenterX     =  0.0,
                CenterY     =  0.0,
                Zoom        =  0.6,
                Iterations  =  500,
                Description = "Julia set at the root of the 1/3 limb (multiplier e^{2πi/3}). A 3-petal parabolic flower. Pair with 'Parabolic implosion (1/3 bulb)'.",
                RegionType  = RegionType.BuiltIn,
                FractalType = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                Params      = new RegionFractalParams { JuliaCRe = -0.125, JuliaCIm = 0.6495 },
            },
            // #1573 — double-slit epic #1565 presets (Docs/User/DoubleSlit-Guide.md). Three per
            // type; the dual-orbit and cascade ones bind a built-in animation. Talbot carpets run
            // at 16 iterations so FF's repeating themes span the scale about once.
            new()
            {
                Name          = "Double Slit - Which-Path Collapse",
                CenterX       = -0.6,
                CenterY       =  0.05,
                Zoom          =  0.875,
                Iterations    =  400,
                Description   = "Two orbits as the two slits: each escaping orbit is a path with a phase, and the paths interfere. Enable the bound 'Double slit: which-path collapse' animation to switch the which-path detector on and off: the fringes fade to a flat field and come back.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.JulibrotPair,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Double slit: which-path collapse",
                Params        = new RegionFractalParams
                {
                    DualOrbitField = (int)FracturingFog.DualOrbitField.PathInterference,
                    DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 20.0,
                },
            },
            new()
            {
                Name          = "Double Slit - Angular Momentum Spirals",
                CenterX       = -0.6,
                CenterY       =  0.0,
                Zoom          =  0.525,
                Iterations    =  400,
                Description   = "The interference phase with an orbital-angular-momentum twist of 3: the rings round the set become three-armed spirals. The bound 'Double slit: angular momentum sweep' animation steps the twist from -4 to 4, so the arms change count and handedness.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.JulibrotPair,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Double slit: angular momentum sweep",
                Params        = new RegionFractalParams
                {
                    DualOrbitField = (int)FracturingFog.DualOrbitField.PathInterferencePhase,
                    DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 2.0,
                    DualOrbitAngularMomentum = 3,
                },
            },
            new()
            {
                Name          = "Double Slit - Opposite Winding Rays",
                CenterX       = -0.6,
                CenterY       =  0.0,
                Zoom          =  0.525,
                Iterations    =  400,
                Description   = "The two paths twist in opposite directions, so the intensity fringes follow the sum of their external angles and fan out from the set as radial rays.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.JulibrotPair,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    DualOrbitField = (int)FracturingFog.DualOrbitField.PathInterference,
                    DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 1.0,
                    DualOrbitAngularMomentum = 3, DualOrbitWindingMode = (int)FracturingFog.DualOrbitWindingMode.Opposite,
                },
            },
            new()
            {
                // #1604 — the which-path detector switched by the music (#1567).
                Name          = "Double Slit - Beat Collapse",
                CenterX       = -0.6,
                CenterY       =  0.05,
                Zoom          =  0.875,
                Iterations    =  400,
                Description   = "Which-Path Collapse played by the music: every beat switches the which-path detector on, so the fringes flash flat on the beat and come back between beats. Needs audio input (Audio Settings); without it the fringes stay.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.JulibrotPair,
                QualityPreset = QualityPreset.Standard,
                AudioBindings = AudioDrive("DualOrbitWhichPath", FracturingFog.Audio.AudioSignalKind.BeatPulse, 0.0, 1.0,
                                           release: 0.15),
                Params        = new RegionFractalParams
                {
                    DualOrbitField = (int)FracturingFog.DualOrbitField.PathInterference,
                    DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 20.0,
                },
            },
            new()
            {
                // #1604 — the spiral-arm count stepped by the beat (#1568).
                Name          = "Double Slit - Spiral Beat",
                CenterX       = -0.6,
                CenterY       =  0.0,
                Zoom          =  0.525,
                Iterations    =  400,
                Description   = "Angular Momentum Spirals played by the music: the beat pulse sets the twist, so the phase rings jump from two to five spiral arms on every beat and fall back in steps as the pulse decays. Needs audio input (Audio Settings); without it the spirals keep three arms.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.JulibrotPair,
                QualityPreset = QualityPreset.Standard,
                AudioBindings = AudioDrive("DualOrbitAngularMomentum", FracturingFog.Audio.AudioSignalKind.BeatPulse, 2.0, 5.0),
                Params        = new RegionFractalParams
                {
                    DualOrbitField = (int)FracturingFog.DualOrbitField.PathInterferencePhase,
                    DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 2.0,
                    DualOrbitAngularMomentum = 3,
                },
            },
            new()
            {
                // #1602 — N-path interference: the two orbits as a six-slit grating.
                Name          = "Double Slit - Orbit Grating",
                CenterX       = -0.6,
                CenterY       =  0.05,
                Zoom          =  0.875,
                Iterations    =  400,
                Description   = "Six paths instead of two, like a grating with six slits: the bright fringes stay where the two-path fringes peak but get about three times thinner, with faint fringes between. The bound 'Double slit: more slits' animation steps the number of paths from 2 to 12 and back.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.JulibrotPair,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Double slit: more slits",
                Params        = new RegionFractalParams
                {
                    DualOrbitField = (int)FracturingFog.DualOrbitField.PathInterference,
                    DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitInterferenceK = 20.0,
                    DualOrbitInterferencePaths = 6, DualOrbitPathLayout = (int)FracturingFog.DualOrbitPathLayout.Grating,
                },
            },
            new()
            {
                Name          = "Slit Cascade - Ring Diffraction",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.21875,
                Iterations    =  400,
                Description   = "Six slits on a ring, each replaced by six smaller ones, four levels deep; each slit lets through the share of light the two-slit fringes put at its place. This is the far-field diffraction pattern. The bound 'Slit cascade: wavelength breathing' animation slides the fringes, so slits move on and off dark fringes.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SlitCascade,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Slit cascade: wavelength breathing",
                Params        = new RegionFractalParams
                {
                    SlitCascadeChildren = 6, SlitCascadeRatio = 0.3, SlitCascadeDepth = 4,
                    SlitCascadeSeparation = 1.0, SlitCascadeWidth = 0.2, SlitCascadeWavelength = 0.6,
                    SlitCascadeLayout = (int)FracturingFog.SlitCascadeLayout.Ring,
                    SlitCascadeView = (int)FracturingFog.SlitCascadeView.Intensity, SlitCascadeDynamicRange = 6.0,
                },
            },
            new()
            {
                Name          = "Slit Cascade - Probability Sets Dimension",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.4,
                Iterations    =  40,    // HSV turns every 50: the 5 α classes land on 5 distinct hues (400 put them all on red)
                Description   = "The cascade's slits coloured by the Hölder exponent: how concentrated the light is at each point. The status bar shows the fractal dimensions D0, D1, D2, Dq; change the wavelength and watch them move. The probabilities set the dimension.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SlitCascade,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    SlitCascadeChildren = 6, SlitCascadeRatio = 0.3, SlitCascadeDepth = 4,
                    SlitCascadeSeparation = 1.0, SlitCascadeWidth = 0.2, SlitCascadeWavelength = 0.6,
                    SlitCascadeLayout = (int)FracturingFog.SlitCascadeLayout.Ring,
                    SlitCascadeView = (int)FracturingFog.SlitCascadeView.HolderAlpha, SlitCascadeQ = 3.0,
                },
            },
            new()
            {
                Name          = "Slit Cascade - Vortex Core",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.875,
                Iterations    =  400,
                Description   = "Five slits on a ring, three levels, each slit twisted by its angle (charge 1). With equal light through every slit the three levels' vortices stack into one dark core of charge 3 at the centre. Lower the wavelength and they drift apart into a constellation.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SlitCascade,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    SlitCascadeChildren = 5, SlitCascadeRatio = 0.3, SlitCascadeDepth = 3,
                    SlitCascadeSeparation = 1.0, SlitCascadeWidth = 0.0, SlitCascadeWavelength = 1e6,
                    SlitCascadeLayout = (int)FracturingFog.SlitCascadeLayout.Ring,
                    SlitCascadeView = (int)FracturingFog.SlitCascadeView.Intensity, SlitCascadeDynamicRange = 6.0,
                    SlitCascadeCharge = 1, SlitCascadeChargePattern = (int)FracturingFog.SlitCascadeChargePattern.Constant,
                },
            },
            new()
            {
                // #1604 — the wavelength breathing with the bass (#1571: the dimension moves with it).
                Name          = "Slit Cascade - Music Breathing",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.21875,
                Iterations    =  400,
                Description   = "Ring Diffraction played by the music: the bass sets the wavelength between 0.4 and 1.4, so on every kick the fringes swell outward and slits move on and off dark fringes, and the dimension in the status bar moves with them. Smoothed so it breathes rather than flickers. Needs audio input (Audio Settings).",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SlitCascade,
                QualityPreset = QualityPreset.Standard,
                AudioBindings = AudioDrive("SlitCascadeWavelength", FracturingFog.Audio.AudioSignalKind.Bass, 0.4, 1.4,
                                           FracturingFog.Audio.AudioResponseCurve.Smoothstep, attack: 0.05, release: 0.4),
                Params        = new RegionFractalParams
                {
                    SlitCascadeChildren = 6, SlitCascadeRatio = 0.3, SlitCascadeDepth = 4,
                    SlitCascadeSeparation = 1.0, SlitCascadeWidth = 0.2, SlitCascadeWavelength = 0.6,
                    SlitCascadeLayout = (int)FracturingFog.SlitCascadeLayout.Ring,
                    SlitCascadeView = (int)FracturingFog.SlitCascadeView.Intensity, SlitCascadeDynamicRange = 6.0,
                },
            },
            new()
            {
                // #1603 — Domain colouring (#1592) + Relief 3D on the unchanged log-intensity height.
                Name          = "Slit Cascade - Vortex Wells",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  1.3125,
                Iterations    =  400,
                Description   = "The Vortex Core in 3D: height is the brightness, so the dark charge-3 vortex at the centre is a deep pit, and the colour wheel (blue and amber, readable with red/green colour blindness) turns three times round its walls. Domain colouring with Relief 3D.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SlitCascade,
                QualityPreset = QualityPreset.Standard,
                Relief3D      = VortexWellRelief(),
                Params        = new RegionFractalParams
                {
                    SlitCascadeChildren = 5, SlitCascadeRatio = 0.3, SlitCascadeDepth = 3,
                    SlitCascadeSeparation = 1.0, SlitCascadeWidth = 0.0, SlitCascadeWavelength = 1e6,
                    SlitCascadeLayout = (int)FracturingFog.SlitCascadeLayout.Ring,
                    SlitCascadeView = (int)FracturingFog.SlitCascadeView.Domain, SlitCascadeDynamicRange = 6.0,
                    SlitCascadeCharge = 1, SlitCascadeChargePattern = (int)FracturingFog.SlitCascadeChargePattern.Constant,
                },
            },
            new()
            {
                // #1603 — the same cascade with unequal shares: the core splits into single vortices.
                Name          = "Slit Cascade - Vortex Constellation",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.875,
                Iterations    =  400,
                Description   = "Vortex Wells with real slits (width 0.2, wavelength 0.6), so the slits pass unequal shares of light: the charge-3 core breaks up into separate charge-1 vortices, each its own pit with the colour wheel turning once round it. Domain colouring with Relief 3D.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SlitCascade,
                QualityPreset = QualityPreset.Standard,
                Relief3D      = VortexWellRelief(),
                Params        = new RegionFractalParams
                {
                    SlitCascadeChildren = 5, SlitCascadeRatio = 0.3, SlitCascadeDepth = 3,
                    SlitCascadeSeparation = 1.0, SlitCascadeWidth = 0.2, SlitCascadeWavelength = 0.6,
                    SlitCascadeLayout = (int)FracturingFog.SlitCascadeLayout.Ring,
                    SlitCascadeView = (int)FracturingFog.SlitCascadeView.Domain, SlitCascadeDynamicRange = 6.0,
                    SlitCascadeCharge = 1, SlitCascadeChargePattern = (int)FracturingFog.SlitCascadeChargePattern.Constant,
                },
            },
            new()
            {
                // #1599 — the single-particle build-up (Tonomura et al. 1989).
                Name          = "Slit Cascade - Particle Build-Up",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.21875,
                Iterations    =  400,
                Description   = "The Ring Diffraction pattern recorded one particle at a time: each dot lands where the light is bright with the matching probability. A few hundred dots look random; tens of thousands draw the fringes. The bound 'Slit cascade: particle build-up' animation raises the count from 0, so the pattern builds up out of single dots.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.SlitCascade,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Slit cascade: particle build-up",
                Params        = new RegionFractalParams
                {
                    SlitCascadeChildren = 6, SlitCascadeRatio = 0.3, SlitCascadeDepth = 4,
                    SlitCascadeSeparation = 1.0, SlitCascadeWidth = 0.2, SlitCascadeWavelength = 0.6,
                    SlitCascadeLayout = (int)FracturingFog.SlitCascadeLayout.Ring,
                    SlitCascadeView = (int)FracturingFog.SlitCascadeView.Detections, SlitCascadeDynamicRange = 6.0,
                    DetectionCount = 20000, DetectionSeed = 1, DetectionDotRadius = 1, DetectionGain = 0.5,
                },
            },
            new()
            {
                Name          = "Talbot Carpet - Ronchi Grating",
                CenterX       =  0.0,
                CenterY       =  0.5,
                Zoom          =  1.6625,
                Iterations    =  16,
                Description   = "The near field of a bar grating, one Talbot distance tall. The grating images itself at the top and bottom and shifted by half a period across the middle; in between the light weaves a quantum carpet. Looks best with a plain theme such as Greyscale.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.TalbotCarpet,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    TalbotProfile = (int)FracturingFog.TalbotProfile.Ronchi, TalbotDuty = 0.3, TalbotModes = 32,
                    TalbotView = (int)FracturingFog.TalbotView.Intensity, TalbotDynamicRange = 2.0,
                },
            },
            new()
            {
                // #1603 — the carpet as terrain: Domain colouring + Relief 3D.
                Name          = "Talbot Carpet - Relief",
                CenterX       =  0.0,
                CenterY       =  0.5,
                Zoom          =  1.6625,
                Iterations    =  16,
                Description   = "The Ronchi carpet as a landscape: height is the brightness, so the self-images at the top, middle and bottom are ridges and the dark gaps between the woven threads are valleys, coloured by the phase on the blue-amber wheel. Domain colouring with Relief 3D.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.TalbotCarpet,
                QualityPreset = QualityPreset.Standard,
                Relief3D      = new Relief3DSettings
                {
                    Enabled = true, Raymarch = true, CameraElevationDeg = 35.0, CameraZoom = 1.6, CameraExposureEv = 1.5,
                },
                Params        = new RegionFractalParams
                {
                    TalbotProfile = (int)FracturingFog.TalbotProfile.Ronchi, TalbotDuty = 0.3, TalbotModes = 32,
                    TalbotView = (int)FracturingFog.TalbotView.Domain, TalbotDynamicRange = 2.0,
                },
            },
            new()
            {
                // #1601 — the transverse profile walks down the carpet (Berry & Klein 1996).
                Name          = "Talbot Carpet - Profile Walk",
                CenterX       =  0.0,
                CenterY       =  0.5,
                Zoom          =  1.6625,
                Iterations    =  16,
                Description   = "The Ronchi carpet with the brightness across the grating drawn as a white curve at one distance, starting at the golden distance where the profile is a fractal (the status bar shows its dimension). The bound 'Talbot carpet: profile walk' animation moves the curve down one Talbot distance: the grating's own steps at the top, middle and bottom, a few shifted copies at simple fractions, jagged in between.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.TalbotCarpet,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Talbot carpet: profile walk",
                Params        = new RegionFractalParams
                {
                    TalbotProfile = (int)FracturingFog.TalbotProfile.Ronchi, TalbotDuty = 0.3, TalbotModes = 256,
                    TalbotView = (int)FracturingFog.TalbotView.Intensity, TalbotDynamicRange = 2.0,
                    TalbotSliceOverlay = true, TalbotSliceZeta = 0.3819660112501051,
                },
            },
            new()
            {
                Name          = "Talbot Carpet - Fractal Comb",
                CenterX       =  0.0,
                CenterY       =  0.5,
                Zoom          =  1.6625,
                Iterations    =  16,
                Description   = "Infinitely thin slits: the sharpest carpet, a lattice of straight light paths. At irrational distances the pattern is a fractal; zoom in on any row away from the simple fractions to see it.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.TalbotCarpet,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    TalbotProfile = (int)FracturingFog.TalbotProfile.DeltaComb, TalbotModes = 24,
                    TalbotView = (int)FracturingFog.TalbotView.Intensity, TalbotDynamicRange = 2.0,
                },
            },
            new()
            {
                Name          = "Talbot Carpet - Cascade Grating",
                CenterX       =  0.0,
                CenterY       =  0.5,
                Zoom          =  1.6625,
                Iterations    =  16,
                Description   = "A grating whose every period is the slit cascade (the row of slits from the Slit Cascade type). Its carpet carries the cascade's self-similar structure into the near field.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.TalbotCarpet,
                QualityPreset = QualityPreset.Standard,
                Params        = new RegionFractalParams
                {
                    TalbotProfile = (int)FracturingFog.TalbotProfile.SlitCascade, TalbotModes = 64,
                    TalbotView = (int)FracturingFog.TalbotView.Intensity, TalbotDynamicRange = 2.0,
                    SlitCascadeChildren = 6, SlitCascadeRatio = 0.3, SlitCascadeDepth = 4,
                    SlitCascadeSeparation = 1.0, SlitCascadeWidth = 0.2, SlitCascadeWavelength = 0.6,
                },
            },
            // #920 — faithful (Lavaurs-limit) parabolic implosion, one per parabolic
            // root p/q. Near-parabolic Julia sets on the cardioid boundary; each is bound
            // to its 'Parabolic implosion (cardioid, …)' animation, which sweeps c toward
            // the root (θ → p/q) and RAMPS EscapeIterationScale in lock-step — so the base
            // iteration here stays modest and the deep frames get up to 6× more. Initial c
            // = c(θ_far) on the main cardioid (the animation's shallow start frame).
            new()
            {
                Name          = "Parabolic implosion c = 1/4",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.62,
                Iterations    =  3000,
                Description   = "Parabolic implosion at c = 1/4 (period-1, Lavaurs limit): a near-parabolic Julia set on the cardioid boundary. Enable the bound 'Parabolic implosion (cardioid, c=1/4)' animation to sweep c toward the cusp (θ → 0) — satellite spirals bloom, with iterations ramping up automatically as c nears the root. The rigorous counterpart of the naïve circle.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Parabolic implosion (cardioid, c=1/4)",
                // c(θ=0.14) on the main cardioid: e^{2πiθ}/2 − e^{4πiθ}/4
                Params        = new RegionFractalParams { JuliaCRe = 0.36555, JuliaCIm = 0.13967 },
            },
            new()
            {
                Name          = "Parabolic implosion c = -3/4",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.6,
                Iterations    =  3000,
                Description   = "Parabolic implosion at c = -3/4 (period-2 root, multiplier -1): a near-parabolic Julia set on the cardioid boundary. Enable 'Parabolic implosion (cardioid, c=-3/4)' to sweep c toward the root (θ → 1/2) and watch the period-2 cascade bloom (iterations auto-ramp).",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Parabolic implosion (cardioid, c=-3/4)",
                // c(θ=0.42) on the main cardioid
                Params        = new RegionFractalParams { JuliaCRe = -0.57210, JuliaCIm = -0.02982 },
            },
            new()
            {
                Name          = "Parabolic implosion 1/3 bulb",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.6,
                Iterations    =  3000,
                Description   = "Parabolic implosion at the 1/3-bulb root (period-3, multiplier e^{2πi/3}): a near-parabolic Julia set on the cardioid boundary. Enable 'Parabolic implosion (cardioid, 1/3 bulb)' to sweep c toward the root (θ → 1/3) and watch the 3-fold cascade bloom (iterations auto-ramp).",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Parabolic implosion (cardioid, 1/3 bulb)",
                // c(θ=0.29) on the main cardioid
                Params        = new RegionFractalParams { JuliaCRe = 0.09630, JuliaCIm = 0.60170 },
            },
            new()
            {
                Name          = "Parabolic implosion 1/4 bulb",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.6,
                Iterations    =  3000,
                Description   = "Parabolic implosion at the 1/4-bulb root (period-4, multiplier i): a near-parabolic Julia set on the cardioid boundary. Enable 'Parabolic implosion (cardioid, 1/4 bulb)' to sweep c toward the root (θ → 1/4) and watch the 4-fold cascade bloom (iterations auto-ramp).",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Parabolic implosion (cardioid, 1/4 bulb)",
                // c(θ=0.19) on the main cardioid (the animation's shallow start)
                Params        = new RegionFractalParams
                {
                    JuliaCRe = FracturingFog.Abstractions.Animation.ParabolicImplosionMath.CardioidPoint(0.19).Real,
                    JuliaCIm = FracturingFog.Abstractions.Animation.ParabolicImplosionMath.CardioidPoint(0.19).Imaginary,
                },
            },
            new()
            {
                Name          = "Parabolic implosion 2/5 bulb",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.6,
                Iterations    =  3000,
                Description   = "Parabolic implosion at the 2/5-bulb root (period-5, multiplier e^{4πi/5}): a near-parabolic Julia set on the cardioid boundary. Enable 'Parabolic implosion (cardioid, 2/5 bulb)' to sweep c toward the root (θ → 2/5) and watch the 5-fold cascade bloom (iterations auto-ramp).",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Parabolic implosion (cardioid, 2/5 bulb)",
                // c(θ=0.35) on the main cardioid
                Params        = new RegionFractalParams
                {
                    JuliaCRe = FracturingFog.Abstractions.Animation.ParabolicImplosionMath.CardioidPoint(0.35).Real,
                    JuliaCIm = FracturingFog.Abstractions.Animation.ParabolicImplosionMath.CardioidPoint(0.35).Imaginary,
                },
            },
            // #920 user-picked p/q — the GENERAL faithful implosion. FaithfulImplosion mode
            // drives c from the chosen p/q root (set p/q in the Julia panel); starts at the
            // cusp 0/1. Bound to the general 'pick p/q' animation (sweeps the approach depth).
            new()
            {
                Name          = "Parabolic implosion — pick p/q",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.62,
                Iterations    =  3000,
                Description   = "Parabolic implosion at a USER-chosen root: set p/q in the Julia panel (Parabolic-implosion mode); starts at the cusp 0/1 (c = 1/4). Enable 'Parabolic implosion (cardioid, pick p/q)' to sweep the approach depth toward the root — the near-parabolic Julia set blooms its period-q cascade, iterations auto-ramp. The general form of the per-root presets.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Parabolic implosion (cardioid, pick p/q)",
                Params        = new RegionFractalParams
                {
                    JuliaCRe = 0.25, JuliaCIm = 0.0,       // fallback c (cusp) if mode is turned off
                    FaithfulImplosion = true,
                    FaithfulImplosionP = 0, FaithfulImplosionQ = 1,
                    FaithfulImplosionApproach = 0.14,
                },
            },
            // #920 satellite — faithful implosion at a SATELLITE (period-2 bulb) sub-root:
            // the period-doubling cascade. Starts at p/q = 1/2 → the period-4 root c = −5/4.
            new()
            {
                Name          = "Parabolic implosion — satellite (period-doubling)",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.6,
                Iterations    =  3000,
                Description   = "Parabolic implosion at a SATELLITE (period-2 bulb) sub-root — the period-doubling family. Starts at p/q = 1/2 → the period-4 root c = −5/4. Turn on 'Satellite (period-2 bulb)' + set p/q in the Julia panel, then enable 'Parabolic implosion (cardioid, pick p/q)' to implode toward the sub-root. Higher p/q → deeper doublings (period 6, 8, …).",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Parabolic implosion (cardioid, pick p/q)",
                Params        = new RegionFractalParams
                {
                    JuliaCRe = -1.25, JuliaCIm = 0.0,      // fallback c (period-4 root) if mode off
                    FaithfulImplosion = true, FaithfulImplosionSatellite = true,
                    FaithfulImplosionP = 1, FaithfulImplosionQ = 2,
                    FaithfulImplosionApproach = 0.14,
                },
            },
            // #920 deeper nesting — faithful implosion at a SATELLITE-OF-A-SATELLITE via a tree
            // address. Parent path "1/2 1/2" = the period-4 cascade bulb; p/q = 1/2 implodes
            // toward its period-8 onset c ≈ −1.3680989.
            new()
            {
                Name          = "Parabolic implosion — deep nesting (period-4 bulb)",
                CenterX       =  0.0,
                CenterY       =  0.0,
                Zoom          =  0.6,
                Iterations    =  4000,
                Description   = "Parabolic implosion at a SATELLITE-OF-A-SATELLITE (deeper nesting). The 'Parent path' 1/2 1/2 addresses the period-4 bulb of the period-doubling cascade; p/q = 1/2 implodes toward its period-8 onset c ≈ −1.3680989. Set the parent path (space-separated p/q, outermost first) + p/q in the Julia panel, then enable 'Parabolic implosion (cardioid, pick p/q)'. Any address depth works — the bulb-boundary solver descends the whole chain.",
                RegionType    = RegionType.BuiltIn,
                FractalType   = FractalType.Julia,
                QualityPreset = QualityPreset.Standard,
                AnimationName = "Parabolic implosion (cardioid, pick p/q)",
                Params        = new RegionFractalParams
                {
                    JuliaCRe = -1.3680989394, JuliaCIm = 0.0,  // fallback c (period-8 onset) if mode off
                    FaithfulImplosion = true,
                    FaithfulImplosionParentPath = "1/2 1/2",
                    FaithfulImplosionP = 1, FaithfulImplosionQ = 2,
                    FaithfulImplosionApproach = 0.14,
                },
            },
        ];

        // ── Interesting random-zoom regions for the slideshow ────────────────────
        // These are hand-picked coordinates that are visually striking but not
        // shown as named bookmarks in the UI.  The slideshow draws from both
        // _builtIns (and user regions) and _randomPool.
        private static readonly FractalRegion[] _randomPool =
        [
            // ── Deep seahorse spirals ──────────────────────────────────────────────
            new() { Name="R:SeahorseA",  CenterX=-0.74878, CenterY=0.06508, Zoom=12000.0, Iterations=2000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SeahorseB",  CenterX=-0.74529, CenterY=0.11307, Zoom=8000.0,  Iterations=1800, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SeahorseC",  CenterX=-0.74542, CenterY=0.13161, Zoom=290.0,  Iterations=1500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SeahorseD",  CenterX=-0.77568, CenterY=0.13646, Zoom=15000.0, Iterations=2500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Elephant valley variations ─────────────────────────────────────────
            new() { Name="R:ElephantA",  CenterX=0.32530,  CenterY=0.04868, Zoom=4000.0,  Iterations=1600, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:ElephantB",  CenterX=0.375534459723856,  CenterY=-0.221346110647405,Zoom=2000.0,  Iterations=1500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:ElephantC",  CenterX=0.35516,  CenterY=0.09486, Zoom=200.0,  Iterations=2000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Mini Mandelbrots (self-similar copies) ─────────────────────────────
            new() { Name="R:MiniA",      CenterX=-1.6271862274936, CenterY=0.00000, Zoom=55.0,  Iterations=2000, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:MiniB",      CenterX=-0.160229506084313, CenterY=1.03460261104092, Zoom=60.0,  Iterations=500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:MiniC",      CenterX=-1.25067386008417, CenterY=0.0201413514898332, Zoom=54602629.0,  Iterations=2500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:MiniD",      CenterX=0.366432439759528,  CenterY=-0.676487494685914,Zoom=3065.0,  Iterations=1400, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:MiniE",      CenterX=-1.94157, CenterY=0.00000, Zoom=502.0, Iterations=1200, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Spiral galaxies / triple spirals ───────────────────────────────────
            new() { Name="R:SpiralA",    CenterX=-0.562474314086615, CenterY=0.64138011514593, Zoom=91,  Iterations=1200, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SpiralB",    CenterX=-0.0976515101078047, CenterY=0.654455924064267, Zoom=227,  Iterations=1114, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SpiralC",    CenterX=-0.52768, CenterY=0.52768, Zoom=3000.0,  Iterations=1500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:SpiralD",    CenterX=-0.053974358974359, CenterY=0.663897435897436, Zoom=50.0, Iterations=500, QualityPreset=QualityPreset.Standard},
            // ── Period-3 bulb and neighbourhood ───────────────────────────────────
            new() { Name="R:Period3A",   CenterX=-0.0958466539313279, CenterY=0.653567154869739, Zoom=93.0,  Iterations=500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:Period3B",   CenterX=-0.13500, CenterY=0.65000, Zoom=1500.0,  Iterations=1200, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:Period3C",   CenterX=-0.16667, CenterY=1.04000, Zoom=1736.0,  Iterations=670, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            // ── Lightning / filament zones ─────────────────────────────────────────
            new() { Name="R:LightA",     CenterX=-0.626614850667933, CenterY=0.384657235048688, Zoom=744.0,  Iterations=650, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:LightB",     CenterX=-0.507263617832552, CenterY=0.526971432700647, Zoom=175.0,  Iterations=550, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:LightC",     CenterX=-0.740972025145092, CenterY=0.104494920892684, Zoom=800.0,   Iterations=650, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            // ── Parabolic / satellite bulbs ────────────────────────────────────────
            new() { Name="R:ParabA",     CenterX=-1.40115, CenterY=0.00000, Zoom=4000.0,  Iterations=2500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:ParabB",     CenterX=-1.31079592300444, CenterY=0.0731247515540183, Zoom=64694.7,  Iterations=1750, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // Stopped here.
            new() { Name="R:ParabC",     CenterX=0.25033364354215, CenterY=0.25033364354215, Zoom=20003.0, Iterations=2500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Deep double spirals ────────────────────────────────────────────────
            new() { Name="R:DblSpiralA", CenterX=-0.72700, CenterY=0.18900, Zoom=5000.0,  Iterations=2000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:DblSpiralB", CenterX=-0.74108, CenterY=0.16858, Zoom=30000.0, Iterations=3500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:DblSpiralC", CenterX=-0.73657, CenterY=0.18781, Zoom=18000.0, Iterations=3000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Upper filament / star clusters ────────────────────────────────────
            new() { Name="R:StarA",      CenterX=-0.159158498023715, CenterY=1.02331660079051, Zoom=2000.0,  Iterations=1500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:StarB",      CenterX=1.02331660079051, CenterY=1.02525867534908, Zoom=5000.0,  Iterations=2000, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:StarC",      CenterX=-0.22700, CenterY=1.11600, Zoom=3500.0,  Iterations=2000, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            // ── Needle tip zone ───────────────────────────────────────────────────
            new() { Name="R:NeedleA",    CenterX=-1.99991, CenterY=0.00000, Zoom=15000.0, Iterations=3000, QualityPreset=QualityPreset.Ultra, FractalType=FractalType.Mandelbrot },
            new() { Name="R:NeedleB",    CenterX=-1.99999, CenterY=0.00000, Zoom=50000.0, Iterations=5000, QualityPreset=QualityPreset.Ultra, FractalType=FractalType.Mandelbrot },
            // ── Cauliflower / cardioid edge ────────────────────────────────────────
            new() { Name="R:CauliA",     CenterX=0.25010,  CenterY=0.00000, Zoom=2000.0,  Iterations=1500, QualityPreset=QualityPreset.Standard, FractalType=FractalType.Mandelbrot },
            new() { Name="R:CauliB",     CenterX=0.25033364354215,  CenterY=3.9525691699605E-06, Zoom=8000.0,  Iterations=2500, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            // ── Deep zoom demo points (DD precision) ──────────────────────────────
            new() { Name="R:DeepA",      CenterX=-0.743643887037151, CenterY=0.131825904205330, Zoom=1e14, Iterations=8000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:DeepB",      CenterX=-0.73364389241974, CenterY=0.245521140671023, Zoom=5e13, Iterations=6000, QualityPreset=QualityPreset.High, FractalType=FractalType.Mandelbrot },
            new() { Name="R:DeepC",      CenterX=0.001643721971153, CenterY=0.822467633298876,  Zoom=3e9, Iterations=10000,QualityPreset=QualityPreset.Ultra, FractalType=FractalType.Mandelbrot },
        ];

        // ── Public collections ────────────────────────────────────────────────

        public bool IncludeExtremeInAll { get; set; } = false; // For now, we exclude extreme regions from the main list to keep the UI focused on more accessible areas.  This can be made user-configurable in the future.

        /// <summary>Read-only list of built-in regions.</summary>
        public IReadOnlyList<FractalRegion> BuiltIns => _builtIns;

        /// <summary>Mutable list of user-defined regions.</summary>
        public List<FractalRegion> UserRegions { get; } = new();

        /// <summary>
        /// All regions (built-ins first, then user-defined) in display order.
        /// </summary>
        public IEnumerable<FractalRegion> All
        {
            get
            {
                foreach (var r in _builtIns) yield return r;
                foreach (var r in UserRegions) yield return r;
                //foreach (var r in _randomPool) yield return r;
            }
        }

        /// <summary>
        /// All slideshow-eligible regions: built-ins, user-defined, and interesting random pool.
        /// User regions of every fractal type are included — the Avalonia SlideshowEngine commits
        /// each leg through <c>ApplyRegion</c> + a host Trigger, which honours the region's own
        /// fractal type, and its cross-fade already degrades to a fade-through-black for
        /// non-Mandelbrot incoming regions (the offscreen preview render is Mandelbrot-only).
        /// The only quality gate is the <see cref="IncludeExtremeInAll"/> toggle, which controls
        /// whether Extreme-quality user regions join the pool.
        /// </summary>
        public IEnumerable<FractalRegion> AllSlideshowRegions
        {
            get
            {
                foreach (var r in _builtIns) yield return r;

                foreach (var r in UserRegions)
                {
                    if (!IncludeExtremeInAll && r.QualityPreset?.Tier >= QualityTier.Extreme)   // #1861: and Unbounded
                        continue;
                    yield return r;
                }

                foreach (var r in _randomPool) yield return r;
            }
        }

        public int MaxRegionNameLength
        {
            get
            {
                int max = 0;
                foreach (var r in All)
                    if (r.Name.Length > max)
                        max = r.Name.Length;
                return max;
            }
        }

        // ── Persistence ───────────────────────────────────────────────────────

        /// <summary>
        /// Loads user-defined regions from disk.  Safe to call if the file does
        /// not yet exist.
        /// </summary>
        public void Load()
        {
            if (!File.Exists(RegionsFile)) return;

            // #966 — per-entry: one region this build can't read (a FractalType or
            // field from a newer / other-branch build) is kept verbatim and written
            // back on Save instead of wiping every user region; an unparseable file
            // is snapshotted before anything can overwrite it.
            var loaded = _persisted.LoadFile(RegionsFile, (JsonSerializerOptions?)null);
            UserRegions.Clear();
            foreach (var r in loaded)
            {
                r.RegionType = RegionType.UserDefined;
                UserRegions.Add(r);
            }
        }

        // #966 — preserves entries this build could not read across Load/Save.
        private readonly TolerantJsonList<FractalRegion> _persisted = new();

        /// <summary>User regions in regions.json this build could not read (kept on
        /// disk, hidden from the library).</summary>
        public int UnreadableCount => _persisted.UnreadableCount;

        /// <summary>
        /// Persists user-defined regions to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = _persisted.Serialize(UserRegions, options, r => r.Name);

                // Atomic write with one-level rollback (temp + File.Replace →
                // regions.json.bak). A reader never sees a half-written file and
                // the last-known-good copy survives one bad/empty save
                // (regions.json got wiped to "[]" once — the .bak recovers it).
                AtomicFile.WriteAllText(RegionsFile, json);
            }
            catch
            {
                // Non-fatal — user loses saved regions but app continues.
            }
        }

        /// <summary>
        /// Adds a user region and immediately persists the library.
        /// Returns false if a user region with the same name already exists.
        /// </summary>
        public bool AddUserRegion(FractalRegion region)
        {
            region.RegionType = RegionType.UserDefined;
            // Prevent duplicate names.
            foreach (var r in UserRegions)
                if (r.Name.Equals(region.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
            UserRegions.Add(region);
            Save();
            return true;
        }

        /// <summary>
        /// Removes a user-defined region by name and persists.
        /// Returns false if the region is built-in or not found.
        /// </summary>
        public bool RemoveUserRegion(string name)
        {
            for (int i = 0; i < UserRegions.Count; i++)
            {
                if (UserRegions[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    UserRegions.RemoveAt(i);
                    Save();
                    return true;
                }
            }
            return false;
        }

        /// <summary>Finds any region (built-in or user) by name, or null.</summary>
        public FractalRegion? FindByName(string name)
        {
            foreach (var r in All)
                if (r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return r;
            // Back-compat: saved data may reference the pre-ASCII (Unicode)
            // region name. Resolve the alias and retry once.
            var aliased = LegacyNameAliases.Resolve(name);
            if (aliased != null)
                foreach (var r in All)
                    if (r.Name.Equals(aliased, StringComparison.OrdinalIgnoreCase))
                        return r;
            return null;
        }
    }
}
