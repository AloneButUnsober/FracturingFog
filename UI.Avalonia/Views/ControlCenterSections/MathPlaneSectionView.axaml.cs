// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Math Plane" section — the object editor (#1549).
/// See <see cref="ViewSectionView"/> for the detach rationale.</summary>
public sealed partial class MathPlaneSectionView : UserControl
{
    public MathPlaneSectionView() => AvaloniaXamlLoader.Load(this);
}
