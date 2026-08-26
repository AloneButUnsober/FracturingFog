// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Borderless floating window hosting the shared <see cref="ToolbarView"/>
/// (#514). Drag the strip background to move; the bottom-right grip resizes.
/// Created on demand by MainWindow when ShellViewModel.IsToolbarPanelVisible flips
/// true; DataContext is the ShellViewModel so it stays live with the docked
/// toolbar.</summary>
public sealed partial class ToolbarWindow : Window
{
    private bool _resizing;
    private Point _resizeStart;   // pointer, window-relative (window top-left fixed during a bottom-right resize)
    private double _startW;
    private double _startH;

    public ToolbarWindow()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);

        var move = this.FindControl<Border>("MoveHandle");
        if (move != null)
            move.PointerPressed += OnDragPointerPressed;

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
        // Not reached when a child control (button/combo) already handled the
        // press, so toolbar clicks never start a window drag.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        try { BeginMoveDrag(e); }
        catch { /* platform may not support move-drag; ignore */ }
    }

    // Manual bottom-right resize — borderless windows lack reliable OS
    // edge-resize. Delta from the window-relative pointer (top-left fixed during a
    // bottom-right resize) avoids a feedback loop as the window grows.

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
