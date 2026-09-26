// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/IVideoZoomController.cs
//
// Shell-neutral surface for the Video Zoom feature: a smooth animated zoom
// from the classic view to a user-supplied target (or the reverse), with
// optional MP4 / lossless-PNG capture, plus an auto "video slideshow" that
// cycles random regions + themes. The Avalonia shell builds a
// VideoZoomRequest from the ported VideoDialog and drives this controller;
// the concrete implementation lives in the main project (FractalRenderHost
// partial) where it can reach the calculator + renderer internals.
//
// Recording (#946) is not part of this contract: when the request's Record
// flag is set the shell runs the instant-record capture
// (ILiveRecordingController) alongside the run and stops it on Stopped, so
// every video mode shares one recorder and one Save Recording prompt.

using System;

namespace FracturingFog.Render
{
    /// <summary>
    /// Everything the VideoDialog collects, flattened into a transport POCO.
    /// Target coordinates carry the full quad-precision limb set so deep-zoom
    /// targets (≥ 1e15) land on the correct pixel.
    /// </summary>
    public sealed class VideoZoomRequest
    {
        // ── Target (single-shot) ──────────────────────────────────────────
        public double TargetCXHi { get; set; }
        public double TargetCXLo { get; set; }
        public double TargetCX2 { get; set; }
        public double TargetCX3 { get; set; }
        public double TargetCYHi { get; set; }
        public double TargetCYLo { get; set; }
        public double TargetCY2 { get; set; }
        public double TargetCY3 { get; set; }
        public double TargetZoom { get; set; }

        /// <summary>Authored iteration count of the picked region (0 = manual
        /// entry → fall back to the quality preset's auto-computed count).</summary>
        public int TargetIterations { get; set; }

        /// <summary>Authored Quality preset name for the picked region
        /// (null/empty = no region → engine picks tier from target zoom).
        /// Honoured by reverse-zoom and slideshow legs so the played-back
        /// zoom uses the same quality tier the region was authored at,
        /// rather than the lower tier implied by raw zoom magnitude.</summary>
        public string? TargetQualityPresetName { get; set; }

        /// <summary>Name of the region the user picked in the VideoDialog
        /// (single-shot only). Used by the engine to push
        /// <c>FractalRenderHost.RegionName</c> so the watermark top line
        /// follows the target, instead of carrying the stale name from
        /// whatever was on screen when the dialog opened. Null/empty = leave
        /// RegionName untouched.</summary>
        public string? TargetRegionName { get; set; }

        /// <summary>Total animation duration in seconds (single-shot), or the
        /// per-leg duration override for a slideshow (see
        /// <see cref="SlideshowSecondsOverride"/>).</summary>
        public double Seconds { get; set; } = 8.0;

        // ── Mode flags ────────────────────────────────────────────────────

        /// <summary>True → launch the auto slideshow instead of a single zoom.</summary>
        public bool IsSlideshow { get; set; }

        /// <summary>Slideshow only — hold the log-zoom rate constant across
        /// regions, scaling per-leg duration by depth.</summary>
        public bool IsConstantRate { get; set; }

        /// <summary>Per-leg duration override for the slideshow; null = engine
        /// default. Ignored for single-shot.</summary>
        public double? SlideshowSecondsOverride { get; set; }

        /// <summary>Start at the target and animate back to the classic view
        /// (instead of zooming from classic into the target).</summary>
        public bool IsReverse { get; set; }

        /// <summary>Forward single-shot only (#788 slice A): begin the zoom at the
        /// current on-screen view (its centre + zoom) instead of the classic full
        /// view, then animate to the target. Ignored for reverse (which already
        /// starts at the target) and for the slideshow.</summary>
        public bool StartFromCurrentView { get; set; }

        /// <summary>Forward single-shot only (#788 slice B): name of a saved region
        /// to begin the zoom from (its centre + zoom become the start). Takes
        /// precedence over <see cref="StartFromCurrentView"/>. Honoured only when
        /// the start region's fractal type matches the target's; otherwise the
        /// engine falls back to the classic view. Null/empty = not used.</summary>
        public string? StartRegionName { get; set; }

        // ── Recording ─────────────────────────────────────────────────────

        /// <summary>Record this run (#946). Shell-side only: the shell starts the
        /// instant-record capture before launching the run and stops it when
        /// the controller raises <see cref="IVideoZoomController.Stopped"/>,
        /// then shows the Save Recording prompt. Applies to single-shot zooms,
        /// the video slideshow and travel alike.</summary>
        public bool Record { get; set; }

        // ── Smoothing ─────────────────────────────────────────────────────

