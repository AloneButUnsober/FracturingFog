// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

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
    private PanelHostWindow? _lightingFxWin;

    public Relief3DDialog()
    {
        AvaloniaXamlLoader.Load(this);
        Unloaded += (_, _) =>
        {
            _lightingFxWin?.Close();
            _lightingFxWin = null;
        };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();

    /// <summary>#140 — open (or re-focus) the Lighting &amp; FX dialog from the
    /// Relief 3D panel. Shares this dialog's <c>FractalParamsViewModel</c> so
    /// edits propagate through the same path as the params panel's launcher.</summary>
    private void OnOpenLightingFxClick(object? sender, RoutedEventArgs e)
    {
        if (_lightingFxWin is { IsVisible: true })
        {
            _lightingFxWin.Activate();
            return;
        }

        _lightingFxWin = new PanelHostWindow(
            new LightingFxDialog(),
            new PanelHostOptions(
                "Lighting & FX",
                Width: 520, Height: 720, MinWidth: 440, MinHeight: 400,
                SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                StartupLocation: WindowStartupLocation.CenterOwner,
                Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))))
        {
            DataContext = DataContext,
        };
        _lightingFxWin.Closed += (_, _) => _lightingFxWin = null;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null) _lightingFxWin.Show(owner);
        else _lightingFxWin.Show();
    }
}
