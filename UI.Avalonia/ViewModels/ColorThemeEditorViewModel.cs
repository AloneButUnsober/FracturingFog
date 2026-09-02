// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ViewModels/ColorThemeEditorViewModel.cs
//
// Avalonia port of the legacy WinForms ColorThemeEditor. UI.Avalonia stays
// free of System.Drawing + the runtime LightSource / ColorMap / library
// classes by talking only to IColorThemeService and to UI-neutral
// ColorThemeDef DTOs (both defined in FracturingFog.Abstractions).
//
// VM responsibilities:
//   • Mirror every editable field of ColorThemeDef as an observable property.
//   • Maintain three child collections — Stops, MaterialBands, and the three
//     LightSourceRowVm instances (Key / Fill / Rim).
//   • Surface debounce-driven live preview through PreviewRequested.
//   • Raise host-callback events for: theme registry pick, region jump,
//     library save notification, help button, message box, save-file
//     dialogs (JSON + C# class), from-image palette pick.
//
// Host wiring per event:
//   • PreviewRequested(ColorThemeDef)            → MainForm pipes into renderer.
//   • RegionRequested(string)                    → MainForm.JumpToRegion(name).
//   • EditorThemeSelected(string)                → MainForm mirrors pick in
//                                                  toolbar + FloatingMenu combos.
//   • ThemeSavedToLibrary(string)                → MainForm rebuilds combos,
//                                                  selects the saved name.
//   • HelpRequested                              → MainForm shows FloatingHelp.
//   • MessageRequested(ThemeMessageEventArgs)         → host shows MessageBox.
//   • SaveFileRequested(ThemeSaveFileEventArgs)       → host opens SaveFileDialog,
//                                                  writes Content to chosen path,
//                                                  reports back via .Saved.
//   • FromImageRequested(ThemeFromImageEventArgs)     → host opens ImagePaletteView,
//                                                  fills .Stops on success.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using FracturingFog;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class ColorThemeEditorViewModel : ViewModelBase
{
    private readonly IColorThemeService _service;
    // Live global view params (issue #96). Optional — when supplied the editor's
    // In-set section exposes the same 2D interior-alpha BACKGROUND controls as
    // the Params view (background mode / colours / image), editing the same
    // global FractalParameters so the two dialogs stay in sync. The interior
    // ALPHA itself stays per-theme (InSetA). Null in headless / preview-less use.
    private readonly FractalParameters? _viewParams;
    private readonly SerialDisposable _previewDebounce = new();
    private bool _suppressChange;
    private string? _loadedSourceName;

    // Combo sort/filter state (parity with WinForms Controls.cs). The editor's
    // theme combo additionally exposes an "Editable only" toggle.
    private ThemeSortMode _themeSort = ThemeSortMode.Default;
    private string? _themeKind;
    private bool _themeEditableOnly;
    private RegionSortMode _regionSort = RegionSortMode.Default;
    private FractalType _regionType = FractalType.Mandelbrot;

    public ColorThemeEditorViewModel(IColorThemeService service,
                                     string? initialThemeName,
                                     string? initialRegionName,
                                     FractalParameters? viewParams = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _viewParams = viewParams;

        ThemeNames = new ObservableCollection<string>(
            service.EnumerateThemeNames(_themeSort, _themeKind, _themeEditableOnly));
        RegionNames = new ObservableCollection<string>(
            service.EnumerateRegionNames(_regionSort, _regionType));

        KeyLight = new LightSourceRowVm(DefaultKey(), this);
        FillLight = new LightSourceRowVm(DefaultFill(), this);
        RimLight = new LightSourceRowVm(DefaultRim(), this) { IsEnabled = false };

        PbrLightingModes = new ObservableCollection<PbrLightingModeDef>(
            (PbrLightingModeDef[])Enum.GetValues(typeof(PbrLightingModeDef)));

        NewBlankCommand = ReactiveCommand.Create(NewBlank);
        CopyCurrentCommand = ReactiveCommand.Create(CopyCurrent);
        RevertCommand = ReactiveCommand.Create(Revert);
        ApplyCommand = ReactiveCommand.Create(PushPreview);
        ImportPaletteCommand = ReactiveCommand.CreateFromTask(ImportPaletteAsync);
        ExportPaletteCommand = ReactiveCommand.CreateFromTask(ExportPaletteAsync);
        SampleSelectedCommand = ReactiveCommand.CreateFromTask(SampleSelectedAsync);
        // Save / Export / FromImage all round-trip through the host (modal
        // dialogs, file pickers, palette extractor). Created via
        // CreateFromTask so the command itself is async — the editor never
        // blocks the UI thread waiting on the host. The host fills the args
        // and signals the per-args TaskCompletionSource; the editor awaits
        // it before reading the result fields.
        SaveCommand = ReactiveCommand.CreateFromTask(SaveToLibraryAsync);
        ExportJsonCommand = ReactiveCommand.CreateFromTask(ExportJsonAsync);
        ExportCSharpCommand = ReactiveCommand.CreateFromTask(ExportCSharpAsync);
        HelpCommand = ReactiveCommand.Create(() => HelpRequested?.Invoke(this, EventArgs.Empty));
        FromImageCommand = ReactiveCommand.CreateFromTask(FromImageAsync);
        AddStopCommand = ReactiveCommand.Create(AddStop);
        RandomizeCommand = ReactiveCommand.Create(RandomizePalette);
        AddBandCommand = ReactiveCommand.Create(AddBand);
        RemoveStopCommand = ReactiveCommand.Create<ColorStopRowVm>(RemoveStop);
        RemoveBandCommand = ReactiveCommand.Create<MaterialBandRowVm>(RemoveBand);
        SelectThemeCommand = ReactiveCommand.Create<string?>(OnThemeComboSelected);
        SelectRegionCommand = ReactiveCommand.Create<string?>(OnRegionComboSelected);

        if (!string.IsNullOrEmpty(initialRegionName) && RegionNames.Contains(initialRegionName))
        {
            _suppressChange = true;
            SelectedRegion = initialRegionName;
            _suppressChange = false;
        }

        if (!string.IsNullOrEmpty(initialThemeName) && ThemeNames.Contains(initialThemeName))
        {
            _suppressChange = true;
            SelectedTheme = initialThemeName;
            _suppressChange = false;
            LoadFromTheme(initialThemeName);
        }
        else
        {
            // Start with a blank usable theme so the editor is non-empty.
            NewBlank();
        }

        UpdateVisibleKindSections();
    }

    // ── Theme + region combos ─────────────────────────────────────────────

    public ObservableCollection<string> ThemeNames { get; }
    public ObservableCollection<string> RegionNames { get; }

    private string? _selectedTheme;
    public string? SelectedTheme
    {
        get => _selectedTheme;
        // The XAML binds SelectedItem here directly, so the setter is the
        // combo's change hook — drive the load through OnThemeComboSelected
        // (which guards against suppress + header rows).
        set { this.RaiseAndSetIfChanged(ref _selectedTheme, value); OnThemeComboSelected(value); }
    }

    private string? _selectedRegion;
    public string? SelectedRegion
    {
        get => _selectedRegion;
        set { this.RaiseAndSetIfChanged(ref _selectedRegion, value); OnRegionComboSelected(value); }
    }

    private async void OnThemeComboSelected(string? name)
    {
        if (_suppressChange || string.IsNullOrEmpty(name) || IsHeader(name)) return;

        // Unsaved-changes guard: if the user has edited the current theme
        // and is now switching away, prompt before discarding their work.
        // Picking the currently-loaded name is a no-op for this purpose.
        if (IsDirty && !string.Equals(name, _loadedSourceName, StringComparison.Ordinal))
        {
            var choice = await PromptUnsavedAsync();
            if (choice == UnsavedChangesChoice.Cancel)
            {
                // Revert the combo to the loaded theme — user backed out.
                _suppressChange = true;
                SelectedTheme = _loadedSourceName;
                _suppressChange = false;
                return;
            }
            if (choice == UnsavedChangesChoice.Save)
            {
                // "Save" = abort the switch and let the user finish naming /
                // saving themselves. Revert the combo and focus the Name field.
                _suppressChange = true;
                SelectedTheme = _loadedSourceName;
                _suppressChange = false;
                FocusNameRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            // Discard → fall through, load the new theme.
        }

        LoadFromTheme(name);
        // Always push preview on explicit theme pick — even if live-preview
        // is unchecked — so the user sees the chosen theme immediately.
        PushPreview();
        EditorThemeSelected?.Invoke(this, name);
    }

    private void OnRegionComboSelected(string? name)
    {
        if (_suppressChange || string.IsNullOrEmpty(name) || IsHeader(name)) return;
        RegionRequested?.Invoke(this, name);
    }

    /// <summary>True for non-selectable group headers / placeholders the sort
    /// menus inject ("— Kind —", "— select region —").</summary>
    private static bool IsHeader(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith("—", StringComparison.Ordinal);

    // ── Sort-aware combo refresh + right-click menu builders ────────────────

    /// <summary>Re-pull theme names under the current sort state, preserving
    /// the current selection when it survives.</summary>
    public void RefreshThemes()
    {
        string? prev = _selectedTheme;
        _suppressChange = true;
        ThemeNames.Clear();
        foreach (var n in _service.EnumerateThemeNames(_themeSort, _themeKind, _themeEditableOnly))
            ThemeNames.Add(n);
        if (!string.IsNullOrEmpty(prev) && ThemeNames.Contains(prev)) SelectedTheme = prev;
        _suppressChange = false;
    }

    /// <summary>Re-pull region names under the current sort state, preserving
    /// the current selection when it survives.</summary>
    public void RefreshRegions()
    {
        string? prev = _selectedRegion;
        _suppressChange = true;
        RegionNames.Clear();
        foreach (var n in _service.EnumerateRegionNames(_regionSort, _regionType))
            RegionNames.Add(n);
        if (!string.IsNullOrEmpty(prev) && RegionNames.Contains(prev)) SelectedRegion = prev;
        _suppressChange = false;
    }

    /// <summary>Theme combo sort menu — Default / All / per-kind, plus the
    /// editor-only "Editable only" toggle. Mirrors Controls with
    /// includeEditableOnlyOption: true.</summary>
    public IReadOnlyList<ComboMenuItem> BuildThemeSortMenu()
    {
        var items = new List<ComboMenuItem>
        {
            ComboMenuItem.Item("Editable only", _themeEditableOnly,
                () => { _themeEditableOnly = !_themeEditableOnly; RefreshThemes(); }),
            ComboMenuItem.Separator,
            ComboMenuItem.Item("Default", _themeSort == ThemeSortMode.Default,
                () => { _themeSort = ThemeSortMode.Default; RefreshThemes(); }),
            ComboMenuItem.Item("All (A–Z)", _themeSort == ThemeSortMode.All,
                () => { _themeSort = ThemeSortMode.All; RefreshThemes(); }),
            ComboMenuItem.Separator,
        };
        foreach (var kind in _service.EnumerateThemeKinds())
        {
            string k = kind;
            bool chk = _themeSort == ThemeSortMode.ByKind && _themeKind == k;
            items.Add(ComboMenuItem.Item(k, chk,
                () => { _themeSort = ThemeSortMode.ByKind; _themeKind = k; RefreshThemes(); }));
        }
        return items;
    }

    /// <summary>Region combo sort menu — Default / per-FractalType.</summary>
    public IReadOnlyList<ComboMenuItem> BuildRegionSortMenu()
    {
        var items = new List<ComboMenuItem>
        {
            ComboMenuItem.Item("Default", _regionSort == RegionSortMode.Default,
                () => { _regionSort = RegionSortMode.Default; RefreshRegions(); }),
            ComboMenuItem.Separator,
        };
        foreach (var t in Enum.GetValues<FractalType>())
        {
            FractalType ft = t;
            bool chk = _regionSort == RegionSortMode.ByFractalType && _regionType == ft;
            items.Add(ComboMenuItem.Item(ft.ToString(), chk,
                () => { _regionSort = RegionSortMode.ByFractalType; _regionType = ft; RefreshRegions(); }));
        }
        return items;
    }

    // ── Identity ──────────────────────────────────────────────────────────

    private string _name = "My Theme";
    public string Name { get => _name; set { this.RaiseAndSetIfChanged(ref _name, value); FieldChanged(); } }

    private string _category = "User";
    public string Category { get => _category; set { this.RaiseAndSetIfChanged(ref _category, value); FieldChanged(); } }

    private string _description = "";
    public string Description { get => _description; set { this.RaiseAndSetIfChanged(ref _description, value); FieldChanged(); } }

    private double _maxRecommendedZoom;
    public double MaxRecommendedZoom
    {
        get => _maxRecommendedZoom;
        set { this.RaiseAndSetIfChanged(ref _maxRecommendedZoom, value); FieldChanged(); }
    }

    private bool _maxZoomEnabled;
    public bool MaxZoomEnabled
    {
        get => _maxZoomEnabled;
        set { this.RaiseAndSetIfChanged(ref _maxZoomEnabled, value); FieldChanged(); }
    }

    // ── Kind ──────────────────────────────────────────────────────────────

    private ColorThemeKindDef _kind = ColorThemeKindDef.Gradient;
    public ColorThemeKindDef Kind
    {
        get => _kind;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _kind, value))
            {
                UpdateVisibleKindSections();
                this.RaisePropertyChanged(nameof(IsGradient));
                this.RaisePropertyChanged(nameof(IsCycling));
                this.RaisePropertyChanged(nameof(IsPhong));
                this.RaisePropertyChanged(nameof(IsPbr));
                this.RaisePropertyChanged(nameof(IsOrbitTrap));
                FieldChanged();
            }
        }
    }

    public bool IsGradient { get => Kind == ColorThemeKindDef.Gradient; set { if (value) Kind = ColorThemeKindDef.Gradient; } }
    public bool IsCycling { get => Kind == ColorThemeKindDef.Cycling; set { if (value) Kind = ColorThemeKindDef.Cycling; } }
    public bool IsPhong { get => Kind == ColorThemeKindDef.Phong3D; set { if (value) Kind = ColorThemeKindDef.Phong3D; } }
    public bool IsPbr { get => Kind == ColorThemeKindDef.Pbr3D; set { if (value) Kind = ColorThemeKindDef.Pbr3D; } }
    public bool IsOrbitTrap { get => Kind == ColorThemeKindDef.OrbitTrap; set { if (value) Kind = ColorThemeKindDef.OrbitTrap; } }

    private bool _showCycle;
    public bool ShowCycle { get => _showCycle; private set => this.RaiseAndSetIfChanged(ref _showCycle, value); }

    private bool _show3D;
    public bool Show3D { get => _show3D; private set => this.RaiseAndSetIfChanged(ref _show3D, value); }

    private bool _showPhongExtras;
    public bool ShowPhongExtras { get => _showPhongExtras; private set => this.RaiseAndSetIfChanged(ref _showPhongExtras, value); }

    private bool _showPbrExtras;
    public bool ShowPbrExtras { get => _showPbrExtras; private set => this.RaiseAndSetIfChanged(ref _showPbrExtras, value); }

    private bool _showOrbitTrap;
    /// <summary>F13 — the Orbit Trap section (shape + scale/power + interior) is
    /// visible only for the OrbitTrap kind.</summary>
    public bool ShowOrbitTrap { get => _showOrbitTrap; private set => this.RaiseAndSetIfChanged(ref _showOrbitTrap, value); }

    private void UpdateVisibleKindSections()
    {
        // OrbitTrap is a gradient-mapped kind but NOT a cycling one — keep the
        // cycle section hidden for it (its gradient maps trap distance, not iter).
        ShowCycle = Kind == ColorThemeKindDef.Cycling
                 || Kind == ColorThemeKindDef.Phong3D
                 || Kind == ColorThemeKindDef.Pbr3D;
        Show3D = Kind == ColorThemeKindDef.Phong3D || Kind == ColorThemeKindDef.Pbr3D;
        ShowPhongExtras = Kind == ColorThemeKindDef.Phong3D;
        ShowPbrExtras = Kind == ColorThemeKindDef.Pbr3D;
        ShowOrbitTrap = Kind == ColorThemeKindDef.OrbitTrap;
    }

    // ── Orbit Trap (F13) ──────────────────────────────────────────────────

    public OrbitTrapShapeDef[] TrapShapeOptions { get; } = Enum.GetValues<OrbitTrapShapeDef>();

    private OrbitTrapShapeDef _trapShape = OrbitTrapShapeDef.Point;
    /// <summary>Trap shape the orbit distance is measured against.</summary>
    public OrbitTrapShapeDef TrapShape
    {
        get => _trapShape;
        set { this.RaiseAndSetIfChanged(ref _trapShape, value); FieldChanged(); }
    }

    private double _trapScale = 2d;
    /// <summary>Trap distances above this clamp to the gradient end (smaller ⇒
    /// more pixels toward the bright end).</summary>
    public double TrapScale
    {
        get => _trapScale;
        set { this.RaiseAndSetIfChanged(ref _trapScale, Math.Clamp(value, 0.05d, 100d)); FieldChanged(); }
    }

    private double _trapPower = 0.35d;
    /// <summary>Trap-distance response exponent; smaller expands small trap
    /// values into the gradient body.</summary>
    public double TrapPower
    {
        get => _trapPower;
        set { this.RaiseAndSetIfChanged(ref _trapPower, Math.Clamp(value, 0.05d, 8d)); FieldChanged(); }
    }

    private bool _colorInterior;
    /// <summary>F14 — colour in-set (non-escaping) pixels by the accumulated
    /// orbit instead of a flat interior (on paths that support it — the
    /// User-Equation path today).</summary>
    public bool ColorInterior
    {
        get => _colorInterior;
        set { this.RaiseAndSetIfChanged(ref _colorInterior, value); FieldChanged(); }
    }

    // ── Gradient interpolation (Phase A F1 / Phase B F2, F3) ──────────────
    //
    // These bake into the 256-entry LUT (space + curve) or remap the mapping
    // scalar (transfer), so they carry zero per-pixel cost. Combos bind to the
    // *Options arrays; defaults reproduce the historical byte-lerp render.

    public GradientColorSpaceDef[] ColorSpaceOptions { get; } = Enum.GetValues<GradientColorSpaceDef>();
    public InterpolationCurveDef[] CurveOptions { get; } = Enum.GetValues<InterpolationCurveDef>();
    public TransferFunctionDef[] TransferOptions { get; } = Enum.GetValues<TransferFunctionDef>();
    public ColorWrapModeDef[] WrapModeOptions { get; } = Enum.GetValues<ColorWrapModeDef>();

    private GradientColorSpaceDef _interpSpace = GradientColorSpaceDef.Srgb;
    public GradientColorSpaceDef InterpSpace
    {
        get => _interpSpace;
        set { this.RaiseAndSetIfChanged(ref _interpSpace, value); FieldChanged(); }
    }

    private InterpolationCurveDef _interpCurve = InterpolationCurveDef.Linear;
    public InterpolationCurveDef InterpCurve
    {
        get => _interpCurve;
        set { this.RaiseAndSetIfChanged(ref _interpCurve, value); FieldChanged(); }
    }

    private TransferFunctionDef _transferFn = TransferFunctionDef.Linear;
    public TransferFunctionDef TransferFn
    {
        get => _transferFn;
        set { this.RaiseAndSetIfChanged(ref _transferFn, value); FieldChanged(); }
    }

    private double _transferStrength = 1d;
    public double TransferStrength
    {
        get => _transferStrength;
        set { this.RaiseAndSetIfChanged(ref _transferStrength, Math.Clamp(value, 0d, 1d)); FieldChanged(); }
    }

    private double _paletteGamma = 1d;
    /// <summary>Per-theme palette gamma baked into the LUT (F6),
    /// out = in^(1/gamma). 1.0 = neutral; &gt;1 lifts shadows/brightens, &lt;1
    /// darkens. Compounds with the host image gamma.</summary>
    public double PaletteGamma
    {
        get => _paletteGamma;
        set { this.RaiseAndSetIfChanged(ref _paletteGamma, Math.Clamp(value, 0.2d, 3d)); FieldChanged(); }
    }

    // ── Cycling phase / density / wrap (Phase A F4, F5) ───────────────────

    private decimal _colorOffset;
    public decimal ColorOffset
    {
        get => _colorOffset;
        set { this.RaiseAndSetIfChanged(ref _colorOffset, value); FieldChanged(); }
    }

    private decimal _colorDensity = 1M;
    public decimal ColorDensity
    {
        get => _colorDensity;
        set { this.RaiseAndSetIfChanged(ref _colorDensity, value); FieldChanged(); }
    }

    private ColorWrapModeDef _wrapMode = ColorWrapModeDef.Repeat;
    public ColorWrapModeDef WrapMode
    {
        get => _wrapMode;
        set { this.RaiseAndSetIfChanged(ref _wrapMode, value); FieldChanged(); }
    }

    // ── Palette post-fx: sparkle (#254) + seamless cycling (#255) ──────────

    private int _sparkleStride;
    /// <summary>Sparkle stride (#254): brighten every Nth LUT entry. 0 = off.</summary>
    public int SparkleStride
    {
        get => _sparkleStride;
        set { this.RaiseAndSetIfChanged(ref _sparkleStride, Math.Max(0, value)); FieldChanged(); }
    }

    private double _sparkleBoost;
    /// <summary>Sparkle brightness boost (#254), fraction of white [0,1]. 0 = off.</summary>
    public double SparkleBoost
    {
        get => _sparkleBoost;
        set { this.RaiseAndSetIfChanged(ref _sparkleBoost, Math.Clamp(value, 0d, 1d)); FieldChanged(); }
    }

    private bool _seamlessCycle;
    /// <summary>Seamless-under-rotation (#255): close the LUT loop so palette
    /// cycling never seams. Opt-in creative choice; default off.</summary>
    public bool SeamlessCycle
    {
        get => _seamlessCycle;
        set { this.RaiseAndSetIfChanged(ref _seamlessCycle, value); FieldChanged(); }
    }

    private int _xorLevels;
    /// <summary>XOR index post-transform level count (#252). &gt;1 shatters the
    /// gradient into a plaid/moiré. 0 = off.</summary>
    public int XorLevels
    {
        get => _xorLevels;
        set { this.RaiseAndSetIfChanged(ref _xorLevels, Math.Max(0, value)); FieldChanged(); }
    }

    private int _xorMask;
    /// <summary>XOR mask for the quantised index (#252).</summary>
    public int XorMask
    {
        get => _xorMask;
        set { this.RaiseAndSetIfChanged(ref _xorMask, Math.Max(0, value)); FieldChanged(); }
    }

    // ── Stops ─────────────────────────────────────────────────────────────

    public ObservableCollection<ColorStopRowVm> Stops { get; } = new();

    private ColorStopRowVm? _selectedStop;
    public ColorStopRowVm? SelectedStop
    {
        get => _selectedStop;
        set
        {
            if (_selectedStop != null && _selectedStop != value) _selectedStop.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedStop, value);
            if (value != null) value.IsSelected = true;
        }
    }

    /// <summary>Select the row programmatically (used by row click handlers
    /// and by the Inspect highlight path).</summary>
    internal void SelectRow(ColorStopRowVm row) => SelectedStop = row;

    private void AddStop()
    {
        Stops.Add(new ColorStopRowVm(new ColorStopDef { Position = 1f, R = 255, G = 255, B = 255 }, this));
        FieldChanged();
    }

    private void RemoveStop(ColorStopRowVm row)
    {
        if (row == null) return;
        if (_selectedStop == row) _selectedStop = null;
        Stops.Remove(row);
        FieldChanged();
    }

    // ── Randomize (Phase C / F12; Kind-aware extras #83) ──────────────────
    //
    // One-click random theme. The palette is always a golden-ratio hue walk
    // (maximally-spaced hues, no two stops close on the wheel) with jittered
    // saturation/value. On top of that, the extras randomized depend on the
    // current Kind and the three toggles below:
    //   • Cycle settings   — all Kinds except Gradient.
    //   • 3D light rig      — Phong3D / Pbr3D.
    //   • Phong extras      — Phong3D only.
    //   • PBR bands/material— Pbr3D only.
    //   • In-set colour     — all Kinds, when RandomIncludeInSet is on (default).
    //   • Post-FX defaults  — all Kinds, when RandomIncludePostFx is on.
    //
    // RandomExperimental switches every range from "artful" (clamped, logical,
    // related to the palette) to "experimental" (caps removed — go wild).
    // Reproducible — a fresh integer seed drives a local Random each click and
    // is recorded in the Description so a theme the user likes can be traced
    // back.

    private bool _randomExperimental;
    /// <summary>Off (default): random values stay in artful/logical ranges.
    /// On: caps and conservativeness removed — randomization "goes wild" (#83).</summary>
    public bool RandomExperimental
    {
        get => _randomExperimental;
        set => this.RaiseAndSetIfChanged(ref _randomExperimental, value);
    }

    private bool _randomIncludeInSet = true;
    /// <summary>On (default): Randomize includes an In-set (interior) colour.
    /// Off: the In-set section is left untouched by Randomize (#83).</summary>
    public bool RandomIncludeInSet
    {
        get => _randomIncludeInSet;
        set => this.RaiseAndSetIfChanged(ref _randomIncludeInSet, value);
    }

    private bool _randomIncludePostFx;
    /// <summary>Off (default): Randomize leaves Post-FX defaults alone.
    /// On: Randomize also sets Brightness / Contrast / Adaptive-HE (#83).
    /// Toggling this mirrors the three Use-* Post-FX checkboxes so the user
    /// doesn't have to flip them by hand.</summary>
    public bool RandomIncludePostFx
    {
        get => _randomIncludePostFx;
        set
        {
            this.RaiseAndSetIfChanged(ref _randomIncludePostFx, value);
            UseBrightness = value;
            UseContrast = value;
            UseAdaptive = value;
        }
    }

    private bool _randomIncludeInterpolation = true;
    /// <summary>On (default): Randomize also picks Interpolation settings
    /// (Space / Curve / Transfer / Strength / Gamma). Off: they are left
    /// untouched (#83).</summary>
    public bool RandomIncludeInterpolation
    {
        get => _randomIncludeInterpolation;
        set => this.RaiseAndSetIfChanged(ref _randomIncludeInterpolation, value);
    }

    private string _randomSeedText = "";
    /// <summary>The seed field. Only consulted when <see cref="UseRandomSeed"/>
    /// is on. After each Randomize it is set to the seed actually used so a
    /// theme can be traced back / reproduced (#83).</summary>
    public string RandomSeedText
    {
        get => _randomSeedText;
        set => this.RaiseAndSetIfChanged(ref _randomSeedText, value);
    }

    private bool _useRandomSeed;
    /// <summary>Off (default): the Seed field is disabled and ignored — each
    /// click gets a fresh random seed (still written back to the field). On:
    /// the Seed field value drives the next Randomize for reproducible output
    /// (#83).</summary>
    public bool UseRandomSeed
    {
        get => _useRandomSeed;
        set => this.RaiseAndSetIfChanged(ref _useRandomSeed, value);
    }

    private void RandomizePalette()
    {
        int seed = (UseRandomSeed
                    && int.TryParse((RandomSeedText ?? "").Trim(), out int userSeed)
                    && userSeed > 0)
            ? userSeed
            : System.Random.Shared.Next(1, 1_000_000);
        var rng = new Random(seed);
        bool wild = RandomExperimental;

        _suppressChange = true;
        try
        {
            var pal = RandomizeStops(rng, wild);

            if (RandomIncludeInterpolation)
                RandomizeInterpolation(rng, wild);

            if (Kind != ColorThemeKindDef.Gradient)
                RandomizeCycle(rng, wild);

            if (Kind == ColorThemeKindDef.Phong3D || Kind == ColorThemeKindDef.Pbr3D)
                Randomize3DLights(rng, wild, pal);

            if (Kind == ColorThemeKindDef.Phong3D)
                RandomizePhongExtras(rng, wild);

            if (Kind == ColorThemeKindDef.Pbr3D)
                RandomizePbr(rng, wild);

            if (Kind == ColorThemeKindDef.OrbitTrap)
                RandomizeOrbitTrap(rng, wild);

            if (RandomIncludeInSet)
                RandomizeInSet(rng, wild, pal);

            if (RandomIncludePostFx)
                RandomizePostFx(rng, wild);
        }
        finally { _suppressChange = false; }

        Description = $"Random theme (seed {seed}, {(wild ? "experimental" : "artful")})";
        RandomSeedText = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        FieldChanged();
        RaiseLightsChanged();
        PushPreview();
    }

    // ── Randomize helpers (#83) ───────────────────────────────────────────
    // Each helper assumes _suppressChange is already true (single FieldChanged
    // + PushPreview fires once at the end of RandomizePalette).

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Rng(Random r, double lo, double hi) => Lerp(lo, hi, r.NextDouble());
    private static byte RandByte(Random r) => (byte)r.Next(0, 256);

    /// <summary>Golden-ratio hue walk. Returns the generated stop colours so
    /// lights / in-set can stay in relation to the palette (artful mode).</summary>
    private List<(byte R, byte G, byte B)> RandomizeStops(Random rng, bool wild)
    {
        const double golden = 0.6180339887498949;
        int n = wild ? rng.Next(3, 9) : 5;
        double hue = rng.NextDouble();
        double satLo = wild ? 0.10 : 0.55, satHi = wild ? 1.00 : 0.95;
        double valLo = wild ? 0.20 : 0.65, valHi = 1.00;

        var pal = new List<(byte, byte, byte)>(n);
        Stops.Clear();
        for (int i = 0; i < n; i++)
        {
            hue = (hue + golden) % 1.0;
            double sat = Rng(rng, satLo, satHi);
            double val = Rng(rng, valLo, valHi);
            var c = new HsvColor(1.0, hue * 360.0, sat, val).ToRgb();
            float pos = i / (float)(n - 1);
            Stops.Add(new ColorStopRowVm(
                new ColorStopDef { Position = pos, R = c.R, G = c.G, B = c.B }, this));
            pal.Add((c.R, c.G, c.B));
        }
        return pal;
    }

    private void RandomizeInterpolation(Random rng, bool wild)
    {
        InterpSpace = ColorSpaceOptions[rng.Next(ColorSpaceOptions.Length)];
        InterpCurve = CurveOptions[rng.Next(CurveOptions.Length)];
        TransferFn  = TransferOptions[rng.Next(TransferOptions.Length)];
        // Strength clamps to 0..1; artful leans toward the stronger end.
        TransferStrength = wild ? Rng(rng, 0, 1) : Rng(rng, 0.3, 1.0);
        // Gamma clamps to 0.2..3; artful stays near neutral.
        PaletteGamma = wild ? Rng(rng, 0.2, 3.0) : Rng(rng, 0.7, 1.6);
    }

    private void RandomizeCycle(Random rng, bool wild)
    {
        ColorOffset  = (decimal)(wild ? Rng(rng, -10, 10)   : Rng(rng, -1, 1));
        ColorDensity = (decimal)(wild ? Rng(rng, 0, 20)     : Rng(rng, 0.5, 4));
        CycleSpeed   = (decimal)(wild ? Rng(rng, 0.0001, 10): Rng(rng, 0.005, 0.1));
        var wraps = WrapModeOptions;
        WrapMode = wraps[rng.Next(wraps.Length)];

        // #255 — artful cycling wants no seam; wild is a coin-flip for variety.
        SeamlessCycle = wild ? rng.NextDouble() < 0.5 : true;

        // #254 — sparkle is an experimental accent: wild-only, ~30% of the time.
        if (wild && rng.NextDouble() < 0.3)
        {
            SparkleStride = rng.Next(4, 25);
            SparkleBoost = Rng(rng, 0.3, 0.8);
        }
        else
        {
            SparkleStride = 0;
            SparkleBoost = 0d;
        }

        // #252 — XOR moiré is a strong effect: wild-only, ~15% of the time.
        if (wild && rng.NextDouble() < 0.15)
        {
            XorLevels = rng.Next(4, 33);
            XorMask = rng.Next(1, XorLevels);
        }
        else
        {
            XorLevels = 0;
            XorMask = 0;
        }
    }

    private void Randomize3DLights(Random rng, bool wild, List<(byte R, byte G, byte B)> pal)
    {
        Steepness = (decimal)(wild ? Rng(rng, 0.1, 10) : Rng(rng, 0.8, 3.0));
        Ambient   = (decimal)(wild ? Rng(rng, 0, 1)    : Rng(rng, 0.05, 0.30));

        ApplyLight(KeyLight,  rng, wild, pal, shinLo: 16, shinHi: 128);
        ApplyLight(FillLight, rng, wild, pal, shinLo: 8,  shinHi: 64);

        // Rim: artful ~40% of the time, experimental ~70%.
        UseRim = rng.NextDouble() < (wild ? 0.70 : 0.40);
        if (UseRim)
            ApplyLight(RimLight, rng, wild, pal, shinLo: 64, shinHi: 256);
    }

    private void ApplyLight(LightSourceRowVm light, Random rng, bool wild,
                            List<(byte R, byte G, byte B)> pal, int shinLo, int shinHi)
    {
        // Placement: artful keeps Lz positive (light in front of the surface);
        // experimental lets it come from anywhere.
        light.Lx = (float)Rng(rng, -1, 1);
        light.Ly = (float)Rng(rng, -1, 1);
        light.Lz = (float)(wild ? Rng(rng, -1, 1) : Rng(rng, 0.3, 1.0));

        if (wild)
        {
            light.DiffR = RandByte(rng); light.DiffG = RandByte(rng); light.DiffB = RandByte(rng);
            light.SpecR = RandByte(rng); light.SpecG = RandByte(rng); light.SpecB = RandByte(rng);
        }
        else
        {
            // Diffuse pulled from a palette stop, blended toward white so the
            // lit surface reads in the theme's colour family. Specular near white.
            var (r, g, b) = pal.Count > 0 ? pal[rng.Next(pal.Count)] : ((byte)255, (byte)255, (byte)255);
            light.DiffR = (byte)Lerp(r, 255, 0.35);
            light.DiffG = (byte)Lerp(g, 255, 0.35);
            light.DiffB = (byte)Lerp(b, 255, 0.35);
            byte s = (byte)rng.Next(200, 256);
            light.SpecR = s; light.SpecG = s; light.SpecB = s;
        }

        light.Shininess = wild ? rng.Next(1, 513) : rng.Next(shinLo, shinHi + 1);
    }

    private void RandomizePhongExtras(Random rng, bool wild)
    {
        KeySpec  = (decimal)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.4, 1.2));
        FillSpec = (decimal)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.1, 0.5));
        FillDiff = (decimal)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.2, 0.6));
        RimSpec  = (decimal)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.5, 1.5));
        RimDiff  = (decimal)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.1, 0.4));
    }

    private void RandomizePbr(Random rng, bool wild)
    {
        var modes = PbrLightingModes;
        if (modes.Count > 0) PbrLightingMode = modes[rng.Next(modes.Count)];
        GlowExponent = (decimal)(wild ? Rng(rng, 0, 50) : Rng(rng, 2, 16));
        GlowScale    = (decimal)(wild ? Rng(rng, 0, 10) : Rng(rng, 0, 2));

        int bandCount = wild ? rng.Next(1, 9) : rng.Next(2, 5);
        MaterialBands.Clear();
        for (int i = 0; i < bandCount; i++)
        {
            // UpperT rises across the bands, last band = 1.0 (catch-all).
            float upper = (i == bandCount - 1) ? 1f : (i + 1) / (float)bandCount;
            float metal = (float)(wild ? rng.NextDouble() : Rng(rng, 0, 1));
            float rough = (float)(wild ? rng.NextDouble() : Rng(rng, 0.2, 0.9));
            MaterialBands.Add(new MaterialBandRowVm(
                new PbrMaterialBandDef { UpperT = upper, Metal = metal, Roughness = rough }, this));
        }
    }

    private void RandomizeOrbitTrap(Random rng, bool wild)
    {
        var shapes = TrapShapeOptions;
        TrapShape = shapes[rng.Next(shapes.Length)];
        TrapScale = wild ? Rng(rng, 0.1, 100) : Rng(rng, 0.8, 4);
        TrapPower = wild ? Rng(rng, 0.05, 8) : Rng(rng, 0.2, 1.2);
        // Interior orbit colouring: artful ~30% of the time, experimental ~50%.
        ColorInterior = rng.NextDouble() < (wild ? 0.5 : 0.3);
    }

    private void RandomizeInSet(Random rng, bool wild, List<(byte R, byte G, byte B)> pal)
    {
        UseInSet = true;
        if (wild)
        {
            InSetR = RandByte(rng); InSetG = RandByte(rng); InSetB = RandByte(rng);
            InSetA = RandByte(rng);
        }
        else
        {
            // Artful: a dark member of the palette family so the interior reads
            // as a recessed pocket rather than clashing with the exterior bands.
            var (r, g, b) = pal.Count > 0 ? pal[rng.Next(pal.Count)] : ((byte)0, (byte)0, (byte)0);
            InSetR = (byte)Lerp(r, 0, 0.75);
            InSetG = (byte)Lerp(g, 0, 0.75);
            InSetB = (byte)Lerp(b, 0, 0.75);
            InSetA = 255;
        }
    }

    private void RandomizePostFx(Random rng, bool wild)
    {
        UseBrightness = true;
        Brightness = wild ? rng.Next(-100, 101) : rng.Next(-20, 21);
        UseContrast = true;
        Contrast = wild ? rng.Next(-100, 101) : rng.Next(-20, 21);
        UseAdaptive = true;
        // Adaptive histogram-EQ: issue caps artful mode under 50.
        Adaptive = wild ? rng.Next(0, 101) : rng.Next(0, 50);
    }

    // ── Inspect ───────────────────────────────────────────────────────────

    private bool _inspectActive;
    public bool InspectActive
    {
        get => _inspectActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _inspectActive, value);
            if (value)
            {
                if (_inspect3DActive) { _inspect3DActive = false; this.RaisePropertyChanged(nameof(Inspect3DActive)); }
                if (_inspectBandActive) { _inspectBandActive = false; this.RaisePropertyChanged(nameof(InspectBandActive)); }
            }
            RaiseInspectEnabledChanged();
        }
    }

    private bool _inspect3DActive;
    public bool Inspect3DActive
    {
        get => _inspect3DActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _inspect3DActive, value);
            if (value)
            {
                if (_inspectActive) { _inspectActive = false; this.RaisePropertyChanged(nameof(InspectActive)); }
                if (_inspectBandActive) { _inspectBandActive = false; this.RaisePropertyChanged(nameof(InspectBandActive)); }
            }
            RaiseInspectEnabledChanged();
        }
    }

    private bool _inspectBandActive;
    public bool InspectBandActive
    {
        get => _inspectBandActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _inspectBandActive, value);
            if (value)
            {
                if (_inspectActive) { _inspectActive = false; this.RaisePropertyChanged(nameof(InspectActive)); }
                if (_inspect3DActive) { _inspect3DActive = false; this.RaisePropertyChanged(nameof(Inspect3DActive)); }
            }
            RaiseInspectEnabledChanged();
        }
    }

    /// <summary>True while any inspect mode is on. Bootstrap click hook
    /// checks this to decide whether to swallow the click.</summary>
    public bool AnyInspectActive => _inspectActive || _inspect3DActive || _inspectBandActive;

    // ── Inspect mutex: each checkbox stays enabled only when no other
    //    inspect mode is active. Bindings keep the unused checkboxes
    //    visually greyed out instead of letting the user click them off
    //    while another mode is on. ──
    public bool InspectStopEnabled => !_inspect3DActive && !_inspectBandActive;
    public bool Inspect3DEnabled => !_inspectActive && !_inspectBandActive;
    public bool InspectBandEnabled => !_inspectActive && !_inspect3DActive;

    private void RaiseInspectEnabledChanged()
    {
        this.RaisePropertyChanged(nameof(InspectStopEnabled));
        this.RaisePropertyChanged(nameof(Inspect3DEnabled));
        this.RaisePropertyChanged(nameof(InspectBandEnabled));
        this.RaisePropertyChanged(nameof(AnyInspectActive));
    }

    /// <summary>
    /// Called by the shell when Inspect is active and the user clicked the
    /// main rendered image. Highlights (pulses + selects) the stop whose
    /// RGB is closest to the sampled pixel.
    /// </summary>
    public void HandleInspectColor(byte r, byte g, byte b)
    {
        ColorStopRowVm? best = null;
        int bestDist = int.MaxValue;
        foreach (var row in Stops)
        {
            int dr = row.R - r, dg = row.G - g, db = row.B - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestDist) { bestDist = d; best = row; }
        }
        if (best == null) return;
        SelectedStop = best;
        best.Pulse();
        ScrollStopIntoViewRequested?.Invoke(this, best);
    }

    /// <summary>
    /// Called by the shell when 3D Inspect is active and the user clicked
    /// the rendered image. Highlights (pulses + selects) the enabled light
    /// whose Diffuse colour is closest to the sampled pixel.
    /// </summary>
    public void HandleInspect3DColor(byte r, byte g, byte b)
    {
        LightSourceRowVm? best = null;
        int bestDist = int.MaxValue;
        foreach (var light in EnumerateActiveLights())
        {
            int dr = light.DiffR - r, dg = light.DiffG - g, db = light.DiffB - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestDist) { bestDist = d; best = light; }
        }
        if (best == null) return;
        SelectedLight = best;
        best.Pulse();
    }

    private IEnumerable<LightSourceRowVm> EnumerateActiveLights()
    {
        yield return KeyLight;
        yield return FillLight;
        if (UseRim) yield return RimLight;
    }

    /// <summary>
    /// Called by the shell when Band Inspect is active and the user clicked
    /// the rendered image. Maps the sampled pixel to its closest stop
    /// Position (treated as PBR <c>t</c>), then walks the bands in UpperT
    /// order to find the first band whose UpperT exceeds t — the same
    /// selection rule the runtime uses in <c>BuildMaterial</c>. The final
    /// band acts as a catch-all.
    /// </summary>
    public void HandleInspectBandColor(byte r, byte g, byte b)
    {
        if (MaterialBands.Count == 0) return;

        ColorStopRowVm? bestStop = null;
        int bestDist = int.MaxValue;
        foreach (var row in Stops)
        {
            int dr = row.R - r, dg = row.G - g, db = row.B - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestDist) { bestDist = d; bestStop = row; }
        }
        if (bestStop == null) return;

        float t = bestStop.Position;
        var ordered = MaterialBands.OrderBy(x => x.UpperT).ToList();
        MaterialBandRowVm? winner = null;
        for (int i = 0; i < ordered.Count - 1; i++)
        {
            if (t < ordered[i].UpperT) { winner = ordered[i]; break; }
        }
        winner ??= ordered[^1];

        SelectedBand = winner;
        winner.Pulse();
        ScrollBandIntoViewRequested?.Invoke(this, winner);
    }

    /// <summary>Fired when an inspect path picks a stop — view scrolls it
    /// into view in the Stops ItemsControl.</summary>
    public event EventHandler<ColorStopRowVm>? ScrollStopIntoViewRequested;

    /// <summary>Fired when band-inspect picks a band — view scrolls it into
    /// view in the MaterialBands ItemsControl.</summary>
    public event EventHandler<MaterialBandRowVm>? ScrollBandIntoViewRequested;

    private MaterialBandRowVm? _selectedBand;
    public MaterialBandRowVm? SelectedBand
    {
        get => _selectedBand;
        set
        {
            if (_selectedBand != null && _selectedBand != value) _selectedBand.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedBand, value);
            if (value != null) value.IsSelected = true;
        }
    }

    private LightSourceRowVm? _selectedLight;
    public LightSourceRowVm? SelectedLight
    {
        get => _selectedLight;
        set
        {
            if (_selectedLight != null && _selectedLight != value) _selectedLight.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedLight, value);
            if (value != null) value.IsSelected = true;
        }
    }

    /// <summary>Called by a light row's per-channel Sample button. Starts the
    /// eyedropper and applies the picked color to <paramref name="row"/>'s
    /// Diffuse channel.</summary>
    internal async Task BeginSampleForLightDiffAsync(LightSourceRowVm row)
    {
        var args = new ThemeSampleColorEventArgs();
        var handler = SampleColorRequested;
        handler?.Invoke(this, args);
        if (handler == null) { args.Completion.TrySetResult(true); return; }
        await args.Completion.Task;
        if (args.PickedR == null) return;
        row.SetDiffColor(args.PickedR.Value, args.PickedG!.Value, args.PickedB!.Value);
    }

    /// <summary>Same as <see cref="BeginSampleForLightDiffAsync"/> for the
    /// Specular channel.</summary>
    internal async Task BeginSampleForLightSpecAsync(LightSourceRowVm row)
    {
        var args = new ThemeSampleColorEventArgs();
        var handler = SampleColorRequested;
        handler?.Invoke(this, args);
        if (handler == null) { args.Completion.TrySetResult(true); return; }
        await args.Completion.Task;
        if (args.PickedR == null) return;
        row.SetSpecColor(args.PickedR.Value, args.PickedG!.Value, args.PickedB!.Value);
    }

    // ── Palette import / export ───────────────────────────────────────────

    private async Task ImportPaletteAsync()
    {
        var args = new ThemeImportPaletteEventArgs { CurrentCount = Stops.Count };
        var handler = ImportPaletteRequested;
        handler?.Invoke(this, args);
        if (handler == null) { args.Completion.TrySetResult(true); return; }
        await args.Completion.Task;

        if (args.Result == ThemeImportPaletteEventArgs.Choice.Cancel) return;
        if (args.Colors == null || args.Colors.Count == 0) return;

        _suppressChange = true;
        try
        {
            if (args.Result == ThemeImportPaletteEventArgs.Choice.Add)
            {
                // Spec: appended stops all sit at position=1.0; existing
                // stops untouched. The list re-sorts by Position on next
                // BuildDef() so the visible row order may shift then.
                foreach (var c in args.Colors)
                    Stops.Add(new ColorStopRowVm(
                        new ColorStopDef { Position = 1f, R = c.R, G = c.G, B = c.B }, this));
            }
            else // Replace
            {
                Stops.Clear();
                int n = args.Colors.Count;
                if (n == 1)
                {
                    var c = args.Colors[0];
                    Stops.Add(new ColorStopRowVm(
                        new ColorStopDef { Position = 0.5f, R = c.R, G = c.G, B = c.B }, this));
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        float p = (float)i / (n - 1);
                        var c = args.Colors[i];
                        Stops.Add(new ColorStopRowVm(
                            new ColorStopDef { Position = p, R = c.R, G = c.G, B = c.B }, this));
                    }
                }
            }
        }
        finally { _suppressChange = false; }

        FieldChanged();
        PushPreview();
    }

    private async Task ExportPaletteAsync()
    {
        if (Stops.Count == 0)
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Export Palette", "No stops to export.", MessageSeverity.Info));
            return;
        }
        var args = new ThemeExportPaletteEventArgs
        {
            SuggestedName = SanitizeFileName(Name) + ".json",
            PaletteName = string.IsNullOrWhiteSpace(Name) ? "Palette" : Name,
            Stops = Stops.Select(r => r.ToDef()).OrderBy(s => s.Position).ToList(),
        };
        var handler = ExportPaletteRequested;
        handler?.Invoke(this, args);
        if (handler == null) { args.Completion.TrySetResult(true); return; }
        await args.Completion.Task;
    }

    // ── Eyedropper ────────────────────────────────────────────────────────

    private async Task SampleSelectedAsync()
    {
        var args = new ThemeSampleColorEventArgs();
        var handler = SampleColorRequested;
        handler?.Invoke(this, args);
        if (handler == null) { args.Completion.TrySetResult(true); return; }
        await args.Completion.Task;
        if (args.PickedR == null) return;

        var target = SelectedStop;
        if (target == null)
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs(
                "Sample", "Click a stop row first, then Sample.", MessageSeverity.Info));
            return;
        }
        target.SetColor(args.PickedR.Value, args.PickedG!.Value, args.PickedB!.Value);
    }

    /// <summary>Called by a row's per-row Sample button. Starts the
    /// eyedropper and applies the picked color to <paramref name="row"/>.</summary>
    internal async Task BeginSampleForRowAsync(ColorStopRowVm row)
    {
        SelectedStop = row;
        var args = new ThemeSampleColorEventArgs();
        var handler = SampleColorRequested;
        handler?.Invoke(this, args);
        if (handler == null) { args.Completion.TrySetResult(true); return; }
        await args.Completion.Task;
        if (args.PickedR == null) return;
        row.SetColor(args.PickedR.Value, args.PickedG!.Value, args.PickedB!.Value);
    }

    private async Task FromImageAsync()
    {
        var args = new ThemeFromImageEventArgs();
        FromImageRequested?.Invoke(this, args);
        // Host is responsible for completing args.Completion after its modal
        // closes. If no host subscriber exists, complete immediately so the
        // editor doesn't hang.
        if (FromImageRequested == null) args.Completion.TrySetResult(true);
        await args.Completion.Task;

        if (args.Stops == null || args.Stops.Count < 2) return;

        _suppressChange = true;
        Stops.Clear();
        foreach (var s in args.Stops)
            Stops.Add(new ColorStopRowVm(s, this));
        _suppressChange = false;

        // Image-derived palettes work best as a Gradient — only nudge if the
        // current kind has nothing to say about gradient stops as the primary
        // visual signal (all four kinds use them, so leave alone if already set).
        FieldChanged();
        PushPreview();
    }

    // ── Cycle ─────────────────────────────────────────────────────────────

    private decimal _cycleSpeed = 0.02M;
    public decimal CycleSpeed { get => _cycleSpeed; set { this.RaiseAndSetIfChanged(ref _cycleSpeed, value); FieldChanged(); } }

    // ── 3D shared ─────────────────────────────────────────────────────────

    private decimal _steepness = 1.6M;
    public decimal Steepness { get => _steepness; set { this.RaiseAndSetIfChanged(ref _steepness, value); FieldChanged(); } }

    private decimal _ambient = 0.12M;
    public decimal Ambient { get => _ambient; set { this.RaiseAndSetIfChanged(ref _ambient, value); FieldChanged(); } }

    public LightSourceRowVm KeyLight { get; }
    public LightSourceRowVm FillLight { get; }
    public LightSourceRowVm RimLight { get; }

    private bool _useRim;
    public bool UseRim
    {
        get => _useRim;
        set
        {
            this.RaiseAndSetIfChanged(ref _useRim, value);
            RimLight.IsEnabled = value;
            this.RaisePropertyChanged(nameof(RimSpec));
            this.RaisePropertyChanged(nameof(RimDiff));
            FieldChanged();
            RaiseLightsChanged();
        }
    }

    // ── Phong extras ──────────────────────────────────────────────────────

    private decimal _keySpec = 0.85M;
    public decimal KeySpec { get => _keySpec; set { this.RaiseAndSetIfChanged(ref _keySpec, value); FieldChanged(); } }

    private decimal _fillSpec = 0.25M;
    public decimal FillSpec { get => _fillSpec; set { this.RaiseAndSetIfChanged(ref _fillSpec, value); FieldChanged(); } }

    private decimal _fillDiff = 0.35M;
    public decimal FillDiff { get => _fillDiff; set { this.RaiseAndSetIfChanged(ref _fillDiff, value); FieldChanged(); } }

    private decimal _rimSpec = 1.0M;
    public decimal RimSpec { get => _rimSpec; set { this.RaiseAndSetIfChanged(ref _rimSpec, value); FieldChanged(); } }

    private decimal _rimDiff = 0.20M;
    public decimal RimDiff { get => _rimDiff; set { this.RaiseAndSetIfChanged(ref _rimDiff, value); FieldChanged(); } }

    // ── PBR extras ────────────────────────────────────────────────────────

    public ObservableCollection<PbrLightingModeDef> PbrLightingModes { get; }

    private PbrLightingModeDef _pbrLightingMode = PbrLightingModeDef.PBRRealistic;
    public PbrLightingModeDef PbrLightingMode
    {
        get => _pbrLightingMode;
        set { this.RaiseAndSetIfChanged(ref _pbrLightingMode, value); FieldChanged(); }
    }

    private decimal _glowExponent = 8M;
    public decimal GlowExponent { get => _glowExponent; set { this.RaiseAndSetIfChanged(ref _glowExponent, value); FieldChanged(); } }

    private decimal _glowScale;
    public decimal GlowScale { get => _glowScale; set { this.RaiseAndSetIfChanged(ref _glowScale, value); FieldChanged(); } }

    public ObservableCollection<MaterialBandRowVm> MaterialBands { get; } = new();

    private void AddBand()
    {
        MaterialBands.Add(new MaterialBandRowVm(new PbrMaterialBandDef { UpperT = 1f, Metal = 0f, Roughness = 0.7f }, this));
        FieldChanged();
    }

    private void RemoveBand(MaterialBandRowVm row)
    {
        if (row == null) return;
        MaterialBands.Remove(row);
        FieldChanged();
    }

    // ── In-set ────────────────────────────────────────────────────────────

    private bool _useInSet;
    public bool UseInSet
    {
        get => _useInSet;
        set
        {
            this.RaiseAndSetIfChanged(ref _useInSet, value);
            this.RaisePropertyChanged(nameof(InSetSwatchBrush));
            FieldChanged();
        }
    }

    private byte _inSetR;
    public byte InSetR
    {
        get => _inSetR;
        set { this.RaiseAndSetIfChanged(ref _inSetR, value); this.RaisePropertyChanged(nameof(InSetSwatchBrush)); FieldChanged(); }
    }

    private byte _inSetG;
    public byte InSetG
    {
        get => _inSetG;
        set { this.RaiseAndSetIfChanged(ref _inSetG, value); this.RaisePropertyChanged(nameof(InSetSwatchBrush)); FieldChanged(); }
    }

    private byte _inSetB;
    public byte InSetB
    {
        get => _inSetB;
        set { this.RaiseAndSetIfChanged(ref _inSetB, value); this.RaisePropertyChanged(nameof(InSetSwatchBrush)); FieldChanged(); }
    }

    private byte _inSetA = 255;
    /// <summary>Per-theme interior alpha (F10 / #96). 255 = opaque (default).
    /// Below 255 the in-set region composites over the 2D background; the global
    /// <c>FractalParameters.InteriorAlpha</c> knob multiplies this value.</summary>
    public byte InSetA
    {
        get => _inSetA;
        set { this.RaiseAndSetIfChanged(ref _inSetA, value); this.RaisePropertyChanged(nameof(InSetSwatchBrush)); FieldChanged(); }
    }

    /// <summary>Solid swatch brush for the in-set colour preview. Carries the
    /// authored alpha so a translucent interior reads as faded in the swatch.</summary>
    public IBrush InSetSwatchBrush => UseInSet
        ? new ImmutableSolidColorBrush(Color.FromArgb(InSetA, InSetR, InSetG, InSetB))
        : new ImmutableSolidColorBrush(Colors.Black);

    /// <summary>Composite RGB binding target for the ColorPicker control.</summary>
    public Color InSetColor
    {
        get => Color.FromRgb(InSetR, InSetG, InSetB);
        set { InSetR = value.R; InSetG = value.G; InSetB = value.B; this.RaisePropertyChanged(nameof(InSetColor)); }
    }

    // ── 2D interior-alpha background (global — mirrors Params view, #96) ──────
    // These edit the shared FractalParameters (not the theme), so the backdrop
    // choice is consistent with the Params view. Only visible when the editor
    // was given a live params instance. Raising InteriorBackgroundChanged lets
    // the host retrigger a repaint.

    /// <summary>True when the editor has a live params instance and can show the
    /// global background controls (hides them in headless / preview-less use).</summary>
    public bool HasViewParams => _viewParams != null;

    /// <summary>Raised when a global 2D background field changes so the host can
    /// repaint. Distinct from PreviewRequested (which carries the theme def).</summary>
    public event EventHandler? InteriorBackgroundChanged;

    public Array Interior2DBackgroundModes => Enum.GetValues(typeof(Interior2DBackgroundMode));

    public Interior2DBackgroundMode Interior2DBackground
    {
        get => _viewParams?.Interior2DBackground ?? Interior2DBackgroundMode.Checkerboard;
        set
        {
            if (_viewParams == null || _viewParams.Interior2DBackground == value) return;
            _viewParams.Interior2DBackground = value;
            this.RaisePropertyChanged();
            InteriorBackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Interior2DBgTopHex
    {
        get => (_viewParams?.Interior2DBgTop ?? 0xFF202040u).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (_viewParams == null || !TryParseHexColor(value, out uint u) || _viewParams.Interior2DBgTop == u) return;
            _viewParams.Interior2DBgTop = u;
            this.RaisePropertyChanged();
            InteriorBackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Interior2DBgBottomHex
    {
        get => (_viewParams?.Interior2DBgBottom ?? 0xFF101020u).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (_viewParams == null || !TryParseHexColor(value, out uint u) || _viewParams.Interior2DBgBottom == u) return;
            _viewParams.Interior2DBgBottom = u;
            this.RaisePropertyChanged();
            InteriorBackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Interior2DBgImagePath
    {
        get => _viewParams?.Interior2DBgImagePath ?? string.Empty;
        set
        {
            if (_viewParams == null) return;
            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_viewParams.Interior2DBgImagePath, v, StringComparison.Ordinal)) return;
            _viewParams.Interior2DBgImagePath = v;
            this.RaisePropertyChanged();
            InteriorBackgroundChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool TryParseHexColor(string? value, out uint result)
    {
        var s = (value ?? string.Empty).Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    // ── Post-FX defaults ──────────────────────────────────────────────────

    private bool _useBrightness;
    public bool UseBrightness { get => _useBrightness; set { this.RaiseAndSetIfChanged(ref _useBrightness, value); FieldChanged(); } }

    private int _brightness;
    public int Brightness
    {
        get => _brightness;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _brightness, Math.Clamp(value, -100, 100)))
                LivePostFxChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _useContrast;
    public bool UseContrast { get => _useContrast; set { this.RaiseAndSetIfChanged(ref _useContrast, value); FieldChanged(); } }

    private int _contrast;
    public int Contrast
    {
        get => _contrast;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _contrast, Math.Clamp(value, -100, 100)))
                LivePostFxChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _useAdaptive;
    public bool UseAdaptive { get => _useAdaptive; set { this.RaiseAndSetIfChanged(ref _useAdaptive, value); FieldChanged(); } }

    private int _adaptive;
    public int Adaptive
    {
        get => _adaptive;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _adaptive, Math.Clamp(value, 0, 100)))
                LivePostFxChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Fires immediately when Brightness/Contrast/Adaptive change
    /// (bypassing the 150ms preview debounce). Shell pushes the values into
    /// MainViewModel directly so the rendered image responds in real time
    /// like the FloatingMenu sliders do.</summary>
    public event EventHandler? LivePostFxChanged;

    // ── Header / status ───────────────────────────────────────────────────

    private bool _livePreview = true;
    public bool LivePreview
    {
        get => _livePreview;
        set { this.RaiseAndSetIfChanged(ref _livePreview, value); }
    }

    private string _titleText = "Color Theme Editor";
    public string TitleText { get => _titleText; private set => this.RaiseAndSetIfChanged(ref _titleText, value); }

    private bool _canApply = true;
    public bool CanApply { get => _canApply; private set => this.RaiseAndSetIfChanged(ref _canApply, value); }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> NewBlankCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCurrentCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportJsonCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCSharpCommand { get; }
    public ReactiveCommand<Unit, Unit> HelpCommand { get; }
    public ReactiveCommand<Unit, Unit> FromImageCommand { get; }
    public ReactiveCommand<Unit, Unit> AddStopCommand { get; }
    public ReactiveCommand<Unit, Unit> RandomizeCommand { get; }
    public ReactiveCommand<Unit, Unit> AddBandCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportPaletteCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportPaletteCommand { get; }
    public ReactiveCommand<Unit, Unit> SampleSelectedCommand { get; }
    public ReactiveCommand<ColorStopRowVm, Unit> RemoveStopCommand { get; }
    public ReactiveCommand<MaterialBandRowVm, Unit> RemoveBandCommand { get; }
    public ReactiveCommand<string?, Unit> SelectThemeCommand { get; }
    public ReactiveCommand<string?, Unit> SelectRegionCommand { get; }

    // ── Events ────────────────────────────────────────────────────────────

    public event EventHandler<ColorThemeDef>? PreviewRequested;
    public event EventHandler<string>? RegionRequested;
    public event EventHandler<string>? EditorThemeSelected;
    public event EventHandler<string>? ThemeSavedToLibrary;
    public event EventHandler? HelpRequested;
    public event EventHandler<ThemeMessageEventArgs>? MessageRequested;
    public event EventHandler<ThemeSaveFileEventArgs>? SaveFileRequested;
    public event EventHandler<ThemeFromImageEventArgs>? FromImageRequested;
    public event EventHandler<ThemeImportPaletteEventArgs>? ImportPaletteRequested;
    public event EventHandler<ThemeExportPaletteEventArgs>? ExportPaletteRequested;
    public event EventHandler<ThemeSampleColorEventArgs>? SampleColorRequested;

    /// <summary>Editor raises this when the user is about to switch themes /
    /// close the window while <see cref="IsDirty"/>. Host shows the modal
    /// prompt and writes the user's pick into <see cref="UnsavedChangesPromptEventArgs.Result"/>
    /// before signalling <see cref="UnsavedChangesPromptEventArgs.Completion"/>.</summary>
    public event EventHandler<UnsavedChangesPromptEventArgs>? UnsavedChangesPromptRequested;

    /// <summary>View subscribes to this to set keyboard focus on the Name
    /// TextBox after the user picks "Save" in the unsaved-changes prompt.</summary>
    public event EventHandler? FocusNameRequested;

    /// <summary>True when the user has edited any field since the last
    /// successful load / save. Drives the unsaved-changes prompt on theme
    /// switch and window close.</summary>
    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    /// <summary>Raises <see cref="UnsavedChangesPromptRequested"/> and awaits
    /// the host's signal. Returns the user's pick (defaults to Cancel if no
    /// host subscriber so the calling flow stays safe).</summary>
    public async Task<UnsavedChangesChoice> PromptUnsavedAsync()
    {
        var args = new UnsavedChangesPromptEventArgs();
        var handler = UnsavedChangesPromptRequested;
        if (handler == null) { return UnsavedChangesChoice.Cancel; }
        handler.Invoke(this, args);
        await args.Completion.Task;
        return args.Result;
    }

    /// <summary>Public escape hatch so external close handlers (e.g. the
    /// MainWindow editor-Closing handler) can request the Name field be
    /// focused after a "Save" pick.</summary>
    public void RequestFocusNameField() => FocusNameRequested?.Invoke(this, EventArgs.Empty);

    // ── Internal: row-change notification ─────────────────────────────────

    /// <summary>Raised whenever the Key/Fill/Rim light rig changes (field edit,
    /// rim toggle, or theme load) so the in-editor <c>LightCompassControl</c>
    /// can repaint. Display-only — carries no payload.</summary>
    public event EventHandler? LightsChanged;

    /// <summary>Fire <see cref="LightsChanged"/> for the compass overlay. Safe
    /// to over-call — the handler just invalidates a tiny control.</summary>
    internal void RaiseLightsChanged() => LightsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Called by Stops / Bands / Light rows when any of their fields
    /// change. Surfaces a debounced preview push the same way as VM-level
    /// property setters do via <see cref="FieldChanged"/>.</summary>
    internal void NotifyRowChanged() { FieldChanged(); RaiseLightsChanged(); }

    private void FieldChanged()
    {
        if (_suppressChange) return;
        // Any real (non-suppressed) field change marks the editor dirty so
        // theme switch / window close prompts the user to save or discard.
        IsDirty = true;
        if (!LivePreview) return;
        // 150 ms debounce — matches the legacy timer interval.
        _previewDebounce.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(150))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => PushPreview());
    }

    // ── Load / Save / Build ──────────────────────────────────────────────

    private void LoadFromTheme(string themeName)
    {
        _loadedSourceName = themeName;
        var def = _service.LoadTheme(themeName);
        if (def == null)
        {
            TitleText = $"\"{themeName}\" — no editable params. Click \"New\".";
            CanApply = false;
            return;
        }
        CanApply = true;
        TitleText = "Color Theme Editor";
        LoadDef(def);
    }

    private void LoadDef(ColorThemeDef def)
    {
        _suppressChange = true;
        try
        {
            Name = def.Name ?? "";
            Category = string.IsNullOrEmpty(def.Category) ? "User" : def.Category;
            Description = def.Description ?? "";

            if (def.MaxRecommendedZoom.HasValue && !double.IsPositiveInfinity(def.MaxRecommendedZoom.Value))
            {
                MaxZoomEnabled = true;
                MaxRecommendedZoom = Math.Clamp(def.MaxRecommendedZoom.Value, 0d, 1_000_000_000d);
            }
            else
            {
                MaxZoomEnabled = false;
                MaxRecommendedZoom = 0;
            }

            Kind = def.Kind;

            Stops.Clear();
            foreach (var s in def.Stops.OrderBy(x => x.Position))
                Stops.Add(new ColorStopRowVm(s, this));

            TrapShape = def.TrapShape;
            TrapScale = def.TrapScale <= 0f ? 2d : Math.Clamp((double)def.TrapScale, 0.05d, 100d);
            TrapPower = def.TrapPower <= 0f ? 0.35d : Math.Clamp((double)def.TrapPower, 0.05d, 8d);
            ColorInterior = def.ColorInterior;

            InterpSpace = def.InterpolationSpace;
            InterpCurve = def.InterpolationCurve;
            TransferFn = def.TransferFunction;
            TransferStrength = Math.Clamp((double)def.TransferStrength, 0d, 1d);
            PaletteGamma = def.PaletteGamma <= 0f ? 1d : Math.Clamp((double)def.PaletteGamma, 0.2d, 3d);

            CycleSpeed = ClampDec((decimal)def.CycleSpeed, 0.0001M, 10M);
            ColorOffset = ClampDec((decimal)def.ColorOffset, -10M, 10M);
            ColorDensity = ClampDec((decimal)def.ColorDensity, 0M, 20M);
            WrapMode = def.WrapMode;
            SparkleStride = Math.Max(0, def.SparkleStride);
            SparkleBoost = Math.Clamp((double)def.SparkleBoost, 0d, 1d);
            SeamlessCycle = def.SeamlessCycle;
            XorLevels = Math.Max(0, def.XorLevels);
            XorMask = Math.Max(0, def.XorMask);
            Steepness = ClampDec((decimal)def.Steepness, 0.1M, 10M);
            Ambient = ClampDec((decimal)def.Ambient, 0M, 1M);

            KeyLight.Load(def.KeyLight ?? DefaultKey());
            FillLight.Load(def.FillLight ?? DefaultFill());
            UseRim = def.RimLight != null;
            RimLight.Load(def.RimLight ?? DefaultRim());
            RimLight.IsEnabled = UseRim;
            RaiseLightsChanged(); // repaint the in-editor light compass on load

            KeySpec = ClampDec((decimal)def.KeySpecScale, 0M, 10M);
            FillSpec = ClampDec((decimal)def.FillSpecScale, 0M, 10M);
            FillDiff = ClampDec((decimal)def.FillDiffScale, 0M, 10M);
            RimSpec = ClampDec((decimal)def.RimSpecScale, 0M, 10M);
            RimDiff = ClampDec((decimal)def.RimDiffScale, 0M, 10M);

            PbrLightingMode = def.PbrLightingMode;
            GlowExponent = ClampDec((decimal)def.GlowBoostExponent, 0M, 50M);
            GlowScale = ClampDec((decimal)def.GlowBoostScale, 0M, 10M);

            MaterialBands.Clear();
            foreach (var b in def.MaterialBands)
                MaterialBands.Add(new MaterialBandRowVm(b, this));

            if (def.InSetColor != null)
            {
                UseInSet = true;
                InSetR = def.InSetColor.R;
                InSetG = def.InSetColor.G;
                InSetB = def.InSetColor.B;
                InSetA = def.InSetColor.A;
            }
            else
            {
                UseInSet = false;
                InSetR = InSetG = InSetB = 0;
                InSetA = 255;
            }

            UseBrightness = def.Brightness.HasValue;
            Brightness = def.Brightness ?? 0;
            UseContrast = def.Contrast.HasValue;
            Contrast = def.Contrast ?? 0;
            UseAdaptive = def.Adaptive.HasValue;
            Adaptive = def.Adaptive ?? 0;

            UpdateVisibleKindSections();
        }
        finally
        {
            _suppressChange = false;
            // Freshly loaded definition == not dirty. Any field touched after
            // this point flips IsDirty back to true via FieldChanged.
            IsDirty = false;
        }
    }

    private ColorThemeDef BuildDef()
    {
        return new ColorThemeDef
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Unnamed Theme" : Name.Trim(),
            Category = string.IsNullOrWhiteSpace(Category) ? "User" : Category.Trim(),
            Description = Description ?? "",
            MaxRecommendedZoom = MaxZoomEnabled ? MaxRecommendedZoom : (double?)null,
            Kind = Kind,
            Stops = Stops.Select(r => r.ToDef()).OrderBy(s => s.Position).ToList(),
            TrapShape = TrapShape,
            TrapScale = (float)TrapScale,
            TrapPower = (float)TrapPower,
            ColorInterior = ColorInterior,
            InterpolationSpace = InterpSpace,
            InterpolationCurve = InterpCurve,
            TransferFunction = TransferFn,
            TransferStrength = (float)TransferStrength,
            PaletteGamma = (float)PaletteGamma,
            CycleSpeed = (float)CycleSpeed,
            ColorOffset = (float)ColorOffset,
            ColorDensity = (float)ColorDensity,
            WrapMode = WrapMode,
            SparkleStride = SparkleStride,
            SparkleBoost = (float)SparkleBoost,
            SeamlessCycle = SeamlessCycle,
            XorLevels = XorLevels,
            XorMask = XorMask,
            Steepness = (float)Steepness,
            Ambient = (float)Ambient,
            KeyLight = KeyLight.ToDef(),
            FillLight = FillLight.ToDef(),
            RimLight = UseRim ? RimLight.ToDef() : null,
            KeySpecScale = (float)KeySpec,
            FillSpecScale = (float)FillSpec,
            FillDiffScale = (float)FillDiff,
            RimSpecScale = (float)RimSpec,
            RimDiffScale = (float)RimDiff,
            PbrLightingMode = PbrLightingMode,
            GlowBoostExponent = (float)GlowExponent,
            GlowBoostScale = (float)GlowScale,
            MaterialBands = MaterialBands.Select(r => r.ToDef()).ToList(),
            InSetColor = UseInSet ? new InSetColorDef { R = InSetR, G = InSetG, B = InSetB, A = InSetA } : null,
            Brightness = UseBrightness ? Brightness : (int?)null,
            Contrast = UseContrast ? Contrast : (int?)null,
            Adaptive = UseAdaptive ? Adaptive : (int?)null,
        };
    }

    private void PushPreview()
    {
        var def = BuildDef();
        if (def.Stops == null || def.Stops.Count < 2) return;
        PreviewRequested?.Invoke(this, def);
    }

    private void NewBlank()
    {
        _loadedSourceName = null;
        var def = new ColorThemeDef
        {
            Name = "My Theme",
            Category = "User",
            Description = "",
            Kind = ColorThemeKindDef.Gradient,
            Stops =
            {
                new ColorStopDef { Position = 0f, R = 0, G = 0, B = 0 },
                new ColorStopDef { Position = 1f, R = 255, G = 255, B = 255 },
            },
        };
        CanApply = true;
        TitleText = "Color Theme Editor — new theme";
        LoadDef(def);
        PushPreview();
    }

    private void CopyCurrent()
    {
        var def = BuildDef();
        def.Name = "Copy of " + (string.IsNullOrWhiteSpace(def.Name) ? "Theme" : def.Name);
        _loadedSourceName = null;
        CanApply = true;
        TitleText = "Color Theme Editor — new theme (copy)";
        LoadDef(def);
        PushPreview();
    }

    private void Revert()
    {
        if (string.IsNullOrEmpty(_loadedSourceName)) return;
        LoadFromTheme(_loadedSourceName);
        PushPreview();
    }

    private async Task SaveToLibraryAsync()
    {
        var def = BuildDef();
        if (string.IsNullOrWhiteSpace(def.Name))
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs("Save Theme", "Name cannot be empty.", MessageSeverity.Warning));
            return;
        }
        if (def.Stops == null || def.Stops.Count < 2)
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs("Save Theme", "Need at least 2 color stops.", MessageSeverity.Warning));
            return;
        }
        if (_service.ThemeExistsInLibrary(def.Name))
        {
            var confirm = new ThemeMessageEventArgs("Replace Theme",
                $"A user theme named \"{def.Name}\" already exists.\n\nReplace it?",
                MessageSeverity.Question)
            { ExpectsConfirmation = true };
            await RaiseMessageAsync(confirm);
            if (!confirm.Confirmed) return;
        }

        _service.SaveToLibrary(def);
        ThemeSavedToLibrary?.Invoke(this, def.Name);

        // Refresh the names list so the new entry appears, and select it.
        _suppressChange = true;
        ThemeNames.Clear();
        foreach (var n in _service.EnumerateThemeNames(_themeSort, _themeKind, _themeEditableOnly))
            ThemeNames.Add(n);
        SelectedTheme = def.Name;
        _suppressChange = false;
        _loadedSourceName = def.Name;
        // Persisted to library — clear the dirty flag so a subsequent theme
        // switch / window close doesn't re-prompt.
        IsDirty = false;

        await RaiseMessageAsync(new ThemeMessageEventArgs("Save Theme", $"\"{def.Name}\" saved.", MessageSeverity.Info));
    }

    private async Task ExportJsonAsync()
    {
        var def = BuildDef();
        string json = _service.SerializeJson(def);
        var args = new ThemeSaveFileEventArgs
        {
            Title = "Export Color Theme",
            SuggestedName = SanitizeFileName(def.Name) + ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            Content = json,
        };
        await RaiseSaveFileAsync(args);
        if (!args.Saved && !string.IsNullOrEmpty(args.ErrorMessage))
            await RaiseMessageAsync(new ThemeMessageEventArgs("Export", "Export failed:\n" + args.ErrorMessage, MessageSeverity.Error));
    }

    private async Task ExportCSharpAsync()
    {
        var def = BuildDef();
        if (def.Stops == null || def.Stops.Count < 2)
        {
            await RaiseMessageAsync(new ThemeMessageEventArgs("Export C#", "Need at least 2 color stops.", MessageSeverity.Warning));
            return;
        }
        string code = _service.GenerateCSharp(def);
        var args = new ThemeSaveFileEventArgs
        {
            Title = "Export Color Theme as C# Class",
            SuggestedName = MakeClassName(def.Name) + ".cs",
            Filter = "C# files (*.cs)|*.cs|All files (*.*)|*.*",
            Content = code,
        };
        await RaiseSaveFileAsync(args);
        if (!args.Saved && !string.IsNullOrEmpty(args.ErrorMessage))
            await RaiseMessageAsync(new ThemeMessageEventArgs("Export C#", "Export failed:\n" + args.ErrorMessage, MessageSeverity.Error));
    }

    // ── Async event raisers ───────────────────────────────────────────────
    //
    // Each helper raises the event synchronously (so any host subscriber sees
    // it immediately) then awaits the TaskCompletionSource the host signals
    // when it's done. If no host subscribed, complete immediately so the
    // editor never hangs in isolation (designer / tests).

    private Task RaiseMessageAsync(ThemeMessageEventArgs args)
    {
        var handler = MessageRequested;
        handler?.Invoke(this, args);
        if (handler == null) args.Completion.TrySetResult(true);
        return args.Completion.Task;
    }

    private Task RaiseSaveFileAsync(ThemeSaveFileEventArgs args)
    {
        var handler = SaveFileRequested;
        handler?.Invoke(this, args);
        if (handler == null) args.Completion.TrySetResult(true);
        return args.Completion.Task;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static decimal ClampDec(decimal v, decimal min, decimal max)
        => v < min ? min : (v > max ? max : v);

    private static LightSourceDef DefaultKey() => new()
    {
        Lx = 0.4f, Ly = -0.4f, Lz = 0.8f,
        DiffR = 1f, DiffG = 1f, DiffB = 1f,
        SpecR = 1f, SpecG = 1f, SpecB = 1f,
        Shininess = 32f,
    };

    private static LightSourceDef DefaultFill() => new()
    {
        Lx = -0.6f, Ly = 0.3f, Lz = 0.7f,
        DiffR = 0.6f, DiffG = 0.6f, DiffB = 0.7f,
        SpecR = 0.4f, SpecG = 0.4f, SpecB = 0.5f,
        Shininess = 16f,
    };

    private static LightSourceDef DefaultRim() => new()
    {
        Lx = 0.5f, Ly = -0.7f, Lz = 0.3f,
        DiffR = 0.3f, DiffG = 0.3f, DiffB = 0.3f,
        SpecR = 1f, SpecG = 1f, SpecB = 1f,
        Shininess = 128f,
    };

    private static string SanitizeFileName(string s)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "theme" : s;
    }

    private static string MakeClassName(string themeName)
    {
        var sb = new System.Text.StringBuilder();
        bool upper = true;
        foreach (char c in themeName ?? "")
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else upper = true;
        }
        if (sb.Length == 0) sb.Append("MyTheme");
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        sb.Append("Theme");
        return sb.ToString();
    }
}

