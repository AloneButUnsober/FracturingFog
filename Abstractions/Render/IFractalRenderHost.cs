// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/IFractalRenderHost.cs
//
// Shell-neutral surface for the renderer + calculator orchestration that
// MainForm currently holds inline (TriggerCalculation, ApplyViewState,
// UploadProcessedBuffer, BlendOverlays, pan-stop debounce). The Avalonia
// shell talks to this interface, not to IFractalRenderer directly, so the
// 11-calculator factory + dispatcher stays in one place.
//
// Concrete impl lives in the main project (it depends on every
// FracturingFog.Calculators type and on the Vortice D3D11/D3D12 renderer
// implementations). The WinExe constructs the concrete and passes the
// IFractalRenderHost reference to UI.Avalonia VMs.

using System;
using FracturingFog.ViewState;

namespace FracturingFog.Render
{
    /// <summary>Per-frame info pushed up to the shell after every successful
    /// calculation.</summary>
    public readonly record struct RenderFrameInfo(
        double CenterX,
        double CenterY,
        double Zoom,
        int Iterations,
        long ElapsedMs,
        int Width,
        int Height,
        bool HighPrecisionActive,
        bool IterLocked,
        FractalType FractalType,
        string? PrecisionLabel = null,
        // Estimated deepest zoom (log₁₀) at which THIS centre still resolves
        // detail — set from the Mandelbrot reference orbit's δ-amplification.
        // +∞ = centre stays bounded (unbounded depth). The shell warns when the
        // live zoom exceeds it so a collapsed (flat) deep frame reads as a
        // location depth limit, not broken navigation. Other fractal paths
        // leave it +∞ (no notice).
        double MaxUsefulZoomLog10 = double.PositiveInfinity);

    /// <summary>
    /// Orchestrates the renderer + per-fractal-type calculators. The input
    /// controller calls <see cref="Trigger"/> / <see cref="TriggerFast"/>
    /// after every successful pan/zoom; the shell calls <see cref="Resize"/>
    /// when the render panel changes size; the host raises
    /// <see cref="FrameCompleted"/> after each calculation lands so the
    /// shell can update status / mini-map / mini-depth panels.
    /// </summary>
    public interface IFractalRenderHost : IDisposable
    {
        /// <summary>The shared view state this host reads. Same instance the
        /// input controller mutates.</summary>
        FractalViewState ViewState { get; }

        /// <summary>Push the current view state into the active calculator
        /// chain. Cheap; safe to call from the UI thread. Doesn't kick a
        /// calculation by itself — use <see cref="Trigger"/> for that.</summary>
        /// <param name="maxIters">0 = auto-derive from QualityPreset + zoom;
        /// positive value overrides the cap (used for the fast-pan path).</param>
        void ApplyView(int maxIters = 0);

        /// <summary>Cancel any in-flight calculation and schedule a fresh
        /// full-quality render on a background thread. Posts the result
        /// back to the renderer via the host's bound IFractalRenderer.</summary>
        void Trigger(bool progressive = false);

        /// <summary>Fire a capped-iter render for live-drag responsiveness.
        /// The shell's pan-stop debounce timer typically fires a follow-up
        /// <see cref="Trigger"/> once motion stops.</summary>
        void TriggerFast();

        /// <summary>Notify the host that the render panel resized. Discards
        /// the cached prev-frame buffer, resizes every calculator, then
        /// re-applies view state + triggers a render.</summary>
        void Resize(int width, int height);

        /// <summary>Render the most-recent frame again with current post-FX
        /// (brightness / contrast / adaptive / in-set / grid). No
        /// calculation — used by the slider live-tune path.</summary>
        void RepaintWithPostFx();

        /// <summary>Re-apply the Adaptive histogram-equalization pass at the
        /// current strength using cached escape buffers, then re-upload. No
        /// recompute — used by the live Adaptive slider so it updates with
        /// the same latency as Brightness/Contrast.</summary>
        void RepaintWithAdaptive();

        /// <summary>
        /// Copy the currently-displayed BGRA frame (the last buffer uploaded to
        /// the renderer). Returns an empty array with zeroed dimensions when no
        /// frame has been presented yet. Used by the slideshow cross-fade as the
        /// outgoing image to blend from.
        /// </summary>
        uint[] SnapshotFrame(out int width, out int height);

        /// <summary>
        /// Upload an externally-prepared BGRA buffer straight to the renderer and
        /// present it — no calculation, no post-FX, no overlay. Used by the
        /// slideshow cross-fade to push each blended transition frame. The buffer
        /// becomes the new "last uploaded" frame.
        /// </summary>
        void PresentBuffer(uint[] bgra, int width, int height);

