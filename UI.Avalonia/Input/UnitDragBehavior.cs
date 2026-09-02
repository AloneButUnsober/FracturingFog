// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UnitDragBehavior.cs  (#500 — follow-up to #433)
//
// Single-line opt-in: in a workspace-aware panel Window constructor call
// UnitDragBehavior.Attach(this). A right-button drag anywhere on that window
// then translates EVERY registered workspace window (the render MainWindow
// included) by the same delta — the user shifts the whole UI as one unit
// instead of repositioning each window.
//
// This is attached centrally from WindowService.RegisterWindow for every role
// EXCEPT WindowRole.RenderWindow: the render surface already owns the right
// button (rubber-band zoom + context menu) and must never ORIGINATE a unit
// drag, but it still TRANSLATES as part of the group because it is a registered
// window (confirmed scope: "entire UI as one unit").
//
// Right-CLICK vs right-DRAG is separated by a small physical-pixel movement
// threshold — the same down/up-distance trick the render window uses. Below the
// threshold nothing is consumed, so a panel's own context menu (if any) still
// pops; once the threshold is crossed the drag captures the pointer and eats
// the trailing context-menu request.
//
// Geometry note: Window.PointToScreen(clientPoint) always returns the TRUE
// cursor screen position (windowOrigin + clientPoint*scale), even while we move
// the window mid-drag — clientPoint is re-measured against the current origin
// each event, so origin + clientPoint cancels back to the physical cursor. That
// is why applying (snapshot position + total cursor delta) is drift-free with
// no feedback loop, despite the attached window being one of the movers.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using FracturingFog.UI.Avalonia.Services;

namespace FracturingFog.UI.Avalonia.Input;

internal static class UnitDragBehavior
{
    // Physical-pixel distance the pointer must travel before a right-press is
    // treated as a unit drag rather than a plain right-click.
    private const int DragThresholdPx = 6;

    // Dedupe: a role can be re-registered to the same window instance; attach
    // the handlers at most once per window so the group never double-moves.
    private static readonly ConditionalWeakTable<Window, object> s_attached = new();

    public static void Attach(Window window)
    {
        if (window == null) return;
        if (s_attached.TryGetValue(window, out _)) return;
        s_attached.Add(window, new object());

        bool armed = false;      // right button down, watching for the threshold
        bool dragging = false;   // threshold crossed, actively translating
        PixelPoint startScreen = default;
        List<(Window Win, PixelPoint Pos)> snapshot = new();

        void End()
        {
            armed = false;
            dragging = false;
            snapshot = new List<(Window, PixelPoint)>();
        }

        void OnPressed(object? _, PointerPressedEventArgs e)
        {
            var pt = e.GetCurrentPoint(window);
            if (!pt.Properties.IsRightButtonPressed) return;

            startScreen = window.PointToScreen(pt.Position);
            snapshot = TargetWindows(window);
            armed = snapshot.Count > 0;
            dragging = false;
            // Do NOT set e.Handled or capture yet — a plain right-click must fall
            // through to any panel context menu until the drag threshold is met.
        }

        void OnMoved(object? _, PointerEventArgs e)
        {
            if (!armed) return;

            var pt = e.GetCurrentPoint(window);
            if (!pt.Properties.IsRightButtonPressed) { End(); return; }

            var cur = window.PointToScreen(pt.Position);
            int dx = cur.X - startScreen.X;
            int dy = cur.Y - startScreen.Y;

            if (!dragging)
            {
                if (Math.Abs(dx) < DragThresholdPx && Math.Abs(dy) < DragThresholdPx)
                    return;
                dragging = true;
                e.Pointer.Capture(window); // keep events flowing outside the panel
            }

            foreach (var (win, pos) in snapshot)
            {
                if (!win.IsVisible || win.WindowState != WindowState.Normal) continue;
                win.Position = new PixelPoint(pos.X + dx, pos.Y + dy);
            }
            e.Handled = true;
        }

        void OnReleased(object? _, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right) { if (dragging) End(); return; }
            bool wasDrag = dragging;
            End();
            // Swallow the trailing context-menu request after an actual drag so a
            // menu does not pop where the drag ended (mirrors the render window's
            // wasDrag suppression). A no-move right-click is left un-handled.
            if (wasDrag) e.Handled = true;
        }

        void OnCaptureLost(object? _, PointerCaptureLostEventArgs e) => End();

        window.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost, RoutingStrategies.Tunnel);
    }

    // The windows that translate as a unit: every live, visible, normal-state
    // registered workspace window (render MainWindow included), plus the
    // originating window itself in case it is not in the registry. Snapshot of
    // current positions, taken at press so the whole drag stays drift-free.
    private static List<(Window, PixelPoint)> TargetWindows(Window origin)
    {
        var seen = new HashSet<Window>();
        var list = new List<(Window, PixelPoint)>();

        void Add(Window w)
        {
            if (w == null || !w.IsVisible || w.WindowState != WindowState.Normal) return;
            if (!seen.Add(w)) return;
            list.Add((w, w.Position));
        }

        foreach (var (_, win) in WindowService.RegisteredWindows())
            Add(win);
        Add(origin);
        return list;
    }
}