        /// <summary>Temporal (TAA-lite) blend strength, 0..100 %.</summary>
        public int TaaSmoothing { get; set; } = 55;

        /// <summary>Band-edge dither enable.</summary>
        public bool BandDither { get; set; }

        /// <summary>Band-dither magnitude, 0..100 %.</summary>
        public int BandDitherStrength { get; set; } = 25;

        /// <summary>Slideshow only — when true, each region's embedded watermark
        /// (if any) overrides the user's active watermark for its leg. Mirrors
        /// the slideshow engine's <c>UseRegionWatermark</c> behaviour.</summary>
        public bool UseRegionWatermark { get; set; }

        /// <summary>Cycle the colour palette through several themes during the
        /// zoom, cross-fading via the BlendedColorMap so the zoom keeps
        /// advancing across the swap. Honoured by both single-shot and
        /// slideshow legs — false disables the in-leg theme rotation entirely.</summary>
        public bool ThemeFadeEnabled { get; set; }

        /// <summary>Themes shown across the zoom (single-shot) or per slideshow
        /// leg when <see cref="ThemeFadeEnabled"/> is true. Swap timings are
        /// spaced uniformly: schedule fires at t = k / ThemesPerLeg for
        /// k = 1..N-1. Clamped to [1, 12]; values ≤ 1 disable the schedule.</summary>
        public int ThemesPerLeg { get; set; } = 3;

        /// <summary>Adaptive iteration-cap mode used during playback / record.
        /// Off = no cap (full quality, may drop frames on heavy regions /
        /// modest HW). Global = per-frame adaptive multiplier (existing
        /// behaviour). PerTile = per-tile cap (Phase 1 routes to Global at
        /// runtime; Phase 2 implements the real per-tile pass).</summary>
        public FracturingFog.Models.VideoIterCapMode IterCapMode { get; set; }
            = FracturingFog.Models.VideoIterCapMode.Global;

        // ── Animation hooks (Animation Roadmap Phase 5) ───────────────────
        // Opt-in animation support for the video slideshow. Default off ⇒ the
        // proven video path is byte-for-byte unchanged. When on, each leg
        // resolves an animation (region-attached or random type-compatible)
        // and ticks it per frame; TAA reprojection + the leg-locked histogram
        // CDF are disabled for animated legs since both assume only pan/zoom
        // moves between frames.

        /// <summary>Enable per-leg animation on the video slideshow.</summary>
        public bool EnableAnimations { get; set; }

        /// <summary>Whitelist of animation names (null/empty = all eligible).</summary>
        public System.Collections.Generic.IReadOnlyList<string>? IncludedAnimations { get; set; }

        /// <summary>Tag filter for animations (null/empty = no tag filter).</summary>
        public System.Collections.Generic.IReadOnlyList<string>? FilterAnimations { get; set; }

        /// <summary>Ignore each region's attached animation and draw a random
        /// type-compatible library animation instead.</summary>
        public bool RandomizeAnimationsByFractalType { get; set; }

        /// <summary>When animations are enabled (<see cref="EnableAnimations"/>)
        /// and a zoomable-2D leg carries a natural complex constant (Julia c,
        /// Phoenix p, Glynn c) that no authored animation already drives,
        /// synthesise a gentle default constant-path drift for the leg (#92 /
        /// P2) instead of a plain point-zoom. Default on; turn off to keep
        /// un-animated constant legs as static point-zooms.</summary>
        public bool AutoConstantDrift { get; set; } = true;

        /// <summary>Per-leg variance of the default constant drift (#801). When
        /// on, each leg begins at a small bounded random offset from the authored
        /// constant instead of on it (the leg pre-render tracks the offset, so no
        /// first-frame jump). Requires <see cref="AutoConstantDrift"/>; default
        /// off ⇒ every leg starts on the authored constant (P2 behaviour).</summary>
        public bool VaryConstantStart { get; set; }

        /// <summary>Per-leg variance of the default constant-drift speed (#801).
        /// When on, each leg's constant travels at a randomised speed within a
        /// tasteful band. Requires <see cref="AutoConstantDrift"/>; default off ⇒
        /// exactly one traversal per leg (P2 behaviour).</summary>
        public bool VaryConstantSpeed { get; set; }

        /// <summary>Ken-Burns motion on non-spatial static-hold legs (#806).
        /// When on, a hold leg (Plasma, Flame, DLA, Logistic, …) slowly pans +
        /// zooms the already-rendered frame in image space (no fractal recompute)
        /// instead of sitting still. Default off ⇒ the P4 static hold.</summary>
        public bool KenBurnsOnHold { get; set; }

