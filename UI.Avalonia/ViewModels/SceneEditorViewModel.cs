// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/SceneEditorViewModel.cs
//
// Scene Engine Roadmap Phase S5. Editor VM for SceneData assets. Mirrors the
// AnimationEditorViewModel shape: load-existing / new-blank / revert / save /
// delete, plus a per-shot Preview that asks the shell to apply a shot to the
// live view. Persistence + the per-shot asset pickers route through
// IColorThemeService so the VM never references the Engine project (where
// SceneLibrary lives).
//
// A Scene is an ordered list of shots; each shot is one SceneShotRowViewModel
// (region / theme / animation / fractal-type / duration / transition, plus an
// optional keyframed orbit camera for the 3D types). Camera keys are edited
// numerically here (add / edit / delete). Pixel-drag of keyframe handles and
// the horizontal filmstrip's scrub bar are S8 polish — S5 ships the data-
// complete editor. Multi-shot sequenced playback with transitions is S6.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Abstractions.Assets;
using FracturingFog.Audio;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>One keyframe row in a shot's camera track. Bound to the per-key row
/// in the .axaml. Removing routes back to the owning shot via the supplied
/// action.</summary>
public sealed class CameraKeyRowViewModel : ReactiveObject
{
    private readonly Action _onChanged;

    public CameraKeyRowViewModel(CameraKey key, Action onChanged, Action<CameraKeyRowViewModel> onRemove)
    {
        _onChanged = onChanged;
        _time = key.Time;
        _distance = key.State.Distance;
        _theta = key.State.Theta;
        _phi = key.State.Phi;
        _ease = key.Ease;
        RemoveCommand = ReactiveCommand.Create(() => onRemove(this));
    }

    private static readonly IReadOnlyList<CameraEase> _easeKinds = Enum.GetValues<CameraEase>();
    /// <summary>The per-key ease options (D.1), for the key row's combo.</summary>
    public IReadOnlyList<CameraEase> EaseKinds => _easeKinds;

    private double _time;
    public double Time
    {
        get => _time;
        set { this.RaiseAndSetIfChanged(ref _time, value); _onChanged(); }
    }

    private double _distance;
    public double Distance
    {
        get => _distance;
        set { this.RaiseAndSetIfChanged(ref _distance, value); _onChanged(); }
    }

    private double _theta;
    public double Theta
    {
        get => _theta;
        set { this.RaiseAndSetIfChanged(ref _theta, value); _onChanged(); }
    }

    private double _phi;
    public double Phi
    {
        get => _phi;
        set { this.RaiseAndSetIfChanged(ref _phi, value); _onChanged(); }
    }

    private CameraEase _ease;
    /// <summary>Time easing for the segment starting at this key (D.1).</summary>
    public CameraEase Ease
    {
        get => _ease;
        set { this.RaiseAndSetIfChanged(ref _ease, value); _onChanged(); }
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public CameraKey ToKey() => new(_time, new CameraState(_distance, _theta, _phi)) { Ease = _ease };
}

/// <summary>One shot row in the editor's shots list. Carries the shot's
/// authored fields plus its optional camera track. Each row owns its picker
/// lists + sort state (#1054); the names come from a <see cref="ScenePickerSource"/>
/// shared by every row, which caches the default-sorted lists.</summary>
public sealed class SceneShotRowViewModel : ReactiveObject
{
    // Combo sentinels — empty region = render default params for the type;
    // empty theme / animation = the region's own. Combos can't bind a blank
    // string cleanly, so a display sentinel stands in for "none".
    public const string RegionNone = "(default params)";
    public const string ThemeNone = "(region default)";
    public const string AnimationNone = "(none)";
    /// <summary>Lighting combo sentinel — no per-shot lighting override, so the
    /// shot lights from its own region. Maps to no preset and no legacy region.</summary>
    public const string LightingNone = "(use shot's region)";

    // #1059 — Lighting combo items. Built-in and user presets can share a name,
    // so the kind is part of the display string (ASCII prefixes).
    public const string LightingBuiltInPrefix = "Built-in: ";
    public const string LightingUserPrefix = "Mine: ";
    /// <summary>Old scenes borrow another region's lighting (LightingRegionName);
    /// such a pick stays visible + selectable under this prefix until changed.</summary>
    public const string LightingLegacyRegionPrefix = "Region (legacy): ";
    /// <summary>Tone-map combo sentinel — inherit the region lighting's operator
    /// rather than pin one on the shot.</summary>
    public const string ToneMapInherit = "(region default)";

    private readonly Action _onChanged;
    private readonly ScenePickerSource _source;
    private readonly IReadOnlyList<FractalType> _allFractalTypes;
    private readonly Action<SceneEditAssetEventArgs> _onEditAsset;

    // #1054 — per-row sort / filter state behind each combo's right-click menu.
    private readonly RegionComboSort _regionSort = new();
    private readonly ThemeComboSort _themeSort = new();
    private MainViewModel.FractalTypeFilter _fractalFilter = MainViewModel.FractalTypeFilter.Default;

    public SceneShotRowViewModel(
        ScenePickerSource source,
        IReadOnlyList<FractalType> fractalTypes,
        IReadOnlyList<SceneTransitionKind> transitionKinds,
        Action onChanged,
        Action<SceneShotRowViewModel> onRemove,
        Action<SceneShotRowViewModel> onMoveUp,
        Action<SceneShotRowViewModel> onMoveDown,
        Action<SceneShotRowViewModel> onPreview,
        Action<SceneEditAssetEventArgs>? onEditAsset = null,
        Action<SceneShotRowViewModel>? onCaptureCameraKey = null)
    {
        _onChanged = onChanged;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _allFractalTypes = fractalTypes;
        _onEditAsset = onEditAsset ?? (_ => { });
        TransitionKinds = transitionKinds;

        RemoveCommand    = ReactiveCommand.Create(() => onRemove(this));
        MoveUpCommand    = ReactiveCommand.Create(() => onMoveUp(this));
        MoveDownCommand  = ReactiveCommand.Create(() => onMoveDown(this));
        PreviewCommand   = ReactiveCommand.Create(() => onPreview(this));
        EditAnimationCommand = ReactiveCommand.Create(() => _onEditAsset(new SceneEditAssetEventArgs(AssetKind.Animation, SelectedAnimationName)));
        EditLightingCommand = ReactiveCommand.Create(EditLighting);
        AddCameraKeyCommand = ReactiveCommand.Create(AddCameraKey);
        CaptureCameraKeyCommand = ReactiveCommand.Create(() => onCaptureCameraKey?.Invoke(this));

        CameraKeys = new ObservableCollection<CameraKeyRowViewModel>();

        RegionMenu = BuildRegionMenu;
        LightingMenu = BuildLightingMenu;
        ThemeMenu = BuildThemeMenu;
        AnimationMenu = BuildAnimationMenu;
        FractalTypeMenu = BuildFractalTypeMenu;

        RebuildPickers();
    }

    // ── Picker sources (#1054 per-row, #1057 refreshable) ────────────────────
    private IReadOnlyList<string> _regionNames = Array.Empty<string>();
    private IReadOnlyList<string> _themeNames = Array.Empty<string>();
    private IReadOnlyList<string> _animationNames = Array.Empty<string>();
    private IReadOnlyList<string> _lightingOptions = Array.Empty<string>();
    private IReadOnlyList<FractalType> _fractalTypes = Array.Empty<FractalType>();

