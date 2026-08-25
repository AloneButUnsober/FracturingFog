// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Status-bar content shared by the docked strip in
/// <see cref="MainWindow"/> and the floating <see cref="StatusPanelWindow"/>
/// (#499). Binds against the inherited ShellViewModel DataContext.</summary>
public sealed partial class StatusBarView : UserControl
{
    public StatusBarView() => AvaloniaXamlLoader.Load(this);
}
