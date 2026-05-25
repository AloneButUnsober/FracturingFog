using System;
using System.Collections.ObjectModel;
using System.Reactive;
using FracturingFog.Audio;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// Avalonia port of <c>AudioSettingsDialog</c>. Edits a working copy of the
/// caller's <see cref="AudioSettings"/>; <see cref="Result"/> is the committed
/// copy populated on OK. Live BPM/level meters tick from a host-driven
/// callback (host owns the meter timer to avoid sucking an Avalonia
/// dispatcher into the slideshow loop on shutdown).
/// </summary>
public sealed class AudioSettingsViewModel : ViewModelBase
{
    private readonly AudioSettings _working;
    private readonly IBeatSource? _liveSource;
    private readonly Action? _slideshowToggle;
    private readonly Func<bool>? _slideshowIsRunning;

    public static readonly string[] EqBandNames = { "Bass", "LowMid", "Mid", "HighMid", "High" };
    public string Band0Name => EqBandNames[0];
    public string Band1Name => EqBandNames[1];
    public string Band2Name => EqBandNames[2];
    public string Band3Name => EqBandNames[3];
    public string Band4Name => EqBandNames[4];

    public AudioSettingsViewModel(
        AudioSettings current,
        IBeatSource? liveSource,
        Action? slideshowToggle = null,
        Func<bool>? slideshowIsRunning = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _working = Clone(current);
        _liveSource = liveSource;
        _slideshowToggle = slideshowToggle;
        _slideshowIsRunning = slideshowIsRunning;

        _source = _working.Source;
        _filePath = _working.FilePath ?? string.Empty;
        _sensitivityPercent = (int)Math.Round(Math.Clamp(_working.Sensitivity, 0f, 1f) * 100);
        _beatsPerTheme = _working.BeatsPerTheme;
        _beatsPerRegion = _working.BeatsPerRegion;
        _synthBpm = (int)Math.Round(_working.SynthBpm);
        _routeSynth = _working.RouteSynthThroughAnalyzer;
        _playSynth = _working.PlaySynthOutput;
        _fadeFracPercent = (int)Math.Clamp(Math.Round(_working.FadeBeatFraction * 100.0), 10, 200);

        int[] bw = new int[5] { 100, 100, 100, 100, 100 };
        if (_working.BandWeights != null)
            for (int i = 0; i < 5 && i < _working.BandWeights.Length; i++)
                bw[i] = Math.Clamp((int)Math.Round(_working.BandWeights[i] * 100f), 0, 200);
        _band0Percent = bw[0]; _band1Percent = bw[1]; _band2Percent = bw[2];
        _band3Percent = bw[3]; _band4Percent = bw[4];

        Sources = new ObservableCollection<string>
        {
            "System Loopback (what's currently playing)",
            "Audio File (MP3/WAV/FLAC/OGG)",
            "Microphone",
            "Fractal Synth (closed-loop)"
        };

        OkCommand = ReactiveCommand.Create(() => { Commit(); CloseRequested?.Invoke(this, true); });
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, false));
        BrowseFileCommand = ReactiveCommand.Create(() => BrowseFileRequested?.Invoke(this, EventArgs.Empty));
        ResetEqCommand = ReactiveCommand.Create(() =>
        {
            Band0Percent = 100; Band1Percent = 100; Band2Percent = 100;
            Band3Percent = 100; Band4Percent = 100;
        });
        ToggleSlideshowCommand = ReactiveCommand.Create(() =>
        {
            try { _slideshowToggle?.Invoke(); } catch { }
            RefreshSlideshowState();
        });

        UpdateSourceFlags();
        RefreshSlideshowState();
        UpdateMeters();
    }

    public AudioSettings Result { get; private set; } = new();
    public bool ShowSlideshowToggle => _slideshowToggle != null && _slideshowIsRunning != null;

    public ObservableCollection<string> Sources { get; }

    private int _band0Percent;
    public int Band0Percent { get => _band0Percent; set { this.RaiseAndSetIfChanged(ref _band0Percent, Math.Clamp(value, 0, 200)); this.RaisePropertyChanged(nameof(Band0Label)); } }
    public string Band0Label => $"{_band0Percent}%";

    private int _band1Percent;
    public int Band1Percent { get => _band1Percent; set { this.RaiseAndSetIfChanged(ref _band1Percent, Math.Clamp(value, 0, 200)); this.RaisePropertyChanged(nameof(Band1Label)); } }
    public string Band1Label => $"{_band1Percent}%";

    private int _band2Percent;
    public int Band2Percent { get => _band2Percent; set { this.RaiseAndSetIfChanged(ref _band2Percent, Math.Clamp(value, 0, 200)); this.RaisePropertyChanged(nameof(Band2Label)); } }
    public string Band2Label => $"{_band2Percent}%";

    private int _band3Percent;
    public int Band3Percent { get => _band3Percent; set { this.RaiseAndSetIfChanged(ref _band3Percent, Math.Clamp(value, 0, 200)); this.RaisePropertyChanged(nameof(Band3Label)); } }
    public string Band3Label => $"{_band3Percent}%";

    private int _band4Percent;
    public int Band4Percent { get => _band4Percent; set { this.RaiseAndSetIfChanged(ref _band4Percent, Math.Clamp(value, 0, 200)); this.RaisePropertyChanged(nameof(Band4Label)); } }
    public string Band4Label => $"{_band4Percent}%";

    private AudioSourceKind _source;
    public AudioSourceKind Source
    {
        get => _source;
        set { this.RaiseAndSetIfChanged(ref _source, value); UpdateSourceFlags(); }
    }
    public int SourceIndex
    {
        get => (int)_source;
        set { Source = (AudioSourceKind)value; this.RaisePropertyChanged(); }
    }

    private string _filePath;
    public string FilePath { get => _filePath; set => this.RaiseAndSetIfChanged(ref _filePath, value); }

    private bool _fileModeEnabled;
    public bool FileModeEnabled { get => _fileModeEnabled; private set => this.RaiseAndSetIfChanged(ref _fileModeEnabled, value); }

    private bool _synthControlsEnabled;
    public bool SynthControlsEnabled { get => _synthControlsEnabled; private set => this.RaiseAndSetIfChanged(ref _synthControlsEnabled, value); }

    private int _sensitivityPercent;
    public int SensitivityPercent
    {
        get => _sensitivityPercent;
        set { this.RaiseAndSetIfChanged(ref _sensitivityPercent, Math.Clamp(value, 0, 100)); this.RaisePropertyChanged(nameof(SensitivityLabel)); }
    }
    public string SensitivityLabel => $"{_sensitivityPercent}%";

    private int _beatsPerTheme;
    public int BeatsPerTheme { get => _beatsPerTheme; set => this.RaiseAndSetIfChanged(ref _beatsPerTheme, Math.Clamp(value, 1, 128)); }

    private int _beatsPerRegion;
    public int BeatsPerRegion { get => _beatsPerRegion; set => this.RaiseAndSetIfChanged(ref _beatsPerRegion, Math.Clamp(value, 1, 512)); }

    private int _synthBpm;
    public int SynthBpm { get => _synthBpm; set => this.RaiseAndSetIfChanged(ref _synthBpm, Math.Clamp(value, 30, 240)); }

    private bool _routeSynth;
    public bool RouteSynth { get => _routeSynth; set => this.RaiseAndSetIfChanged(ref _routeSynth, value); }

    private bool _playSynth;
    public bool PlaySynth { get => _playSynth; set => this.RaiseAndSetIfChanged(ref _playSynth, value); }

    private int _fadeFracPercent;
    public int FadeFracPercent
    {
        get => _fadeFracPercent;
        set
        {
            this.RaiseAndSetIfChanged(ref _fadeFracPercent, Math.Clamp(value, 10, 200));
            this.RaisePropertyChanged(nameof(FadeFracLabel));
        }
    }
    public string FadeFracLabel => $"{_fadeFracPercent / 100.0:F2}× beat";

    // ── Meter readouts (host calls Tick() periodically) ──
    private string _bpmText = "BPM: —";
    public string BpmText { get => _bpmText; private set => this.RaiseAndSetIfChanged(ref _bpmText, value); }
    private string _levelText = "Level: —";
    public string LevelText { get => _levelText; private set => this.RaiseAndSetIfChanged(ref _levelText, value); }

    // ── Slideshow toggle button state ──
    private string _slideshowButtonText = "▶ Start Slideshow";
    public string SlideshowButtonText { get => _slideshowButtonText; private set => this.RaiseAndSetIfChanged(ref _slideshowButtonText, value); }
    private bool _slideshowRunning;
    public bool SlideshowRunning { get => _slideshowRunning; private set => this.RaiseAndSetIfChanged(ref _slideshowRunning, value); }

    public ReactiveCommand<Unit, Unit> OkCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> BrowseFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetEqCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSlideshowCommand { get; }

    /// <summary>Host opens an OpenFile dialog and sets <see cref="FilePath"/> on success.</summary>
    public event EventHandler? BrowseFileRequested;

    /// <summary>Raised when Ok/Cancel commits the dialog. Bool = true on OK.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Host should call this every ~100ms to refresh the meters/slideshow state.</summary>
    public void Tick()
    {
        UpdateMeters();
        RefreshSlideshowState();
    }

    private void UpdateSourceFlags()
    {
        FileModeEnabled = _source == AudioSourceKind.File;
        SynthControlsEnabled = _source == AudioSourceKind.FractalSynth;
    }

    private void UpdateMeters()
    {
        if (_liveSource is null || !_liveSource.IsActive)
        {
            BpmText = "BPM: —";
            LevelText = "Level: —";
            return;
        }
        double bpm = _liveSource.EstimatedBpm;
        BpmText = bpm > 0 ? $"BPM: {bpm:F1}" : "BPM: (detecting…)";
        var e = _liveSource.CurrentEnergy;
        LevelText = $"Bass {Bar(e.Bass)}  Mid {Bar(e.Mid)}  High {Bar(e.High)}";
    }

    private void RefreshSlideshowState()
    {
        if (_slideshowIsRunning is null) return;
        bool running = _slideshowIsRunning();
        SlideshowRunning = running;
        SlideshowButtonText = running ? "■ Stop Slideshow" : "▶ Start Slideshow";
    }

    private static string Bar(float v)
    {
        int n = Math.Clamp((int)Math.Round(v * 8), 0, 8);
        return new string('█', n) + new string('░', 8 - n);
    }

    private void Commit()
    {
        _working.Source = _source;
        _working.FilePath = string.IsNullOrWhiteSpace(_filePath) ? null : _filePath;
        _working.Sensitivity = _sensitivityPercent / 100f;
        _working.BeatsPerTheme = _beatsPerTheme;
        _working.BeatsPerRegion = _beatsPerRegion;
        _working.RouteSynthThroughAnalyzer = _routeSynth;
        _working.PlaySynthOutput = _playSynth;
        _working.SynthBpm = _synthBpm;
        _working.BandWeights = new float[]
        {
            _band0Percent / 100f, _band1Percent / 100f, _band2Percent / 100f,
            _band3Percent / 100f, _band4Percent / 100f
        };
        _working.FadeBeatFraction = Math.Clamp(_fadeFracPercent / 100.0, 0.1, 2.0);
        Result = _working;
    }

    private static AudioSettings Clone(AudioSettings s) => new()
    {
        Enabled = s.Enabled,
        Source = s.Source,
        FilePath = s.FilePath,
        Sensitivity = s.Sensitivity,
        BeatsPerTheme = s.BeatsPerTheme,
        BeatsPerRegion = s.BeatsPerRegion,
        RouteSynthThroughAnalyzer = s.RouteSynthThroughAnalyzer,
        PlaySynthOutput = s.PlaySynthOutput,
        SynthBpm = s.SynthBpm,
        BandWeights = s.BandWeights != null
            ? (float[])s.BandWeights.Clone()
            : new[] { 1f, 1f, 1f, 1f, 1f },
        FadeBeatFraction = s.FadeBeatFraction
    };
}
