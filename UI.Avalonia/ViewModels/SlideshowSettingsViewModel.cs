using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// View model for <see cref="Views.SlideshowSettingsView"/>. Wraps an active
/// <see cref="SlideshowConfig"/> (from <see cref="SlideshowConfigLibrary"/>)
/// plus the audio-reactive master toggle so the view can bind to observable
/// properties and produce a <see cref="Result"/> +
/// <see cref="AudioReactiveResult"/> on OK.
///
/// Two construction modes:
///   • Legacy ctor — <see cref="SlideshowSettings"/> only. Wraps a single
///     ephemeral preset; Name combo holds one entry, library buttons are
///     no-ops. Kept compiling for shells that don't carry a config file yet.
///   • Library ctor — <see cref="SlideshowConfigFile"/> + active name. Full
///     unified-dialog mode: Name combo, Type droplist, Save/Delete/Import/
///     Export buttons, Start button. Used by the Avalonia shell bootstrap.
/// </summary>
public sealed class SlideshowSettingsViewModel : ViewModelBase
{
    // ── Library mode state ────────────────────────────────────────────────

    private readonly SlideshowConfigFile? _file;
    private SlideshowConfig _working;

    // ── Timing mirror (always present so the view bindings work) ─────────

    private bool _audioReactive;
    private bool _useExtremeRegions;
    private int _totalDisplaySec;
    private int _themeFadeMs;
    private int _regionFadeMs;
    private int _fadeSteps;
    private bool _useRegionWatermark;
    private SlideshowType _type;
    private string _activeName = "Default";
    private bool _isDirty;
    private bool _initializing;
    private bool _startRequested;

    public SlideshowSettingsViewModel(SlideshowSettings current, bool audioReactive)
        : this(BuildEphemeralFile(current), audioReactive, libraryMode: false)
    {
    }

    public SlideshowSettingsViewModel(SlideshowConfigFile file, bool audioReactive)
        : this(file, audioReactive, libraryMode: true)
    {
    }

    private SlideshowSettingsViewModel(SlideshowConfigFile file, bool audioReactive, bool libraryMode)
    {
        ArgumentNullException.ThrowIfNull(file);

        _initializing = true;
        _file = file;
        IsLibraryMode = libraryMode;

        SavedConfigNames = new ObservableCollection<string>(file.Configs.Select(c => c.Name));
        _activeName = string.IsNullOrWhiteSpace(file.ActiveName) ? "Default" : file.ActiveName;
        _working = SlideshowConfigLibrary.GetActive(file);

        LoadWorkingIntoBindings();
        _audioReactive = audioReactive;

        OkCommand = ReactiveCommand.Create(() => { Commit(); });
        CancelCommand = ReactiveCommand.Create(() => { });
        ShowAudioDialogCommand = ReactiveCommand.Create(() =>
            ShowAudioDialogRequested?.Invoke(this, EventArgs.Empty));

        SaveCommand = ReactiveCommand.Create(SaveActive);
        DeleteCommand = ReactiveCommand.Create(DeleteActive);
        ImportCommand = ReactiveCommand.Create(() =>
            ImportRequested?.Invoke(this, EventArgs.Empty));
        ExportCommand = ReactiveCommand.Create(() =>
            ExportRequested?.Invoke(this, EventArgs.Empty));
        StartCommand = ReactiveCommand.Create(RequestStart);
        EditVideoSettingsCommand = ReactiveCommand.Create(() =>
            EditVideoSettingsRequested?.Invoke(this, EventArgs.Empty));

        _initializing = false;
    }

    private static SlideshowConfigFile BuildEphemeralFile(SlideshowSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SlideshowConfigFile
        {
            ActiveName = "Default",
            Configs = { SlideshowConfig.FromLegacy("Default", settings, audioReactive: false) },
        };
    }

    /// <summary>True when the VM owns a real library (not the ephemeral
    /// single-config wrapper). Drives visibility of Name combo + library
    /// buttons in the view.</summary>
    public bool IsLibraryMode { get; }