        /// <summary>Mid-leg param sweep on the sweepable non-spatial hold families
        /// (#806): Logistic pans its r-window, AcidWarp morphs its flow. These
        /// re-render per frame (cheap families only). Takes precedence over
        /// <see cref="KenBurnsOnHold"/> for those families; other hold families
        /// are unaffected. Default off ⇒ static hold / Ken-Burns.</summary>
        public bool SweepParamsOnHold { get; set; }

        /// <summary>#954 — how the run moves. Auto keeps the per-family default
        /// (single-shot: plane zoom / 3D camera dolly / hold for non-spatial;
        /// slideshow: its existing leg routing). Explicit modes are adapted to the
        /// family by VideoMotionPlan.Resolve.</summary>
        public FracturingFog.Models.VideoMotionMode Motion { get; set; }

        /// <summary>#954 — raymarched 3D only: swing the camera azimuth this many
        /// degrees over the zoom / each camera leg. 0 = no orbit.</summary>
        public double OrbitDegrees { get; set; }

        // ── Region / theme restrictions (video slideshow) ─────────────────
        // Mirror the image slideshow's include/filter sets so a saved Video
        // preset that pins one region + one theme actually plays just that,
        // instead of the engine cycling the whole library (#45). Null/empty =
        // no restriction (full pool). A restriction that matches zero regions
        // is authoritative — the slideshow reports empty rather than falling
        // back to the unfiltered universe.

        /// <summary>Whitelist of region names (null/empty = all eligible).</summary>
        public System.Collections.Generic.IReadOnlyList<string>? IncludedRegions { get; set; }

        /// <summary>Whitelist of colour-theme names (null/empty = all).</summary>
        public System.Collections.Generic.IReadOnlyList<string>? IncludedColorThemes { get; set; }

        /// <summary>Fractal-type filter by enum name (null/empty = no filter).
        /// The video pool is Mandelbrot-only, so this only ever narrows to
        /// nothing when Mandelbrot is excluded.</summary>
        public System.Collections.Generic.IReadOnlyList<string>? FilterFractalTypes { get; set; }

        /// <summary>Quality-preset filter by name (null/empty = no filter).</summary>
        public System.Collections.Generic.IReadOnlyList<string>? FilterQualityPresets { get; set; }
    }

    /// <summary>
    /// Drives the Video Zoom animation + auto slideshow. Implemented in the
    /// main project so it can touch the calculator's recolor / histogram /
    /// reprojection internals and the renderer upload pipeline directly.
    /// </summary>
    public interface IVideoZoomController
    {
        /// <summary>True while a single-shot zoom OR the slideshow is running.</summary>
        bool IsRunning { get; }

        /// <summary>True specifically while the auto video slideshow is running
        /// (so the shell can show the VCR transport).</summary>
        bool IsSlideshowRunning { get; }

        /// <summary>Begin a single-shot zoom to the request's target (or the
        /// reverse). Starts recording first when the save flags are set.</summary>
        void StartVideo(VideoZoomRequest request);

        /// <summary>Begin the auto video slideshow (random region + theme legs,
        /// cross-faded, looping until stopped).</summary>
        void StartSlideshow(VideoZoomRequest request);

        /// <summary>#789 slice C — a continuous video "travel": fly through the
        /// planned <paramref name="regionNames"/> in order, one region→region
        /// zoom leg at a time (the far/deep dolly handles the motion between
        /// stops). Plays the journey once, then stops. All regions must share a
        /// video-zoomable fractal type (the CoursePlanner guarantees one type).</summary>
        void StartVideoTravel(System.Collections.Generic.IReadOnlyList<string> regionNames, double secondsPerLeg);

        /// <summary>Stop the running zoom / slideshow.</summary>
        void Stop();

        /// <summary>Slideshow only — abort the current leg and advance to the
        /// next without ending the slideshow.</summary>
        void SkipLeg();

        /// <summary>Status-bar updates ("Video zoom → …", per-leg labels, etc.).</summary>
        event EventHandler<string>? StatusChanged;

        /// <summary>Raised on the UI thread when the run (single-shot or
        /// slideshow) has fully stopped, so the shell can reset button text
        /// and hide the VCR.</summary>
        event EventHandler? Stopped;

        /// <summary>Optional adaptive-sweep schedule used by the auto video
        /// slideshow. Null disables the per-leg ramp.</summary>
        global::FracturingFog.Models.AdaptiveSweepConfig? VideoSweepConfig { get; set; }

        /// <summary>Callback invoked with the current Adaptive value as the
        /// per-leg ramp advances. Shell marshals to the UI thread.</summary>
        Action<int>? VideoAdaptiveValueSink { get; set; }
    }
}