    public IReadOnlyList<string> RegionNames => _regionNames;
    public IReadOnlyList<string> ThemeNames => _themeNames;
    public IReadOnlyList<string> AnimationNames => _animationNames;
    /// <summary>#1059 — Lighting combo items: <see cref="LightingNone"/>, then the
    /// built-in presets (catalogue order), then the user's presets (A–Z), plus a
    /// legacy region borrow when the shot still carries one.</summary>
    public IReadOnlyList<string> LightingOptions => _lightingOptions;
    /// <summary>Fractal types under this row's filter (right-click menu).</summary>
    public IReadOnlyList<FractalType> FractalTypes => _fractalTypes;

    // Right-click menu builders, bound through ComboSortMenu.MenuSource.
    public Func<IReadOnlyList<ComboMenuItem>> RegionMenu { get; }
    public Func<IReadOnlyList<ComboMenuItem>> LightingMenu { get; }
    public Func<IReadOnlyList<ComboMenuItem>> ThemeMenu { get; }
    public Func<IReadOnlyList<ComboMenuItem>> AnimationMenu { get; }
    public Func<IReadOnlyList<ComboMenuItem>> FractalTypeMenu { get; }

    /// <summary>#1057 — re-pull every picker list after a library change (the
    /// shared source was invalidated by the editor), keeping each selection that
    /// still exists and falling back to the "none" sentinel for a deleted one.</summary>
    public void RefreshPickers()
    {
        if (!IsSentinel(_selectedRegion, RegionNone) && !_source.RegionExists(_selectedRegion))
            _selectedRegion = RegionNone;
        if (!LightingPickExists(_selectedLighting)) _selectedLighting = LightingNone;
        if (!IsSentinel(_selectedTheme, ThemeNone) && !_source.ThemeExists(_selectedTheme))
            _selectedTheme = ThemeNone;
        if (!IsSentinel(_selectedAnimation, AnimationNone) && !_source.Animations.Contains(_selectedAnimation))
            _selectedAnimation = AnimationNone;
        RebuildPickers();
        SyncFromRegion();
    }

    // Rebuild each list from its sort state. ItemsSource is raised before the
    // selection so the combo re-selects against the new list (the selection
    // setters ignore the transient null a combo pushes during the swap). A
    // selection the current sort/filter hides — not deleted, just filtered — is
    // appended so the combo never goes blank and the pick is never lost.
    private void RebuildPickers()
    {
        RebuildRegions(); RebuildLighting(); RebuildThemes(); RebuildAnimations(); RebuildFractalTypes();
    }

    private void RebuildRegions()
    {
        _regionNames = WithSentinel(RegionNone, _source.Regions(_regionSort), _selectedRegion);
        RaiseList(nameof(RegionNames), nameof(SelectedRegion));
    }

    private void RebuildLighting()
    {
        var items = new List<string> { LightingNone };
        foreach (var p in FracturingFog.Rendering.Lighting.VolumetricFxPresets.All)
            items.Add(LightingBuiltInPrefix + p.Name);
        foreach (var n in _source.UserLightingPresets) items.Add(LightingUserPrefix + n);
        if (!IsSentinel(_selectedLighting, LightingNone) && !items.Contains(_selectedLighting))
            items.Add(_selectedLighting); // legacy region borrow (or a pick refreshed away)
        _lightingOptions = items;
        RaiseList(nameof(LightingOptions), nameof(SelectedLighting));
    }

    // Does the item still name something that exists?
    private bool LightingPickExists(string item)
    {
        if (IsSentinel(item, LightingNone)) return true;
        if (item.StartsWith(LightingBuiltInPrefix, StringComparison.Ordinal))
            return SceneShotLighting.IsBuiltIn(item[LightingBuiltInPrefix.Length..]);
        if (item.StartsWith(LightingUserPrefix, StringComparison.Ordinal))
            return _source.UserLightingPresets.Contains(item[LightingUserPrefix.Length..]);
        if (item.StartsWith(LightingLegacyRegionPrefix, StringComparison.Ordinal))
            return _source.RegionExists(item[LightingLegacyRegionPrefix.Length..]);
        return false;
    }

    /// <summary>#1059 / #1060 — what the Lighting combo currently picks.</summary>
    public (string? PresetName, bool BuiltIn, string? LegacyRegion) LightingPick
    {
        get
        {
            var item = _selectedLighting;
            if (item.StartsWith(LightingBuiltInPrefix, StringComparison.Ordinal))
                return (item[LightingBuiltInPrefix.Length..], true, null);
            if (item.StartsWith(LightingUserPrefix, StringComparison.Ordinal))
                return (item[LightingUserPrefix.Length..], false, null);
            if (item.StartsWith(LightingLegacyRegionPrefix, StringComparison.Ordinal))
                return (null, false, item[LightingLegacyRegionPrefix.Length..]);
            return (null, false, null);
        }
    }

    private void RebuildThemes()
    {
        _themeNames = WithSentinel(ThemeNone, _source.Themes(_themeSort), _selectedTheme);
        RaiseList(nameof(ThemeNames), nameof(SelectedTheme));
    }

    private void RebuildAnimations()
    {
        _animationNames = WithSentinel(AnimationNone, _source.Animations, _selectedAnimation);
        RaiseList(nameof(AnimationNames), nameof(SelectedAnimation));
    }

    private void RebuildFractalTypes()
    {
        var list = _allFractalTypes
            .Where(t => MainViewModel.MatchesFractalFilter(t, _fractalFilter))
            .ToList();
        if (!list.Contains(_fractalType)) list.Add(_fractalType);
        _fractalTypes = list;
        RaiseList(nameof(FractalTypes), nameof(FractalType));
    }

    private void RaiseList(string listName, string selectionName)
    {
        this.RaisePropertyChanged(listName);
        this.RaisePropertyChanged(selectionName);
    }

    private static List<string> WithSentinel(string sentinel, IReadOnlyList<string> names, string current)
    {
        var list = new List<string>(names.Count + 2) { sentinel };
        list.AddRange(names);
        if (!IsSentinel(current, sentinel) && !list.Contains(current)) list.Add(current);
        return list;
    }

    private static bool IsSentinel(string value, string sentinel)
        => string.Equals(value, sentinel, StringComparison.Ordinal);

    // ── Right-click menus (#1054) ────────────────────────────────────────────
    // Same entries as the toolbar / Control Center combos: an "Edit …" row for
    // the picked asset, then that combo's sort / filter modes.

    private IReadOnlyList<ComboMenuItem> BuildRegionMenu()
        => WithEdit("Edit region…", AssetKind.Region, SelectedRegionName,
            _regionSort.BuildMenu(RebuildRegions));

    private IReadOnlyList<ComboMenuItem> BuildLightingMenu()
    {
        var (preset, _, legacy) = LightingPick;
        if (legacy != null)
            return WithEdit("Edit region…", AssetKind.Region, legacy, Array.Empty<ComboMenuItem>());
        return preset == null
            ? Array.Empty<ComboMenuItem>()
            : new[] { ComboMenuItem.Item("Edit preset…", false, EditLighting) };
    }

    // #1060 — open the Lighting & FX dialog on this shot's preset (or a legacy
    // region in the Region Editor). No pick → just open the dialog.
    private void EditLighting()
    {
        var (preset, builtIn, legacy) = LightingPick;
        if (legacy != null) { _onEditAsset(new SceneEditAssetEventArgs(AssetKind.Region, legacy)); return; }
        _onEditAsset(new SceneEditAssetEventArgs(AssetKind.LightingFx, preset, builtIn));
    }

