// ViewModels/FloatingMenuViewModel.cs
//
// Avalonia port of the legacy WinForms FloatingMenu — the main floating
// control panel: navigation (CX/CY/Zoom/Iter), region library, color theme
// library, post-FX sliders (brightness/contrast/adaptive), resolution +
// quality combos, slideshow/video buttons, grid + status toggles.
//
// VM is a thin command/state surface. The host (MainForm or future
// ShellViewModel) owns the actual fractal calculator, region library,
// theme library, and renderer pipeline. Every button + slider raises a
// strongly-typed event the host wires up at construction time.
//
// Combo lists are exposed as ObservableCollection<string>: host fills them
// at startup and after each import/delete/reload via SetRegions /
// SetThemes / SetResolutions / SetQualities. SetXxxSilent() variants
// suppress the change event so host-driven combo mirroring doesn't
// re-enter the change handler.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Avalonia.Threading;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class FloatingMenuViewModel : ViewModelBase
{
    private bool _suppressRegionChange;
    private bool _suppressThemeChange;
    private bool _suppressResolutionChange;
    private bool _suppressQualityChange;

    public FloatingMenuViewModel()
    {
        ResetCommand            = MakeCmd(() => ResetClick?.Invoke(this, EventArgs.Empty));
        SpanCommand             = MakeCmd(() => SpanClick?.Invoke(this, EventArgs.Empty));
        ScreenshotCommand       = MakeCmd(() => ScreenshotClick?.Invoke(this, EventArgs.Empty));
        PosterCommand           = MakeCmd(() => PosterClick?.Invoke(this, EventArgs.Empty));
        SlideshowCommand        = MakeCmd(() => SlideshowClick?.Invoke(this, EventArgs.Empty));
        VideoCommand            = MakeCmd(() => VideoClick?.Invoke(this, EventArgs.Empty));
        CloseProgramCommand     = MakeCmd(() => CloseProgramClick?.Invoke(this, EventArgs.Empty));
        HelpCommand             = MakeCmd(() => HelpClick?.Invoke(this, EventArgs.Empty));
        CloseCommand            = MakeCmd(() => CloseClick?.Invoke(this, EventArgs.Empty));
        CopyCoordsCommand       = MakeCmd(() => CopyCoordsClick?.Invoke(this, EventArgs.Empty));
        GoCommand               = MakeCmd(() => GoClick?.Invoke(this, EventArgs.Empty));
        FlipCommand             = MakeCmd(() => FlipClick?.Invoke(this, EventArgs.Empty));
        SaveViewCommand         = MakeCmd(() => SaveViewClick?.Invoke(this, EventArgs.Empty));
        DeleteRegionCommand     = MakeCmd(() => DeleteRegionClick?.Invoke(this, EventArgs.Empty));
        ExportRegionsCommand    = MakeCmd(() => ExportRegionsClick?.Invoke(this, EventArgs.Empty));
        ImportRegionsCommand    = MakeCmd(() => ImportRegionsClick?.Invoke(this, EventArgs.Empty));
        ExportThemeCommand      = MakeCmd(() => ExportThemeClick?.Invoke(this, EventArgs.Empty));
        ImportThemeCommand      = MakeCmd(() => ImportThemeClick?.Invoke(this, EventArgs.Empty));
        DeleteThemeCommand      = MakeCmd(() => DeleteThemeClick?.Invoke(this, EventArgs.Empty));
        ReloadThemesCommand     = MakeCmd(() => ReloadThemesClick?.Invoke(this, EventArgs.Empty));
        EditThemeCommand        = MakeCmd(() => EditThemeClick?.Invoke(this, EventArgs.Empty));
        SlideshowSettingsCommand= MakeCmd(() => SlideshowSettingsClick?.Invoke(this, EventArgs.Empty));
        ServerCommand           = MakeCmd(() => ServerClick?.Invoke(this, EventArgs.Empty));
        ClientCommand           = MakeCmd(() => ClientClick?.Invoke(this, EventArgs.Empty));
        ToggleAdaptiveSweepCommand = ReactiveCommand.Create(ToggleAdaptiveSweep);
    }

    private static ReactiveCommand<Unit, Unit> MakeCmd(Action a) => ReactiveCommand.Create(a);

    // ── Combo data ────────────────────────────────────────────────────────

    public ObservableCollection<string> RegionNames { get; } = new();
    public ObservableCollection<string> ThemeNames { get; } = new();
    public ObservableCollection<string> ResolutionNames { get; } = new();
    public ObservableCollection<string> QualityNames { get; } = new();

    public void SetRegions(IEnumerable<string> names)
    {
        _suppressRegionChange = true;
        try { RegionNames.Clear(); foreach (var n in names) RegionNames.Add(n); }
        finally { _suppressRegionChange = false; }
    }
    public void SetThemes(IEnumerable<string> names)
    {
        _suppressThemeChange = true;
        try { ThemeNames.Clear(); foreach (var n in names) ThemeNames.Add(n); }
        finally { _suppressThemeChange = false; }
    }
    public void SetResolutions(IEnumerable<string> names)
    {
        _suppressResolutionChange = true;
        try { ResolutionNames.Clear(); foreach (var n in names) ResolutionNames.Add(n); }
        finally { _suppressResolutionChange = false; }
    }
    public void SetQualities(IEnumerable<string> names)
    {
        _suppressQualityChange = true;
        try { QualityNames.Clear(); foreach (var n in names) QualityNames.Add(n); }
        finally { _suppressQualityChange = false; }
    }

    // ── Sort-aware combo refresh (Region + Theme right-click parity) ────────
    //
    // The host service knows how to group / sort / filter; the VM holds the
    // current sort state and re-pulls the names whenever it changes. Headers
    // ("— Kind —" / "— select region —") arrive as plain items the SelectedXxx
    // setters ignore (see the "—" guard below).

    private IColorThemeService? _themeService;
    private ThemeSortMode _themeSort = ThemeSortMode.Default;
    private string? _themeKind;
    private bool _themeEditableOnly;
    private RegionSortMode _regionSort = RegionSortMode.Default;
    private FractalType _regionType = FractalType.Mandelbrot;

    /// <summary>Hand the menu the host theme service so its Region / Theme
    /// combos can sort + filter themselves. Performs the initial fill.</summary>
    public void AttachThemeService(IColorThemeService service)
    {
        _themeService = service;
        RefreshRegions();
        RefreshThemes();
    }

    /// <summary>Re-pull theme names under the current sort state, preserving the
    /// current selection when it survives the rebuild.</summary>
    public void RefreshThemes()
    {
        if (_themeService == null) return;
        string? prev = _selectedTheme;
        SetThemes(_themeService.EnumerateThemeNames(_themeSort, _themeKind, _themeEditableOnly));
        if (!string.IsNullOrEmpty(prev) && ThemeNames.Contains(prev)) SetThemeSilent(prev);
    }

    /// <summary>Re-pull region names under the current sort state, preserving the
    /// current selection when it survives the rebuild.</summary>
    public void RefreshRegions()
    {
        if (_themeService == null) return;
        string? prev = _selectedRegion;
        SetRegions(_themeService.EnumerateRegionNames(_regionSort, _regionType));
        if (!string.IsNullOrEmpty(prev) && RegionNames.Contains(prev)) SetRegionSilent(prev);
    }

    /// <summary>Build the theme combo's right-click sort menu (Default / All /
    /// per-kind). Mirrors Controls.ShowColorComboSortMenu (no Editable-only
    /// toggle — that lives on the editor's combo).</summary>
    public IReadOnlyList<ComboMenuItem> BuildThemeSortMenu()
    {
        var items = new List<ComboMenuItem>
        {
            ComboMenuItem.Item("Default", _themeSort == ThemeSortMode.Default,
                () => { _themeSort = ThemeSortMode.Default; RefreshThemes(); }),
            ComboMenuItem.Item("All (A–Z)", _themeSort == ThemeSortMode.All,
                () => { _themeSort = ThemeSortMode.All; RefreshThemes(); }),
            ComboMenuItem.Separator,
        };
        if (_themeService != null)
            foreach (var kind in _themeService.EnumerateThemeKinds())
            {
                string k = kind;
                bool chk = _themeSort == ThemeSortMode.ByKind && _themeKind == k;
                items.Add(ComboMenuItem.Item(k, chk,
                    () => { _themeSort = ThemeSortMode.ByKind; _themeKind = k; RefreshThemes(); }));
            }
        return items;
    }

    /// <summary>Build the region combo's right-click sort menu (Default /
    /// per-FractalType). Mirrors Controls.ShowRegionComboSortMenu.</summary>
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

    private string? _selectedRegion;
    public string? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRegion, value);
            if (!_suppressRegionChange && !string.IsNullOrEmpty(value) && !IsHeader(value))
                RegionComboChanged?.Invoke(this, value!);
        }
    }

    private string? _selectedTheme;
    public string? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTheme, value);
            if (!_suppressThemeChange && !string.IsNullOrEmpty(value) && !IsHeader(value))
                ColorThemeChanged?.Invoke(this, value!);
        }
    }

    /// <summary>True for non-selectable group headers / placeholders the sort
    /// menus inject ("— Kind —", "— select region —"). Em-dash prefix matches
    /// the WinForms convention (Controls.cs).</summary>
    private static bool IsHeader(string? s)
        => !string.IsNullOrEmpty(s) && s.StartsWith("—", StringComparison.Ordinal);

    private string? _selectedResolution;
    public string? SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedResolution, value);
            if (!_suppressResolutionChange && !string.IsNullOrEmpty(value))
                ResolutionChanged?.Invoke(this, value!);
        }
    }

    private string? _selectedQuality;
    public string? SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedQuality, value);
            if (!_suppressQualityChange && !string.IsNullOrEmpty(value))
                QualityChanged?.Invoke(this, value!);
        }
    }

    /// <summary>Label for the Span button. Flips between "Span" and "Back"
    /// as the host enters/exits borderless multi-monitor fullscreen. The
    /// ShellViewModel sets this when it forwards <see cref="SpanClick"/>.</summary>
    private string _spanButtonText = "Span";
    public string SpanButtonText
    {
        get => _spanButtonText;
        set => this.RaiseAndSetIfChanged(ref _spanButtonText, value);
    }

    /// <summary>Label for the Video button. Flips between "Video" and "Stop"
    /// while a single-shot video zoom or the video slideshow is running. The
    /// ShellViewModel sets this when it forwards <see cref="VideoClick"/>.</summary>
    private string _videoButtonText = "Video";
    public string VideoButtonText
    {
        get => _videoButtonText;
        set => this.RaiseAndSetIfChanged(ref _videoButtonText, value);
    }

    /// <summary>Mirror an externally-driven region selection without firing
    /// <see cref="RegionComboChanged"/>. Used when the toolbar or theme editor
    /// jumps to a region and we want the menu combo to track it.</summary>
    public void SetRegionSilent(string? name)
    {
        _suppressRegionChange = true;
        try { SelectedRegion = name; }
        finally { _suppressRegionChange = false; }
    }

    public void SetThemeSilent(string? name)
    {
        _suppressThemeChange = true;
        try { SelectedTheme = name; }
        finally { _suppressThemeChange = false; }
    }

    public void SetResolutionSilent(string? name)
    {
        _suppressResolutionChange = true;
        try { SelectedResolution = name; }
        finally { _suppressResolutionChange = false; }
    }

    public void SetQualitySilent(string? name)
    {
        _suppressQualityChange = true;
        try { SelectedQuality = name; }
        finally { _suppressQualityChange = false; }
    }

    // ── Coordinates / Zoom / Iter ─────────────────────────────────────────

    private string _cx = "";
    public string CX { get => _cx; set => this.RaiseAndSetIfChanged(ref _cx, value); }

    private string _cy = "";
    public string CY { get => _cy; set => this.RaiseAndSetIfChanged(ref _cy, value); }

    private string _zoom = "1";
    public string Zoom { get => _zoom; set => this.RaiseAndSetIfChanged(ref _zoom, value); }

    private string _iter = "256";
    public string Iter { get => _iter; set => this.RaiseAndSetIfChanged(ref _iter, value); }

    private bool _suppressIterLock;
    private bool _iterLocked;
    public bool IterLocked
    {
        get => _iterLocked;
        set
        {
            this.RaiseAndSetIfChanged(ref _iterLocked, value);
            if (!_suppressIterLock && int.TryParse(Iter, out var i))
                IterLockChanged?.Invoke(this, new IterLockEventArgs(value, i));
        }
    }

    /// <summary>Update the lock checkbox (and iteration textbox) to mirror a
    /// state set elsewhere (region jump / startup) without re-firing
    /// IterLockChanged back into the shell.</summary>
    public void SetIterLockSilent(bool locked, int iterations)
    {
        _suppressIterLock = true;
        try
        {
            if (iterations > 0)
                Iter = iterations.ToString(System.Globalization.CultureInfo.InvariantCulture);
            IterLocked = locked;
        }
        finally { _suppressIterLock = false; }
    }

    /// <summary>Bulk-update from host. Skips the property that the user is
    /// currently editing — caller passes which textbox has focus (or null)
    /// as <paramref name="activeField"/>. Matches legacy
    /// <c>UpdateCoordBoxes</c> guard.</summary>
    public void UpdateCoords(string cx, string cy, string zoom, string iter, string? activeField = null)
    {
        if (activeField != nameof(CX))   CX   = cx;
        if (activeField != nameof(CY))   CY   = cy;
        if (activeField != nameof(Zoom)) Zoom = zoom;
        if (activeField != nameof(Iter)) Iter = iter;
    }

    // ── Post-FX sliders ───────────────────────────────────────────────────

    private int _brightness;
    public int Brightness
    {
        get => _brightness;
        set
        {
            int v = Math.Clamp(value, -100, 100);
            this.RaiseAndSetIfChanged(ref _brightness, v);
            this.RaisePropertyChanged(nameof(BrightnessLabel));
            BrightnessSlide?.Invoke(this, v);
        }
    }
    public string BrightnessLabel => $"Brightness: {Brightness}";

    private int _contrast;
    public int Contrast
    {
        get => _contrast;
        set
        {
            int v = Math.Clamp(value, -100, 100);
            this.RaiseAndSetIfChanged(ref _contrast, v);
            this.RaisePropertyChanged(nameof(ContrastLabel));
            ContrastSlide?.Invoke(this, v);
        }
    }
    public string ContrastLabel => $"Contrast: {Contrast}";

    private int _adaptive;
    public int Adaptive
    {
        get => _adaptive;
        set
        {
            int v = Math.Clamp(value, 0, 100);
            this.RaiseAndSetIfChanged(ref _adaptive, v);
            this.RaisePropertyChanged(nameof(AdaptiveLabel));
            AdaptiveSlide?.Invoke(this, v);
        }
    }
    public string AdaptiveLabel => $"Adaptive: {Adaptive}";

    private bool _brightnessLocked;
    public bool BrightnessLocked { get => _brightnessLocked; set => this.RaiseAndSetIfChanged(ref _brightnessLocked, value); }

    private bool _contrastLocked;
    public bool ContrastLocked { get => _contrastLocked; set => this.RaiseAndSetIfChanged(ref _contrastLocked, value); }

    private bool _adaptiveLocked;
    public bool AdaptiveLocked { get => _adaptiveLocked; set => this.RaiseAndSetIfChanged(ref _adaptiveLocked, value); }

    private bool _adaptiveEnabled = true;
    public bool AdaptiveEnabled { get => _adaptiveEnabled; set => this.RaiseAndSetIfChanged(ref _adaptiveEnabled, value); }

    // ── Adaptive sweep ────────────────────────────────────────────────────
    //
    // Animates Adaptive 0 → 100 over AdaptiveSweepDurationSeconds using a
    // sine ease-in/out curve, then stops automatically. Each tick writes
    // through the Adaptive property so the slider UI and Main.Adaptive both
    // stay in sync — the throttled RepaintWithAdaptive in MainViewModel
    // coalesces ticks into a steady render cadence.
    //
    // Tick interval is held slightly above the render debounce window so
    // the trailing-edge debounce gets a chance to fire between writes
    // (otherwise each tick would reset the debounce and stall the render).

    private const int AdaptiveSweepTickMs = 50;

    private double _adaptiveSweepDurationSeconds = 5.0;
    public double AdaptiveSweepDurationSeconds
    {
        get => _adaptiveSweepDurationSeconds;
        set => this.RaiseAndSetIfChanged(
            ref _adaptiveSweepDurationSeconds,
            Math.Clamp(value, 0.25, 600.0));
    }

    private bool _isAdaptiveSweeping;
    public bool IsAdaptiveSweeping
    {
        get => _isAdaptiveSweeping;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isAdaptiveSweeping, value);
            this.RaisePropertyChanged(nameof(AdaptiveSweepButtonLabel));
        }
    }

    public string AdaptiveSweepButtonLabel => IsAdaptiveSweeping ? "Stop Sweep" : "Sweep";

    private DispatcherTimer? _adaptiveSweepTimer;
    private DateTime _adaptiveSweepStartedUtc;
    private double _adaptiveSweepDurationMsSnapshot;

    private void ToggleAdaptiveSweep()
    {
        if (IsAdaptiveSweeping) StopAdaptiveSweep();
        else                    StartAdaptiveSweep();
    }

    private void StartAdaptiveSweep()
    {
        if (IsAdaptiveSweeping) return;
        _adaptiveSweepDurationMsSnapshot = Math.Max(250.0, AdaptiveSweepDurationSeconds * 1000.0);
        _adaptiveSweepStartedUtc = DateTime.UtcNow;
        Adaptive = 0;
        IsAdaptiveSweeping = true;

        // Render priority (not Background): Background is the lowest dispatch
        // tier and gets starved when the shell is busy with layout / status
        // bar / binding updates at large window sizes — that's the visible
        // sweep stutter at high resolutions. Render runs after input but
        // ahead of idle work, so 50 ms tick fires reliably without
        // monopolising the UI thread.
        _adaptiveSweepTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(AdaptiveSweepTickMs),
            DispatcherPriority.Render,
            OnAdaptiveSweepTick);
        _adaptiveSweepTimer.Start();
    }

    private void StopAdaptiveSweep()
    {
        _adaptiveSweepTimer?.Stop();
        _adaptiveSweepTimer = null;
        IsAdaptiveSweeping = false;
    }

    private void OnAdaptiveSweepTick(object? sender, EventArgs e)
    {
        double elapsedMs = (DateTime.UtcNow - _adaptiveSweepStartedUtc).TotalMilliseconds;
        double t = elapsedMs / _adaptiveSweepDurationMsSnapshot;
        if (t >= 1.0)
        {
            Adaptive = 100;
            StopAdaptiveSweep();
            return;
        }
        // Linear ramp. The previous sine ease-in/out felt janky in practice:
        // its derivative is ~0 at both endpoints, so the integer Adaptive
        // value stays at 0 for the first ~225 ms (visible as "delay at the
        // start") and stays at 99/100 for the last ~225 ms (visible as "slows
        // at the end"). Mid-sweep the derivative peaked at ~π/2× the linear
        // rate, so int values jumped 2 at a time — the "jumpy" mid-sweep.
        // A flat linear ramp removes all three artifacts.
        Adaptive = (int)Math.Round(t * 100.0);
    }

    /// <summary>Programmatic setter that does NOT raise the BrightnessSlide
    /// event. Used by theme-switch snap so the slider mirrors the theme's
    /// default without round-tripping back to the renderer.</summary>
    public void SetBrightnessSilent(int value)
    {
        int v = Math.Clamp(value, -100, 100);
        if (_brightness == v) return;
        _brightness = v;
        this.RaisePropertyChanged(nameof(Brightness));
        this.RaisePropertyChanged(nameof(BrightnessLabel));
    }

    public void SetContrastSilent(int value)
    {
        int v = Math.Clamp(value, -100, 100);
        if (_contrast == v) return;
        _contrast = v;
        this.RaisePropertyChanged(nameof(Contrast));
        this.RaisePropertyChanged(nameof(ContrastLabel));
    }

    public void SetAdaptiveSilent(int value)
    {
        int v = Math.Clamp(value, 0, 100);
        if (_adaptive == v) return;
        _adaptive = v;
        this.RaisePropertyChanged(nameof(Adaptive));
        this.RaisePropertyChanged(nameof(AdaptiveLabel));
    }

    // ── Toggles ──────────────────────────────────────────────────────────

    private bool _showStatusBar = true;
    public bool ShowStatusBar
    {
        get => _showStatusBar;
        set
        {
            this.RaiseAndSetIfChanged(ref _showStatusBar, value);
            StatusBarToggled?.Invoke(this, value);
        }
    }

    private bool _showGrid;
    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            this.RaiseAndSetIfChanged(ref _showGrid, value);
            GridToggled?.Invoke(this, value);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ResetCommand { get; }
    public ReactiveCommand<Unit, Unit> SpanCommand { get; }
    public ReactiveCommand<Unit, Unit> ScreenshotCommand { get; }
    public ReactiveCommand<Unit, Unit> PosterCommand { get; }
    public ReactiveCommand<Unit, Unit> SlideshowCommand { get; }
    public ReactiveCommand<Unit, Unit> VideoCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseProgramCommand { get; }
    public ReactiveCommand<Unit, Unit> HelpCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCoordsCommand { get; }
    public ReactiveCommand<Unit, Unit> GoCommand { get; }
    public ReactiveCommand<Unit, Unit> FlipCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveViewCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteRegionCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportRegionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportRegionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadThemesCommand { get; }
    public ReactiveCommand<Unit, Unit> EditThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> SlideshowSettingsCommand { get; }
    public ReactiveCommand<Unit, Unit> ServerCommand { get; }
    public ReactiveCommand<Unit, Unit> ClientCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAdaptiveSweepCommand { get; }

    // ── Events ────────────────────────────────────────────────────────────

    public event EventHandler? ResetClick;
    public event EventHandler? SpanClick;
    public event EventHandler? ScreenshotClick;
    public event EventHandler? PosterClick;
    public event EventHandler? SlideshowClick;
    public event EventHandler? VideoClick;
    public event EventHandler? CloseProgramClick;
    public event EventHandler? HelpClick;
    public event EventHandler? CloseClick;
    public event EventHandler? CopyCoordsClick;
    public event EventHandler? GoClick;
    public event EventHandler? FlipClick;
    public event EventHandler? SaveViewClick;
    public event EventHandler? DeleteRegionClick;
    public event EventHandler? ExportRegionsClick;
    public event EventHandler? ImportRegionsClick;
    public event EventHandler? ExportThemeClick;
    public event EventHandler? ImportThemeClick;
    public event EventHandler? DeleteThemeClick;
    public event EventHandler? ReloadThemesClick;
    public event EventHandler? EditThemeClick;
    public event EventHandler? SlideshowSettingsClick;
    public event EventHandler? ServerClick;
    public event EventHandler? ClientClick;

    public event EventHandler<string>? RegionComboChanged;
    public event EventHandler<string>? ColorThemeChanged;
    public event EventHandler<string>? ResolutionChanged;
    public event EventHandler<string>? QualityChanged;

    public event EventHandler<int>? BrightnessSlide;
    public event EventHandler<int>? ContrastSlide;
    public event EventHandler<int>? AdaptiveSlide;

    public event EventHandler<bool>? StatusBarToggled;
    public event EventHandler<bool>? GridToggled;

    public event EventHandler<IterLockEventArgs>? IterLockChanged;
}

public sealed class IterLockEventArgs : EventArgs
{
    public IterLockEventArgs(bool locked, int currentIter)
    {
        Locked = locked;
        CurrentIter = currentIter;
    }
    public bool Locked { get; }
    public int CurrentIter { get; }
}
