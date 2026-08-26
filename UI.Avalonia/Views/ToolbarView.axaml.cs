// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Render-window toolbar content shared by the docked strip in
/// <see cref="MainWindow"/> and the floating <see cref="ToolbarWindow"/> (#514).
/// Binds against the inherited ShellViewModel DataContext. The Type/Region/Theme
/// combos' right-click sort/filter flyouts are wired here (per instance) — they
/// used to live in MainWindow.AttachShell, but that breaks once the combos move
/// out of MainWindow's visual tree, so each ToolbarView wires its own.</summary>
public sealed partial class ToolbarView : UserControl
{
    private ShellViewModel? _shell;
    private bool _sortMenusAttached;

    public ToolbarView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _shell = DataContext as ShellViewModel;
        if (_shell == null || _sortMenusAttached) return;

        // Attach once — ComboSortMenu adds a ContextRequested handler; the build
        // callbacks read the live _shell so they stay correct across DataContext
        // swaps.
        ComboSortMenu.Attach(this.FindControl<ComboBox>("ToolbarTypeCombo"),
            () => _shell?.Main.BuildFractalTypeSortMenu() ?? Array.Empty<ComboMenuItem>());
        ComboSortMenu.Attach(this.FindControl<ComboBox>("ToolbarRegionCombo"),
            BuildRegionComboMenu);
        ComboSortMenu.Attach(this.FindControl<ComboBox>("ToolbarThemeCombo"),
            () => _shell?.FloatingMenu.BuildThemeSortMenu() ?? Array.Empty<ComboMenuItem>());
        _sortMenusAttached = true;
    }

    // Region combo right-click menu: "Edit region…" + separator, then the
    // FloatingMenu's filter-by-fractal-type entries (RegionSortMode). Rebuilt on
    // every open so the filter's checked state stays live.
    private IReadOnlyList<ComboMenuItem> BuildRegionComboMenu()
    {
        var items = new List<ComboMenuItem>
        {
            ComboMenuItem.Item("Edit region…", false,
                () => _shell?.ShowRegionEditorCommand.Execute().Subscribe()),
        };
        if (_shell != null)
        {
            items.Add(ComboMenuItem.Separator);
            items.AddRange(_shell.FloatingMenu.BuildRegionSortMenu());
        }
        return items;
    }
}
