// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>#1051 — borderless scene debug overlay tethered to a render-window
/// corner by <see cref="MiniWindowTether"/> (same drag-handle + double-tap-reset
/// UX as <see cref="PostFxHudWindow"/>). Bound to the shell's
/// <see cref="ViewModels.ShellViewModel.SceneDebugText"/>; created on demand by
/// MainWindow when <see cref="ViewModels.ShellViewModel.IsSceneDebugVisible"/>
/// flips true.</summary>
public sealed partial class SceneDebugHudWindow : Window
{
    /// <summary>Double-tap on the drag handle: snap back to the default corner.</summary>
    public event EventHandler? ResetAnchorRequested;

    public SceneDebugHudWindow()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        Services.WindowService.AttachUiScale(this);

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
