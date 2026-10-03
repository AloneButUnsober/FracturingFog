// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Controls/ComboSortMenu.cs
//
// View-layer helper that gives a ComboBox the WinForms right-click "sort
// mode" menu (Controls.AttachColorComboSortMenu / AttachRegionComboSortMenu).
// On Windows the legacy combos rebuilt themselves from a ContextMenuStrip;
// here the VM supplies an ordered list of ComboMenuItem records and this
// helper renders them into an Avalonia MenuFlyout shown at the cursor.
//
// The build callback is re-invoked on every right-click so the checked
// state always reflects the combo's current sort mode.
//
// #1057 — two ways to attach: Attach(combo, build) from code-behind for a
// named combo, or the MenuSource attached property for combos inside a
// DataTemplate (e.g. the Scene Editor's per-shot rows), where FindControl by
// name can't reach them:
//   <ComboBox controls:ComboSortMenu.MenuSource="{Binding RegionMenu}" />
// where RegionMenu is a Func<IReadOnlyList<ComboMenuItem>> on the item VM.

using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Controls;

/// <summary>Attaches a right-click sort/filter menu to a <see cref="ComboBox"/>.</summary>
public sealed class ComboSortMenu
{
    private ComboSortMenu() { }

    /// <summary>Templated-combo form: the menu builder, typically bound from the
    /// item VM. Re-read on each right-click, so rebinding takes effect at once;
    /// null (or an empty menu) shows nothing.</summary>
    public static readonly AttachedProperty<Func<IReadOnlyList<ComboMenuItem>>?> MenuSourceProperty =
        AvaloniaProperty.RegisterAttached<ComboSortMenu, ComboBox, Func<IReadOnlyList<ComboMenuItem>>?>("MenuSource");

    // Guards against wiring ContextRequested twice when MenuSource rebinds
    // (a recycled row container gets a new DataContext, not a new combo).
    private static readonly AttachedProperty<bool> IsWiredProperty =
        AvaloniaProperty.RegisterAttached<ComboSortMenu, ComboBox, bool>("IsWired");

    public static Func<IReadOnlyList<ComboMenuItem>>? GetMenuSource(ComboBox combo)
        => combo.GetValue(MenuSourceProperty);

    public static void SetMenuSource(ComboBox combo, Func<IReadOnlyList<ComboMenuItem>>? value)
        => combo.SetValue(MenuSourceProperty, value);

    static ComboSortMenu()
    {
        MenuSourceProperty.Changed.AddClassHandler<ComboBox>((combo, _) =>
        {
            if (combo.GetValue(IsWiredProperty)) return;
            combo.SetValue(IsWiredProperty, true);
            combo.ContextRequested += (_, e) => Show(combo, GetMenuSource(combo), e);
        });
    }

    /// <summary>Wire <paramref name="combo"/>'s right-click (ContextRequested)
    /// to a MenuFlyout built from <paramref name="build"/>. Safe to call once
    /// per combo; the build callback runs fresh on each open.</summary>
    public static void Attach(ComboBox? combo, Func<IReadOnlyList<ComboMenuItem>> build)
    {
        if (combo == null || build == null) return;
        combo.ContextRequested += (_, e) => Show(combo, build, e);
    }

    private static void Show(ComboBox combo, Func<IReadOnlyList<ComboMenuItem>>? build, ContextRequestedEventArgs e)
    {
        if (build == null) return;

        // Close the dropdown first so the flyout isn't fighting it for
        // the pointer (matches WinForms which drops DroppedDown).
        if (combo.IsDropDownOpen) combo.IsDropDownOpen = false;

        var items = build();
        if (items == null || items.Count == 0) return;

        var flyout = new MenuFlyout();
        foreach (var it in items)
        {
            if (it.IsSeparator)
            {
                flyout.Items.Add(new Separator());
                continue;
            }
            var captured = it;
            var mi = new MenuItem
            {
                // "✓ " prefix marks the active mode; pad others so headers align.
                Header = (it.IsChecked ? "✓ " : "    ") + it.Header,
            };
            mi.Click += (_, _) => captured.Invoke?.Invoke();
            flyout.Items.Add(mi);
        }

        flyout.ShowAt(combo, showAtPointer: true);
        e.Handled = true;
    }
}