// ─── Row VMs ──────────────────────────────────────────────────────────────

public sealed class ColorStopRowVm : ReactiveObject
{
    private readonly ColorThemeEditorViewModel _parent;
    private System.Threading.Timer? _pulseTimer;

    public ColorStopRowVm(ColorStopDef seed, ColorThemeEditorViewModel parent)
    {
        _parent = parent;
        _position = seed.Position;
        _r = seed.R;
        _g = seed.G;
        _b = seed.B;
        _a = seed.A;
        _midpoint = seed.Midpoint <= 0f ? 0.5f : seed.Midpoint;

        SelectCommand = ReactiveCommand.Create(() => _parent.SelectRow(this));
        SampleCommand = ReactiveCommand.CreateFromTask(() => _parent.BeginSampleForRowAsync(this));
    }

    public ReactiveCommand<Unit, Unit> SelectCommand { get; }
    public ReactiveCommand<Unit, Unit> SampleCommand { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    private bool _isPulsing;
    public bool IsPulsing
    {
        get => _isPulsing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPulsing, value);
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    /// <summary>Background brush combining selection + pulse state. Bound
    /// by the XAML row template.</summary>
    public IBrush RowBackground
    {
        get
        {
            if (_isPulsing) return new ImmutableSolidColorBrush(Color.FromRgb(0x50, 0x6E, 0x3C));
            if (_isSelected) return new ImmutableSolidColorBrush(Color.FromRgb(0x2D, 0x3C, 0x50));
            return new ImmutableSolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        }
    }

    /// <summary>Update the RGB channels in one shot (used by Sample +
    /// Inspect paths). Triggers a single FieldChanged via _parent.</summary>
    internal void SetColor(byte r, byte g, byte b)
    {
        _r = r; _g = g; _b = b;
        this.RaisePropertyChanged(nameof(R));
        this.RaisePropertyChanged(nameof(G));
        this.RaisePropertyChanged(nameof(B));
        this.RaisePropertyChanged(nameof(StopColor));
        this.RaisePropertyChanged(nameof(SwatchBrush));
        _parent.NotifyRowChanged();
    }

    /// <summary>Flash the row background green for ~700ms.</summary>
    public void Pulse()
    {
        IsPulsing = true;
        _pulseTimer?.Dispose();
        _pulseTimer = new System.Threading.Timer(_ =>
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => IsPulsing = false);
        }, null, 700, System.Threading.Timeout.Infinite);
    }

    private float _position;
    public float Position
    {
        get => _position;
        set
        {
            float clamped = value < 0f ? 0f : (value > 1f ? 1f : value);
            this.RaiseAndSetIfChanged(ref _position, clamped);
            _parent.NotifyRowChanged();
        }
    }

    private byte _r;
    public byte R
    {
        get => _r;
        set { this.RaiseAndSetIfChanged(ref _r, value); this.RaisePropertyChanged(nameof(SwatchBrush)); _parent.NotifyRowChanged(); }
    }

    private byte _g;
    public byte G
    {
        get => _g;
        set { this.RaiseAndSetIfChanged(ref _g, value); this.RaisePropertyChanged(nameof(SwatchBrush)); _parent.NotifyRowChanged(); }
    }

    private byte _b;
    public byte B
    {
        get => _b;
        set { this.RaiseAndSetIfChanged(ref _b, value); this.RaisePropertyChanged(nameof(SwatchBrush)); _parent.NotifyRowChanged(); }
    }

    private byte _a;
    /// <summary>Per-stop alpha (F10). 255 = opaque (default). Authored here and
    /// carried through the theme JSON + LUT; visible surfacing in the render /
    /// export path is a later F10 phase.</summary>
    public byte A
    {
        get => _a;
        set { this.RaiseAndSetIfChanged(ref _a, value); this.RaisePropertyChanged(nameof(SwatchBrush)); this.RaisePropertyChanged(nameof(StopColor)); _parent.NotifyRowChanged(); }
    }

    // Swatch reflects the authored alpha so a translucent stop reads as a
    // partly-transparent chip over the row background (visual feedback the
    // render path can't yet give).
    public IBrush SwatchBrush => new ImmutableSolidColorBrush(Color.FromArgb(A, R, G, B));

    private float _midpoint = 0.5f;
    /// <summary>Segment blend bias in (0,1) for the segment starting at this
    /// stop (Phase B / F7). 0.5 = linear.</summary>
    public float Midpoint
    {
        get => _midpoint;
        set
        {
            float clamped = value < 0.01f ? 0.01f : (value > 0.99f ? 0.99f : value);
            this.RaiseAndSetIfChanged(ref _midpoint, clamped);
            _parent.NotifyRowChanged();
        }
    }

    /// <summary>Composite ARGB binding target for the ColorPicker control. Alpha
    /// is carried so a ColorPicker with its alpha slider enabled edits per-stop
    /// opacity too; the explicit A field remains the primary control.</summary>
    public Color StopColor
    {
        get => Color.FromArgb(A, R, G, B);
        set { R = value.R; G = value.G; B = value.B; A = value.A; this.RaisePropertyChanged(nameof(StopColor)); }
    }

    public ColorStopDef ToDef() => new() { Position = Position, R = R, G = G, B = B, A = A, Midpoint = Midpoint };
}

