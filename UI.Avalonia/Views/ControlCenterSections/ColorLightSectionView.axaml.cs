// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Color &amp; Light" section (themes, lighting/FX
/// launcher, post-FX). See <see cref="ViewSectionView"/> for the detach
/// rationale.</summary>
public sealed partial class ColorLightSectionView : UserControl
{
    public ColorLightSectionView()
    {
        AvaloniaXamlLoader.Load(this);

        // Right-click Theme combo → same sort flyout the toolbar Theme combo
        // carries (issue #51). Build callback reads the live DataContext so it
        // works docked or detached into a floating window.
        ComboSortMenu.Attach(this.FindControl<ComboBox>("ThemeCombo"), BuildThemeComboMenu);
    }

    private IReadOnlyList<ComboMenuItem> BuildThemeComboMenu()
    {
        if (DataContext is not ControlCenterViewModel vm)
            return Array.Empty<ComboMenuItem>();
        return vm.Menu.BuildThemeSortMenu();
    }
}
