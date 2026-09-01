// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Input/IFractalInputController.cs
//
// Shell-neutral input pipeline. Pan, zoom, double-click recentering,
// 2D + 3D keyboard nav, mouse-wheel zoom with cursor anchor, right-drag
// 3D camera rotation — all on top of the same FractalViewState. The
// concrete FractalInputController owns the precision-aware math (double /
// double-double / quad-double) so callers don't have to.
//
// The controller never triggers rendering directly. After it mutates the
// view state it raises ViewChanged. The renderer host (step C) subscribes
// and re-triggers calculation; the status bar also subscribes for the
// "Quality → Ultra (zoom 1.2e22)" auto-promotion messages.

using System;

namespace FracturingFog.Input
{
    /// <summary>Severity of a status message raised by the input layer.</summary>
    public enum InputStatusKind { Info, Warning }

    /// <summary>One status message from the input layer. Usually a quality
    /// auto-promotion notice or a clamped-coordinate warning.</summary>
    public readonly record struct InputStatusMessage(string Text, InputStatusKind Kind);

    /// <summary>How the upcoming render should be scheduled.</summary>
    public enum RenderHint
    {
        /// <summary>Standard full-quality render. Cancel anything in flight.</summary>
        Full,
        /// <summary>Cap iterations for live drag feedback; full render fires when
        /// motion stops via the host's pan-stop timer.</summary>
        Fast,
    }

    /// <summary>One view change emitted by the input controller. The
    /// renderer host subscribes and decides how to schedule the calculation.</summary>
    public readonly record struct ViewChangedArgs(RenderHint Hint);

    /// <summary>
    /// Translates shell-neutral input events into mutations on a
    /// <see cref="ViewState.FractalViewState"/>. Raises
    /// <see cref="ViewChanged"/> after every successful mutation so the
    /// renderer host can re-trigger calculation.
    /// </summary>
    public interface IFractalInputController
    {
        /// <summary>The view state the controller mutates. Same instance the
        /// renderer host reads. Pass-by-reference (sealed class).</summary>
        ViewState.FractalViewState ViewState { get; }

        // ── Pointer ───────────────────────────────────────────────────────────
        void OnPointerDown(PointerInput e);
        void OnPointerMove(PointerInput e);
        void OnPointerUp(PointerInput e);
        void OnPointerDoubleClick(PointerInput e);
        void OnWheel(WheelInput e);

        /// <summary>S3 click-to-focus (#400) — optional host hook. When set and the
        /// user Alt+double-clicks the render, the controller asks the host to set the
        /// relief DOF focal plane from the clicked pixel's depth instead of
        /// recentering. The handler returns true when it consumed the click; false /
        /// unset falls through to the normal recenter, so the gesture is harmless off
        /// the relief-raymarch path.</summary>
        Func<PointerInput, bool>? ReliefFocusPickHandler { get; set; }

        // ── Keyboard ──────────────────────────────────────────────────────────
        /// <summary>Returns true when the controller consumed the key (so the
        /// shell adapter can set Handled=true on its KeyEventArgs).</summary>
        bool OnKeyDown(KeyInput e);

        /// <summary>Sets the cursor the shell should display, or null to
        /// restore the default. Raised whenever the controller's drag state
        /// changes.</summary>
        event EventHandler<InputCursorRequest>? CursorRequested;

        /// <summary>Raised after any mutation that requires a re-render.</summary>
        event EventHandler<ViewChangedArgs>? ViewChanged;

        /// <summary>Raised when the input layer wants the status bar to show
        /// a message (e.g. quality auto-promotion).</summary>
        event EventHandler<InputStatusMessage>? StatusRequested;

        /// <summary>Raised while the user is right-drag-selecting a zoom
        /// region in 2D, and once more with <c>null</c> when the drag ends
        /// (release or cancel). The host renders the preview rectangle on top
        /// of the current frame; the controller applies the zoom itself when
        /// the drag completes.</summary>
        event EventHandler<SelectionBoxChange?>? SelectionBoxChanged;
    }

    /// <summary>Canonical cursor names the input layer can request. The
    /// shell adapter maps these to its platform's cursor enum.</summary>
    public enum InputCursor { Default, Cross, SizeAll, NoMove2D }

    /// <summary>Cursor request from the input controller to the shell.</summary>
    public readonly record struct InputCursorRequest(InputCursor Cursor);
}