        /// <summary>Render the most-recently-completed frame as a live ASCII /
        /// text-art cell grid (#227), consuming the real IColorMap-coloured
        /// buffer + smooth field. Returns null before the first frame. Thread-safe
        /// (reads the frame buffers under the host's upload gate); the returned
        /// grid is a fresh allocation the caller owns.</summary>
        /// <param name="columns">Target character columns; rows are derived from
        /// the frame aspect and <paramref name="cellAspect"/>.</param>
        /// <param name="cellAspect">Character cell height ÷ width of the display
        /// font, so the art keeps its shape.</param>
        /// <param name="color">True for per-cell truecolor; false for monochrome
        /// (glyph ramp only).</param>
        /// <param name="invert">Invert the glyph ramp.</param>
        /// <param name="fineRamp">Use the 70-step ramp instead of the 10-step.</param>
        /// <param name="rampFromColor">Drive the glyph ramp from the post-FX pixel
        /// luminance instead of the raw smooth field, so adaptive/brightness/
        /// contrast modulate glyph density and not only colour.</param>
        /// <param name="fx">ASCII-native effect settings to apply to the cell grid
        /// (#229) — the full FX chain. Null / all-off = none. The host owns the
        /// cross-frame FX state for the stateful effects (rain / particles /
        /// trails).</param>
        AsciiFrame? RenderLastFrameAscii(
            int columns, double cellAspect, bool color, bool invert, bool fineRamp,
            bool rampFromColor = false, FracturingFog.Imaging.AsciiFxSettings? fx = null);

        /// <summary>Record the current frame's ASCII FX animation to a shareable
        /// text container (#230). Re-renders the same last frame <paramref name="frames"/>
        /// times, advancing the FX clock by 1/<paramref name="fps"/> each step so
        /// the animated effects (hue / plasma / rain / breathe / reveals …) play
        /// out, and serialises the sequence. Returns null before the first frame.</summary>
        /// <param name="format">"cast" (asciinema v2), "svg" (animated SVG), or
        /// "ans" (raw ANSI frame sequence).</param>
        /// <returns>The serialised animation text, or null if unavailable.</returns>
        string? RecordAsciiAnimation(
            int columns, double cellAspect, bool invert, bool fineRamp, bool rampFromColor,
            FracturingFog.Imaging.AsciiFxSettings fx, int frames, double fps, string format);

        /// <summary>Like <see cref="RecordAsciiAnimation"/> but returns the raw
        /// per-frame ASCII grids (glyph + colour) instead of a serialised text
        /// container — for the MP4 exporter, which rasterises each grid to pixels
        /// and feeds the ffmpeg pipeline. Null before the first frame.</summary>
        System.Collections.Generic.IReadOnlyList<AsciiFrame>? RecordAsciiFrames(
            int columns, double cellAspect, bool invert, bool fineRamp, bool rampFromColor,
            FracturingFog.Imaging.AsciiFxSettings fx, int frames, double fps);

        /// <summary>Present the current GPU back buffer to the screen.
        /// Safe to call from any thread — the host serialises this with
        /// every other renderer access (UpdateTexture / Resize) so the
        /// underlying D3D11 immediate context is never touched concurrently.
        /// Callers normally do NOT need to invoke this — the host auto-presents
        /// after each successful frame upload and after each resize. It is
        /// exposed mainly so shells that want a periodic redraw (e.g. an
        /// animation slideshow) can drive it explicitly.</summary>
        void Present();

        /// <summary>Raised after each completed calculation. Carries the
        /// info MainForm currently pushes into the status bar.</summary>
        event EventHandler<RenderFrameInfo>? FrameCompleted;

        /// <summary>Raised after EVERY buffer upload to the renderer — not only
        /// full calculations (<see cref="FrameCompleted"/>) but also the post-FX
        /// and adaptive repaints (<see cref="RepaintWithPostFx"/> /
        /// <see cref="RepaintWithAdaptive"/>) that re-upload without recomputing.
        /// A superset of FrameCompleted; consumers that mirror the on-screen
        /// buffer (the live ASCII view) subscribe to this so brightness /
        /// contrast / adaptive-sweep changes update them, not just re-renders.
        /// May fire on a background thread; marshal before touching UI.</summary>
        event EventHandler? FrameBufferChanged;

        /// <summary>Raised when an in-flight calculate gets cancelled (rapid
        /// pan/zoom or animation tick before the prior frame's TAA / final
        /// stage reaches FrameCompleted). Lets the status-bar consumer clear
        /// the lingering "Calculating…" string the matching Trigger posted.
        /// No payload — caller decides whether to restore prior frame info
        /// or fall back to a generic idle string.</summary>
        event EventHandler? RenderCancelled;

        /// <summary>Raised after the GPU texture upload for the most recent
        /// frame finishes — including the cancelled case so the UserBulb
        /// animation timer doesn't get stuck waiting on a render that never
        /// completes.</summary>
        event EventHandler? AnimationFrameUploaded;

        /// <summary>Raised when the host wants the status bar updated with
        /// a string ("Calculating…", "Quality → Ultra (zoom 1.2e22)", etc.).</summary>
        event EventHandler<string>? StatusRequested;

