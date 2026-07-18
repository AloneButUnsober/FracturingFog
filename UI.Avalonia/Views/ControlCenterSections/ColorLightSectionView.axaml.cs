// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Color &amp; Light" section (themes, lighting/FX
/// launcher, post-FX). See <see cref="ViewSectionView"/> for the detach
/// rationale.</summary>
public sealed partial class ColorLightSectionView : UserControl
{
    public ColorLightSectionView() => AvaloniaXamlLoader.Load(this);
}