public sealed class MaterialBandRowVm : ReactiveObject
{
    private readonly ColorThemeEditorViewModel _parent;
    private System.Threading.Timer? _pulseTimer;

    public MaterialBandRowVm(PbrMaterialBandDef seed, ColorThemeEditorViewModel parent)
    {
        _parent = parent;
        _upperT = seed.UpperT;
        _metal = seed.Metal;
        _roughness = seed.Roughness;
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    private bool _isPulsing;
    public bool IsPulsing
    {
        get => _isPulsing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPulsing, value);
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    /// <summary>Background brush combining selection + pulse state. Mirrors
    /// <see cref="ColorStopRowVm.RowBackground"/>.</summary>
    public IBrush RowBackground
    {
        get
        {
            if (_isPulsing) return new ImmutableSolidColorBrush(Color.FromRgb(0x50, 0x6E, 0x3C));
            if (_isSelected) return new ImmutableSolidColorBrush(Color.FromRgb(0x2D, 0x3C, 0x50));
            return new ImmutableSolidColorBrush(Colors.Transparent);
        }
    }

    public void Pulse()
    {
        IsPulsing = true;
        _pulseTimer?.Dispose();
        _pulseTimer = new System.Threading.Timer(_ =>
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => IsPulsing = false);
        }, null, 700, System.Threading.Timeout.Infinite);
    }

    private float _upperT;
    public float UpperT
    {
        get => _upperT;
        set { this.RaiseAndSetIfChanged(ref _upperT, value); _parent.NotifyRowChanged(); }
    }

    private float _metal;
    public float Metal
    {
        get => _metal;
        set { this.RaiseAndSetIfChanged(ref _metal, value); _parent.NotifyRowChanged(); }
    }

    private float _roughness;
    public float Roughness
    {
        get => _roughness;
        set { this.RaiseAndSetIfChanged(ref _roughness, value); _parent.NotifyRowChanged(); }
    }

    public PbrMaterialBandDef ToDef() => new() { UpperT = UpperT, Metal = Metal, Roughness = Roughness };
}

