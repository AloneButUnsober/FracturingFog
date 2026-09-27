// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Command" section — the CLI Command Builder (#994).
/// See <see cref="ViewSectionView"/> for the detach rationale.</summary>
public sealed partial class CommandSectionView : UserControl
{
    public CommandSectionView() => AvaloniaXamlLoader.Load(this);
}
