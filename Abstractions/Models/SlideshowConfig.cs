// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SlideshowConfig.cs
//
// Superset configuration for the unified Slideshow / Video-Slideshow dialog.
// Wraps the legacy timing DTO (SlideshowSettings) and adds the new
// per-config knobs: type (Image vs Video), region/theme/fractal-type/quality
// filter lists, adaptive-sweep block, post-FX block, audio toggle, and an
// embedded VideoSettings sub-DTO that is populated only when Type=Video.
//
// One config is the "active" config; the library persists a keyed collection
// so the user can author, name, save, delete, import, and export slideshow
// presets. The legacy single-file settings (%APPDATA%\FracturingFog\
// slideshow-settings.json) is migrated into a "Default" entry on first load.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FracturingFog.Models
{
    /// <summary>Whether the active config drives an image slideshow
    /// (cross-fade between regions/themes) or a video slideshow
    /// (animated zoom legs).</summary>
    public enum SlideshowType
    {
        /// <summary>Classic slideshow — region + theme cross-fade.</summary>
        Image = 0,
        /// <summary>Video Zoom slideshow — animated zoom per leg.</summary>
        Video = 1,
        /// <summary>Animation slideshow — each leg picks a region + theme +
        /// animation triple and plays the animation live during the leg's
        /// hold. Falls back to a static leg when no library animation is
        /// compatible with the chosen region (Animation Roadmap Phase 4).</summary>
        Animation = 2,
    }

    /// <summary>Per-frame iteration-cap policy applied during video record /
    /// slideshow playback. Trades image quality for sustained frame rate when
    /// the CPU calc path can't hit the per-frame budget at full iteration
    /// count (e.g. cardioid neighbourhoods, deep Julia stalks on modest HW).
    /// User picks in Video Settings dialog; default Global preserves the
    /// prior auto-adaptive behaviour.</summary>
    public enum VideoIterCapMode
    {
        /// <summary>No cap. Calculator runs at full maxIterations for every
        /// frame. Best image quality, can drop frames on heavy regions /
        /// modest hardware. Recommended for strong HW.</summary>
        Off = 0,
        /// <summary>Global per-frame adaptive multiplier (existing
        /// behaviour). Frame elapsed &gt; 1.5× budget ratchets the cap down;
        /// frame elapsed &lt; 0.9× budget ratchets back up. Single multiplier
        /// applied to the whole frame.</summary>
        Global = 1,
        /// <summary>Per-tile adaptive cap (subdivides frame into a tile grid
        /// and caps each tile independently from prior-frame tile elapsed).
        /// Phase 1: routes to Global at runtime with a one-time console
        /// warning — real per-tile pass lands in Phase 2.</summary>
        PerTile = 2,
    }

    /// <summary>Direction model for the Adaptive slider sweep during a leg.
    /// Mirrors the FloatingMenu sweep mode so a config can replay it.</summary>
    public enum AdaptiveSweepMode
    {
        Forward = 0,
        Reverse = 1,
        PingPong = 2,
    }

    /// <summary>Adaptive-sweep block: drives the Adaptive slider over a leg
    /// from <see cref="Start"/> to <see cref="End"/> using <see cref="Mode"/>.
    /// Disabled when <see cref="Enabled"/> is false.</summary>
    public sealed class AdaptiveSweepConfig
    {
        public bool Enabled { get; set; }
        public int Start { get; set; } = 0;
        public int End { get; set; } = 100;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AdaptiveSweepMode Mode { get; set; } = AdaptiveSweepMode.Forward;
        public bool Loop { get; set; }

        /// <summary>When the parent <see cref="SlideshowConfig.AudioReactive"/>
        /// is on and the slideshow has a live BeatSource, the sweep cycle
        /// duration becomes <c>BeatFraction × beatPeriodMs</c>. Mirrors the
        /// <c>FadeBeatFraction</c> knob in AudioSettings: 1.0 = one beat per
        /// full sweep, 0.5 = half-beat, 4.0 = four-beat cycle. Clamped to
        /// [0.0625, 32] at consumer-time. Ignored when audio-reactive is off
        /// (the wall-clock <c>legMs</c> envelope is used instead).</summary>
        public double BeatFraction { get; set; } = 1.0;
    }

    /// <summary>Post-FX block snapshot. Concrete fields kept loose for now
    /// (Phase 2 will populate the full set once the dialog binds them);
    /// captured as a name + intensity grab-bag so the schema can evolve
    /// without rewriting the file on every UI tweak.</summary>
    public sealed class PostFxConfig
    {
        public bool Enabled { get; set; }
        public Dictionary<string, double> Values { get; set; } = new();
    }

    /// <summary>Video Zoom settings carried inside a Video-type config.
    /// Populated when the user clicks "Video Settings…" inside the unified
    /// dialog and accepts the embedded VideoDialog. Mirrors the relevant
    /// fields of the legacy WinForms VideoDialog so a future single-shot
    /// Avalonia VideoDialog can deserialize the same blob.</summary>
    public sealed class VideoSettingsConfig
    {
        /// <summary>Speed preset name: Slow | Medium | Fast | Custom.</summary>
        public string SpeedPreset { get; set; } = "Medium";

        /// <summary>Custom seconds value used only when
        /// <see cref="SpeedPreset"/>=Custom (else derived from the preset).</summary>
        public double CustomSeconds { get; set; } = 30.0;

        /// <summary>Effective per-leg duration in seconds. Resolved from
        /// preset/custom at OK time so consumers don't repeat the lookup.</summary>
        public double SecondsPerLeg { get; set; } = 30.0;

        /// <summary>Inter-leg pause in milliseconds (slideshow only).</summary>
        public int PauseBetweenMs { get; set; } = 7_000;

        /// <summary>Hold log-zoom rate constant across regions and scale
        /// per-leg duration with region depth.</summary>
        public bool ConstantRate { get; set; }

        /// <summary>Reverse zoom: start at the target and zoom out to classic.</summary>
        public bool Reverse { get; set; }

        /// <summary>Record an MP4 alongside playback (single-shot only).</summary>
        public bool SaveVideo { get; set; }

        /// <summary>Record a lossless PNG sequence (single-shot only).</summary>
        public bool SaveLossless { get; set; }

        /// <summary>Post-capture re-encode choice for the PNG sequence:
        /// None | LosslessH264Mp4 | Ffv1Mkv | HighQualityH264Mp4.</summary>
        public string LosslessEncode { get; set; } = "None";

        /// <summary>Temporal smoothing strength 0..100. Maps to TAA blend weight.</summary>
        public int TaaSmoothing { get; set; } = 55;

        /// <summary>Band-edge dither enable.</summary>
        public bool BandDither { get; set; }

        /// <summary>Band-dither strength 0..100.</summary>
        public int BandDitherStrength { get; set; } = 25;

        /// <summary>Enable in-leg color-theme fade (Phase 3 feature).</summary>
        public bool ThemeFadeEnabled { get; set; } = true;

        /// <summary>Number of themes shown per leg (drives the fade schedule).</summary>
        public int ThemesPerLeg { get; set; } = 3;

        /// <summary>Adaptive iteration-cap policy applied per frame during
        /// playback / record. Default Global = prior auto-adaptive behaviour.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public VideoIterCapMode IterCapMode { get; set; } = VideoIterCapMode.Global;

        /// <summary>Loose bag for forward-compat fields not yet promoted.</summary>
        public Dictionary<string, string> Extras { get; set; } = new();
    }

    /// <summary>One slideshow preset. Identified by <see cref="Name"/>;
    /// stored keyed in <see cref="SlideshowConfigLibrary"/>.</summary>
    public sealed class SlideshowConfig
    {
        /// <summary>User-visible preset name. Doubles as the library key.</summary>
        public string Name { get; set; } = "Default";

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SlideshowType Type { get; set; } = SlideshowType.Image;

        /// <summary>Timing + fade DTO (legacy SlideshowSettings shape).
        /// Always present so audio-OFF playback has values to drive timing.</summary>
        public SlideshowSettings Timing { get; set; } = new();

        /// <summary>Master audio-reactive toggle for this preset.
        /// AudioSettings (sensitivity, BPM, EQ, etc) live in their own file.</summary>
        public bool AudioReactive { get; set; }

        /// <summary>Whitelist of region names. Null/empty = all eligible.</summary>
        public List<string> IncludedRegions { get; set; } = new();

        /// <summary>Whitelist of color theme names. Null/empty = all eligible.</summary>
        public List<string> IncludedColorThemes { get; set; } = new();

        /// <summary>Restrict regions by fractal type. Empty = no fractal-type filter.
        /// Stored as enum names so the JSON stays readable and survives enum reordering.</summary>
        public List<string> FilterFractalTypes { get; set; } = new();

        /// <summary>Restrict regions by quality-preset name. Empty = no quality filter.</summary>
        public List<string> FilterQualityPresets { get; set; } = new();

        /// <summary>Whitelist of animation names. Null/empty = every library
        /// animation is eligible. Peer of <see cref="IncludedColorThemes"/>;
        /// consulted only when <see cref="Type"/>=Animation (Animation Roadmap
        /// Phase 4).</summary>
        public List<string> IncludedAnimations { get; set; } = new();

        /// <summary>Restrict animations by tag. Empty = no tag filter. An
        /// animation survives when it carries at least one tag in this list.
        /// Peer of <see cref="FilterFractalTypes"/> (Animation Roadmap
        /// Phase 4).</summary>
        public List<string> FilterAnimations { get; set; } = new();

        /// <summary>When true, the Animation slideshow ignores any animation
        /// the picked region carries and instead draws a random library
        /// animation whose <c>TargetFractalTypes</c> include the region's
        /// fractal type. When false, a region's attached animation wins if it
        /// has one (Animation Roadmap Phase 4).</summary>
        public bool RandomizeAnimationsByFractalType { get; set; }

        /// <summary>Opt-in animation support for the Image and Video slideshow
        /// types. When true, each leg resolves an animation the same way the
        /// Animation type does (region-attached or a random type-compatible
        /// library animation) and plays it during the leg. When false — the
        /// default — Image / Video behave exactly as before (no animation).
        /// Ignored for <see cref="Type"/>=Animation, which always animates
        /// (Animation Roadmap Phase 5).</summary>
        public bool EnableAnimations { get; set; }

        /// <summary>Adaptive-sweep block. Drives the Adaptive slider per leg.</summary>
        public AdaptiveSweepConfig AdaptiveSweep { get; set; } = new();

        /// <summary>Post-FX block snapshot.</summary>
        public PostFxConfig PostFx { get; set; } = new();

        /// <summary>Video Zoom settings, used only when <see cref="Type"/>=Video.
        /// Null otherwise so the JSON stays tidy.</summary>
        public VideoSettingsConfig? Video { get; set; }

        // Clone via a JSON round-trip rather than member-wise copy. A
        // hand-rolled clone silently dropped any newly-added field (RandomSeed
        // did exactly that until this was fixed), because the copy list has to
        // be kept in lockstep with the properties by hand. Serializing to the
        // same JSON shape the config persists in guarantees every persisted
        // property is carried, so new fields can't vanish on save/cancel.
        private static readonly JsonSerializerOptions CloneOpts = new();

        /// <summary>Deep clone — used by the VM working copy and by save/cancel
        /// round-trips so an in-flight edit never mutates the on-disk config.</summary>
        public SlideshowConfig Clone()
        {
            var clone = JsonSerializer.Deserialize<SlideshowConfig>(
                            JsonSerializer.Serialize(this, CloneOpts), CloneOpts)
                        ?? new SlideshowConfig();

            // Normalise nullable collections / sub-blocks to non-null (parity
            // with the old member-wise clone) so consumers enumerate without
            // null guards. Data is already copied above; this is null-safety
            // only, not a per-field maintenance list.
            clone.Timing ??= new();
            clone.IncludedRegions ??= new();
            clone.IncludedColorThemes ??= new();
            clone.FilterFractalTypes ??= new();
            clone.FilterQualityPresets ??= new();
            clone.IncludedAnimations ??= new();
            clone.FilterAnimations ??= new();
            clone.AdaptiveSweep ??= new();
            clone.PostFx ??= new();
            return clone;
        }

        /// <summary>Factory: build a default config from the legacy
        /// SlideshowSettings shape (used during migration).</summary>
        public static SlideshowConfig FromLegacy(string name, SlideshowSettings legacy, bool audioReactive)
        {
            return new SlideshowConfig
            {
                Name = name,
                Type = SlideshowType.Image,
                Timing = legacy ?? new SlideshowSettings(),
                AudioReactive = audioReactive,
            };
        }
    }
}