    /// <summary>Static enum-value list bound to the Type droplist.</summary>
    public IReadOnlyList<SlideshowType> AllSlideshowTypes { get; } =
        new[] { SlideshowType.Image, SlideshowType.Video };

    /// <summary>Available regions for the include-list (checkable items).</summary>
    public ObservableCollection<CheckableItem> AvailableRegions { get; } = new();

    /// <summary>Available color themes for the include-list.</summary>
    public ObservableCollection<CheckableItem> AvailableThemes { get; } = new();

    /// <summary>Available fractal types for the filter.</summary>
    public ObservableCollection<CheckableItem> AvailableFractalTypes { get; } = new();

    /// <summary>Available quality presets for the filter.</summary>
    public ObservableCollection<CheckableItem> AvailableQualityPresets { get; } = new();

    /// <summary>Host supplies the region + theme name lists. Either may be
    /// null/empty (legacy mode leaves the panel blank). Fractal-type and
    /// quality-preset choices are static and built from
    /// <see cref="Models.FractalType"/> + a known preset name set.</summary>
    public void PopulateAvailableLists(
        IReadOnlyList<string>? regionNames,
        IReadOnlyList<string>? themeNames)
    {
        AvailableRegions.Clear();
        if (regionNames != null)
            foreach (var name in regionNames)
                AvailableRegions.Add(new CheckableItem(name, _working.IncludedRegions.Contains(name)) { Owner = this });

        AvailableThemes.Clear();
        if (themeNames != null)
            foreach (var t in themeNames)
                AvailableThemes.Add(new CheckableItem(t, _working.IncludedColorThemes.Contains(t)) { Owner = this });

        AvailableFractalTypes.Clear();
        foreach (var ft in Enum.GetValues<FractalType>())
            AvailableFractalTypes.Add(new CheckableItem(ft.ToString(), _working.FilterFractalTypes.Contains(ft.ToString())) { Owner = this });

        AvailableQualityPresets.Clear();
        foreach (var name in new[] { "Draft", "Standard", "High", "Ultra", "Extreme" })
            AvailableQualityPresets.Add(new CheckableItem(name, _working.FilterQualityPresets.Contains(name)) { Owner = this });
    }

    internal void OnFilterItemChanged() => MarkDirty();

    /// <summary>Resulting config DTO populated by <see cref="OkCommand"/>
    /// (and by <see cref="SaveCommand"/> / <see cref="StartCommand"/>).
    /// Null until OK fires.</summary>
    public SlideshowConfig? Result { get; private set; }

    /// <summary>Back-compat shim for callers that still want a flat
    /// <see cref="SlideshowSettings"/>. Returns the Result's Timing block.</summary>
    public SlideshowSettings? ResultSettings => Result?.Timing;

    /// <summary>Audio-reactive master toggle as it was at OK time.</summary>
    public bool AudioReactiveResult { get; private set; }

    /// <summary>True when the user clicked Start (rather than OK) so the
    /// shell knows to begin playback after the dialog closes.</summary>
    public bool StartRequested => _startRequested;

    public ObservableCollection<string> SavedConfigNames { get; }

    /// <summary>Active preset name. Setter swaps the working copy to the
    /// chosen preset (after confirming via dirty prompt at the view layer).</summary>
    public string ActiveName
    {
        get => _activeName;
        set
        {
            if (string.Equals(_activeName, value, StringComparison.Ordinal)) return;
            this.RaiseAndSetIfChanged(ref _activeName, value ?? string.Empty);
            if (_file != null && !string.IsNullOrWhiteSpace(value))
            {
                _file.ActiveName = value;
                _working = SlideshowConfigLibrary.GetActive(_file);
                _initializing = true;
                LoadWorkingIntoBindings();
                _initializing = false;
                IsDirty = false;
            }
        }
    }

    public SlideshowType Type
    {
        get => _type;
        set
        {
            this.RaiseAndSetIfChanged(ref _type, value);
            this.RaisePropertyChanged(nameof(IsVideo));
            MarkDirty();
        }
    }

