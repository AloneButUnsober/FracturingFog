// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Advanced" section (remote rendering + system). See
/// <see cref="ViewSectionView"/> for the detach rationale.</summary>
public sealed partial class AdvancedSectionView : UserControl
{
    public AdvancedSectionView() => AvaloniaXamlLoader.Load(this);
}
