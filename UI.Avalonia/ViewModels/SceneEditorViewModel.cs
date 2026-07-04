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
using FracturingFog.Models;
using FracturingFog.Render;
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
/// authored fields plus its optional camera track. The asset-picker lists
/// (regions / themes / animations / fractal types / transitions) are shared
/// references handed down by the parent so every row's combos bind to the same
/// snapshot.</summary>
public sealed class SceneShotRowViewModel : ReactiveObject
{
    // Combo sentinels — empty region = render default params for the type;
    // empty theme / animation = the region's own. Combos can't bind a blank
    // string cleanly, so a display sentinel stands in for "none".
    public const string RegionNone = "(default params)";
    public const string ThemeNone = "(region default)";
    public const string AnimationNone = "(none)";

    private readonly Action _onChanged;

    public SceneShotRowViewModel(
        IReadOnlyList<string> regionNames,
        IReadOnlyList<string> themeNames,
        IReadOnlyList<string> animationNames,
        IReadOnlyList<FractalType> fractalTypes,
        IReadOnlyList<SceneTransitionKind> transitionKinds,
        Action onChanged,
        Action<SceneShotRowViewModel> onRemove,
        Action<SceneShotRowViewModel> onMoveUp,
        Action<SceneShotRowViewModel> onMoveDown,
        Action<SceneShotRowViewModel> onPreview)
    {
        _onChanged = onChanged;
        RegionNames = regionNames;
        ThemeNames = themeNames;
        AnimationNames = animationNames;
        FractalTypes = fractalTypes;
        TransitionKinds = transitionKinds;

        RemoveCommand    = ReactiveCommand.Create(() => onRemove(this));
        MoveUpCommand    = ReactiveCommand.Create(() => onMoveUp(this));
        MoveDownCommand  = ReactiveCommand.Create(() => onMoveDown(this));
        PreviewCommand   = ReactiveCommand.Create(() => onPreview(this));
        AddCameraKeyCommand = ReactiveCommand.Create(AddCameraKey);

        CameraKeys = new ObservableCollection<CameraKeyRowViewModel>();
    }

    // ── Shared picker sources ────────────────────────────────────────────────
    public IReadOnlyList<string> RegionNames { get; }
    public IReadOnlyList<string> ThemeNames { get; }
    public IReadOnlyList<string> AnimationNames { get; }
    public IReadOnlyList<FractalType> FractalTypes { get; }
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
        set { this.RaiseAndSetIfChanged(ref _selectedRegion, value); _onChanged(); }
    }

    private string _selectedTheme = ThemeNone;
    public string SelectedTheme
    {
        get => _selectedTheme;
        set { this.RaiseAndSetIfChanged(ref _selectedTheme, value); _onChanged(); }
    }

    private string _selectedAnimation = AnimationNone;
    public string SelectedAnimation
    {
        get => _selectedAnimation;
        set { this.RaiseAndSetIfChanged(ref _selectedAnimation, value); _onChanged(); }
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
            _onChanged();
        }
    }

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

    /// <summary>True when this shot's fractal type has an orbit camera to drive
    /// (the raymarch 3D types). Hides the camera row for 2D shots.</summary>
    public bool Supports3DCamera => CameraParamBinding.Supports(_fractalType);

    public ObservableCollection<CameraKeyRowViewModel> CameraKeys { get; }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCameraKeyCommand { get; }

    private void AddCameraKey()
    {
        // Seed the new key one second past the current last, mirroring its pose
        // so the user tweaks from a sane starting point rather than zeros.
        double time = CameraKeys.Count > 0 ? CameraKeys[^1].Time + 1.0 : 0.0;
        var seed = CameraKeys.Count > 0
            ? new CameraState(CameraKeys[^1].Distance, CameraKeys[^1].Theta, CameraKeys[^1].Phi)
            : new CameraState(2.6, 0.0, 0.3);
        AddKeyRow(new CameraKey(time, seed));
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
            AnimationName = string.Equals(_selectedAnimation, AnimationNone, StringComparison.Ordinal)
                ? null : _selectedAnimation,
            FractalType = _fractalType,
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

    /// <summary>Populate this row from a saved shot.</summary>
    public void Populate(SceneShot shot)
    {
        _name = shot.Name ?? string.Empty;
        _selectedRegion = string.IsNullOrEmpty(shot.RegionName) ? RegionNone
            : (RegionNames.Contains(shot.RegionName) ? shot.RegionName : RegionNone);
        _selectedTheme = string.IsNullOrEmpty(shot.ThemeName) ? ThemeNone
            : (ThemeNames.Contains(shot.ThemeName!) ? shot.ThemeName! : ThemeNone);
        _selectedAnimation = string.IsNullOrEmpty(shot.AnimationName) ? AnimationNone
            : (AnimationNames.Contains(shot.AnimationName!) ? shot.AnimationName! : AnimationNone);
        _fractalType = shot.FractalType;
        _durationSeconds = shot.DurationSeconds;
        _transition = shot.Transition;
        _transitionSeconds = shot.TransitionSeconds;

        CameraKeys.Clear();
        if (shot.Camera != null)
        {
            _interpolation = shot.Camera.Interpolation;
            foreach (var k in shot.Camera.Keys) AddKeyRow(k);
        }

        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(SelectedRegion));
        this.RaisePropertyChanged(nameof(SelectedTheme));
        this.RaisePropertyChanged(nameof(SelectedAnimation));
        this.RaisePropertyChanged(nameof(FractalType));
        this.RaisePropertyChanged(nameof(Supports3DCamera));
        this.RaisePropertyChanged(nameof(DurationSeconds));
        this.RaisePropertyChanged(nameof(Transition));
        this.RaisePropertyChanged(nameof(TransitionSeconds));
        this.RaisePropertyChanged(nameof(Interpolation));
    }
}