    public bool IsVideo => _type == SlideshowType.Video;

    public bool AudioReactive
    {
        get => _audioReactive;
        set
        {
            this.RaiseAndSetIfChanged(ref _audioReactive, value);
            this.RaisePropertyChanged(nameof(TimingEnabled));
            this.RaisePropertyChanged(nameof(TimingNoteVisible));
            MarkDirty();
        }
    }

    public bool UseExtremeRegions
    {
        get => _useExtremeRegions;
        set { this.RaiseAndSetIfChanged(ref _useExtremeRegions, value); MarkDirty(); }
    }

    public int TotalDisplaySec
    {
        get => _totalDisplaySec;
        set { this.RaiseAndSetIfChanged(ref _totalDisplaySec, Math.Clamp(value, 3, 600)); MarkDirty(); }
    }

    public int ThemeFadeMs
    {
        get => _themeFadeMs;
        set { this.RaiseAndSetIfChanged(ref _themeFadeMs, Math.Clamp(value, 100, 20_000)); MarkDirty(); }
    }

    public int RegionFadeMs
    {
        get => _regionFadeMs;
        set { this.RaiseAndSetIfChanged(ref _regionFadeMs, Math.Clamp(value, 100, 20_000)); MarkDirty(); }
    }

    public int FadeSteps
    {
        get => _fadeSteps;
        set { this.RaiseAndSetIfChanged(ref _fadeSteps, Math.Clamp(value, 2, 200)); MarkDirty(); }
    }