    private IReadOnlyList<ComboMenuItem> BuildThemeMenu()
        => WithEdit("Edit theme…", AssetKind.ColorTheme, SelectedThemeName,
            _themeSort.BuildMenu(_source.Service, RebuildThemes));

    private IReadOnlyList<ComboMenuItem> BuildAnimationMenu()
        => WithEdit("Edit animation…", AssetKind.Animation, SelectedAnimationName,
            Array.Empty<ComboMenuItem>());

    private IReadOnlyList<ComboMenuItem> BuildFractalTypeMenu()
    {
        ComboMenuItem Filter(string header, MainViewModel.FractalTypeFilter f) =>
            ComboMenuItem.Item(header, _fractalFilter == f,
                () => { _fractalFilter = f; RebuildFractalTypes(); });
        return new List<ComboMenuItem>
        {
            Filter("Default", MainViewModel.FractalTypeFilter.Default),
            ComboMenuItem.Separator,
            Filter("2D",      MainViewModel.FractalTypeFilter.TwoD),
            Filter("3D",      MainViewModel.FractalTypeFilter.ThreeD),
            Filter("User",    MainViewModel.FractalTypeFilter.User),
            Filter("CalcGen", MainViewModel.FractalTypeFilter.CalcGen),
        };
    }

    // "Edit …" is offered only when a real asset is picked (not the sentinel).
    private IReadOnlyList<ComboMenuItem> WithEdit(string header, AssetKind kind, string? name,
        IReadOnlyList<ComboMenuItem> sortItems)
    {
        var items = new List<ComboMenuItem>();
        if (!string.IsNullOrEmpty(name))
        {
            items.Add(ComboMenuItem.Item(header, false, () => _onEditAsset(new SceneEditAssetEventArgs(kind, name))));
            if (sortItems.Count > 0) items.Add(ComboMenuItem.Separator);
        }
        items.AddRange(sortItems);
        return items;
    }

    /// <summary>The selected region / lighting region / theme name, or null for
    /// the "none" sentinel.</summary>
    public string? SelectedRegionName => IsSentinel(_selectedRegion, RegionNone) ? null : _selectedRegion;
    public string? SelectedThemeName => IsSentinel(_selectedTheme, ThemeNone) ? null : _selectedTheme;

    /// <summary>True when a combo write should be ignored: the transient null a
    /// ComboBox pushes while its ItemsSource is swapped, or a non-selectable
    /// "— header —" row from a sort-aware list.</summary>
    private static bool IsUnselectable(string? value)
        => value is null || ComboSort.IsHeader(value);
    public IReadOnlyList<SceneTransitionKind> TransitionKinds { get; }

