// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// EscapeCloseBehavior.cs
//
// Single-line opt-in: in a dialog/floating Window constructor call
// EscapeCloseBehavior.Attach(this). Unmodified Esc then closes the
// window (mirrors the OS X-button path — Closing handlers still fire,
// so modeless windows that cancel-close-and-flip-visible-flag in
// MainWindow keep working). Skip on MainWindow.
//
// Tunnel routing: parent sees Esc before children, so a focused
// TextBox/ComboBox does not swallow the key. Standard dialog UX.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FracturingFog.UI.Avalonia.Input;

internal static class EscapeCloseBehavior
{
    public static void Attach(Window window)
    {
        window.AddHandler(
            InputElement.KeyDownEvent,
            OnKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: false);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key != Key.Escape) return;
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (sender is not Window w) return;
        e.Handled = true;
        w.Close();
    }
}
