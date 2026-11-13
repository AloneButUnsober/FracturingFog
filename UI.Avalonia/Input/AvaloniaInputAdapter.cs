// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Input/AvaloniaInputAdapter.cs
//
// Bridges Avalonia input events (PointerPressed / PointerMoved /
// PointerReleased / DoubleTapped / PointerWheelChanged / KeyDown) into
// the shell-neutral IFractalInputController surface defined in
// FracturingFog.Abstractions/Input/.
//
// Attach via AvaloniaInputAdapter.Attach(targetControl, controller). The
// adapter listens on the supplied control's events; client code never
// calls the controller's OnXxx methods directly.
//
// Cursor translation: the controller raises InputCursorRequest; this
// adapter sets the host control's Cursor accordingly. Done here (not in
// the controller) so the Abstractions assembly stays free of Avalonia
// references.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FracturingFog.Input;
using FracturingFog.Render;

namespace FracturingFog.UI.Avalonia.Input;

/// <summary>
/// Attaches an <see cref="IFractalInputController"/> to an Avalonia
/// <see cref="InputElement"/>. Disposing the returned token unhooks every
/// event subscription.
/// </summary>
public static class AvaloniaInputAdapter
{
    /// <summary>
    /// Wires every relevant Avalonia input event on <paramref name="target"/>
    /// into the controller. Returns an IDisposable that unhooks every
    /// subscription when disposed.
    /// </summary>
    public static IDisposable Attach(InputElement target, IFractalInputController controller)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (controller == null) throw new ArgumentNullException(nameof(controller));

        target.Focusable = true;

        void OnPressed(object? s, PointerPressedEventArgs e)
        {
            // Clicking the render surface grabs keyboard focus so the WASD /
            // QE pan-zoom and 3D camera/light keys route here. A Focusable
            // Border is not auto-focused on click in Avalonia, so do it
            // explicitly — otherwise the controller never sees a KeyDown.
            long t0 = TraceIn(InputKind.Press, e.Timestamp);
            target.Focus();
            controller.OnPointerDown(ToPointer(e, target));
            TraceOut(InputKind.Press, t0);
        }
        void OnMoved(object? s, PointerEventArgs e)
        {
            long t0 = 0;
            if (InteractionTrace.Enabled)   // #1762 — arrival, OS delay, points coalesced into this move
            {
                int coalesced = 1;
                try { coalesced = e.GetIntermediatePoints(target).Count; } catch { }
                InteractionTrace.Mark(TraceKind.PointerMove, 0, OsDelayMs(e.Timestamp), coalesced);
                t0 = InteractionTrace.Now();
            }
            controller.OnPointerMove(ToPointer(e, target));
            TraceOut(InputKind.Move, t0);
        }
        void OnReleased(object? s, PointerReleasedEventArgs e)
        {
            long t0 = TraceIn(InputKind.Release, e.Timestamp);
            controller.OnPointerUp(ToPointerReleased(e, target));
            TraceOut(InputKind.Release, t0);
        }
        void OnDouble(object? s, TappedEventArgs e)         => controller.OnPointerDoubleClick(ToPointerFromTap(e, target));
        void OnWheel(object? s, PointerWheelEventArgs e)
        {
            long t0 = TraceIn(InputKind.Wheel, e.Timestamp);
            controller.OnWheel(ToWheel(e, target));
            TraceOut(InputKind.Wheel, t0);
        }
        void OnKey(object? s, KeyEventArgs e)
        {
            var ki = ToKey(e, target);
            if (ki.Key == InputKey.None) return;
            long t0 = TraceIn(InputKind.Key, 0);   // KeyEventArgs carries no OS time stamp
            if (controller.OnKeyDown(ki)) e.Handled = true;
            TraceOut(InputKind.Key, t0);
        }

        void OnCursor(object? s, InputCursorRequest req)
        {
            target.Cursor = TranslateCursor(req.Cursor);
        }

