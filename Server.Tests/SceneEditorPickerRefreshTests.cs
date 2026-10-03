// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1057 — Scene Editor Wave 0 prep: picker-list refresh after a library change,
// and the reusable Region / Theme combo sort state extracted from
// FloatingMenuViewModel.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SceneEditorPickerRefreshTests
{
    [Fact]
    public void RefreshNameLists_AddsNewNames_AndKeepsSelection()
    {
        var (svc, state) = LibraryService.Create();
        state.Animations = new() { "Spin" };
        var vm = new SceneEditorViewModel(svc);
        var row = vm.Shots.Single();
        row.SelectedAnimation = "Spin";

        state.Animations = new() { "Spin", "Pulse" };
        vm.RefreshNameLists();

        Assert.Contains("Pulse", row.AnimationNames);
        Assert.Equal("Spin", row.SelectedAnimation);
        Assert.Equal("Spin", row.ToShot().AnimationName);
    }

    [Fact]
    public void RefreshNameLists_DeletedName_FallsBackToSentinel()
    {
        var (svc, state) = LibraryService.Create();
        state.Regions = new() { "Seahorse", "Elephant" };
        state.Themes = new() { "Fire" };
        var vm = new SceneEditorViewModel(svc);
        var row = vm.Shots.Single();
        row.SelectedRegion = "Seahorse";
        // A legacy lighting-region borrow (#1059 keeps it selectable until changed).
        row.SelectedLighting = SceneShotRowViewModel.LightingLegacyRegionPrefix + "Seahorse";
        row.SelectedTheme = "Fire";

        state.Regions = new() { "Elephant" };
        state.Themes = new();
        vm.RefreshNameLists();

        Assert.Equal(SceneShotRowViewModel.RegionNone, row.SelectedRegion);
        Assert.Equal(SceneShotRowViewModel.LightingNone, row.SelectedLighting);
        Assert.Equal(SceneShotRowViewModel.ThemeNone, row.SelectedTheme);
        var shot = row.ToShot();
        Assert.Equal(string.Empty, shot.RegionName);
        Assert.Null(shot.LightingRegionName);
        Assert.Null(shot.ThemeName);
    }

    [Fact]
    public void RefreshNameLists_ReachesRowsAddedBeforeAndAfter()
    {
        var (svc, state) = LibraryService.Create();
        var vm = new SceneEditorViewModel(svc);
        vm.AddShotCommand.Execute().Subscribe();

        state.Regions = new() { "New" };
        vm.RefreshNameLists();
        Assert.All(vm.Shots, r => Assert.Contains("New", r.RegionNames));

        vm.AddShotCommand.Execute().Subscribe();
        Assert.Contains("New", vm.Shots.Last().RegionNames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("— select region —")]
    [InlineData("— Gradient —")]
    public void SelectionSetters_IgnoreNullAndHeaderRows(string? value)
    {
        var (svc, state) = LibraryService.Create();
        state.Regions = new() { "Seahorse" };
        var vm = new SceneEditorViewModel(svc);
        var row = vm.Shots.Single();
        row.SelectedRegion = "Seahorse";

        // A ComboBox pushes null while its ItemsSource is swapped; a sort-aware
        // list carries "— header —" rows. Neither may clobber the selection.
        row.SelectedRegion = value!;
        row.SelectedTheme = value!;

        Assert.Equal("Seahorse", row.SelectedRegion);
        Assert.Equal(SceneShotRowViewModel.ThemeNone, row.SelectedTheme);
    }

    [Fact]
    public void RegionComboSort_MenuPick_ChangesEnumerationAndChecksRow()
    {
        var (svc, state) = LibraryService.Create();
        var sort = new RegionComboSort();
        int changed = 0;

        sort.Enumerate(svc);
        Assert.Equal((RegionSortMode.Default, FractalType.Mandelbrot), state.LastRegionQuery);

        var menu = sort.BuildMenu(() => changed++);
        menu.Single(i => i.Header == nameof(FractalType.Mandelbulb)).Invoke!();
        sort.Enumerate(svc);

        Assert.Equal(1, changed);
        Assert.Equal((RegionSortMode.ByFractalType, FractalType.Mandelbulb), state.LastRegionQuery);
        var rebuilt = sort.BuildMenu(() => { });
        Assert.True(rebuilt.Single(i => i.Header == nameof(FractalType.Mandelbulb)).IsChecked);
        Assert.False(rebuilt.Single(i => i.Header == "Default").IsChecked);
    }

    [Fact]
    public void ThemeComboSort_PassesCompatTypeOnlyInCompatMode()
    {
        var (svc, state) = LibraryService.Create();
        state.Kinds = new() { "Gradient" };
        var sort = new ThemeComboSort { CompatFractalType = FractalType.Kifs };

        sort.Enumerate(svc);
        Assert.Equal((ThemeSortMode.Default, (string?)null, (FractalType?)null), state.LastThemeQuery);

        sort.BuildMenu(svc, () => { }).Single(i => i.Header.StartsWith("Compatible", StringComparison.Ordinal)).Invoke!();
        sort.Enumerate(svc);
        Assert.Equal((ThemeSortMode.ByFractalCompat, (string?)null, (FractalType?)FractalType.Kifs), state.LastThemeQuery);

        sort.BuildMenu(svc, () => { }).Single(i => i.Header == "Gradient").Invoke!();
        sort.Enumerate(svc);
        Assert.Equal((ThemeSortMode.ByKind, (string?)"Gradient", (FractalType?)null), state.LastThemeQuery);
    }

    [Theory]
    [InlineData("— Gradient —", true)]
    [InlineData("— select region —", true)]
    [InlineData("Seahorse", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ComboSort_IsHeader(string? s, bool expected) => Assert.Equal(expected, ComboSort.IsHeader(s));

    // ── Fake library ────────────────────────────────────────────────────────

    public sealed class LibraryState
    {
        public List<string> Regions { get; set; } = new();
        public List<string> Themes { get; set; } = new();
        public List<string> Animations { get; set; } = new();
        public List<string> Kinds { get; set; } = new();
        public (RegionSortMode, FractalType)? LastRegionQuery { get; set; }
        public int SortedRegionCalls { get; set; }
        /// <summary>Optional per-type region membership for ByFractalType queries.</summary>
        public Dictionary<string, FractalType> RegionTypes { get; } = new();
        /// <summary>#1058 / #1049 — GetRegionSceneInfo answers.</summary>
        public Dictionary<string, (FractalType Type, bool Relief3D)> RegionInfo { get; } = new();
        public (ThemeSortMode, string?, FractalType?)? LastThemeQuery { get; set; }
    }

    // IColorThemeService proxy: the enumerations the editor / sort objects call
    // read LibraryState; everything else returns an empty / default value.
    public class LibraryService : DispatchProxy
    {
        private LibraryState _state = null!;

        public static (IColorThemeService Service, LibraryState State) Create()
        {
            var state = new LibraryState();
            var svc = DispatchProxy.Create<IColorThemeService, LibraryService>();
            ((LibraryService)(object)svc)._state = state;
            return (svc, state);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IColorThemeService.EnumerateRegionNames) when args!.Length == 0:
                    return _state.Regions.ToArray();
                case nameof(IColorThemeService.EnumerateRegionNames):
                    _state.SortedRegionCalls++;
                    var mode = (RegionSortMode)args[0]!;
                    var type = (FractalType)args[1]!;
                    _state.LastRegionQuery = (mode, type);
                    // Mirror the host: a "— select region —" placeholder first.
                    var list = new List<string> { "— select region —" };
                    list.AddRange(mode == RegionSortMode.ByFractalType
                        ? _state.Regions.Where(r => _state.RegionTypes.TryGetValue(r, out var t) && t == type)
                        : _state.Regions);
                    return list.ToArray();
                case nameof(IColorThemeService.EnumerateThemeNames) when args!.Length == 0:
                    return _state.Themes.ToArray();
                case nameof(IColorThemeService.EnumerateThemeNames):
                    _state.LastThemeQuery = ((ThemeSortMode)args[0]!, (string?)args[1],
                        args.Length > 3 ? (FractalType?)args[3] : null);
                    return _state.Themes.ToArray();
                case nameof(IColorThemeService.GetRegionSceneInfo):
                    return _state.RegionInfo.TryGetValue((string)args![0]!, out var info)
                        ? info : ((FractalType, bool)?)null;
                case nameof(IColorThemeService.EnumerateAnimationNames):
                    return _state.Animations.ToArray();
                case nameof(IColorThemeService.EnumerateThemeKinds):
                    return _state.Kinds.ToArray();
            }
            var rt = method.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt != typeof(string) && rt.IsAssignableFrom(typeof(string[]))) return Array.Empty<string>();
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }
}
