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
    public sealed class FractalRegion
    {
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
        [JsonConverter(typeof(JsonStringEnumConverter))]
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
                if (entry != null) { p.UserEquationSource = entry.Source; p.UserEquationName = entry.Name; p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive; }
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
                    p.UserBulbSource = UserBulbSource;
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
        [JsonIgnore(Condition = OmitNull)] public int? IFSIterations { get; set; }
        [JsonIgnore(Condition = OmitNull)] public int? LSystemDepth { get; set; }
        [JsonIgnore(Condition = OmitNull)] public string? AttractorPresetName { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AttractorA { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AttractorB { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AttractorC { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? AttractorD { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? UserEquationRotationDegrees { get; set; }
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
                },
                FractalType.Multibrot => new RegionFractalParams
                {
                    MultibrotExponent = p.MultibrotExponent,
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
                FractalType.Newton or FractalType.Nova or FractalType.Halley => new RegionFractalParams
                {
                    NewtonExponent = p.NewtonExponent,
                    NewtonRelaxation = p.NewtonRelaxation,
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
                FractalType.BicomplexMandelbrot => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.BicomplexCameraDistance,
                    Cam3DTheta = p.BicomplexCameraTheta,
                    Cam3DPhi = p.BicomplexCameraPhi,
                    Cam3DSliceW = p.BicomplexSliceW,
                },
                FractalType.Coquaternion => new RegionFractalParams
                {
                    Cam3DFamily = (int)type,
                    Cam3DDistance = p.CoquaternionCameraDistance,
                    Cam3DTheta = p.CoquaternionCameraTheta,
                    Cam3DPhi = p.CoquaternionCameraPhi,
                    Cam3DSliceW = p.CoquaternionSliceW,
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
                FractalType.DualOrbitEscape => new RegionFractalParams
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
                },
                // UserBulb keeps its source + dedicated camera on the region itself;
                // the animatable power / iterations / time ride here.
                FractalType.UserBulb => new RegionFractalParams
                {
                    BulbPower = Nd(p.BulbPower, D.BulbPower),
                    UserBulbIterations = Ni(p.UserBulbIterations, D.UserBulbIterations),
                    UserBulbTime = Nd(p.UserBulbTime, D.UserBulbTime),
                },
                // Mandelbrot, Tricorn, BurningShip, Magnet1/2, TearDrop and the
                // generated families need no extra params — defaults suffice.
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

            // #961 — a family whose params are all at their defaults (every field
            // omitted) carries no block, so a stock region never saves an empty {}.
            if (rp != null && JsonSerializer.Serialize(rp, s_omitNull) == "{}") rp = null;

            return rp;
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
            if (Cam3DFamily.HasValue && Cam3DDistance.HasValue
                && Cam3DTheta.HasValue && Cam3DPhi.HasValue)
            {
                double d = Cam3DDistance.Value, th = Cam3DTheta.Value, ph = Cam3DPhi.Value;
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
                        if (QMandelDualOrbitColor.HasValue) p.QMandelDualOrbitColor = QMandelDualOrbitColor.Value;
                        if (QMandelDualSeedX.HasValue) p.QMandelDualSeedX = QMandelDualSeedX.Value;
                        if (QMandelDualSeedY.HasValue) p.QMandelDualSeedY = QMandelDualSeedY.Value;
                        if (QMandelDualSeedZ.HasValue) p.QMandelDualSeedZ = QMandelDualSeedZ.Value;
                        break;
                    case FractalType.Kleinian:
                        p.KleinianCameraDistance = d; p.KleinianCameraTheta = th; p.KleinianCameraPhi = ph;
                        if (KleinianPreset.HasValue) p.KleinianPreset = (FracturingFog.KleinianPreset)KleinianPreset.Value;
                        if (KleinianNecklaceCount.HasValue) p.KleinianNecklaceCount = KleinianNecklaceCount.Value;
                        if (KleinianCustomSpheres != null) p.KleinianCustomSpheres = KleinianCustomSpheres.ConvertAll(s => s.Clone());
                        if (KleinianRotationAngle.HasValue) p.KleinianRotationAngle = KleinianRotationAngle.Value;
                        if (KleinianRotationAxisX.HasValue) p.KleinianRotationAxisX = KleinianRotationAxisX.Value;
                        if (KleinianRotationAxisY.HasValue) p.KleinianRotationAxisY = KleinianRotationAxisY.Value;
                        if (KleinianRotationAxisZ.HasValue) p.KleinianRotationAxisZ = KleinianRotationAxisZ.Value;
                        if (KleinianColorSource.HasValue) p.KleinianColorSource = (FracturingFog.KleinianColorSource)KleinianColorSource.Value;
                        if (KleinianDeFactor.HasValue) p.KleinianDeFactor = KleinianDeFactor.Value;
                        break;
                    case FractalType.BicomplexMandelbrot:
                        p.BicomplexCameraDistance = d; p.BicomplexCameraTheta = th; p.BicomplexCameraPhi = ph;
                        if (Cam3DSliceW.HasValue) p.BicomplexSliceW = Cam3DSliceW.Value;
                        break;
                    case FractalType.Coquaternion:
                        p.CoquaternionCameraDistance = d; p.CoquaternionCameraTheta = th; p.CoquaternionCameraPhi = ph;
                        if (Cam3DSliceW.HasValue) p.CoquaternionSliceW = Cam3DSliceW.Value;
                        break;
                }
            }

            // #94 (P4) — non-spatial family params (static-hold legs).
            if (PlasmaSeed.HasValue) p.PlasmaSeed = PlasmaSeed.Value;
            if (PlasmaRoughness.HasValue) p.PlasmaRoughness = PlasmaRoughness.Value;
            if (!string.IsNullOrEmpty(FlamePresetName)) p.FlamePresetName = FlamePresetName;
            if (FlameGamma.HasValue) p.FlameGamma = FlameGamma.Value;
            if (DlaParticles.HasValue) p.DlaParticles = DlaParticles.Value;
            if (DlaSeed.HasValue) p.DlaSeed = DlaSeed.Value;
            if (LogisticSeed.HasValue) p.LogisticSeed = LogisticSeed.Value;
            if (LogisticBurnIn.HasValue) p.LogisticBurnIn = LogisticBurnIn.Value;
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
            if (IFSIterations.HasValue) p.IFSIterations = IFSIterations.Value;
            if (LSystemDepth.HasValue) p.LSystemDepth = LSystemDepth.Value;
            if (!string.IsNullOrEmpty(AttractorPresetName)) p.AttractorPresetName = AttractorPresetName;
            if (AttractorA.HasValue) p.AttractorA = AttractorA.Value;
            if (AttractorB.HasValue) p.AttractorB = AttractorB.Value;
            if (AttractorC.HasValue) p.AttractorC = AttractorC.Value;
            if (AttractorD.HasValue) p.AttractorD = AttractorD.Value;
            if (UserEquationRotationDegrees.HasValue) p.UserEquationRotationDegrees = UserEquationRotationDegrees.Value;
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
        public bool DenoiseTemporal { get; set; } = false;
        public double DenoiseTemporalFeedback { get; set; } = 0.8;
        public double DenoiseVarianceScale { get; set; } = 4.0;

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
                DenoiseTemporal    = p.Relief2DDenoiseTemporal,
                DenoiseTemporalFeedback = p.Relief2DDenoiseTemporalFeedback,
                DenoiseVarianceScale = p.Relief2DDenoiseVarianceScale,
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
            p.Relief2DDenoiseTemporal        = DenoiseTemporal;
            p.Relief2DDenoiseTemporalFeedback = DenoiseTemporalFeedback;
            p.Relief2DDenoiseVarianceScale   = DenoiseVarianceScale;
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
                Zoom        =  2.0,
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
                Zoom        =  1.0,
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
            new()
            {
                Name        = "Apollonian - L/R Kissing Cusp",
                CenterX     =  0.0,
                CenterY     =  0.4,
                Zoom        =  6.0,
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
                    if (!IncludeExtremeInAll && QualityPreset.Extreme.Equals(r.QualityPreset))
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