        target.PointerPressed       += OnPressed;
        target.PointerMoved         += OnMoved;
        target.PointerReleased      += OnReleased;
        target.DoubleTapped         += OnDouble;
        target.PointerWheelChanged  += OnWheel;
        target.KeyDown              += OnKey;
        controller.CursorRequested  += OnCursor;

        return new DisposableAction(() =>
        {
            target.PointerPressed       -= OnPressed;
            target.PointerMoved         -= OnMoved;
            target.PointerReleased      -= OnReleased;
            target.DoubleTapped         -= OnDouble;
            target.PointerWheelChanged  -= OnWheel;
            target.KeyDown              -= OnKey;
            controller.CursorRequested  -= OnCursor;
        });
    }

    /// <summary>
    /// Builds a shell-neutral <see cref="KeyInput"/> from an Avalonia key
    /// event, using <paramref name="surface"/> for the client dimensions.
    /// The result's <see cref="KeyInput.Key"/> is <see cref="InputKey.None"/>
    /// for keys the controller does not care about. Exposed so the window can
    /// forward pan / zoom / 3-D camera keys to the controller when keyboard
    /// focus is on a toolbar control rather than the input sponge.
    /// </summary>
    public static KeyInput BuildKeyInput(KeyEventArgs e, Visual surface) => ToKey(e, surface);

    // ── Event translators ────────────────────────────────────────────────

    private static PointerInput ToPointer(PointerEventArgs e, Visual target)
    {
        var p = e.GetPosition(target);
        var props = e.GetCurrentPoint(target).Properties;
        var btn = PointerButton.None;
        if (props.IsLeftButtonPressed)   btn |= PointerButton.Left;
        if (props.IsRightButtonPressed)  btn |= PointerButton.Right;
        if (props.IsMiddleButtonPressed) btn |= PointerButton.Middle;
        var bounds = (target as Control)?.Bounds ?? new Rect();
        return new PointerInput(
            (int)p.X, (int)p.Y,
            (int)Math.Max(1, bounds.Width),
            (int)Math.Max(1, bounds.Height),
            btn,
            TranslateModifiers(e.KeyModifiers));
    }

    private static PointerInput ToPointerReleased(PointerReleasedEventArgs e, Visual target)
    {
        var p = e.GetPosition(target);
        // PointerReleased flags the released button only — synthesize the
        // matching PointerButton flag from InitialPressMouseButton.
        var btn = e.InitialPressMouseButton switch
        {
            MouseButton.Left => PointerButton.Left,
            MouseButton.Right => PointerButton.Right,
            MouseButton.Middle => PointerButton.Middle,
            _ => PointerButton.None,
        };
        var bounds = (target as Control)?.Bounds ?? new Rect();
        return new PointerInput(
            (int)p.X, (int)p.Y,
            (int)Math.Max(1, bounds.Width),
            (int)Math.Max(1, bounds.Height),
            btn,
            TranslateModifiers(e.KeyModifiers));
    }

    private static PointerInput ToPointerFromTap(TappedEventArgs e, Visual target)
    {
        var p = e.GetPosition(target);
        var bounds = (target as Control)?.Bounds ?? new Rect();
        return new PointerInput(
            (int)p.X, (int)p.Y,
            (int)Math.Max(1, bounds.Width),
            (int)Math.Max(1, bounds.Height),
            PointerButton.Left,
            TranslateModifiers(e.KeyModifiers));
    }

    private static WheelInput ToWheel(PointerWheelEventArgs e, Visual target)
    {
        var p = e.GetPosition(target);
        var bounds = (target as Control)?.Bounds ?? new Rect();
        // Avalonia normalises wheel delta to ~1 per detent; scale to match
        // legacy WinForms (~120 per detent).
        int delta = (int)(e.Delta.Y * 120);
        return new WheelInput(
            (int)p.X, (int)p.Y,
            (int)Math.Max(1, bounds.Width),
            (int)Math.Max(1, bounds.Height),
            delta,
            TranslateModifiers(e.KeyModifiers));
    }

    private static KeyInput ToKey(KeyEventArgs e, Visual target)
    {
        var bounds = (target as Control)?.Bounds ?? new Rect();
        var key = TranslateKey(e.Key, e.KeyModifiers);
        return new KeyInput(
            key,
            TranslateModifiers(e.KeyModifiers),
            (int)Math.Max(1, bounds.Width),
            (int)Math.Max(1, bounds.Height));
    }

    private static InputModifiers TranslateModifiers(KeyModifiers m)
    {
        var r = InputModifiers.None;
        if ((m & KeyModifiers.Shift) != 0)   r |= InputModifiers.Shift;
        if ((m & KeyModifiers.Control) != 0) r |= InputModifiers.Control;
        if ((m & KeyModifiers.Alt) != 0)     r |= InputModifiers.Alt;
        return r;
    }

    private static InputKey TranslateKey(Key key, KeyModifiers mods)
    {
        // Ctrl+Shift diagnostic toggles.
        if ((mods & KeyModifiers.Control) != 0 && (mods & KeyModifiers.Shift) != 0)
        {
            return key switch
            {
                Key.S => InputKey.DiagToggleSeries,
                Key.A => InputKey.DiagToggleAcceleration,
                _ => InputKey.None,
            };
        }
        return key switch
        {
            Key.W => InputKey.W,
            Key.A => InputKey.A,
            Key.S => InputKey.S,
            Key.D => InputKey.D,
            Key.Q => InputKey.Q,
            Key.E => InputKey.E,
            Key.M => InputKey.M,
            Key.T => InputKey.T,
            Key.R => InputKey.R,
            Key.V => InputKey.V,
            Key.Up => InputKey.Up,
            Key.Down => InputKey.Down,
            Key.Left => InputKey.Left,
            Key.Right => InputKey.Right,
            Key.PageUp => InputKey.PageUp,
            Key.PageDown => InputKey.PageDown,
            Key.Home => InputKey.Home,
            Key.End => InputKey.End,
            Key.Escape => InputKey.Escape,
            Key.Delete => InputKey.Delete,
            _ => InputKey.None,
        };
    }

    private static Cursor? TranslateCursor(InputCursor c) => c switch
    {
        InputCursor.Default  => Cursor.Default,
        InputCursor.Cross    => new Cursor(StandardCursorType.Cross),
        InputCursor.SizeAll  => new Cursor(StandardCursorType.SizeAll),
        InputCursor.NoMove2D => new Cursor(StandardCursorType.None),
        _ => Cursor.Default,
    };

    // #1762 — interaction trace (FF_INTERACTION_TRACE): input arrival and handler time.
    private static long TraceIn(InputKind kind, ulong osTimestamp)
    {
        if (!InteractionTrace.Enabled) return 0;
        InteractionTrace.Mark(TraceKind.InputOther, 0, OsDelayMs(osTimestamp), (long)kind);
        return InteractionTrace.Now();
    }

    private static void TraceOut(InputKind kind, long t0)
    {
        if (t0 != 0) InteractionTrace.Mark(TraceKind.InputHandled, 0, InteractionTrace.MicrosSince(t0), (long)kind);
    }

    // The event's OS time stamp against the tick count, in ms. On Windows both count milliseconds
    // since boot (GetMessageTime vs GetTickCount, ~15.6 ms steps), so the difference is how long the
    // event waited before the UI thread handled it. Other platforms stamp from other clocks; an
    // implausible difference is reported as -1 (unknown).
    private static long OsDelayMs(ulong osTimestamp)
    {
        if (osTimestamp == 0) return -1;
        long d = unchecked((uint)Environment.TickCount - (uint)osTimestamp);
        return d is >= 0 and <= 10_000 ? d : -1;
    }

    private sealed class DisposableAction : IDisposable
    {
        private Action? _action;
        public DisposableAction(Action action) { _action = action; }
        public void Dispose()
        {
            _action?.Invoke();
            _action = null;
        }
    }
}
