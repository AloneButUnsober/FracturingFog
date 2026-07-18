// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Borderless post-processing HUD (brightness / contrast / adaptive)
/// tethered to a render-window corner by <see cref="MiniWindowTether"/>. Bound
/// to the shared <see cref="ViewModels.FloatingMenuViewModel"/>, so it drives
/// the same post-FX state as the Control Center Post-FX panel. Created on demand
/// by MainWindow when <see cref="ViewModels.ShellViewModel.IsPostFxHudVisible"/>
/// flips true. Same drag-handle + double-tap-reset UX as MiniMapWindow.</summary>
public sealed partial class PostFxHudWindow : Window
{
    /// <summary>Raised on a double-tap of the drag handle so the host can snap
    /// the window back to its default corner via MiniWindowTether.ResetAnchor().</summary>
    public event EventHandler? ResetAnchorRequested;

    public PostFxHudWindow()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);

        var handle = this.FindControl<Border>("DragHandle");
        if (handle != null)
            handle.PointerPressed += OnDragHandlePointerPressed;
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