public sealed class SceneEditorViewModel : ViewModelBase
{
    private readonly IColorThemeService _service;
    private bool _suppressChange;
    private string? _loadedSourceName;

    public SceneEditorViewModel(IColorThemeService service, string? initialSceneName = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));

        SceneNames = new ObservableCollection<string>(_service.EnumerateSceneNames());

        // Region / theme / animation picker sources, each prefixed with a "none"
        // sentinel so a shot can opt out of the override.
        _regionNames = BuildList(SceneShotRowViewModel.RegionNone, _service.EnumerateRegionNames());
        _themeNames = BuildList(SceneShotRowViewModel.ThemeNone, _service.EnumerateThemeNames());
        _animationNames = BuildList(SceneShotRowViewModel.AnimationNone, _service.EnumerateAnimationNames());

        AvailableFractalTypes = Enum.GetValues<FractalType>()
            .OrderBy(ft => ft.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        TransitionKinds = Enum.GetValues<SceneTransitionKind>();

        Shots = new ObservableCollection<SceneShotRowViewModel>();

        NewBlankCommand    = ReactiveCommand.Create(NewBlank);
        RevertCommand      = ReactiveCommand.Create(Revert);
        SaveCommand        = ReactiveCommand.CreateFromTask(SaveAsync);
        DeleteCommand      = ReactiveCommand.CreateFromTask(DeleteAsync);
        AddShotCommand     = ReactiveCommand.Create(AddShot);
        PlayCommand        = ReactiveCommand.Create(Play);
        ExportCommand      = ReactiveCommand.CreateFromTask(ExportAsync);
        StopPreviewCommand = ReactiveCommand.Create(StopPreview);
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

    private static List<string> BuildList(string sentinel, IReadOnlyList<string> names)
    {
        var list = new List<string>(names.Count + 1) { sentinel };
        list.AddRange(names);
        return list;
    }

    // ── Collections ───────────────────────────────────────────────────────────
    public ObservableCollection<string> SceneNames { get; }
    public ObservableCollection<SceneShotRowViewModel> Shots { get; }

    private readonly List<string> _regionNames;
    private readonly List<string> _themeNames;
    private readonly List<string> _animationNames;
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
    };

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
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> StopPreviewCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    // ── Events for the shell ───────────────────────────────────────────────────

    /// <summary>Fires after a successful Save so the shell can refresh the Asset
    /// Manager / scene lists.</summary>
    public event EventHandler<string>? SceneSavedToLibrary;
    public event EventHandler<string>? SceneDeletedFromLibrary;

    /// <summary>Preview a single shot: the shell applies the shot's region /
    /// theme / animation to the live view (static framing).</summary>
    public event EventHandler<SceneShot>? PreviewShotRequested;

    /// <summary>Play the whole scene in realtime (S6): the shell walks the
    /// timeline, sequencing shots on the live view with per-shot camera + param
    /// motion on the animation bus.</summary>
    public event EventHandler<SceneData>? PlaySceneRequested;

    /// <summary>Export the whole scene to a video file offline (S8 polish): the
    /// host picks an output path and runs the Engine's frame-locked
    /// SceneVideoRenderer (motion blur + composited transitions).</summary>
    public event EventHandler<SceneExportEventArgs>? ExportSceneRequested;

    public event EventHandler? StopPreviewRequested;

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
        };
        foreach (var row in Shots) data.Shots.Add(row.ToShot());
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
            TitleText = $"Scene Editor — {Name}";
        }
        finally { _suppressChange = false; }
        RecomputeTotal();
    }

    private SceneShotRowViewModel NewShotRow()
        => new(_regionNames, _themeNames, _animationNames, AvailableFractalTypes, TransitionKinds,
               FieldChanged, RemoveShot, MoveShotUp, MoveShotDown, PreviewShot);

    private void AddShot()
    {
        var row = NewShotRow();
        row.Name = $"Shot {Shots.Count + 1}";
        Shots.Add(row);
        SelectedShot = row;
        FieldChanged();
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

    private void PreviewShot(SceneShotRowViewModel row)
    {
        SelectedShot = row;
        PreviewShotRequested?.Invoke(this, row.ToShot());
    }

    private void Play() => PlaySceneRequested?.Invoke(this, BuildData());

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
        _                        => SceneExportEncode.HighQualityH264,
    };

    private void StopPreview() => StopPreviewRequested?.Invoke(this, EventArgs.Empty);

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
            Shots.Clear();
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
        foreach (var n in _service.EnumerateSceneNames()) SceneNames.Add(n);
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
        foreach (var n in _service.EnumerateSceneNames()) SceneNames.Add(n);
        _suppressChange = false;

        SceneDeletedFromLibrary?.Invoke(this, deleted);
        NewBlank();
        await RaiseMessageAsync(new ThemeMessageEventArgs(
            "Delete Scene", $"\"{deleted}\" deleted.", MessageSeverity.Info));
    }

    private Task RaiseMessageAsync(ThemeMessageEventArgs args)
    {
        var handler = MessageRequested;
        handler?.Invoke(this, args);
        if (handler == null) args.Completion.TrySetResult(true);
        return args.Completion.Task;
    }
}
