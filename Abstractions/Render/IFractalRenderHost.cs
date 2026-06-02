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
        string? PrecisionLabel = null);

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

        /// <summary>Region label rendered in the watermark.</summary>
        string? RegionName { get; set; }

        /// <summary>Theme label rendered in the watermark.</summary>
        string? ThemeName { get; set; }

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
