// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>#1447 — borderless window hosting the chaotic-billiard ray-path inset,
/// tethered to a render-window corner by MainWindow.SyncBilliardRayInset.</summary>
public sealed partial class BilliardRayInsetWindow : Window
{
    private readonly BilliardRayInsetViewModel _vm;
    private readonly TextBlock _status;

    /// <summary>Raised when the user double-taps the drag handle (snap back to the corner).</summary>
    public event EventHandler? ResetAnchorRequested;

    public BilliardRayInsetWindow(BilliardRayInsetViewModel vm)
    {
        _vm = vm;
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        Services.WindowService.AttachUiScale(this);

        var inset = this.FindControl<BilliardRayInsetControl>("Inset")
            ?? throw new InvalidOperationException("BilliardRayInsetControl x:Name=Inset missing");
        inset.Attach(vm);
        _status = this.FindControl<TextBlock>("StatusText")
            ?? throw new InvalidOperationException("TextBlock x:Name=StatusText missing");
        _status.Text = vm.Status;
        vm.InsetChanged += (_, _) => _status.Text = vm.Status;

        if (this.FindControl<Button>("ClearPins") is { } clear)
            clear.Click += (_, _) => _vm.ClearPins();
        if (this.FindControl<Border>("DragHandle") is { } handle)
            handle.PointerPressed += OnDragHandlePointerPressed;
    }

    // Avalonia's XAML loader needs a parameterless constructor for the previewer.
    public BilliardRayInsetWindow() : this(new BilliardRayInsetViewModel()) { }

    private void OnDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
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