public sealed class LightSourceRowVm : ReactiveObject
{
    private readonly ColorThemeEditorViewModel _parent;
    private System.Threading.Timer? _pulseTimer;

    public LightSourceRowVm(LightSourceDef seed, ColorThemeEditorViewModel parent)
    {
        _parent = parent;
        Load(seed);
        SampleDiffCommand = ReactiveCommand.CreateFromTask(() => _parent.BeginSampleForLightDiffAsync(this));
        SampleSpecCommand = ReactiveCommand.CreateFromTask(() => _parent.BeginSampleForLightSpecAsync(this));
    }

    public ReactiveCommand<Unit, Unit> SampleDiffCommand { get; }
    public ReactiveCommand<Unit, Unit> SampleSpecCommand { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    private bool _isPulsing;
    public bool IsPulsing
    {
        get => _isPulsing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPulsing, value);
            this.RaisePropertyChanged(nameof(RowBackground));
        }
    }

    /// <summary>Background brush combining selection + pulse state for the
    /// light card. Mirrors <see cref="ColorStopRowVm.RowBackground"/>.</summary>
    public IBrush RowBackground
    {
        get
        {
            if (_isPulsing) return new ImmutableSolidColorBrush(Color.FromRgb(0x50, 0x6E, 0x3C));
            if (_isSelected) return new ImmutableSolidColorBrush(Color.FromRgb(0x2D, 0x3C, 0x50));
            return new ImmutableSolidColorBrush(Colors.Transparent);
        }
    }

    /// <summary>Flash the light card background green for ~700ms.</summary>
    public void Pulse()
    {
        IsPulsing = true;
        _pulseTimer?.Dispose();
        _pulseTimer = new System.Threading.Timer(_ =>
        {
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => IsPulsing = false);
        }, null, 700, System.Threading.Timeout.Infinite);
    }

    /// <summary>Update the Diffuse RGB channels in one shot. Used by the
    /// per-light eyedropper.</summary>
    internal void SetDiffColor(byte r, byte g, byte b)
    {
        _diffR = r; _diffG = g; _diffB = b;
        this.RaisePropertyChanged(nameof(DiffR));
        this.RaisePropertyChanged(nameof(DiffG));
        this.RaisePropertyChanged(nameof(DiffB));
        this.RaisePropertyChanged(nameof(DiffColor));
        this.RaisePropertyChanged(nameof(DiffSwatchBrush));
        _parent.NotifyRowChanged();
    }

    /// <summary>Update the Specular RGB channels in one shot.</summary>
    internal void SetSpecColor(byte r, byte g, byte b)
    {
        _specR = r; _specG = g; _specB = b;
        this.RaisePropertyChanged(nameof(SpecR));
        this.RaisePropertyChanged(nameof(SpecG));
        this.RaisePropertyChanged(nameof(SpecB));
        this.RaisePropertyChanged(nameof(SpecColor));
        this.RaisePropertyChanged(nameof(SpecSwatchBrush));
        _parent.NotifyRowChanged();
    }

    public void Load(LightSourceDef seed)
    {
        _lx = seed.Lx;
        _ly = seed.Ly;
        _lz = seed.Lz;
        _diffR = (byte)Math.Round(Math.Clamp(seed.DiffR, 0f, 1f) * 255f);
        _diffG = (byte)Math.Round(Math.Clamp(seed.DiffG, 0f, 1f) * 255f);
        _diffB = (byte)Math.Round(Math.Clamp(seed.DiffB, 0f, 1f) * 255f);
        _specR = (byte)Math.Round(Math.Clamp(seed.SpecR, 0f, 1f) * 255f);
        _specG = (byte)Math.Round(Math.Clamp(seed.SpecG, 0f, 1f) * 255f);
        _specB = (byte)Math.Round(Math.Clamp(seed.SpecB, 0f, 1f) * 255f);
        _shininess = (int)Math.Clamp(seed.Shininess, 1f, 512f);
        this.RaisePropertyChanged(nameof(Lx));
        this.RaisePropertyChanged(nameof(Ly));
        this.RaisePropertyChanged(nameof(Lz));
        this.RaisePropertyChanged(nameof(DiffR));
        this.RaisePropertyChanged(nameof(DiffG));
        this.RaisePropertyChanged(nameof(DiffB));
        this.RaisePropertyChanged(nameof(SpecR));
        this.RaisePropertyChanged(nameof(SpecG));
        this.RaisePropertyChanged(nameof(SpecB));
        this.RaisePropertyChanged(nameof(Shininess));
        this.RaisePropertyChanged(nameof(DiffSwatchBrush));
        this.RaisePropertyChanged(nameof(SpecSwatchBrush));
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    private float _lx;
    public float Lx { get => _lx; set { this.RaiseAndSetIfChanged(ref _lx, value); _parent.NotifyRowChanged(); } }

    private float _ly;
    public float Ly { get => _ly; set { this.RaiseAndSetIfChanged(ref _ly, value); _parent.NotifyRowChanged(); } }

    private float _lz;
    public float Lz { get => _lz; set { this.RaiseAndSetIfChanged(ref _lz, value); _parent.NotifyRowChanged(); } }

    private byte _diffR;
    public byte DiffR { get => _diffR; set { this.RaiseAndSetIfChanged(ref _diffR, value); this.RaisePropertyChanged(nameof(DiffSwatchBrush)); _parent.NotifyRowChanged(); } }

    private byte _diffG;
    public byte DiffG { get => _diffG; set { this.RaiseAndSetIfChanged(ref _diffG, value); this.RaisePropertyChanged(nameof(DiffSwatchBrush)); _parent.NotifyRowChanged(); } }

    private byte _diffB;
    public byte DiffB { get => _diffB; set { this.RaiseAndSetIfChanged(ref _diffB, value); this.RaisePropertyChanged(nameof(DiffSwatchBrush)); _parent.NotifyRowChanged(); } }

    private byte _specR;
    public byte SpecR { get => _specR; set { this.RaiseAndSetIfChanged(ref _specR, value); this.RaisePropertyChanged(nameof(SpecSwatchBrush)); _parent.NotifyRowChanged(); } }

    private byte _specG;
    public byte SpecG { get => _specG; set { this.RaiseAndSetIfChanged(ref _specG, value); this.RaisePropertyChanged(nameof(SpecSwatchBrush)); _parent.NotifyRowChanged(); } }

    private byte _specB;
    public byte SpecB { get => _specB; set { this.RaiseAndSetIfChanged(ref _specB, value); this.RaisePropertyChanged(nameof(SpecSwatchBrush)); _parent.NotifyRowChanged(); } }

    private int _shininess;
    public int Shininess { get => _shininess; set { this.RaiseAndSetIfChanged(ref _shininess, Math.Clamp(value, 1, 512)); _parent.NotifyRowChanged(); } }

    public IBrush DiffSwatchBrush => new ImmutableSolidColorBrush(Color.FromRgb(DiffR, DiffG, DiffB));
    public IBrush SpecSwatchBrush => new ImmutableSolidColorBrush(Color.FromRgb(SpecR, SpecG, SpecB));

    // Composite RGB binding targets for the ColorPicker control. Setting the
    // composite property updates the three byte channels in one shot; each
    // channel setter still raises its own PropertyChanged + NotifyRowChanged
    // (debounced into a single PushPreview by FieldChanged).
    public Color DiffColor
    {
        get => Color.FromRgb(DiffR, DiffG, DiffB);
        set { DiffR = value.R; DiffG = value.G; DiffB = value.B; this.RaisePropertyChanged(nameof(DiffColor)); }
    }
    public Color SpecColor
    {
        get => Color.FromRgb(SpecR, SpecG, SpecB);
        set { SpecR = value.R; SpecG = value.G; SpecB = value.B; this.RaisePropertyChanged(nameof(SpecColor)); }
    }

    public LightSourceDef ToDef() => new()
    {
        Lx = Lx, Ly = Ly, Lz = Lz,
        DiffR = DiffR / 255f, DiffG = DiffG / 255f, DiffB = DiffB / 255f,
        SpecR = SpecR / 255f, SpecG = SpecG / 255f, SpecB = SpecB / 255f,
        Shininess = Shininess,
    };
}

