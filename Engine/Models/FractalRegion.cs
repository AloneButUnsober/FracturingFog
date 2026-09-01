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

        // #253 — cross-fractal domain warp. Carried for the escape-time family
        // (Julia, Burning Ship, Tricorn, Multibrot, Magnet 1/2, Glynn, Phoenix,
        // Spider) whenever it's enabled, even on types whose base block is null.
        // Only recorded when on; recall resets the warp off for any region that
        // doesn't carry it (authoritative, like Relief 3D).
        [JsonIgnore(Condition = OmitNull)] public bool? DomainWarpEnabled { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DomainWarpStrength { get; set; }
        [JsonIgnore(Condition = OmitNull)] public double? DomainWarpFrequency { get; set; }

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

        public static RegionFractalParams? Snapshot(FractalType type, FractalParameters? p)
        {
            if (p == null) return null;
            var rp = type switch
            {
                FractalType.Julia => new RegionFractalParams
                {
                    JuliaCRe = p.JuliaC.Real,
                    JuliaCIm = p.JuliaC.Imaginary,
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
                },
                FractalType.Apollonian => new RegionFractalParams
                {
                    ApollonianDepth = p.ApollonianDepth,
                    ApollonianMinPixelRadius = p.ApollonianMinPixelRadius,
                    ApollonianColorByDepth = p.ApollonianColorByDepth,
                },
                FractalType.AcidWarp => new RegionFractalParams
                {
                    AcidWarpPattern = p.AcidWarpPattern,
                    AcidWarpFrequency = p.AcidWarpFrequency,
                    AcidWarpWarpStrength = p.AcidWarpWarpStrength,
                    AcidWarpMorph = p.AcidWarpMorph ? true : null,
                    AcidWarpFlow = p.AcidWarpMorph ? p.AcidWarpFlow : null,
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

            return rp;
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
            try
            {
                if (!File.Exists(RegionsFile)) return;

                string json = File.ReadAllText(RegionsFile);
                var loaded = JsonSerializer.Deserialize<List<FractalRegion>>(json);
                if (loaded == null) return;

                UserRegions.Clear();
                foreach (var r in loaded)
                {
                    r.RegionType = RegionType.UserDefined;
                    UserRegions.Add(r);
                }
            }
            catch
            {
                // If the file is corrupt, silently start fresh.
                UserRegions.Clear();
            }
        }

        /// <summary>
        /// Persists user-defined regions to disk.
        /// </summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(UserRegions, options);

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
