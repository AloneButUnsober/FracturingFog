// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/ComboSort.cs
//
// #1057 — reusable sort state for the Region / Theme combos. The host service
// already knows how to group / sort / filter (IColorThemeService.Enumerate*
// with a sort mode); what used to be private to FloatingMenuViewModel was the
// per-combo *state* (mode + filter) and the right-click menu that edits it.
// Pulling that into small objects lets any combo own an independent sort —
// including the templated per-shot combos in the Scene Editor — without
// touching FloatingMenu state.
//
// The sort-aware lists carry non-selectable header rows ("— select region —",
// "— {kind} —"); see IsHeader. Consumers must ignore a header selection.

using System;
using System.Collections.Generic;

using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>Helpers shared by the sort-aware combos.</summary>
public static class ComboSort
{
    /// <summary>True for the non-selectable group headers / placeholders the
    /// sort-aware enumerations inject ("— Kind —", "— select region —"). The
    /// em-dash prefix matches the WinForms convention (Controls.cs).</summary>
    public static bool IsHeader(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith("—", StringComparison.Ordinal);
}

/// <summary>Sort/filter state for a region combo (Default = built-ins first then
/// user, alpha; or filtered to one <see cref="FractalType"/>).</summary>
public sealed class RegionComboSort
{
    public RegionSortMode Mode { get; private set; } = RegionSortMode.Default;
    public FractalType TypeFilter { get; private set; } = FractalType.Mandelbrot;

    /// <summary>Region names under the current state, including the leading
    /// "— select region —" placeholder the service emits.</summary>
    public IReadOnlyList<string> Enumerate(IColorThemeService service)
        => service.EnumerateRegionNames(Mode, TypeFilter);

    /// <summary>Build the right-click sort menu (Default / per-FractalType).
    /// <paramref name="onChanged"/> runs after a pick so the owner re-pulls.
    /// Mirrors Controls.ShowRegionComboSortMenu.</summary>
    public IReadOnlyList<ComboMenuItem> BuildMenu(Action onChanged)
    {
        var items = new List<ComboMenuItem>
        {
            ComboMenuItem.Item("Default", Mode == RegionSortMode.Default,
                () => { Mode = RegionSortMode.Default; onChanged(); }),
            ComboMenuItem.Separator,
        };
        foreach (var t in Enum.GetValues<FractalType>())
        {
            FractalType ft = t;
            bool chk = Mode == RegionSortMode.ByFractalType && TypeFilter == ft;
            items.Add(ComboMenuItem.Item(ft.ToString(), chk,
                () => { Mode = RegionSortMode.ByFractalType; TypeFilter = ft; onChanged(); }));
        }
        return items;
    }
}

/// <summary>Sort/filter state for a colour-theme combo (Default = grouped by
/// kind; All = flat A–Z; one kind; or compatible with a fractal type).</summary>
public sealed class ThemeComboSort
{
    private readonly bool _editableOnly;

    public ThemeComboSort(bool editableOnly = false) => _editableOnly = editableOnly;

    public ThemeSortMode Mode { get; private set; } = ThemeSortMode.Default;
    public string? Kind { get; private set; }

    /// <summary>Target of <see cref="ThemeSortMode.ByFractalCompat"/>. Owners push
    /// the relevant fractal type (live view, or a shot's type).</summary>
    public FractalType CompatFractalType { get; set; } = FractalType.Mandelbrot;

    /// <summary>Theme names under the current state (may include "— Kind —"
    /// group headers).</summary>
    public IReadOnlyList<string> Enumerate(IColorThemeService service)
    {
        FractalType? compat = Mode == ThemeSortMode.ByFractalCompat ? CompatFractalType : null;
        return service.EnumerateThemeNames(Mode, Kind, _editableOnly, compat);
    }

    /// <summary>Build the right-click sort menu (Default / All / compatible /
    /// per-kind). <paramref name="service"/> supplies the kind list (null = no
    /// kind rows). Mirrors Controls.ShowColorComboSortMenu.</summary>
    public IReadOnlyList<ComboMenuItem> BuildMenu(IColorThemeService? service, Action onChanged)
    {
        var items = new List<ComboMenuItem>
        {
            ComboMenuItem.Item("Default", Mode == ThemeSortMode.Default,
                () => { Mode = ThemeSortMode.Default; onChanged(); }),
            ComboMenuItem.Item("All (A–Z)", Mode == ThemeSortMode.All,
                () => { Mode = ThemeSortMode.All; onChanged(); }),
            ComboMenuItem.Item(
                $"Compatible with {CompatFractalType}",
                Mode == ThemeSortMode.ByFractalCompat,
                () => { Mode = ThemeSortMode.ByFractalCompat; onChanged(); }),
            ComboMenuItem.Separator,
        };
        if (service != null)
            foreach (var kind in service.EnumerateThemeKinds())
            {
                string k = kind;
                bool chk = Mode == ThemeSortMode.ByKind && Kind == k;
                items.Add(ComboMenuItem.Item(k, chk,
                    () => { Mode = ThemeSortMode.ByKind; Kind = k; onChanged(); }));
            }
        return items;
    }
}