    // ── Fields ───────────────────────────────────────────────────────────────

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); _onChanged(); }
    }

    private string _selectedRegion = RegionNone;
    public string SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (IsUnselectable(value)) return;
            this.RaiseAndSetIfChanged(ref _selectedRegion, value);
            SyncFromRegion();
            _onChanged();
        }
    }

    // ── #1058 / #1049 — the shot's region decides its type + camera ─────────
    private bool _isRelief3D;

    /// <summary>The shot's region is a Relief 3D raymarch, so its camera track
    /// drives the oblique relief camera (#1049).</summary>
    public bool IsRelief3D => _isRelief3D;

    /// <summary>True when a real region is picked. The export renders the
    /// region's fractal type whatever the shot says, so the Fractal combo is
    /// only editable for a region-free ("default params") shot (#1058).</summary>
    public bool HasRegion => !IsSentinel(_selectedRegion, RegionNone);
    public bool IsFractalTypeEditable => !HasRegion;

    /// <summary>Camera section header.</summary>
    public string CameraHeader => _isRelief3D ? "Camera (Relief 3D)" : "Camera";

    // Pull the picked region's type + Relief 3D flag onto the row. Before #1058
    // picking a region left Fractal at whatever it was, so the camera row could
    // be hidden for a 3D region (or bind the wrong fractal's camera fields).
    private void SyncFromRegion()
    {
        var info = HasRegion ? _source.RegionInfo(_selectedRegion) : null;
        _isRelief3D = info?.Relief3D ?? false;
        if (info is { } i && i.Type != _fractalType) FractalType = i.Type; // raises Supports3DCamera
        if (!_fractalTypes.Contains(_fractalType)) RebuildFractalTypes();
        this.RaisePropertyChanged(nameof(IsRelief3D));
        this.RaisePropertyChanged(nameof(HasRegion));
        this.RaisePropertyChanged(nameof(IsFractalTypeEditable));
        this.RaisePropertyChanged(nameof(CameraHeader));
        this.RaisePropertyChanged(nameof(Supports3DCamera));
    }

    private string _selectedTheme = ThemeNone;
    public string SelectedTheme
    {
        get => _selectedTheme;
        set { if (IsUnselectable(value)) return; this.RaiseAndSetIfChanged(ref _selectedTheme, value); _onChanged(); }
    }

    private string _selectedLighting = LightingNone;
    /// <summary>Selected per-shot lighting-override region name, or the none
    /// sentinel. Maps to <see cref="SceneShot.LightingRegionName"/> (null when
    /// none) — the shot borrows that region's lighting without changing its own
    /// region/params.</summary>
    /// <summary>#1059 — the Lighting combo's selected item (see <see cref="LightingOptions"/>).</summary>
    public string SelectedLighting
    {
        get => _selectedLighting;
        set { if (IsUnselectable(value)) return; this.RaiseAndSetIfChanged(ref _selectedLighting, value); _onChanged(); }
    }

    private string _selectedAnimation = AnimationNone;
    public string SelectedAnimation
    {
        get => _selectedAnimation;
        set { if (IsUnselectable(value)) return; this.RaiseAndSetIfChanged(ref _selectedAnimation, value); _onChanged(); }
    }

    private FractalType _fractalType = FractalType.Mandelbrot;
    public FractalType FractalType
    {
        get => _fractalType;
        set
        {
            if (_fractalType == value) return;
            this.RaiseAndSetIfChanged(ref _fractalType, value);
            this.RaisePropertyChanged(nameof(Supports3DCamera));
            // The theme combo's "Compatible with …" mode tracks the shot's type.
            _themeSort.CompatFractalType = value;
            if (_themeSort.Mode == ThemeSortMode.ByFractalCompat) RebuildThemes();
            _onChanged();
        }
    }

    private bool _rotateThemes;
    /// <summary>#1052 — cycle the region's curated themes during the shot.</summary>
    public bool RotateThemes
    {
        get => _rotateThemes;
        set { this.RaiseAndSetIfChanged(ref _rotateThemes, value); _onChanged(); }
    }

    private double _themeRotateSeconds = SceneThemeSchedule.DefaultRotateSeconds;
    /// <summary>#1052 — seconds per theme while rotating.</summary>
    public double ThemeRotateSeconds
    {
        get => _themeRotateSeconds;
        set { this.RaiseAndSetIfChanged(ref _themeRotateSeconds, value); _onChanged(); }
    }

    private bool _isPreviewing;
    /// <summary>#1052 — this shot's Preview is live on the main view; the
    /// Preview button reads "■ Stop" and stops it.</summary>
    public bool IsPreviewing
    {
        get => _isPreviewing;
        set
        {
            if (_isPreviewing == value) return;
            this.RaiseAndSetIfChanged(ref _isPreviewing, value);
            this.RaisePropertyChanged(nameof(PreviewButtonText));
        }
    }

    public string PreviewButtonText => _isPreviewing ? "■ Stop" : "Preview";

    private double _durationSeconds = 5.0;
    public double DurationSeconds
    {
        get => _durationSeconds;
        set { this.RaiseAndSetIfChanged(ref _durationSeconds, value); _onChanged(); }
    }

    private SceneTransitionKind _transition = SceneTransitionKind.Crossfade;
    public SceneTransitionKind Transition
    {
        get => _transition;
        set { this.RaiseAndSetIfChanged(ref _transition, value); _onChanged(); }
    }

    private double _transitionSeconds = 1.0;
    public double TransitionSeconds
    {
        get => _transitionSeconds;
        set { this.RaiseAndSetIfChanged(ref _transitionSeconds, value); _onChanged(); }
    }

    private CameraInterpolation _interpolation = CameraInterpolation.CatmullRom;
    public CameraInterpolation Interpolation
    {
        get => _interpolation;
        set { this.RaiseAndSetIfChanged(ref _interpolation, value); _onChanged(); }
    }

    public IReadOnlyList<CameraInterpolation> InterpolationKinds { get; } =
        Enum.GetValues<CameraInterpolation>();

    private static readonly IReadOnlyList<string> _toneMapOptions = BuildToneMapOptions();
    /// <summary>Tone-map combo source — the inherit sentinel then every operator.
    /// A shot pins its own HDR tone-map or inherits the region's (S8).</summary>
    public IReadOnlyList<string> ToneMapOptions => _toneMapOptions;

    private static IReadOnlyList<string> BuildToneMapOptions()
    {
        var list = new List<string>(5) { ToneMapInherit };
        foreach (var op in Enum.GetValues<ToneMapOperator>()) list.Add(op.ToString());
        return list;
    }

    private string _selectedToneMap = ToneMapInherit;
    /// <summary>Selected tone-map operator name, or the inherit sentinel. Maps to
    /// <see cref="SceneShot.ToneMap"/> (null when inheriting).</summary>
    public string SelectedToneMap
    {
        get => _selectedToneMap;
        set { this.RaiseAndSetIfChanged(ref _selectedToneMap, value); _onChanged(); }
    }

    /// <summary>True when this shot's fractal type has an orbit camera to drive
    /// (the raymarch 3D types). Hides the camera row for 2D shots.</summary>
    public bool Supports3DCamera => _isRelief3D || CameraParamBinding.Supports(_fractalType);

    public ObservableCollection<CameraKeyRowViewModel> CameraKeys { get; }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }
    /// <summary>#1055 — open the Animation Editor on this shot's animation.</summary>
    public ReactiveCommand<Unit, Unit> EditAnimationCommand { get; }
    /// <summary>#1060 — open the Lighting &amp; FX dialog on this shot's preset.</summary>
    public ReactiveCommand<Unit, Unit> EditLightingCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCameraKeyCommand { get; }
    /// <summary>#1049 — add a key from the live view's current camera.</summary>
    public ReactiveCommand<Unit, Unit> CaptureCameraKeyCommand { get; }

    /// <summary>The selected animation name, or null for the "(none)" sentinel.</summary>
    public string? SelectedAnimationName => IsSentinel(_selectedAnimation, AnimationNone) ? null : _selectedAnimation;

    private void AddCameraKey()
    {
        // Seed the new key one second past the current last, mirroring its pose
        // so the user tweaks from a sane starting point rather than zeros.
        double time = CameraKeys.Count > 0 ? CameraKeys[^1].Time + 1.0 : 0.0;
        var seed = CameraKeys.Count > 0
            ? new CameraState(CameraKeys[^1].Distance, CameraKeys[^1].Theta, CameraKeys[^1].Phi)
            : DefaultCameraSeed;
        AddKeyRow(new CameraKey(time, seed));
        _onChanged();
    }

    // First-key pose: the relief camera's defaults (zoom 1, azimuth 0, 45°
    // elevation) or a typical 3D orbit.
    private CameraState DefaultCameraSeed => _isRelief3D
        ? new CameraState(1.0, 0.0, 45.0 * Math.PI / 180.0)
        : new CameraState(2.6, 0.0, 0.3);

    /// <summary>#1049 — append a key at <paramref name="state"/>, one second after
    /// the last key (or at 0).</summary>
    public void AddCapturedCameraKey(CameraState state)
    {
        double time = CameraKeys.Count > 0 ? CameraKeys[^1].Time + 1.0 : 0.0;
        AddKeyRow(new CameraKey(time, state));
        _onChanged();
    }

    private void AddKeyRow(CameraKey key)
        => CameraKeys.Add(new CameraKeyRowViewModel(key, _onChanged, RemoveCameraKey));

    private void RemoveCameraKey(CameraKeyRowViewModel row)
    {
        CameraKeys.Remove(row);
        _onChanged();
    }

    /// <summary>Build the persistable <see cref="SceneShot"/>. The camera is
    /// emitted only for a 3D-camera type that actually has keys — a 2D shot or an
    /// empty track stays null (which is how S6 tells "no camera" apart).</summary>
    public SceneShot ToShot()
    {
        var shot = new SceneShot
        {
            Name = _name ?? string.Empty,
            RegionName = string.Equals(_selectedRegion, RegionNone, StringComparison.Ordinal)
                ? string.Empty : _selectedRegion,
            ThemeName = string.Equals(_selectedTheme, ThemeNone, StringComparison.Ordinal)
                ? null : _selectedTheme,
            LightingRegionName = LightingPick.LegacyRegion,
            LightingPresetName = LightingPick.PresetName,
            LightingPresetIsBuiltIn = LightingPick.BuiltIn,
            AnimationName = string.Equals(_selectedAnimation, AnimationNone, StringComparison.Ordinal)
                ? null : _selectedAnimation,
            FractalType = _fractalType,
            ToneMap = string.Equals(_selectedToneMap, ToneMapInherit, StringComparison.Ordinal)
                ? null
                : Enum.Parse<ToneMapOperator>(_selectedToneMap),
            RotateThemes = _rotateThemes,
            ThemeRotateSeconds = _themeRotateSeconds,
            DurationSeconds = _durationSeconds,
            Transition = _transition,
            TransitionSeconds = _transitionSeconds,
        };

        if (Supports3DCamera && CameraKeys.Count > 0)
        {
            var track = new CameraTrack { Interpolation = _interpolation };
            foreach (var k in CameraKeys) track.Add(k.ToKey());
            shot.Camera = track;
        }
        return shot;
    }

    // The combo item for a saved shot: its preset, else its legacy region borrow;
    // a name that no longer exists falls back to "none".
    private string LightingItemFor(SceneShot shot)
    {
        string item = LightingNone;
        if (!string.IsNullOrWhiteSpace(shot.LightingPresetName))
            item = (shot.LightingPresetIsBuiltIn ? LightingBuiltInPrefix : LightingUserPrefix) + shot.LightingPresetName;
        else if (!string.IsNullOrWhiteSpace(shot.LightingRegionName))
            item = LightingLegacyRegionPrefix + shot.LightingRegionName;
        return LightingPickExists(item) ? item : LightingNone;
    }

    /// <summary>Populate this row from a saved shot.</summary>
    public void Populate(SceneShot shot)
    {
        _name = shot.Name ?? string.Empty;
        _selectedRegion = string.IsNullOrEmpty(shot.RegionName) ? RegionNone
            : (_source.RegionExists(shot.RegionName) ? shot.RegionName : RegionNone);
        _selectedTheme = string.IsNullOrEmpty(shot.ThemeName) ? ThemeNone
            : (_source.ThemeExists(shot.ThemeName!) ? shot.ThemeName! : ThemeNone);
        _selectedLighting = LightingItemFor(shot);
        _selectedAnimation = string.IsNullOrEmpty(shot.AnimationName) ? AnimationNone
            : (_source.Animations.Contains(shot.AnimationName!) ? shot.AnimationName! : AnimationNone);
        _fractalType = shot.FractalType;
        _themeSort.CompatFractalType = shot.FractalType;
        _isRelief3D = false;
        _selectedToneMap = shot.ToneMap.HasValue ? shot.ToneMap.Value.ToString() : ToneMapInherit;
        _rotateThemes = shot.RotateThemes;
        _themeRotateSeconds = shot.ThemeRotateSeconds > 0 ? shot.ThemeRotateSeconds : SceneThemeSchedule.DefaultRotateSeconds;
        _durationSeconds = shot.DurationSeconds;
        _transition = shot.Transition;
        _transitionSeconds = shot.TransitionSeconds;

        CameraKeys.Clear();
        if (shot.Camera != null)
        {
            _interpolation = shot.Camera.Interpolation;
            foreach (var k in shot.Camera.Keys) AddKeyRow(k);
        }

        RebuildPickers();
        SyncFromRegion();
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(SelectedRegion));
        this.RaisePropertyChanged(nameof(SelectedTheme));
        this.RaisePropertyChanged(nameof(SelectedLighting));
        this.RaisePropertyChanged(nameof(SelectedAnimation));
        this.RaisePropertyChanged(nameof(FractalType));
        this.RaisePropertyChanged(nameof(Supports3DCamera));
        this.RaisePropertyChanged(nameof(SelectedToneMap));
        this.RaisePropertyChanged(nameof(RotateThemes));
        this.RaisePropertyChanged(nameof(ThemeRotateSeconds));
        this.RaisePropertyChanged(nameof(DurationSeconds));
        this.RaisePropertyChanged(nameof(Transition));
        this.RaisePropertyChanged(nameof(TransitionSeconds));
        this.RaisePropertyChanged(nameof(Interpolation));
    }
}

