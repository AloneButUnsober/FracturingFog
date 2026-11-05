// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Environment" section — FF_* switches (#1669, epic #1667).
/// See <see cref="ViewSectionView"/> for the detach rationale.</summary>
public sealed partial class EnvironmentSectionView : UserControl
{
    public EnvironmentSectionView() => AvaloniaXamlLoader.Load(this);
}
