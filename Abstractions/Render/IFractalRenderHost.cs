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
        FractalType FractalType);

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
    }
}
