// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Explore" section (region navigation, coordinates,
/// deep-zoom diagnostics). See <see cref="ViewSectionView"/> for the detach
/// rationale.</summary>
public sealed partial class ExploreSectionView : UserControl
{
    public ExploreSectionView()
    {
        AvaloniaXamlLoader.Load(this);

        // Right-click Region combo → same "Edit region…" + filter-by-fractal-type
        // flyout the toolbar Region combo carries (issue #51). The build callback
        // reads the live DataContext so it works docked or detached into a
        // floating window (each instance attaches its own handler).
        ComboSortMenu.Attach(this.FindControl<ComboBox>("RegionCombo"), BuildRegionComboMenu);
    }

    private IReadOnlyList<ComboMenuItem> BuildRegionComboMenu()
    {
        if (DataContext is not ControlCenterViewModel vm)
            return Array.Empty<ComboMenuItem>();

        var items = new List<ComboMenuItem>
        {
            ComboMenuItem.Item("Edit region…", false,
                () => vm.Shell.ShowRegionEditorCommand.Execute().Subscribe()),
            ComboMenuItem.Separator,
        };
        items.AddRange(vm.Menu.BuildRegionSortMenu());
        return items;
    }
}
