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
// The save flow (SaveFileDialog for MP4, folder pick + ffmpeg encode for the
// PNG sequence) is shell-specific, so the engine merely raises
// RecordingFinished with the temp artefact paths and the chosen encode mode;
// the shell handles the prompts.

using System;

namespace FracturingFog.Render
{
    /// <summary>Post-capture encode choice for a saved lossless PNG sequence.</summary>
    public enum VideoLosslessEncode
    {
        /// <summary>Keep the PNG sequence only — no video produced.</summary>
        None,
        /// <summary>libx264 CRF 0 → .mp4 (mathematically lossless H.264).</summary>
        LosslessH264Mp4,
        /// <summary>FFV1 v3 → .mkv (true lossless intermediate).</summary>
        Ffv1Mkv,
        /// <summary>libx264 CRF 18 → .mp4 (visually lossless, smaller).</summary>
        HighQualityH264Mp4,
    }

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

        // ── Recording (single-shot only) ──────────────────────────────────
        public bool IsSaveVideo { get; set; }
        public bool IsSaveLossless { get; set; }
        public VideoLosslessEncode LosslessEncode { get; set; } = VideoLosslessEncode.None;

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
    }

    /// <summary>Outcome of a single-shot recording, raised once the zoom ends
    /// so the shell can prompt for a destination. Either artefact path is null
    /// when that recorder was not active (or the run was cancelled).</summary>
    public sealed class VideoRecordingResult
    {
        /// <summary>Temp .mp4 path to move into place, or null.</summary>
        public string? Mp4TempPath { get; init; }

        /// <summary>Temp folder of the PNG sequence to keep/encode, or null.</summary>
        public string? PngFolder { get; init; }

        /// <summary>Encode to apply to the PNG sequence after the user picks a
        /// destination folder.</summary>
        public VideoLosslessEncode Encode { get; init; } = VideoLosslessEncode.None;

        /// <summary>True when the zoom was cancelled/faulted — the shell should
        /// discard temp artefacts instead of prompting to save.</summary>
        public bool Cancelled { get; init; }
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

        /// <summary>Stop the running zoom / slideshow.</summary>
        void Stop();

        /// <summary>Slideshow only — abort the current leg and advance to the
        /// next without ending the slideshow.</summary>
        void SkipLeg();

        /// <summary>Status-bar updates ("Video zoom → …", per-leg labels, etc.).</summary>
        event EventHandler<string>? StatusChanged;

        /// <summary>Raised once a single-shot zoom finishes (or is cancelled)
        /// with recording active. Carries the temp artefact paths for the
        /// shell's save prompt.</summary>
        event EventHandler<VideoRecordingResult>? RecordingFinished;

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