        /// <summary>Perceived luminance of the active colour map's middle band,
        /// in [0, 255]. Sampled by the host across a handful of iteration
        /// depths and Rec.709-weighted. Exposed so overlay controls (grid +
        /// watermark) can pick a contrast-aware ink colour without reaching
        /// into the main-project <c>IColorMap</c> type from here (that would
        /// reverse the existing Abstractions → main dependency direction).
        /// Implementations return 255 (white) when no map is bound yet so
        /// the overlay falls back to black ink.</summary>
        byte OverlayContrastLuma { get; }

        /// <summary>Raised whenever the active colour map is replaced — typically
        /// from a theme pick or a color-theme-editor live preview. Shell-side
        /// overlays subscribe so they can re-read
        /// <see cref="OverlayContrastLuma"/> and invalidate.</summary>
        event EventHandler? ColorMapChanged;

        // ── Overlay state ────────────────────────────────────────────────
        //
        // Grid + watermark are CPU-composited into the BGRA buffer the host
        // hands the renderer (on Windows the swap-chain HWND occludes every
        // Avalonia.Media overlay regardless of XAML Z-order, so an in-tree
        // Avalonia overlay can't render on top of it). The shell sets these
        // flags + label strings; the host blends them into every uploaded
        // frame from now on.

        /// <summary>True to blend the Cartesian grid + axis labels into the
        /// next uploaded frame. Take effect on the next render — the caller
        /// typically follows a toggle with <see cref="RepaintWithPostFx"/>
        /// so the change shows up immediately.</summary>
        bool ShowGrid { get; set; }

        /// <summary>True to blend the region/theme + program-name watermark
        /// into the next uploaded frame.</summary>
        bool ShowWatermark { get; set; }

        /// <summary>True to blend the perf HUD (phase timings + HW summary)
        /// into the top-left of the next uploaded frame. Cheap (~0.1 ms /
        /// frame) — safe during video record. Toggled by the shell.</summary>
        bool ShowPerfHud { get; set; }

        /// <summary>Clear the perf HUD's rolling buffers + reset the
        /// GC-rate baseline so the next capture window starts clean.</summary>
        void ResetPerfStats();

        /// <summary>T3.1: toggle GPU compute on the SP Mandelbrot path.
        /// Setter has no effect when the active renderer is not D3D11 —
        /// caller should re-read after setting to verify.</summary>
        bool UseGpuCompute { get; set; }

        /// <summary>Diagnostic toggle — bypass BLA + SA on the legacy
        /// MandelbrotCalculator HP path. Used to isolate deep-zoom precision
        /// regressions: when on, perturbation runs raw (no BLA skip, no SA
        /// prelude) so a pixelation block can be attributed to the
        /// acceleration path vs the QD math.</summary>
        bool MandelbrotDisableAcceleration { get; set; }

        /// <summary>Diagnostic toggle — bypass SA prelude only (BLA still
        /// applies) on the legacy MandelbrotCalculator HP path.</summary>
        bool MandelbrotDisableSeriesApproximation { get; set; }

        /// <summary>Diagnostic toggle — force legacy single-precision BLA
        /// table (pre-Wave-2.10) instead of the DD-precision merge. Used to
        /// isolate suspected Wave 2.10 regressions at extreme zoom.</summary>
        bool MandelbrotDisableDdBla { get; set; }

        /// <summary>SM-2 A/B toggle — when on, glitched deep-zoom pixels resolve
        /// via the fast rebasing PT path instead of per-pixel QD/OD (≈100×
        /// faster at extreme zoom, matching the QD image). Off = legacy
        /// per-pixel QD/OD fallback.</summary>
        bool MandelbrotAllowPtRebasing { get; set; }

        /// <summary>Region label rendered in the watermark.</summary>
        string? RegionName { get; set; }

        /// <summary>Theme label rendered in the watermark.</summary>
        string? ThemeName { get; set; }

        /// <summary>Optional user-configured watermark that replaces the
        /// default Region/Theme + auto-contrast composition. Null = default
        /// behaviour (today). Set by the shell after applying the precedence
        /// chain (FloatingMenu override → region embedded → master toggle).
        /// Host re-uploads the next frame with the new watermark composited.</summary>
        FracturingFog.Models.WatermarkDef? ActiveWatermark { get; set; }

        /// <summary>Set (or clear with all-null) the rubber-band rectangle
        /// drawn on top of the current frame while the user is right-drag
        /// selecting a zoom region in 2D. The host re-uploads the most-recent
        /// frame with the rect composited on top — no recompute, so the
        /// preview stays smooth during the drag.</summary>
        void SetSelectionBox(int? x, int? y, int? w, int? h);

        /// <summary>Compile the Roslyn-backed UserEquation source. Returns
        /// (true, null) on success; (false, error) when the compiler rejects
        /// it. Used by promoted registered-equation pickers in the shell.</summary>
        (bool ok, string? error) CompileUserEquation(string source);

        /// <summary>Compile the restricted Sandbox-DSL source.</summary>
        (bool ok, string? error) CompileSandbox(string source);

        /// <summary>Compile the 3D UserBulb step source.</summary>
        (bool ok, string? error) CompileUserBulb(string source);
    }
}
