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