// ─── Event args ───────────────────────────────────────────────────────────

public enum MessageSeverity { Info, Warning, Error, Question }

public sealed class ThemeMessageEventArgs : EventArgs
{
    public ThemeMessageEventArgs(string title, string body, MessageSeverity severity)
    {
        Title = title; Body = body; Severity = severity;
    }
    public string Title { get; }
    public string Body { get; }
    public MessageSeverity Severity { get; }
    public bool ExpectsConfirmation { get; set; }
    /// <summary>Host fills with Yes/No result when <see cref="ExpectsConfirmation"/>.</summary>
    public bool Confirmed { get; set; }

    /// <summary>Host signals here after it has finished interacting with the
    /// user (or immediately, for sync hosts). The editor awaits this before
    /// reading <see cref="Confirmed"/>. Without this, the editor would have
    /// to block the UI thread on a Dispatcher round-trip and deadlock the
    /// modal dialog pump.</summary>
    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ThemeSaveFileEventArgs : EventArgs
{
    public string Title { get; set; } = "Save";
    public string SuggestedName { get; set; } = "file.txt";
    public string Filter { get; set; } = "All files (*.*)|*.*";
    public string Content { get; set; } = "";
    /// <summary>Host sets true after a successful write.</summary>
    public bool Saved { get; set; }
    /// <summary>Host sets when an exception occurred so the VM can surface
    /// a follow-up message box.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Host signals here after the SaveFilePicker closes (or
    /// immediately on cancel / failure). The editor awaits this before
    /// reading <see cref="Saved"/> / <see cref="ErrorMessage"/>.</summary>
    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ThemeFromImageEventArgs : EventArgs
{
    /// <summary>Host fills with the image-derived stops on success; leave
    /// null or fewer than 2 entries to cancel the apply.</summary>
    public List<ColorStopDef>? Stops { get; set; }

    /// <summary>Host signals here after the image-palette dialog closes.
    /// The editor awaits this before reading <see cref="Stops"/>.</summary>
    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ThemeImportPaletteEventArgs : EventArgs
{
    public enum Choice { Cancel, Add, Replace }

    /// <summary>VM-set: number of stops currently in the editor. Used by the
    /// host's Add/Replace prompt to display "Current stops: N".</summary>
    public int CurrentCount { get; init; }

    /// <summary>Host fills with the parsed colors from the picked file.</summary>
    public List<(byte R, byte G, byte B)>? Colors { get; set; }

    /// <summary>Host fills with the user's Add/Replace/Cancel pick.</summary>
    public Choice Result { get; set; } = Choice.Cancel;

    /// <summary>Host signals here after the file picker + Add/Replace prompt
    /// have both closed. VM awaits before reading Colors + Result.</summary>
    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ThemeExportPaletteEventArgs : EventArgs
{
    public string SuggestedName { get; init; } = "palette.json";
    public string PaletteName { get; init; } = "Palette";
    public List<ColorStopDef> Stops { get; init; } = new();
    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Result of the editor's unsaved-changes prompt:
/// <list type="bullet">
///   <item><term>Save</term><description>Stay open, focus Name field so the user can save manually.</description></item>
///   <item><term>Discard</term><description>Drop edits and proceed with the impending close / switch.</description></item>
///   <item><term>Cancel</term><description>Back out — abort the close / switch entirely.</description></item>
/// </list></summary>
public enum UnsavedChangesChoice { Save, Discard, Cancel }

public sealed class UnsavedChangesPromptEventArgs : EventArgs
{
    /// <summary>Host writes the user's pick before signalling
    /// <see cref="Completion"/>. Defaults to Cancel so a host that signals
    /// without picking does the safe thing.</summary>
    public UnsavedChangesChoice Result { get; set; } = UnsavedChangesChoice.Cancel;

    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ThemeSampleColorEventArgs : EventArgs
{
    /// <summary>Host fills with the sampled pixel. Nullable channels so the
    /// VM can detect "user cancelled" vs "got a real color".</summary>
    public byte? PickedR { get; set; }
    public byte? PickedG { get; set; }
    public byte? PickedB { get; set; }

    public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// ReactiveObjectExtensions.RaiseAndSetIfChangedReturnsChanged is defined
// once in UserBulbViewModel.cs and shared across UI.Avalonia VMs.
