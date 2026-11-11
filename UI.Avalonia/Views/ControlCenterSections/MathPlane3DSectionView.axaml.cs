// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Math Plane 3D" section — the 3D object editor (#1716).
/// See <see cref="ViewSectionView"/> for the detach rationale.</summary>
public sealed partial class MathPlane3DSectionView : UserControl
{
    public MathPlane3DSectionView() => AvaloniaXamlLoader.Load(this);
}
