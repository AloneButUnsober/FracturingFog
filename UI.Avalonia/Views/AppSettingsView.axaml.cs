// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// General application-settings panel (Avalonia). VM holds all state and
/// raises <see cref="ViewModels.AppSettingsViewModel.CloseRequested"/>; the
/// host window (<see cref="Services.PanelHostWindow"/>) or shell owns closing —
/// this view is a plain <see cref="UserControl"/> so it can dock or pop out.
/// </summary>
public sealed partial class AppSettingsView : UserControl
{
    public AppSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
