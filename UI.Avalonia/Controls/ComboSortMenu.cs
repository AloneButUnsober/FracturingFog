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

using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Input;

using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Controls;

/// <summary>Attaches a right-click sort/filter menu to a <see cref="ComboBox"/>.</summary>
public static class ComboSortMenu
{
    /// <summary>Wire <paramref name="combo"/>'s right-click (ContextRequested)
    /// to a MenuFlyout built from <paramref name="build"/>. Safe to call once
    /// per combo; the build callback runs fresh on each open.</summary>
    public static void Attach(ComboBox? combo, Func<IReadOnlyList<ComboMenuItem>> build)
    {
        if (combo == null || build == null) return;

        combo.ContextRequested += (_, e) =>
        {
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
        };
    }
}
