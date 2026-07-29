// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

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
}