/// <summary>One scene-wide audio-reactive track row (#265 / Audio-Reactive
/// Phase 6). Edits a <see cref="SceneAudioTrack"/> — a <see cref="SceneGlobalTarget"/>
/// scalar (exposure / bloom / vignette / chromatic aberration) driven live from
/// an audio signal + shaping, applied on top of every shot. Removing routes back
/// to the owning editor via the supplied action.</summary>
public sealed class SceneAudioTrackRowViewModel : ReactiveObject
{
    private readonly Action _onChanged;
    private readonly AudioModulationBinding _binding;

    public static IReadOnlyList<SceneGlobalTarget> Targets { get; } =
        Enum.GetValues<SceneGlobalTarget>();
    public static IReadOnlyList<AudioSignalKind> Signals { get; } =
        Enum.GetValues<AudioSignalKind>();
    public static IReadOnlyList<AudioResponseCurve> Curves { get; } =
        Enum.GetValues<AudioResponseCurve>();

    public SceneAudioTrackRowViewModel(SceneAudioTrack track, Action onChanged,
        Action<SceneAudioTrackRowViewModel> onRemove)
    {
        ArgumentNullException.ThrowIfNull(track);
        _onChanged = onChanged;
        _target = track.Target;
        _binding = track.Binding ?? new AudioModulationBinding();
        RemoveCommand = ReactiveCommand.Create(() => onRemove(this));
    }

    private SceneGlobalTarget _target;
    public SceneGlobalTarget Target
    {
        get => _target;
        set { this.RaiseAndSetIfChanged(ref _target, value); _onChanged(); }
    }

    public AudioSignalKind Source
    {
        get => _binding.Source;
        set { _binding.Source = value; this.RaisePropertyChanged(); _onChanged(); }
    }

    public AudioResponseCurve Curve
    {
        get => _binding.Curve;
        set { _binding.Curve = value; this.RaisePropertyChanged(); _onChanged(); }
    }

    public bool Invert
    {
        get => _binding.Invert;
        set { _binding.Invert = value; this.RaisePropertyChanged(); _onChanged(); }
    }

    public double Gain
    {
        get => _binding.Gain;
        set { _binding.Gain = value; this.RaisePropertyChanged(); _onChanged(); }
    }

    public double OutMin
    {
        get => _binding.OutMin;
        set { _binding.OutMin = value; this.RaisePropertyChanged(); _onChanged(); }
    }

    public double OutMax
    {
        get => _binding.OutMax;
        set { _binding.OutMax = value; this.RaisePropertyChanged(); _onChanged(); }
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    /// <summary>Build the persistable track. The binding is the shared instance
    /// this row has been editing in place.</summary>
    public SceneAudioTrack ToTrack() => new() { Target = _target, Binding = _binding };
}

public sealed class SceneEditorViewModel : ViewModelBase
{
    private readonly IColorThemeService _service;
    private bool _suppressChange;
    private string? _loadedSourceName;

    public SceneEditorViewModel(IColorThemeService service, string? initialSceneName = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));

        SceneNames = new ObservableCollection<string>(SortedSceneNames());

        // Region / theme / animation names for the shot rows' combos (#1054).
        _pickers = new ScenePickerSource(_service);

        AvailableFractalTypes = Enum.GetValues<FractalType>()
            .OrderBy(ft => ft.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        TransitionKinds = Enum.GetValues<SceneTransitionKind>();

        Shots = new ObservableCollection<SceneShotRowViewModel>();
        AudioTracks = new ObservableCollection<SceneAudioTrackRowViewModel>();

        NewBlankCommand    = ReactiveCommand.Create(NewBlank);
        RevertCommand      = ReactiveCommand.Create(Revert);
        SaveCommand        = ReactiveCommand.CreateFromTask(SaveAsync);
        DeleteCommand      = ReactiveCommand.CreateFromTask(DeleteAsync);
        AddShotCommand     = ReactiveCommand.Create(AddShot);
        AddAudioTrackCommand = ReactiveCommand.Create(AddAudioTrack);
        BrowseAudioFileCommand = ReactiveCommand.CreateFromTask(BrowseAudioFileAsync);
        ClearAudioFileCommand = ReactiveCommand.Create(() => { AudioFilePath = string.Empty; });
        PlayCommand        = ReactiveCommand.Create(Play);
        ExportCommand      = ReactiveCommand.CreateFromTask(ExportAsync);
        ImportCommand      = ReactiveCommand.Create(() =>
            ImportRequested?.Invoke(this, EventArgs.Empty));
        StopPreviewCommand = ReactiveCommand.Create(StopPreview);
        ResetPreviewLightingCommand = ReactiveCommand.Create(
            () => ResetPreviewLightingRequested?.Invoke(this, EventArgs.Empty));
        CloseCommand       = ReactiveCommand.Create(() =>
        {
            StopPreview();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });

        if (!string.IsNullOrEmpty(initialSceneName) && SceneNames.Contains(initialSceneName))
        {
            _suppressChange = true;
            SelectedScene = initialSceneName;
            _suppressChange = false;
            LoadFromLibrary(initialSceneName);
        }
        else
        {
            NewBlank();
        }
    }

    /// <summary>#1057 — re-pull the shot rows' picker lists after a library
    /// change (a save, delete or import elsewhere), preserving each row's
    /// selection where the name still exists. The lists were built once at
    /// construction before, so an asset created while the editor was open never
    /// appeared.</summary>
    public void RefreshNameLists()
    {
        _pickers.Invalidate();
        bool prev = _suppressChange;
        _suppressChange = true;
        try
        {
            foreach (var row in Shots) row.RefreshPickers();
        }
        finally { _suppressChange = prev; }
    }

