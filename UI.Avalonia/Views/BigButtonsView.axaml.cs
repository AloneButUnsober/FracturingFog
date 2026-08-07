// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Standalone Big Buttons kid dialog (Color / Place / Show). A plain
/// <see cref="UserControl"/> hosted in a large resizable <c>PanelHostWindow</c>
/// by <c>AvaloniaShellBootstrap</c>; all behaviour lives on
/// <see cref="ViewModels.BigButtonsViewModel"/>.
/// </summary>
public sealed partial class BigButtonsView : UserControl
{
    public BigButtonsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => (TopLevel.GetTopLevel(this) as Window)?.Close();
}
