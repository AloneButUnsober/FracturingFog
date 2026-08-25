// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Borderless floating window hosting the shared <see cref="StatusBarView"/>
/// (#499). Click-drag the strip moves it; the bottom-right grip resizes it.
/// Created on demand by MainWindow when ShellViewModel.IsStatusPanelVisible flips
/// true; DataContext is the ShellViewModel so it stays live with the docked bar.</summary>
public sealed partial class StatusPanelWindow : Window
{
    private bool _resizing;
    private Point _resizeStart;   // pointer, window-relative (window top-left is fixed during a bottom-right resize)
    private double _startW;
    private double _startH;

    public StatusPanelWindow()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);

        var drag = this.FindControl<Border>("DragRoot");
        if (drag != null)
            drag.PointerPressed += OnDragPointerPressed;

        var grip = this.FindControl<Border>("ResizeGrip");
        if (grip != null)
        {
            grip.PointerPressed += OnGripPressed;
            grip.PointerMoved += OnGripMoved;
            grip.PointerReleased += OnGripReleased;
        }
    }

    private void OnDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Not reached when a child control (e.g. the Cancel button) already
        // handled the press, so button clicks never start a window drag.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        try { BeginMoveDrag(e); }
        catch { /* platform may not support move-drag; ignore */ }
    }

    // ── Manual bottom-right resize ───────────────────────────────────────────
    // Borderless windows don't get reliable OS edge-resize, so the grip drives
    // it. Delta is computed from the pointer position relative to the window,
    // whose top-left stays fixed during a bottom-right resize, so there is no
    // feedback loop as the window (and grip) grow under the cursor.

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _resizing = true;
        _resizeStart = e.GetPosition(this);
        _startW = double.IsNaN(Width) ? Bounds.Width : Width;
        _startH = double.IsNaN(Height) ? Bounds.Height : Height;
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;
        var p = e.GetPosition(this);
        double w = _startW + (p.X - _resizeStart.X);
        double h = _startH + (p.Y - _resizeStart.Y);
        Width = w < MinWidth ? MinWidth : w;
        Height = h < MinHeight ? MinHeight : h;
        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
