// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Borderless floating window hosting the shared <see cref="StatusBarView"/>
/// (#499). Click-drag anywhere on the strip moves it. Created on demand by
/// MainWindow when ShellViewModel.IsStatusPanelVisible flips true; DataContext is
/// the ShellViewModel so it stays live with the docked status bar.</summary>
public sealed partial class StatusPanelWindow : Window
{
    public StatusPanelWindow()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);

        var drag = this.FindControl<Border>("DragRoot");
        if (drag != null)
            drag.PointerPressed += OnDragPointerPressed;
    }

    private void OnDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Not reached when a child control (e.g. the Cancel button) already
        // handled the press, so button clicks never start a window drag.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        try { BeginMoveDrag(e); }
        catch { /* platform may not support move-drag; ignore */ }
    }
}
