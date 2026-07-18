// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Topmost borderless window hosting the MiniMapControl. Created on
/// demand by MainWindow code-behind when ShellViewModel.IsMiniMapVisible
/// flips true; positioned and lifecycle-tracked by MiniWindowTether.</summary>
public sealed partial class MiniMapWindow : Window
{
    /// <summary>Raised when the user double-taps the drag handle. The host
    /// MainWindow forwards this to MiniWindowTether.ResetAnchor() so the
    /// window snaps back to its default corner position.</summary>
    public event EventHandler? ResetAnchorRequested;

    public MiniMapWindow()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);

        var handle = this.FindControl<Border>("DragHandle");
        if (handle != null)
        {
            handle.PointerPressed += OnDragHandlePointerPressed;
        }
    }

    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            ResetAnchorRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        try { BeginMoveDrag(e); }
        catch { /* platform may not support; ignore */ }
    }
}
