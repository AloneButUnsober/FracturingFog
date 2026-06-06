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
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class ColorThemeEditorViewModel : ViewModelBase
{
    private readonly IColorThemeService _service;
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
                                     string? initialRegionName)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));

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

    private void OnThemeComboSelected(string? name)
    {
        if (_suppressChange || string.IsNullOrEmpty(name) || IsHeader(name)) return;
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
                FieldChanged();
            }
        }
    }

    public bool IsGradient { get => Kind == ColorThemeKindDef.Gradient; set { if (value) Kind = ColorThemeKindDef.Gradient; } }
    public bool IsCycling { get => Kind == ColorThemeKindDef.Cycling; set { if (value) Kind = ColorThemeKindDef.Cycling; } }
    public bool IsPhong { get => Kind == ColorThemeKindDef.Phong3D; set { if (value) Kind = ColorThemeKindDef.Phong3D; } }
    public bool IsPbr { get => Kind == ColorThemeKindDef.Pbr3D; set { if (value) Kind = ColorThemeKindDef.Pbr3D; } }

    private bool _showCycle;
    public bool ShowCycle { get => _showCycle; private set => this.RaiseAndSetIfChanged(ref _showCycle, value); }

    private bool _show3D;
    public bool Show3D { get => _show3D; private set => this.RaiseAndSetIfChanged(ref _show3D, value); }

    private bool _showPhongExtras;
    public bool ShowPhongExtras { get => _showPhongExtras; private set => this.RaiseAndSetIfChanged(ref _showPhongExtras, value); }

    private bool _showPbrExtras;
    public bool ShowPbrExtras { get => _showPbrExtras; private set => this.RaiseAndSetIfChanged(ref _showPbrExtras, value); }

    private void UpdateVisibleKindSections()
    {
        ShowCycle = Kind != ColorThemeKindDef.Gradient;
        Show3D = Kind == ColorThemeKindDef.Phong3D || Kind == ColorThemeKindDef.Pbr3D;
        ShowPhongExtras = Kind == ColorThemeKindDef.Phong3D;
        ShowPbrExtras = Kind == ColorThemeKindDef.Pbr3D;
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

    // ── Inspect ───────────────────────────────────────────────────────────

    private bool _inspectActive;
    public bool InspectActive
    {
        get => _inspectActive;
        set => this.RaiseAndSetIfChanged(ref _inspectActive, value);
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

    /// <summary>Solid swatch brush for the in-set colour preview.</summary>
    public IBrush InSetSwatchBrush => UseInSet
        ? new ImmutableSolidColorBrush(Color.FromRgb(InSetR, InSetG, InSetB))
        : new ImmutableSolidColorBrush(Colors.Black);

    /// <summary>Composite RGB binding target for the ColorPicker control.</summary>
    public Color InSetColor
    {
        get => Color.FromRgb(InSetR, InSetG, InSetB);
        set { InSetR = value.R; InSetG = value.G; InSetB = value.B; this.RaisePropertyChanged(nameof(InSetColor)); }
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

    // ── Internal: row-change notification ─────────────────────────────────

    /// <summary>Called by Stops / Bands / Light rows when any of their fields
    /// change. Surfaces a debounced preview push the same way as VM-level
    /// property setters do via <see cref="FieldChanged"/>.</summary>
    internal void NotifyRowChanged() => FieldChanged();

    private void FieldChanged()
    {
        if (_suppressChange) return;
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

            CycleSpeed = ClampDec((decimal)def.CycleSpeed, 0.0001M, 10M);
            Steepness = ClampDec((decimal)def.Steepness, 0.1M, 10M);
            Ambient = ClampDec((decimal)def.Ambient, 0M, 1M);

            KeyLight.Load(def.KeyLight ?? DefaultKey());
            FillLight.Load(def.FillLight ?? DefaultFill());
            UseRim = def.RimLight != null;
            RimLight.Load(def.RimLight ?? DefaultRim());
            RimLight.IsEnabled = UseRim;

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
            }
            else
            {
                UseInSet = false;
                InSetR = InSetG = InSetB = 0;
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
            CycleSpeed = (float)CycleSpeed,
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
            InSetColor = UseInSet ? new InSetColorDef { R = InSetR, G = InSetG, B = InSetB } : null,
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

    public IBrush SwatchBrush => new ImmutableSolidColorBrush(Color.FromRgb(R, G, B));

    /// <summary>Composite RGB binding target for the ColorPicker control.</summary>
    public Color StopColor
    {
        get => Color.FromRgb(R, G, B);
        set { R = value.R; G = value.G; B = value.B; this.RaisePropertyChanged(nameof(StopColor)); }
    }

    public ColorStopDef ToDef() => new() { Position = Position, R = R, G = G, B = B };
}

public sealed class MaterialBandRowVm : ReactiveObject
{
    private readonly ColorThemeEditorViewModel _parent;

    public MaterialBandRowVm(PbrMaterialBandDef seed, ColorThemeEditorViewModel parent)
    {
        _parent = parent;
        _upperT = seed.UpperT;
        _metal = seed.Metal;
        _roughness = seed.Roughness;
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

    public LightSourceRowVm(LightSourceDef seed, ColorThemeEditorViewModel parent)
    {
        _parent = parent;
        Load(seed);
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
