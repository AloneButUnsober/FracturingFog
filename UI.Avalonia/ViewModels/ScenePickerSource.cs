// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/ScenePickerSource.cs
//
// #1054 — name lists for the Scene Editor's per-shot combos. Each shot row owns
// its own Region / Theme sort state (right-click menu), so the lists are no
// longer one shared snapshot. To keep that cheap, the Default-mode lists (what
// nearly every row shows) are enumerated once per refresh and shared; only a
// row with a non-default sort calls the service itself. Invalidate() (from
// SceneEditorViewModel.RefreshNameLists, #1057) drops the cache after a
// library change.

using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class ScenePickerSource
{
    private readonly IColorThemeService _service;
    private IReadOnlyList<string>? _regionsDefault;
    private HashSet<string>? _regionSet;
    private IReadOnlyList<string>? _themesDefault;
    private HashSet<string>? _themeSet;
    private IReadOnlyList<string>? _animations;
    private IReadOnlyList<string>? _userLightingPresets;
    private readonly Dictionary<string, (FractalType Type, bool Relief3D)?> _regionInfo = new(StringComparer.Ordinal);
    private readonly Func<IEnumerable<string>> _loadUserLightingPresets;

    /// <param name="userLightingPresets">#1059 — user Lighting &amp; FX preset names;
    /// defaults to the on-disk library (lighting-fx-presets.json).</param>
    public ScenePickerSource(IColorThemeService service, Func<IEnumerable<string>>? userLightingPresets = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _loadUserLightingPresets = userLightingPresets
            ?? (() => LightingFxPresetLibrary.Load().Presets.Select(p => p.Name));
    }

    public IColorThemeService Service => _service;

    /// <summary>Drop the cached lists so the next read re-enumerates.</summary>
    public void Invalidate()
    {
        _regionsDefault = null; _regionSet = null;
        _themesDefault = null; _themeSet = null;
        _animations = null;
        _userLightingPresets = null;
        _regionInfo.Clear();
    }

    /// <summary>#1058 / #1049 — the region's fractal type + Relief 3D flag (cached
    /// per refresh), or null for an unknown region.</summary>
    public (FractalType Type, bool Relief3D)? RegionInfo(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!_regionInfo.TryGetValue(name, out var info))
            _regionInfo[name] = info = _service.GetRegionSceneInfo(name);
        return info;
    }

    /// <summary>#1059 — the user's Lighting &amp; FX preset names, A–Z.</summary>
    public IReadOnlyList<string> UserLightingPresets
        => _userLightingPresets ??= _loadUserLightingPresets()
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Region names under <paramref name="sort"/>, headers stripped.</summary>
    public IReadOnlyList<string> Regions(RegionComboSort sort)
        => sort.Mode == RegionSortMode.Default ? RegionsDefault : StripHeaders(sort.Enumerate(_service));

    /// <summary>Theme names under <paramref name="sort"/> (may hold "— Kind —" headers).</summary>
    public IReadOnlyList<string> Themes(ThemeComboSort sort)
        => sort.Mode == ThemeSortMode.Default ? ThemesDefault : sort.Enumerate(_service);

    /// <summary>Animation names, A–Z (the library has no sort modes).</summary>
    public IReadOnlyList<string> Animations
        => _animations ??= _service.EnumerateAnimationNames()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    // Existence is checked against the flat, unsorted enumerations — the full
    // library — so a name a sort view happens to omit is never mistaken for a
    // deleted one (which would silently reset the shot's pick).

    /// <summary>True when a region with this name exists (any sort / filter).</summary>
    public bool RegionExists(string name)
        => (_regionSet ??= new HashSet<string>(_service.EnumerateRegionNames(), StringComparer.Ordinal)).Contains(name);

    /// <summary>True when a theme with this name exists (any sort / filter).</summary>
    public bool ThemeExists(string name)
        => (_themeSet ??= new HashSet<string>(_service.EnumerateThemeNames(), StringComparer.Ordinal)).Contains(name);

    private IReadOnlyList<string> RegionsDefault
        => _regionsDefault ??= StripHeaders(new RegionComboSort().Enumerate(_service));

    private IReadOnlyList<string> ThemesDefault
        => _themesDefault ??= new ThemeComboSort().Enumerate(_service);

    private static List<string> StripHeaders(IReadOnlyList<string> names)
        => names.Where(n => !ComboSort.IsHeader(n)).ToList();
}