    public bool UseRegionWatermark
    {
        get => _useRegionWatermark;
        set { this.RaiseAndSetIfChanged(ref _useRegionWatermark, value); MarkDirty(); }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    public bool TimingEnabled => !_audioReactive;
    public bool TimingNoteVisible => _audioReactive;

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowAudioDialogCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> EditVideoSettingsCommand { get; }

    /// <summary>Raised when the user clicks the Audio… button. The host
    /// decides which audio dialog to open.</summary>
    public event EventHandler? ShowAudioDialogRequested;

    /// <summary>Raised when the user clicks Import… The host shows a file
    /// picker and calls back into <see cref="ApplyImportedConfig"/>.</summary>
    public event EventHandler? ImportRequested;

    /// <summary>Raised when the user clicks Export… The host shows a file
    /// picker and calls <see cref="SlideshowConfigLibrary.Export"/>.</summary>
    public event EventHandler? ExportRequested;

    /// <summary>Raised when the user clicks "Video Settings…". The host pops
    /// the embedded VideoDialog and calls back via
    /// <see cref="ApplyEditedVideoSettings"/>.</summary>
    public event EventHandler? EditVideoSettingsRequested;

    /// <summary>Raised when the unified Start path needs the unsaved-prompt
    /// (Start-or-Save). Host shows the prompt and either calls
    /// <see cref="ProceedToStart"/> or focuses the Name combo via
    /// <see cref="RequestNameFocus"/>.</summary>
    public event EventHandler? UnsavedStartPrompt;

    /// <summary>Raised when the host should focus the Name combo (Save path
    /// after the unsaved-prompt).</summary>
    public event EventHandler? NameFocusRequested;

    private void RequestStart()
    {
        Commit();
        if (IsDirty && IsLibraryMode)
            UnsavedStartPrompt?.Invoke(this, EventArgs.Empty);
        else
            ProceedToStart();
    }

    /// <summary>Called by the host after a confirmed Start dispatch.</summary>
    public void ProceedToStart()
    {
        _startRequested = true;
        StartRequestedRaised?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fires after <see cref="ProceedToStart"/>. The host listens to
    /// close the dialog and route to the slideshow engine.</summary>
    public event EventHandler? StartRequestedRaised;

    public void RequestNameFocus() => NameFocusRequested?.Invoke(this, EventArgs.Empty);

    private void SaveActive()
    {
        if (_file == null) return;
        Commit();
        if (Result == null) return;
        SlideshowConfigLibrary.Upsert(_file, Result);
        RefreshNameList();
        IsDirty = false;
    }

    private void DeleteActive()
    {
        if (_file == null) return;
        if (SlideshowConfigLibrary.Delete(_file, _activeName))
        {
            _working = SlideshowConfigLibrary.GetActive(_file);
            _initializing = true;
            _activeName = _file.ActiveName;
            LoadWorkingIntoBindings();
            _initializing = false;
            RefreshNameList();
            this.RaisePropertyChanged(nameof(ActiveName));
            IsDirty = false;
        }
    }

    /// <summary>Host calls this after the user picks a file in the Import
    /// dialog. The VM refreshes the Name list and switches to the imported
    /// preset.</summary>
    public void ApplyImportedConfig(string? importedName)
    {
        if (_file == null || string.IsNullOrWhiteSpace(importedName)) return;
        _working = SlideshowConfigLibrary.GetActive(_file);
        _initializing = true;
        _activeName = _file.ActiveName;
        LoadWorkingIntoBindings();
        _initializing = false;
        RefreshNameList();
        this.RaisePropertyChanged(nameof(ActiveName));
        IsDirty = false;
    }

    /// <summary>Host calls this after the embedded VideoDialog returns OK.</summary>
    public void ApplyEditedVideoSettings(VideoSettingsConfig? video)
    {
        if (video == null) return;
        _working.Video = video;
        MarkDirty();
    }

    private void Commit()
    {
        AudioReactiveResult = _audioReactive;
        _working.Name = string.IsNullOrWhiteSpace(_activeName) ? "Default" : _activeName;
        _working.Type = _type;
        _working.Timing.UseExtremeRegions = _useExtremeRegions;
        _working.Timing.TotalDisplayMsPerRegion = _totalDisplaySec * 1000;
        _working.Timing.ColorThemeFadeMs = _themeFadeMs;
        _working.Timing.RegionFadeMs = _regionFadeMs;
        _working.Timing.FadeSteps = _fadeSteps;
        _working.Timing.UseRegionWatermark = _useRegionWatermark;
        _working.AudioReactive = _audioReactive;

        _working.IncludedRegions = AvailableRegions.Where(i => i.IsChecked).Select(i => i.Name).ToList();
        _working.IncludedColorThemes = AvailableThemes.Where(i => i.IsChecked).Select(i => i.Name).ToList();
        _working.FilterFractalTypes = AvailableFractalTypes.Where(i => i.IsChecked).Select(i => i.Name).ToList();
        _working.FilterQualityPresets = AvailableQualityPresets.Where(i => i.IsChecked).Select(i => i.Name).ToList();

        Result = _working.Clone();
    }

    private void LoadWorkingIntoBindings()
    {
        _type = _working.Type;
        _useExtremeRegions = _working.Timing.UseExtremeRegions;
        _totalDisplaySec = Math.Clamp(_working.Timing.TotalDisplayMsPerRegion / 1000, 3, 600);
        _themeFadeMs = _working.Timing.ColorThemeFadeMs;
        _regionFadeMs = _working.Timing.RegionFadeMs;
        _fadeSteps = _working.Timing.FadeSteps;
        _useRegionWatermark = _working.Timing.UseRegionWatermark;
        this.RaisePropertyChanged(nameof(Type));
        this.RaisePropertyChanged(nameof(IsVideo));
        this.RaisePropertyChanged(nameof(UseExtremeRegions));
        this.RaisePropertyChanged(nameof(TotalDisplaySec));
        this.RaisePropertyChanged(nameof(ThemeFadeMs));
        this.RaisePropertyChanged(nameof(RegionFadeMs));
        this.RaisePropertyChanged(nameof(FadeSteps));
        this.RaisePropertyChanged(nameof(UseRegionWatermark));
    }

    private void RefreshNameList()
    {
        if (_file == null) return;
        SavedConfigNames.Clear();
        foreach (var c in _file.Configs) SavedConfigNames.Add(c.Name);
    }

    private void MarkDirty()
    {
        if (_initializing) return;
        IsDirty = true;
    }
}