    /// <summary>Scene names, A–Z (#1054 — the library order made a long list
    /// hard to search).</summary>
    private IEnumerable<string> SortedSceneNames()
        => _service.EnumerateSceneNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    // ── Collections ───────────────────────────────────────────────────────────
    public ObservableCollection<string> SceneNames { get; }
    public ObservableCollection<SceneShotRowViewModel> Shots { get; }

    /// <summary>Scene-wide audio-reactive tracks (#265). Edited as a flat list —
    /// each drives one post/look scalar from an audio signal.</summary>
    public ObservableCollection<SceneAudioTrackRowViewModel> AudioTracks { get; }

    /// <summary>The loaded scene's keyframe global tracks (S8), carried through a
    /// load → save round-trip untouched — the editor has no keyframe-global UI, so
    /// preserving the reference stops a re-save from silently dropping an authored
    /// exposure ramp / bloom swell.</summary>
    private List<SceneGlobalTrack> _preservedGlobalTracks = new();

    private readonly ScenePickerSource _pickers;
    public IReadOnlyList<FractalType> AvailableFractalTypes { get; }
    public IReadOnlyList<SceneTransitionKind> TransitionKinds { get; }

    // ── Export (offline render, S8 polish) ─────────────────────────────────────
    // Tunable export knobs surfaced as fields (matches the "expose tunables"
    // preference); the host maps them onto the Engine's SceneVideoRenderer.
    public IReadOnlyList<string> EncodeOptions { get; } = new[]
    {
        "H.264 — high quality (MP4)",
        "H.264 — lossless (MP4)",
        "FFV1 — lossless (MKV)",
        GifEncodeLabel,
    };

    /// <summary>#1053 — the GIF encode option's label (built-in encoder).</summary>
    public const string GifEncodeLabel = "GIF — animated, looping (no audio)";

    private int _exportWidth = 1920;
    public int ExportWidth { get => _exportWidth; set => this.RaiseAndSetIfChanged(ref _exportWidth, value); }

    private int _exportHeight = 1080;
    public int ExportHeight { get => _exportHeight; set => this.RaiseAndSetIfChanged(ref _exportHeight, value); }

    private int _exportFps = 30;
    public int ExportFps { get => _exportFps; set => this.RaiseAndSetIfChanged(ref _exportFps, value); }

    private int _exportMotionBlur = 1;
    public int ExportMotionBlur { get => _exportMotionBlur; set => this.RaiseAndSetIfChanged(ref _exportMotionBlur, value); }

    private string _selectedEncode = "H.264 — high quality (MP4)";
    public string SelectedEncode { get => _selectedEncode; set => this.RaiseAndSetIfChanged(ref _selectedEncode, value); }

    // ── Load selection ─────────────────────────────────────────────────────────

