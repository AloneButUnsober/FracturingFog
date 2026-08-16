// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Services;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Standalone Relief 3D (2D heightfield) panel (#137). Extracted from the inline
/// block in EscapeTimeParamsView so the compact Params dialog stays short. Bound
/// to the same <see cref="ViewModels.FractalParamsViewModel"/> as
/// <see cref="FractalParamsView"/> (the params view passes its DataContext
/// through when opening this dialog), so every Relief2D* knob routes through the
/// existing bindings. Reusable across any fractal type where
/// <c>IsRelief2DApplicable</c> is true.
/// </summary>
public sealed partial class Relief3DDialog : UserControl
{
    public Relief3DDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();

    /// <summary>#140 — open (or re-focus) the Lighting &amp; FX dialog from the
    /// Relief 3D panel. Shares this dialog's <c>FractalParamsViewModel</c> so
    /// edits propagate through the same path as the params panel's launcher.</summary>
    private void OnOpenLightingFxClick(object? sender, RoutedEventArgs e)
    {
        // Open (or re-focus) the single app-wide Lighting & FX window, owned by
        // the main window (not this panel) via WindowService, so closing Relief
        // 3D leaves it open. Shares this dialog's VM so edits re-render through
        // the same ParamChanged path.
        if (DataContext != null)
            WindowService.ShowLightingFx(DataContext, "Volumetric Lighting & FX");
    }
}
