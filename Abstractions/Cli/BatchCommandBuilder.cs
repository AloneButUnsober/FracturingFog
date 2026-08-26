// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Cli/BatchCommandBuilder.cs
// #361 / #362 (slices of #64) — CLI Command Builder.
//
// Turns a snapshot of the live 2D configuration into a copy/paste-ready
// `--batch` command line. This is the *reverse* of Batch/BatchOptions.TryParse;
// both sides now share the flag names + defaults in Batch/BatchFlags.cs, so the
// two cannot drift (#362). A parse-round-trip test in Server.Tests locks the
// pairing.
//
// #362 also adds FIDELITY GAP DETECTION: the live config carries fx that the
// 2D batch path has no flag for (relief, interior-alpha, stereo/SBS, domain
// warp, and unsaved themes with no name to reference). Emitting a command that
// silently drops them would produce a different image than the screen — worse
// than no command. BuildWithReport surfaces those gaps so the UI can warn.
//
// Fx that ARE carried by the theme (3D/PBR lighting via ColorThemeDef) are NOT
// treated as gaps here: --theme reproduces them. Per-fx flags for the remaining
// families are tracked in #363; as they land, the gap list shrinks.

using System;
using System.Collections.Generic;
using System.Globalization;
using FracturingFog.Batch;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Cli
{
    /// <summary>
    /// Immutable snapshot of the live 2D configuration the command builder
    /// serialises. Populated by the UI from the active view-state; kept as a
    /// plain input so the builder stays pure and unit-testable without a shell.
    /// </summary>
    public sealed class BatchCommandSnapshot
    {
        public FractalType Fractal { get; init; } = FractalType.Mandelbrot;

        // Live authoritative coordinates. Emitted verbatim (round-trippable) so
        // the command reproduces exactly what is on screen — even when a region
        // was loaded and then panned/zoomed away from its saved coordinates.
        public double CenterX { get; init; } = BatchDefaults.CenterX;
        public double CenterY { get; init; } = BatchDefaults.CenterY;
        public double Zoom { get; init; } = BatchDefaults.Zoom;

        /// <summary>Effective iteration count in use for the current render.
        /// Emitted as <c>--iter</c> whenever &gt; 0 so the poster does not drift
        /// with the quality preset's iteration formula.</summary>
        public int Iterations { get; init; }

        public string ThemeName { get; init; } = BatchDefaults.ThemeName;
        public string QualityName { get; init; } = BatchDefaults.QualityName;

        public int Width { get; init; } = BatchDefaults.Width;
        public int Height { get; init; } = BatchDefaults.Height;

        // Post-FX (parity with the interactive sliders). 0 = neutral / omit.
        public int Brightness { get; init; }
        public int Contrast { get; init; }
        public int HistogramEq { get; init; }

        /// <summary>Global interior alpha, 0..255 (#96). 255 = opaque (omit the
        /// flag); below 255 emits <c>--interior-alpha N</c>.</summary>
        public int InteriorAlpha { get; init; } = 255;

        /// <summary>Output-stage view transform / tonemap (roadmap S2, #389).
        /// None (default) omits the flag; anything else emits
        /// <c>--view-transform NAME</c>.</summary>
        public ViewTransform ViewTransform { get; init; } = ViewTransform.None;

        /// <summary>Exposure in stops before the view transform (roadmap S2,
        /// #389). 0 (default) omits the flag; otherwise emits <c>--exposure EV</c>.</summary>
        public double ViewExposureEv { get; init; }

        /// <summary>Fractal-specific parameters. Only the fields with a matching
        /// batch flag are read, and only for the matching fractal type.</summary>
        public FractalParameters? Parameters { get; init; }

        /// <summary>Executable name to lead the command with. The user runs from
        /// wherever the exe lives; a bare name keeps the string portable.</summary>
        public string ExecutableName { get; init; } = "FracturingFog";

        /// <summary>Placeholder token emitted for <c>--out</c>. The builder never
        /// invents a real path — the user substitutes one.</summary>
        public string OutputPlaceholder { get; init; } = "<OUTPUT.png>";

        // ── Fidelity-gap inputs (#362) ────────────────────────────────────────
        // Live fx state the 2D batch path cannot express. When set, the builder
        // records a gap; the command is still emitted (it reproduces everything
        // else) but the UI warns that these will not survive the round trip.

        /// <summary>2D relief / height-field shading is active
        /// (FractalParameters.Relief2DEnabled). Emits <c>--relief</c> + core
        /// knobs (#363).</summary>
        public bool ReliefEnabled { get; init; }

        /// <summary>Relief uses the oblique raymarch path (Relief2DRaymarch).
        /// Emits <c>--relief-raymarch</c>.</summary>
        public bool ReliefRaymarch { get; init; }

        // Relief core knobs. Defaults mirror FractalParameters; emitted only when
        // relief is on and the value deviates from its default.
        public double ReliefHeight { get; init; } = 1.0;

        /// <summary>#518 — filament detail-gain (1 = off), feature radius (0 = auto),
        /// height gamma (1 = off). Emitted only when non-default.</summary>
        public double ReliefDetailGain { get; init; } = 1.0;
        public int    ReliefDetailRadius { get; init; }
        public double ReliefHeightGamma { get; init; } = 1.0;
        public double ReliefStrength { get; init; } = 1.0;
        public double ReliefLightAzimuth { get; init; } = 135.0;
        public double ReliefLightElevation { get; init; } = 30.0;
        public double ReliefShadow { get; init; } = 0.6;

        /// <summary>Emboss absolute-height mode (Relief2DAbsolute).</summary>
        public bool ReliefAbsolute { get; init; }

        // Relief raymarch camera. Emitted only when relief + raymarch are on and
        // the value deviates from its default. Defaults mirror FractalParameters.
        public double ReliefCameraAzimuth { get; init; } = 0.0;
        public double ReliefCameraElevation { get; init; } = 45.0;
        public double ReliefCameraFov { get; init; } = 50.0;
        public double ReliefCameraZoom { get; init; } = 1.0;
        public bool ReliefCameraOrtho { get; init; }

        /// <summary>#520 — far-detail factor (1 = off). Emitted when &lt; 1.</summary>
        public double ReliefFarDetail { get; init; } = 1.0;

        // Depth of field on the relief raymarch camera (roadmap S3, #389).
        // Aperture 0 = pinhole (omit both flags). Emitted only on the raymarch path.
        public double ReliefDofAperture { get; init; }
        public double ReliefDofFocus { get; init; }

        /// <summary>Froxel volumetrics on the relief raymarch (roadmap S6, #408).
        /// Emitted as <c>--relief-froxel</c> on the raymarch path.</summary>
        public bool ReliefFroxel { get; init; }

        /// <summary>Froxel resolution (roadmap S6, #408). Balanced (default) is omitted;
        /// otherwise emits <c>--relief-froxel-quality Q</c> (which itself implies froxel).</summary>
        public FracturingFog.Models.FroxelQuality ReliefFroxelQuality { get; init; } = FracturingFog.Models.FroxelQuality.Balanced;

        /// <summary>Per-light fog contribution bitmask (roadmap S6, #408). 7 (all
        /// lights fog) = default → omitted; otherwise emits <c>--fog-light-mask N</c>.</summary>
        public int FogLightMask { get; init; } = 0x7;

        // Guided À-Trous denoise (S4, #389). Emitted only on the raymarch path,
        // iterations > 0. Sigmas default to the operator defaults.
        public int ReliefDenoiseIterations { get; init; }
        public double ReliefDenoiseColorSigma { get; init; } = 0.10;
        public double ReliefDenoiseNormalSigma { get; init; } = 0.30;
        public double ReliefDenoiseDepthSigma { get; init; } = 0.20;

        // Relief isolate masking. Emitted when relief + isolate are on.
        public bool ReliefIsolate { get; init; }
        public bool ReliefIsolateByDetail { get; init; } = true;
        public double ReliefIsolateThreshold { get; init; } = 0.6;
        public bool ReliefIsolateByColor { get; init; }
        public string ReliefIsolateColors { get; init; } = "";
        public double ReliefIsolateTolerance { get; init; } = 0.12;

        /// <summary>Stereo / SBS output is on (Lighting.StereoMode != Off).
        /// No batch flag (#363).</summary>
        public bool StereoActive { get; init; }

        /// <summary>Domain-warp post-fx is on (DomainWarpEnabled). Emitted as
        /// <c>--domain-warp</c> (#363).</summary>
        public bool DomainWarpActive { get; init; }

        /// <summary>Domain-warp strength; emitted when domain warp is on and the
        /// value is non-zero.</summary>
        public double DomainWarpStrength { get; init; }

        /// <summary>Domain-warp frequency; emitted when domain warp is on and the
        /// value differs from the default (1.0).</summary>
        public double DomainWarpFrequency { get; init; } = 1.0;

        /// <summary>The active theme is a custom/edited palette with no saved
        /// name to reference. The command cannot emit <c>--theme</c> for it, so
        /// the poster falls back to the batch default (HSV).</summary>
        public bool ThemeIsUnsaved { get; init; }
    }

    /// <summary>Result of <see cref="BatchCommandBuilder.BuildWithReport"/>:
    /// the command string plus any fidelity gaps the caller should surface.</summary>
    public sealed class CommandBuildReport
    {
        public string Command { get; }
        public IReadOnlyList<string> Gaps { get; }
        public bool HasGaps => Gaps.Count > 0;

        public CommandBuildReport(string command, IReadOnlyList<string> gaps)
        {
            Command = command;
            Gaps = gaps;
        }
    }

    public static class BatchCommandBuilder
    {
        /// <summary>Serialise <paramref name="snap"/> to a single-line
        /// <c>--batch</c> command string (no gap report).</summary>
        public static string Build(BatchCommandSnapshot snap) => BuildWithReport(snap).Command;

        /// <summary>Serialise <paramref name="snap"/> and collect any fidelity
        /// gaps (live fx the 2D batch path cannot reproduce).</summary>
        public static CommandBuildReport BuildWithReport(BatchCommandSnapshot snap)
        {
            if (snap == null) throw new ArgumentNullException(nameof(snap));

            var parts = new List<string>(24)
            {
                Token(snap.ExecutableName),
                "--batch",
            };

            // Fractal type — always explicit so the command is self-describing.
            parts.Add(BatchFlags.Fractal);
            parts.Add(Token(snap.Fractal.ToString()));

            // Live coordinates + iterations — always emitted for exact fidelity.
            parts.Add(BatchFlags.X);    parts.Add(Num(snap.CenterX));
            parts.Add(BatchFlags.Y);    parts.Add(Num(snap.CenterY));
            parts.Add(BatchFlags.Zoom); parts.Add(Num(snap.Zoom));
            if (snap.Iterations > 0)
            {
                parts.Add(BatchFlags.Iter);
                parts.Add(snap.Iterations.ToString(CultureInfo.InvariantCulture));
            }

            // Theme — emit unless it is the batch default (HSV) or unsaved (no
            // name to reference). An unsaved theme is recorded as a gap below.
            if (!snap.ThemeIsUnsaved &&
                !string.IsNullOrWhiteSpace(snap.ThemeName) &&
                !string.Equals(snap.ThemeName, BatchDefaults.ThemeName, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(BatchFlags.Theme);
                parts.Add(Token(snap.ThemeName));
            }

            // Quality — emit unless the batch default (Standard).
            if (!string.IsNullOrWhiteSpace(snap.QualityName) &&
                !string.Equals(snap.QualityName, BatchDefaults.QualityName, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(BatchFlags.Quality);
                parts.Add(Token(snap.QualityName));
            }

            // Output size — always explicit (deterministic poster dimensions).
            parts.Add(BatchFlags.Width);  parts.Add(snap.Width.ToString(CultureInfo.InvariantCulture));
            parts.Add(BatchFlags.Height); parts.Add(snap.Height.ToString(CultureInfo.InvariantCulture));

            // Post-FX — only when non-neutral.
            if (snap.Brightness != 0)  { parts.Add(BatchFlags.Brightness); parts.Add(snap.Brightness.ToString(CultureInfo.InvariantCulture)); }
            if (snap.Contrast != 0)    { parts.Add(BatchFlags.Contrast);   parts.Add(snap.Contrast.ToString(CultureInfo.InvariantCulture)); }
            if (snap.HistogramEq != 0) { parts.Add(BatchFlags.Adaptive);   parts.Add(snap.HistogramEq.ToString(CultureInfo.InvariantCulture)); }
            if (snap.InteriorAlpha < 255) { parts.Add(BatchFlags.InteriorAlpha); parts.Add(snap.InteriorAlpha.ToString(CultureInfo.InvariantCulture)); }

            // View transform / tonemap (S2, #389) — only when non-identity.
            if (snap.ViewTransform != ViewTransform.None)
            {
                parts.Add(BatchFlags.ViewTransform);
                parts.Add(ViewTransformFlagValue(snap.ViewTransform));
            }
            if (snap.ViewExposureEv != 0.0)
            {
                parts.Add(BatchFlags.Exposure);
                parts.Add(snap.ViewExposureEv.ToString("0.###", CultureInfo.InvariantCulture));
            }

            // Domain-warp post-fx (any fractal). Emit the toggle, plus knobs when
            // they deviate from their defaults.
            if (snap.DomainWarpActive)
            {
                parts.Add(BatchFlags.DomainWarp);
                if (snap.DomainWarpStrength != 0.0)
                { parts.Add(BatchFlags.DomainWarpStrength); parts.Add(Num(snap.DomainWarpStrength)); }
                if (snap.DomainWarpFrequency != 1.0)
                { parts.Add(BatchFlags.DomainWarpFrequency); parts.Add(Num(snap.DomainWarpFrequency)); }
            }

            // 2D relief (any fractal the poster renders relief for). Master flag
            // plus core knobs when they deviate from their defaults.
            if (snap.ReliefEnabled)
            {
                parts.Add(BatchFlags.Relief);
                if (snap.ReliefRaymarch) parts.Add(BatchFlags.ReliefRaymarch);
                if (snap.ReliefAbsolute) parts.Add(BatchFlags.ReliefAbsolute);
                if (snap.ReliefHeight != 1.0)          { parts.Add(BatchFlags.ReliefHeight);         parts.Add(Num(snap.ReliefHeight)); }
                if (snap.ReliefDetailGain != 1.0)      { parts.Add(BatchFlags.ReliefDetailGain);     parts.Add(Num(snap.ReliefDetailGain)); }
                if (snap.ReliefDetailRadius != 0)      { parts.Add(BatchFlags.ReliefDetailRadius);   parts.Add(snap.ReliefDetailRadius.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                if (snap.ReliefHeightGamma != 1.0)     { parts.Add(BatchFlags.ReliefHeightGamma);    parts.Add(Num(snap.ReliefHeightGamma)); }
                if (snap.ReliefStrength != 1.0)        { parts.Add(BatchFlags.ReliefStrength);       parts.Add(Num(snap.ReliefStrength)); }
                if (snap.ReliefLightAzimuth != 135.0)  { parts.Add(BatchFlags.ReliefLightAzimuth);   parts.Add(Num(snap.ReliefLightAzimuth)); }
                if (snap.ReliefLightElevation != 30.0) { parts.Add(BatchFlags.ReliefLightElevation); parts.Add(Num(snap.ReliefLightElevation)); }
                if (snap.ReliefShadow != 0.6)          { parts.Add(BatchFlags.ReliefShadow);         parts.Add(Num(snap.ReliefShadow)); }

                // Raymarch camera framing — only relevant on the raymarch path.
                if (snap.ReliefRaymarch)
                {
                    if (snap.ReliefCameraOrtho) parts.Add(BatchFlags.ReliefCameraOrtho);
                    if (snap.ReliefFarDetail != 1.0) { parts.Add(BatchFlags.ReliefFarDetail); parts.Add(Num(snap.ReliefFarDetail)); }
                    if (snap.ReliefCameraAzimuth != 0.0)    { parts.Add(BatchFlags.ReliefCameraAzimuth);   parts.Add(Num(snap.ReliefCameraAzimuth)); }
                    if (snap.ReliefCameraElevation != 45.0) { parts.Add(BatchFlags.ReliefCameraElevation); parts.Add(Num(snap.ReliefCameraElevation)); }
                    if (snap.ReliefCameraFov != 50.0)       { parts.Add(BatchFlags.ReliefCameraFov);       parts.Add(Num(snap.ReliefCameraFov)); }
                    if (snap.ReliefCameraZoom != 1.0)       { parts.Add(BatchFlags.ReliefCameraZoom);      parts.Add(Num(snap.ReliefCameraZoom)); }

                    // Depth of field (S3, #389) — emit only when the lens is open.
                    if (snap.ReliefDofAperture > 0.0)
                    {
                        parts.Add(BatchFlags.DofAperture); parts.Add(Num(snap.ReliefDofAperture));
                        if (snap.ReliefDofFocus > 0.0) { parts.Add(BatchFlags.DofFocus); parts.Add(Num(snap.ReliefDofFocus)); }
                    }

                    // Froxel volumetrics (S6, #408). A non-Balanced quality flag implies
                    // froxel, so emit the bare --relief-froxel only at Balanced.
                    if (snap.ReliefFroxel)
                    {
                        if (snap.ReliefFroxelQuality != FracturingFog.Models.FroxelQuality.Balanced)
                        {
                            parts.Add(BatchFlags.ReliefFroxelQuality);
                            parts.Add(snap.ReliefFroxelQuality.ToString());
                        }
                        else
                        {
                            parts.Add(BatchFlags.ReliefFroxel);
                        }
                    }

                    // Guided À-Trous denoise (S4, #389) — emit only when on. The
                    // sigmas ride along only when the pass runs and they differ
                    // from the pure operator's defaults (0.10 / 0.30 / 0.20).
                    if (snap.ReliefDenoiseIterations > 0)
                    {
                        parts.Add(BatchFlags.Denoise); parts.Add(snap.ReliefDenoiseIterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        if (snap.ReliefDenoiseColorSigma != 0.10)  { parts.Add(BatchFlags.DenoiseColorSigma);  parts.Add(Num(snap.ReliefDenoiseColorSigma)); }
                        if (snap.ReliefDenoiseNormalSigma != 0.30) { parts.Add(BatchFlags.DenoiseNormalSigma); parts.Add(Num(snap.ReliefDenoiseNormalSigma)); }
                        if (snap.ReliefDenoiseDepthSigma != 0.20)  { parts.Add(BatchFlags.DenoiseDepthSigma);  parts.Add(Num(snap.ReliefDenoiseDepthSigma)); }
                    }
                }

                // Isolate masking.
                if (snap.ReliefIsolate)
                {
                    parts.Add(BatchFlags.ReliefIsolate);
                    if (!snap.ReliefIsolateByDetail) parts.Add(BatchFlags.ReliefIsolateNoDetail);
                    if (snap.ReliefIsolateThreshold != 0.6) { parts.Add(BatchFlags.ReliefIsolateThreshold); parts.Add(Num(snap.ReliefIsolateThreshold)); }
                    if (snap.ReliefIsolateByColor) parts.Add(BatchFlags.ReliefIsolateByColor);
                    if (!string.IsNullOrEmpty(snap.ReliefIsolateColors)) { parts.Add(BatchFlags.ReliefIsolateColors); parts.Add(Token(snap.ReliefIsolateColors)); }
                    if (snap.ReliefIsolateTolerance != 0.12) { parts.Add(BatchFlags.ReliefIsolateTolerance); parts.Add(Num(snap.ReliefIsolateTolerance)); }
                }
            }

            // Per-light fog contribution mask (roadmap S6, #408) — only when it
            // deviates from the all-lights-fog default (7).
            if (snap.FogLightMask != 0x7)
            {
                parts.Add(BatchFlags.FogLightMask);
                parts.Add(snap.FogLightMask.ToString(CultureInfo.InvariantCulture));
            }

            // Per-light point / spot lights (roadmap S8, #404). Emitted for any
            // light whose Type is not Directional (positional lights are the only
            // ones with batch flags). Reads the live Lighting off Parameters.
            AppendLights(parts, snap);

            // Fractal-specific parameters that have batch flags. Emitted only for
            // the matching fractal type so unrelated defaults never clutter.
            AppendFractalParams(parts, snap);

            // Output path placeholder — always last, always a placeholder.
            parts.Add(BatchFlags.Out);
            parts.Add(Token(snap.OutputPlaceholder));

            return new CommandBuildReport(string.Join(" ", parts), DetectGaps(snap));
        }

        /// <summary>List the live fx the emitted command cannot reproduce.
        /// Empty when the 2D config is fully expressible.</summary>
        public static IReadOnlyList<string> DetectGaps(BatchCommandSnapshot snap)
        {
            var gaps = new List<string>();
            if (snap.ThemeIsUnsaved)
                gaps.Add("Custom/unsaved theme (save it first so the command can reference it by name; falls back to HSV)");
            if (snap.StereoActive)
                gaps.Add("Stereo / side-by-side (SBS) output");
            return gaps;
        }

        /// <summary>Emit <c>--lightN-*</c> flags (roadmap S8, #404) for any of the
        /// three lights that is a point or spot light. Directional lights have no
        /// batch flags (they are the default path), so an all-directional scene
        /// emits nothing here.</summary>
        private static void AppendLights(List<string> parts, BatchCommandSnapshot snap)
        {
            var p = snap.Parameters;
            if (p == null) return;
            AppendLight(parts, 1, p.Lighting.Light1);
            AppendLight(parts, 2, p.Lighting.Light2);
            AppendLight(parts, 3, p.Lighting.Light3);
        }

        private static void AppendLight(List<string> parts, int n, DirectionalLight d)
        {
            if (d.Type == LightType.Directional) return;

            parts.Add(BatchFlags.LightFlag(n, BatchFlags.LightFieldType));
            parts.Add(d.Type == LightType.Spot ? "spot" : "point");

            parts.Add(BatchFlags.LightFlag(n, BatchFlags.LightFieldIntensity));
            parts.Add(Num(d.Intensity));

            parts.Add(BatchFlags.LightFlag(n, BatchFlags.LightFieldDir));
            parts.Add(Num(d.Theta) + "," + Num(d.Phi));

            parts.Add(BatchFlags.LightFlag(n, BatchFlags.LightFieldPos));
            parts.Add(Num(d.PosX) + "," + Num(d.PosY) + "," + Num(d.PosZ));

            if (d.Range != 0.0)
            {
                parts.Add(BatchFlags.LightFlag(n, BatchFlags.LightFieldRange));
                parts.Add(Num(d.Range));
            }

            if (d.Type == LightType.Spot)
            {
                parts.Add(BatchFlags.LightFlag(n, BatchFlags.LightFieldCone));
                parts.Add(Num(d.SpotInnerDeg) + "," + Num(d.SpotOuterDeg));
            }

            // Colour (roadmap S8, #404). Emit only when it deviates from this
            // slot's default so unchanged lights stay terse; the parser keeps the
            // slot default when the flag is absent, so this round-trips exactly.
            if (d.Color != DefaultLightColor(n))
            {
                parts.Add(BatchFlags.LightFlag(n, BatchFlags.LightFieldColor));
                parts.Add(HexColor(d.Color));
            }

            // Area angular radius (roadmap S8, #404) — a finite emitter size that
            // softens the shadow penumbra. Emit only when set; 0 = punctual (the
            // parser's default), so an unchanged light stays terse. Directional
            // area lights are covered by the directional-emit decouple (#490).
            if (d.AreaAngularRadius != 0.0)
            {
                parts.Add(BatchFlags.LightFlag(n, BatchFlags.LightFieldArea));
                parts.Add(Num(d.AreaAngularRadius));
            }
        }

        /// <summary>The <see cref="LightingFxData.CreateDefault"/> colour for light
        /// slot <paramref name="n"/> (1..3) — the parser's baseline when
        /// <c>--lightN-color</c> is omitted.</summary>
        private static uint DefaultLightColor(int n) => n switch
        {
            1 => 0xFFFFFFFFu,   // white key
            2 => 0xFFB0C8FFu,   // cool fill
            3 => 0xFFFFC890u,   // warm rim
            _ => 0xFFFFFFFFu,
        };

        /// <summary>Format a packed 0xAARRGGBB colour as <c>#RRGGBB</c> (opaque) or
        /// <c>#AARRGGBB</c> (when alpha ≠ FF), matching the parser's hex grammar.</summary>
        private static string HexColor(uint c)
            => ((c >> 24) & 0xFF) == 0xFF
                ? "#" + (c & 0x00FFFFFFu).ToString("X6", System.Globalization.CultureInfo.InvariantCulture)
                : "#" + c.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

        private static void AppendFractalParams(List<string> parts, BatchCommandSnapshot snap)
        {
            var p = snap.Parameters;
            if (p == null) return;

            switch (snap.Fractal)
            {
                case FractalType.Multibrot:
                    parts.Add(BatchFlags.MultibrotExp);
                    parts.Add(p.MultibrotExponent.ToString(CultureInfo.InvariantCulture));
                    break;

                case FractalType.Mandelbulb:
                    parts.Add(BatchFlags.BulbPower);
                    parts.Add(Num(p.BulbPower));
                    break;

                case FractalType.LSystem:
                    parts.Add(BatchFlags.LSystemPreset);
                    parts.Add(Token(p.LSystemPresetName));
                    parts.Add(BatchFlags.LSystemDepth);
                    parts.Add(p.LSystemDepth.ToString(CultureInfo.InvariantCulture));
                    break;

                case FractalType.Plasma:
                    parts.Add(BatchFlags.PlasmaRoughness);
                    parts.Add(Num(p.PlasmaRoughness));
                    parts.Add(BatchFlags.PlasmaSeed);
                    parts.Add(p.PlasmaSeed.ToString(CultureInfo.InvariantCulture));
                    break;

                case FractalType.Flame:
                    parts.Add(BatchFlags.FlamePreset);
                    parts.Add(Token(p.FlamePresetName));
                    parts.Add(BatchFlags.FlameIter);
                    parts.Add(p.FlameIterations.ToString(CultureInfo.InvariantCulture));
                    parts.Add(BatchFlags.FlameGamma);
                    parts.Add(Num(p.FlameGamma));
                    parts.Add(BatchFlags.FlameVibrancy);
                    parts.Add(Num(p.FlameVibrancy));
                    break;

                case FractalType.AcidWarp:
                    // Static pattern knobs only — morph / flow / palette-cycle are
                    // animation and have no meaning for a still poster.
                    parts.Add(BatchFlags.AcidPattern);
                    parts.Add(p.AcidWarpPattern.ToString(CultureInfo.InvariantCulture));
                    parts.Add(BatchFlags.AcidFrequency);
                    parts.Add(Num(p.AcidWarpFrequency));
                    parts.Add(BatchFlags.AcidWarpStrength);
                    parts.Add(Num(p.AcidWarpWarpStrength));
                    parts.Add(BatchFlags.AcidSeed);
                    parts.Add(p.AcidWarpSeed.ToString(CultureInfo.InvariantCulture));
                    break;
            }
        }

        /// <summary>Round-trippable invariant formatting for a double so the
        /// parsed value reproduces the live one bit-for-bit.</summary>
        private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Short flag spelling for a view transform, matching the aliases
        /// <see cref="BatchOptions.TryParseViewTransform"/> accepts.</summary>
        private static string ViewTransformFlagValue(ViewTransform vt) => vt switch
        {
            ViewTransform.Reinhard   => "reinhard",
            ViewTransform.AcesFilmic => "aces",
            ViewTransform.AgX        => "agx",
            ViewTransform.Filmic     => "filmic",
            _                        => "none",
        };

        /// <summary>Quote a token when it contains whitespace or characters a
        /// shell would split on; escape embedded double quotes.</summary>
        private static string Token(string value)
        {
            value ??= string.Empty;
            bool needsQuote = value.Length == 0 || value.IndexOfAny(QuoteTriggers) >= 0;
            if (!needsQuote) return value;
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static readonly char[] QuoteTriggers = { ' ', '\t', '"', '\'', '<', '>', '&', '|', '(', ')' };
    }
}