    private string? _selectedScene;
    public string? SelectedScene
    {
        get => _selectedScene;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedScene, value);
            if (_suppressChange || string.IsNullOrEmpty(value)) return;
            LoadFromLibrary(value);
        }
    }

    private SceneShotRowViewModel? _selectedShot;
    public SceneShotRowViewModel? SelectedShot
    {
        get => _selectedShot;
        set => this.RaiseAndSetIfChanged(ref _selectedShot, value);
    }

    // ── Top-line fields ─────────────────────────────────────────────────────────

    private string _name = "My Scene";
    public string Name
    {
        get => _name;
        set { this.RaiseAndSetIfChanged(ref _name, value); FieldChanged(); }
    }

    private string _description = string.Empty;
    public string Description
    {
        get => _description;
        set { this.RaiseAndSetIfChanged(ref _description, value); FieldChanged(); }
    }

    private string _category = "User";
    public string Category
    {
        get => _category;
        set { this.RaiseAndSetIfChanged(ref _category, value); FieldChanged(); }
    }

    private string _tags = string.Empty;
    /// <summary>Comma-separated tags. Empty = no tags.</summary>
    public string Tags
    {
        get => _tags;
        set { this.RaiseAndSetIfChanged(ref _tags, value); FieldChanged(); }
    }

    private string _audioFilePath = string.Empty;
    /// <summary>Audio file that drives the scene's audio tracks during offline
    /// export (Phase 7 / #266). Empty = audio-silent export. Live playback is
    /// unaffected (it uses the running capture source).</summary>
    public string AudioFilePath
    {
        get => _audioFilePath;
        set { this.RaiseAndSetIfChanged(ref _audioFilePath, value); this.RaisePropertyChanged(nameof(HasAudioFile)); FieldChanged(); }
    }

    /// <summary>True when an export audio file is set — drives the Clear button.</summary>
    public bool HasAudioFile => !string.IsNullOrWhiteSpace(_audioFilePath);

    private string _titleText = "Scene Editor — new";
    public string TitleText
    {
        get => _titleText;
        set => this.RaiseAndSetIfChanged(ref _titleText, value);
    }

    private string _totalDurationText = "0 s";
    /// <summary>Running total of the shots' durations, refreshed on any change.</summary>
    public string TotalDurationText
    {
        get => _totalDurationText;
        private set => this.RaiseAndSetIfChanged(ref _totalDurationText, value);
    }

    // ── Commands ─────────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> NewBlankCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> AddShotCommand { get; }
    public ReactiveCommand<Unit, Unit> AddAudioTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseAudioFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAudioFileCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> StopPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetPreviewLightingCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    // ── Events for the shell ───────────────────────────────────────────────────

    /// <summary>Fires after a successful Save so the shell can refresh the Asset
    /// Manager / scene lists.</summary>
    public event EventHandler<string>? SceneSavedToLibrary;
    public event EventHandler<string>? SceneDeletedFromLibrary;

    /// <summary>Preview a single shot: the shell applies the shot's region /
    /// theme / animation to the live view (static framing).</summary>
    public event EventHandler<SceneShot>? PreviewShotRequested;

    /// <summary>#1055 / #1054 — a shot asked to edit one of its assets: the
    /// Animation "Edit…" button (name may be null for "(none)" → just open the
    /// editor) or a combo's right-click "Edit region… / theme… / animation…".
    /// The shell opens the matching editor with the asset preloaded.</summary>
    public event EventHandler<SceneEditAssetEventArgs>? EditAssetRequested;

    /// <summary>Play the whole scene in realtime (S6): the shell walks the
    /// timeline, sequencing shots on the live view with per-shot camera + param
    /// motion on the animation bus.</summary>
    public event EventHandler<SceneData>? PlaySceneRequested;

    /// <summary>Export the whole scene to a video file offline (S8 polish): the
    /// host picks an output path and runs the Engine's frame-locked
    /// SceneVideoRenderer (motion blur + composited transitions).</summary>
    public event EventHandler<SceneExportEventArgs>? ExportSceneRequested;

    /// <summary>Import scenes from a JSON file (one scene object, or an array
    /// of them). The shell owns the import — SceneLibrary lives in Engine — and
    /// calls <see cref="RefreshSceneNames"/> back when it lands.</summary>
    public event EventHandler? ImportRequested;

    /// <summary>Raised by the "Browse…" button to pick the export audio file. The
    /// host fills <see cref="OpenFileEventArgs.Path"/> with the chosen path (async
    /// file picker), mirroring the equation editors' import prompt.</summary>
    public event Func<OpenFileEventArgs, Task>? BrowseAudioFileRequested;

    public event EventHandler? StopPreviewRequested;

    /// <summary>#1051 — the "Debug overlay" toggle changed.</summary>
    public event EventHandler<bool>? DebugOverlayChanged;

    private bool _showDebugOverlay;
    /// <summary>#1051 — show the scene debug overlay (scene clock, shot, camera
    /// key + pose, frame time) beside the render window during Play / Preview.</summary>
    public bool ShowDebugOverlay
    {
        get => _showDebugOverlay;
        set
        {
            if (_showDebugOverlay == value) return;
            this.RaiseAndSetIfChanged(ref _showDebugOverlay, value);
            DebugOverlayChanged?.Invoke(this, value);
        }
    }

    /// <summary>#307 — snap the live view's lighting back to stock defaults. A
    /// per-shot "lighting from region" borrow mutates the shared live params, so
    /// after previewing a lit scene the live lighting stays dialled to that
    /// scene; this lets the user clear it on demand before playing a scene whose
    /// shots name no lighting source (which would otherwise inherit the stale
    /// lighting). The shell owns LightingFxData, so it does the reset.</summary>
    public event EventHandler? ResetPreviewLightingRequested;

    public event EventHandler? CloseRequested;
    public event EventHandler<ThemeMessageEventArgs>? MessageRequested;

    // ── Build / load ───────────────────────────────────────────────────────────

    /// <summary>Build a persistable <see cref="SceneData"/> from the editor.</summary>
    public SceneData BuildData()
    {
        var data = new SceneData
        {
            Name = string.IsNullOrWhiteSpace(_name) ? "Unnamed Scene" : _name.Trim(),
            Description = _description ?? string.Empty,
            Category = string.IsNullOrWhiteSpace(_category) ? "User" : _category.Trim(),
            AudioFilePath = _audioFilePath ?? string.Empty,
        };
        foreach (var row in Shots) data.Shots.Add(row.ToShot());
        foreach (var row in AudioTracks) data.AudioTracks.Add(row.ToTrack());
        // Keyframe global tracks have no editor UI — pass the loaded set through
        // untouched so a re-save doesn't drop an authored exposure ramp / swell.
        data.GlobalTracks = new List<SceneGlobalTrack>(_preservedGlobalTracks);
        if (!string.IsNullOrWhiteSpace(_tags))
        {
            foreach (var t in _tags.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var tag = t.Trim();
                if (tag.Length > 0) data.Tags.Add(tag);
            }
        }
        return data;
    }

    private void LoadFromLibrary(string name)
    {
        var data = _service.GetScene(name);
        if (data == null) return;
        _loadedSourceName = name;
        LoadData(data);
    }

    private void LoadData(SceneData data)
    {
        _suppressChange = true;
        try
        {
            Name = data.Name ?? string.Empty;
            Description = data.Description ?? string.Empty;
            Category = string.IsNullOrWhiteSpace(data.Category) ? "User" : data.Category!;
            Tags = string.Join(", ", data.Tags ?? new List<string>());
            AudioFilePath = data.AudioFilePath ?? string.Empty;

            Shots.Clear();
            if (data.Shots != null)
            {
                foreach (var s in data.Shots)
                {
                    var row = NewShotRow();
                    row.Populate(s);
                    Shots.Add(row);
                }
            }
            SelectedShot = Shots.FirstOrDefault();

            AudioTracks.Clear();
            if (data.AudioTracks != null)
                foreach (var t in data.AudioTracks) AddAudioTrackRow(t);

            // Keep keyframe global tracks for a lossless re-save (no editor UI).
            _preservedGlobalTracks = data.GlobalTracks != null
                ? new List<SceneGlobalTrack>(data.GlobalTracks)
                : new List<SceneGlobalTrack>();

            TitleText = $"Scene Editor — {Name}";
        }
        finally { _suppressChange = false; }
        RecomputeTotal();
    }

    private SceneShotRowViewModel NewShotRow()
        => new(_pickers, AvailableFractalTypes, TransitionKinds,
               FieldChanged, RemoveShot, MoveShotUp, MoveShotDown, PreviewShot,
               e => EditAssetRequested?.Invoke(this, e), CaptureCameraKey);

    /// <summary>#1049 — reads the live view's camera for a shot's "Capture" key:
    /// (shot fractal type, shot is Relief 3D) → the live pose, or null when the
    /// live view isn't showing that kind of camera. Set by the shell.</summary>
    public Func<FractalType, bool, CameraState?>? CaptureLiveCamera { get; set; }

    private void CaptureCameraKey(SceneShotRowViewModel row)
    {
        var state = CaptureLiveCamera?.Invoke(row.FractalType, row.IsRelief3D);
        if (state is { } st)
        {
            row.AddCapturedCameraKey(st);
            return;
        }
        MessageRequested?.Invoke(this, new ThemeMessageEventArgs("Capture Camera Key",
            row.IsRelief3D
                ? "The live view isn't showing a Relief 3D raymarch. Preview this shot first, frame it, then capture."
                : $"The live view isn't showing a {row.FractalType} camera. Preview this shot first, frame it, then capture.",
            MessageSeverity.Info));
    }

    private void AddShot()
    {
        var row = NewShotRow();
        row.Name = $"Shot {Shots.Count + 1}";
        Shots.Add(row);
        SelectedShot = row;
        FieldChanged();
    }

    private void AddAudioTrack()
    {
        // Seed a sensible exposure pump on the kick so a fresh row does something
        // audible without configuration (exposure neutral 1.0 → +40% on bass).
        var track = new SceneAudioTrack
        {
            Target = SceneGlobalTarget.Exposure,
            Binding = new AudioModulationBinding
            {
                Source = AudioSignalKind.Bass,
                Curve = AudioResponseCurve.Smoothstep,
                OutMin = 1.0,
                OutMax = 1.4,
            },
        };
        AddAudioTrackRow(track);
        FieldChanged();
    }

    private void AddAudioTrackRow(SceneAudioTrack track)
        => AudioTracks.Add(new SceneAudioTrackRowViewModel(track, FieldChanged, RemoveAudioTrack));

    private void RemoveAudioTrack(SceneAudioTrackRowViewModel row)
    {
        AudioTracks.Remove(row);
        FieldChanged();
    }

    private async Task BrowseAudioFileAsync()
    {
        if (BrowseAudioFileRequested is not { } pick) return;
        var args = new OpenFileEventArgs(
            "Select export audio file",
            "Audio (*.mp3;*.wav;*.flac;*.m4a;*.ogg;*.aac)|*.mp3;*.wav;*.flac;*.m4a;*.ogg;*.aac|All files (*.*)|*.*");
        await pick(args);
        if (!string.IsNullOrWhiteSpace(args.Path)) AudioFilePath = args.Path!;
    }

    private void RemoveShot(SceneShotRowViewModel row)
    {
        int idx = Shots.IndexOf(row);
        if (idx < 0) return;
        Shots.RemoveAt(idx);
        SelectedShot = Shots.Count > 0 ? Shots[Math.Min(idx, Shots.Count - 1)] : null;
        FieldChanged();
    }

    private void MoveShotUp(SceneShotRowViewModel row)
    {
        int idx = Shots.IndexOf(row);
        if (idx <= 0) return;
        Shots.Move(idx, idx - 1);
        FieldChanged();
    }

    private void MoveShotDown(SceneShotRowViewModel row)
    {
        int idx = Shots.IndexOf(row);
        if (idx < 0 || idx >= Shots.Count - 1) return;
        Shots.Move(idx, idx + 1);
        FieldChanged();
    }

    // #1052 — the row's Preview button doubles as its Stop: a second click on
    // the previewing shot stops it (same as the toolbar Stop). Only one shot
    // previews at a time; Play / Stop / Close clear the flag.
    private void PreviewShot(SceneShotRowViewModel row)
    {
        if (row.IsPreviewing)
        {
            StopPreview();
            return;
        }
        ClearPreviewing();
        SelectedShot = row;
        row.IsPreviewing = true;
        PreviewShotRequested?.Invoke(this, row.ToShot());
    }

    private void ClearPreviewing()
    {
        foreach (var r in Shots) r.IsPreviewing = false;
    }

    private void Play()
    {
        ClearPreviewing();
        PlaySceneRequested?.Invoke(this, BuildData());
    }

    /// <summary>Build the scene + export settings and hand off to the host, then
    /// await its Completion so the command stays "running" (button disabled)
    /// while the offline render + encode proceed.</summary>
    private async Task ExportAsync()
    {
        var scene = BuildData();
        if (scene.Shots.Count == 0 || scene.TotalDurationSeconds <= 0)
        {
            MessageRequested?.Invoke(this, new ThemeMessageEventArgs(
                "Export Scene",
                "This scene has no shots with a positive duration to render.",
                MessageSeverity.Warning));
            return;
        }
        if (ExportSceneRequested == null) return;

        // Clamp the tunables to the Engine's accepted ranges.
        int w = Math.Clamp(ExportWidth, 16, 16384) & ~1;
        int h = Math.Clamp(ExportHeight, 16, 16384) & ~1;
        int fps = Math.Clamp(ExportFps, 1, 240);
        int mb = Math.Clamp(ExportMotionBlur, 1, 64);

        var settings = new SceneExportSettings
        {
            Width = w,
            Height = h,
            Fps = fps,
            MotionBlurSubframes = mb,
            ShutterFraction = 0.5,
            Encode = MapEncode(SelectedEncode),
        };

        var args = new SceneExportEventArgs(scene, settings);
        ExportSceneRequested.Invoke(this, args);
        await args.Completion.Task;
    }

    private static SceneExportEncode MapEncode(string label) => label switch
    {
        "H.264 — lossless (MP4)" => SceneExportEncode.LosslessH264,
        "FFV1 — lossless (MKV)"  => SceneExportEncode.Ffv1,
        GifEncodeLabel           => SceneExportEncode.Gif,
        _                        => SceneExportEncode.HighQualityH264,
    };

    private void StopPreview()
    {
        ClearPreviewing();
        StopPreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FieldChanged()
    {
        if (_suppressChange) return;
        RecomputeTotal();
    }

    private void RecomputeTotal()
    {
        double total = 0.0;
        foreach (var s in Shots) if (s.DurationSeconds > 0) total += s.DurationSeconds;
        TotalDurationText = $"{total:0.###} s · {Shots.Count} shot" + (Shots.Count == 1 ? "" : "s");
    }

    private void NewBlank()
    {
        _loadedSourceName = null;
        _suppressChange = true;
        try
        {
            Name = "My Scene";
            Description = string.Empty;
            Category = "User";
            Tags = string.Empty;
            AudioFilePath = string.Empty;
            Shots.Clear();
            AudioTracks.Clear();
            _preservedGlobalTracks = new List<SceneGlobalTrack>();
            // Seed one shot so a brand-new scene isn't empty.
            var row = NewShotRow();
            row.Name = "Shot 1";
            Shots.Add(row);
            SelectedShot = row;
            TitleText = "Scene Editor — new";
        }
        finally { _suppressChange = false; }
        RecomputeTotal();
    }

    private void Revert()
    {
        if (string.IsNullOrEmpty(_loadedSourceName)) { NewBlank(); return; }
        LoadFromLibrary(_loadedSourceName);
    }

    private async Task SaveAsync()
    {
        var data = BuildData();
        if (string.IsNullOrWhiteSpace(data.Name))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Save Scene", "Name cannot be empty.", MessageSeverity.Warning));
            return;
        }
        if (data.Shots.Count == 0)
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Save Scene", "A scene needs at least one shot.", MessageSeverity.Warning));
            return;
        }
        if (_service.SceneExistsInLibrary(data.Name)
            && !string.Equals(_loadedSourceName, data.Name, StringComparison.OrdinalIgnoreCase))
        {
            var confirm = new ThemeMessageEventArgs("Replace Scene",
                $"A scene named \"{data.Name}\" already exists.\n\nReplace it?",
                MessageSeverity.Question) { ExpectsConfirmation = true };
            await RaiseMessageAsync(confirm);
            if (!confirm.Confirmed) return;
        }

        if (!_service.SaveScene(data))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Save Scene", "Save failed (see log).", MessageSeverity.Warning));
            return;
        }
        SceneSavedToLibrary?.Invoke(this, data.Name);

        _suppressChange = true;
        SceneNames.Clear();
        foreach (var n in SortedSceneNames()) SceneNames.Add(n);
        SelectedScene = data.Name;
        _suppressChange = false;
        _loadedSourceName = data.Name;
        TitleText = $"Scene Editor — {data.Name}";

        await RaiseMessageAsync(new ThemeMessageEventArgs(
            "Save Scene", $"\"{data.Name}\" saved.", MessageSeverity.Info));
    }

    private async Task DeleteAsync()
    {
        if (string.IsNullOrEmpty(_loadedSourceName))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Delete Scene", "No saved scene loaded.", MessageSeverity.Warning));
            return;
        }
        var confirm = new ThemeMessageEventArgs("Delete Scene",
            $"Delete \"{_loadedSourceName}\" from the library?",
            MessageSeverity.Question) { ExpectsConfirmation = true };
        await RaiseMessageAsync(confirm);
        if (!confirm.Confirmed) return;

        string deleted = _loadedSourceName;
        bool removed = _service.DeleteScene(deleted);
        if (!removed)
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Delete Scene", $"\"{deleted}\" could not be deleted.", MessageSeverity.Warning));
            return;
        }

        _suppressChange = true;
        SceneNames.Clear();
        foreach (var n in SortedSceneNames()) SceneNames.Add(n);
        _suppressChange = false;

        SceneDeletedFromLibrary?.Invoke(this, deleted);
        NewBlank();
        await RaiseMessageAsync(new ThemeMessageEventArgs(
            "Delete Scene", $"\"{deleted}\" deleted.", MessageSeverity.Info));
    }

    /// <summary>Re-pull the saved-scene list from the library. Called by the
    /// shell after an import adds entries behind the editor's back; the current
    /// edit buffer and selection are left alone.</summary>
    public void RefreshSceneNames()
    {
        string? selected = SelectedScene;
        _suppressChange = true;
        SceneNames.Clear();
        foreach (var n in SortedSceneNames()) SceneNames.Add(n);
        if (!string.IsNullOrEmpty(selected) && SceneNames.Contains(selected))
            SelectedScene = selected;
        _suppressChange = false;
    }

    private Task RaiseMessageAsync(ThemeMessageEventArgs args)
    {
        var handler = MessageRequested;
        handler?.Invoke(this, args);
        if (handler == null) args.Completion.TrySetResult(true);
        return args.Completion.Task;
    }
}

/// <summary>#1054 / #1055 — payload of <see cref="SceneEditorViewModel.EditAssetRequested"/>.</summary>
/// <remarks><see cref="BuiltIn"/> (#1060) — for <see cref="AssetKind.LightingFx"/>,
/// the name is a built-in curated preset rather than a user preset.</remarks>
public sealed record SceneEditAssetEventArgs(AssetKind Kind, string? Name, bool BuiltIn = false);
