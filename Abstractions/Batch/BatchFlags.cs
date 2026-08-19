// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Batch/BatchFlags.cs
// #362 (slice of #64) — single source of truth for the --batch flag grammar.
//
// Both the PARSER (BatchOptions.TryParse) and the EMITTER
// (Cli.BatchCommandBuilder) reference these constants, so a flag can never be
// spelled one way on one side and another way on the other. Adding or renaming
// a shared flag is a single edit here.
//
// Only the flags the command builder can emit are consts here today; the
// remaining video / slideshow / scene / remote flags still live as literals in
// the parser (they are not part of the 2D poster builder yet — #363).

namespace FracturingFog.Batch
{
    /// <summary>Canonical (primary) spellings of the batch CLI flags shared
    /// between the parser and the command builder. Values are lower-case so the
    /// parser's <c>ToLowerInvariant</c> switch can match them directly.</summary>
    public static class BatchFlags
    {
        public const string Fractal        = "--fractal";
        public const string Region         = "--region";
        public const string X              = "--x";
        public const string Y              = "--y";
        public const string Zoom           = "--zoom";
        public const string Iter           = "--iter";
        public const string Theme          = "--theme";
        public const string Quality        = "--quality";
        public const string Width          = "--width";
        public const string Height         = "--height";
        public const string Out            = "--out";

        public const string Brightness     = "--brightness";
        public const string Contrast       = "--contrast";
        public const string Adaptive       = "--adaptive";
        public const string InteriorAlpha  = "--interior-alpha";

        // Output-stage view transform / tonemap (roadmap S2, #389).
        public const string ViewTransform  = "--view-transform";
        public const string Exposure       = "--exposure";

        public const string MultibrotExp   = "--multibrot-exp";
        public const string BulbPower      = "--bulb-power";
        public const string LSystemPreset  = "--lsystem-preset";
        public const string LSystemDepth   = "--lsystem-depth";
        public const string PlasmaRoughness = "--plasma-roughness";
        public const string PlasmaSeed     = "--plasma-seed";
        public const string FlamePreset    = "--flame-preset";
        public const string FlameIter      = "--flame-iter";
        public const string FlameGamma     = "--flame-gamma";
        public const string FlameVibrancy  = "--flame-vibrancy";

        // Acid Warp static pattern knobs (#363). Time-varying morph/flow/cycle
        // are animation-only and have no still-poster flag.
        public const string AcidPattern      = "--acid-pattern";
        public const string AcidFrequency    = "--acid-frequency";
        public const string AcidWarpStrength = "--acid-warp-strength";
        public const string AcidSeed         = "--acid-seed";

        // Domain-warp post-fx distortion (#363), applies to any fractal.
        public const string DomainWarp          = "--domain-warp";
        public const string DomainWarpStrength  = "--domain-warp-strength";
        public const string DomainWarpFrequency = "--domain-warp-frequency";

        // 2D heightfield relief — Tier-1 core knobs (#363). Any relief flag
        // implies relief on. Raymarch camera + isolate knobs are a follow-up.
        public const string Relief               = "--relief";
        public const string ReliefHeight         = "--relief-height";
        public const string ReliefStrength       = "--relief-strength";
        public const string ReliefLightAzimuth   = "--relief-light-azimuth";
        public const string ReliefLightElevation = "--relief-light-elevation";
        public const string ReliefShadow         = "--relief-shadow";
        public const string ReliefRaymarch       = "--relief-raymarch";
        public const string ReliefAbsolute       = "--relief-absolute";

        // Relief raymarch camera (#363 follow-up). Only meaningful with
        // --relief-raymarch; all imply relief on.
        public const string ReliefCameraAzimuth   = "--relief-camera-azimuth";
        public const string ReliefCameraElevation = "--relief-camera-elevation";
        public const string ReliefCameraFov       = "--relief-camera-fov";
        public const string ReliefCameraZoom      = "--relief-camera-zoom";
        public const string ReliefCameraOrtho     = "--relief-camera-ortho";

        // Depth of field on the relief raymarch camera (roadmap S3, #389). Any
        // DOF flag implies --relief-raymarch (DOF is perspective-camera only).
        public const string DofAperture           = "--dof-aperture";
        public const string DofFocus              = "--dof-focus";

        // Relief isolate masking (#363 follow-up). --relief-isolate turns it on;
        // sub-knobs imply it on.
        public const string ReliefIsolate          = "--relief-isolate";
        public const string ReliefIsolateNoDetail  = "--relief-isolate-no-detail";
        public const string ReliefIsolateThreshold = "--relief-isolate-threshold";
        public const string ReliefIsolateByColor   = "--relief-isolate-by-color";
        public const string ReliefIsolateColors    = "--relief-isolate-colors";
        public const string ReliefIsolateTolerance = "--relief-isolate-tolerance";
    }

    /// <summary>Default values shared between the parser (what it initialises an
    /// unset option to) and the builder (what it treats as "omit this flag").
    /// Keeping them here means the builder can never disagree with the parser
    /// about, say, whether "Standard" quality is the default.</summary>
    public static class BatchDefaults
    {
        public const string ThemeName   = "HSV";
        public const string QualityName = "Standard";
        public const int    Width       = 1920;
        public const int    Height      = 1080;

        // View-state defaults used to seed a builder snapshot when the caller
        // supplies nothing. Mirror FractalViewState's own defaults.
        public const double CenterX = -0.5;
        public const double CenterY = 0.0;
        public const double Zoom    = 0.13;
    }
}
