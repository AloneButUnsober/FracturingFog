// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/ComboMenuItem.cs
//
// Shell-neutral description of one right-click sort-menu entry for a
// Region / Color-Theme combo. The VM builds an ordered list of these
// (the WinForms combos use ContextMenuStrip / ToolStripMenuItem); the
// View layer (Controls.ComboSortMenu) renders them into an Avalonia
// MenuFlyout. Keeping the data here means UI.Avalonia VMs stay free of
// any Avalonia.Controls.MenuItem / Flyout types.

using System;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>One entry in a combo's right-click sort menu. Either a real
/// command row (<see cref="IsSeparator"/> false, <see cref="Invoke"/> set)
/// or a visual divider (<see cref="Separator"/>).</summary>
public sealed record ComboMenuItem(string Header, bool IsChecked, bool IsSeparator, Action? Invoke)
{
    /// <summary>A horizontal divider row.</summary>
    public static ComboMenuItem Separator { get; } = new(string.Empty, false, true, null);

    /// <summary>A clickable row that runs <paramref name="invoke"/> when chosen.</summary>
    public static ComboMenuItem Item(string header, bool isChecked, Action invoke)
        => new(header, isChecked, false, invoke);
}
