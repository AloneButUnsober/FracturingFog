// ViewModels/UserBulbViewModel.cs
//
// Avalonia port of the legacy WinForms UserBulbDialog. Mirrors
// UserEquationViewModel's debounce-on-source-change pattern (500 ms idle
// → CompileRequested) plus a deeper knob set covering camera / render /
// julia / colour driver / lighting / view / chain / params / animation
// for the 3D bulb pipeline.
//
// Knob changes are split into two channels:
//   • Source / structural (chain, params, axis mode, dropdown picks
//     that change the compiled function) → CompileRequested
//   • Numeric / value tweaks (camera angle, light intensity, julia c,
//     animation t) → RenderRequested (no recompile)
//
// Host-side side effects routed via events: NamePromptRequested (save),
// ConfirmDeleteRequested, OpenFilePromptRequested (.fbulb import),
// SaveFilePromptRequested (.fbulb export, OBJ mesh export),
// MessageRequested (errors / info), PromotionChanged.
//
// AnimationTick() is the public step entry point the host calls from its
// own ~30 Hz timer when SetAnimationPlaying(true) is in effect — that
// keeps the threading model in main project hands and avoids spinning
// up a UI-thread DispatcherTimer in the VM.

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class UserBulbViewModel : ViewModelBase
{
    private readonly FractalParameters _params;
    private readonly System.Reactive.Disposables.SerialDisposable _debounce = new();
    private bool _loadingNamedEquation;
    private bool _suppressRender;

    public UserBulbViewModel(FractalParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _params = parameters;

        _source = string.IsNullOrWhiteSpace(parameters.UserBulbSource)
            ? DefaultSource
            : parameters.UserBulbSource;

        // Initial knob mirror.
        _camDistance     = _params.UserBulbCameraDistance;
        _camThetaDeg     = RadToDeg(_params.UserBulbCameraTheta);
        _camPhiDeg       = RadToDeg(_params.UserBulbCameraPhi);
        _lightThetaDeg   = RadToDeg(_params.UserBulbLightTheta);
        _lightPhiDeg     = RadToDeg(_params.UserBulbLightPhi);

        _iterations   = _params.UserBulbIterations;
        _maxSteps     = _params.UserBulbMaxSteps;
        _epsilon      = _params.UserBulbEpsilon;
        _bailout      = _params.UserBulbBailout;
        _jacobianH    = _params.UserBulbJacobianH;
        _cullRadius   = _params.UserBulbCullRadius;
        _deModeIndex  = (int)_params.UserBulbDEMode;
        _backendIndex = (int)_params.UserBulbBackend;
        _compilerIndex = (int)_params.UserBulbCompiler;
        _axisModeIndex = (int)_params.UserBulbAxisMode;
        _quatSliceW   = _params.UserBulbQuatSliceW;

        _juliaMode = _params.UserBulbJuliaMode;
        _juliaCX = _params.UserBulbJuliaCX;
        _juliaCY = _params.UserBulbJuliaCY;
        _juliaCZ = _params.UserBulbJuliaCZ;
        _juliaCW = _params.UserBulbJuliaCW;

        _colorDriverIndex = (int)_params.UserBulbColorDriver;
        _trapX = _params.UserBulbOrbitTrapX;
        _trapY = _params.UserBulbOrbitTrapY;
        _trapZ = _params.UserBulbOrbitTrapZ;
        _iterAxis = Math.Clamp(_params.UserBulbIterComponentAxis, 0, 2);

        _light1 = _params.UserBulbLight1Intensity;
        _light2 = _params.UserBulbLight2Intensity;
        _light3 = _params.UserBulbLight3Intensity;
        _aoSamples = _params.UserBulbAOSamples;
        _fogDensity = _params.UserBulbFogDensity;

        _fovDegrees = _params.UserBulbFovDegrees;
        _clipPlane  = _params.UserBulbClipPlaneEnabled;
        _ssIndex = _params.UserBulbSuperSample switch { 4 => 2, 2 => 1, _ => 0 };

        _animTime = _params.UserBulbTime;

        Params = new ObservableCollection<UserBulbParam>(_params.UserBulbParams);
        Chain  = new ObservableCollection<UserBulbChainStep>(_params.UserBulbChain);
        SavedNames = new ObservableCollection<string>();

        UserBulbStore.Instance.Load();
        RefreshSavedList(_params.UserBulbName);

        SaveCommand = ReactiveCommand.Create(OnSave);
        DeleteCommand = ReactiveCommand.Create(OnDelete,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        ImportCommand = ReactiveCommand.Create(OnImport);
        ExportCommand = ReactiveCommand.Create(OnExport,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        ResetCameraCommand = ReactiveCommand.Create(OnResetCamera);
        AddParamCommand = ReactiveCommand.Create(OnAddParam);
        AddChainCommand = ReactiveCommand.Create(OnAddChain);
        RemoveParamCommand = ReactiveCommand.Create<UserBulbParam>(OnRemoveParam);
        RemoveChainCommand = ReactiveCommand.Create<UserBulbChainStep>(OnRemoveChain);
        TogglePlayCommand = ReactiveCommand.Create(OnTogglePlay);
        ExportMeshCommand = ReactiveCommand.Create(OnExportMesh);
        OpenHelpCommand = ReactiveCommand.Create(() =>
        {
            // Jump directly to the Sandbox DSL chapter when the Sandbox
            // compiler is active — otherwise show the whole guide from top.
            string? anchor = IsSandbox ? "Sandbox DSL Compiler" : null;
            HelpRequested?.Invoke(this, ("User/UserBulb-Guide.md", anchor, "User Bulb 3D — Help"));
        });
    }

    // ── Source + debounce ──────────────────────────────────────────────

    private string _source;
    public string Source
    {
        get => _source;
        set
        {
            this.RaiseAndSetIfChanged(ref _source, value);
            if (_loadingNamedEquation) return;
            // Manual edits dissociate from any named saved entry.
            _params.UserBulbName = null;
            this.RaisePropertyChanged(nameof(SelectedSavedName));
            ScheduleCompile();
        }
    }

    private void ScheduleCompile()
    {
        // 1200 ms debounce matches the User Equation editor — shorter values
        // let the selection-based error-span highlight clobber the next
        // keystroke during fast typing. See [[feedback_validation_debounce]].
        _debounce.Disposable = Observable
            .Timer(TimeSpan.FromMilliseconds(1200))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                _params.UserBulbSource = _source;
                CompileRequested?.Invoke(this, EventArgs.Empty);
            });
    }

    // ── Error span (consumed by code-behind to set TextBox.Selection) ─────

    private int _errorSpanStart;
    private int _errorSpanLength;
    public int ErrorSpanStart { get => _errorSpanStart; private set => this.RaiseAndSetIfChanged(ref _errorSpanStart, value); }
    public int ErrorSpanLength { get => _errorSpanLength; private set => this.RaiseAndSetIfChanged(ref _errorSpanLength, value); }

    /// <summary>Raised after error-span changes so the view can apply the
    /// span to the source TextBox.</summary>
    public event EventHandler? ErrorSpanChanged;

    /// <summary>Host calls this with the parser-reported position + length.
    /// Pass (-1, 0) to clear.</summary>
    public void SetErrorSpan(int position, int length)
    {
        int clampedStart = Math.Max(0, position);
        int clampedLen = position < 0 ? 0 : Math.Max(0, length);
        bool changed = clampedStart != _errorSpanStart || clampedLen != _errorSpanLength;
        ErrorSpanStart = clampedStart;
        ErrorSpanLength = clampedLen;
        if (changed) ErrorSpanChanged?.Invoke(this, EventArgs.Empty);
    }

    public string HintText => HintFor((UserBulbAxisModeKind)_axisModeIndex);

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _statusMessage, value);
            this.RaisePropertyChanged(nameof(StatusIsError));
        }
    }

    private bool _statusIsError;
    public bool StatusIsError
    {
        get => _statusIsError;
        private set => this.RaiseAndSetIfChanged(ref _statusIsError, value);
    }

    // ── Saved equations ────────────────────────────────────────────────

    public ObservableCollection<string> SavedNames { get; }

    private string? _selectedSavedName;
    public string? SelectedSavedName
    {
        get => _selectedSavedName;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSavedName, value);
            SyncPromote();
            if (!string.IsNullOrEmpty(value) && !_loadingNamedEquation)
                LoadEquationByName(value);
        }
    }

    private bool _promote;
    public bool Promote
    {
        get => _promote;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _promote, value)
                && !string.IsNullOrEmpty(_selectedSavedName))
            {
                if (UserBulbStore.Instance.SetPromoted(_selectedSavedName!, value))
                    PromotionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool _promoteEnabled;
    public bool PromoteEnabled
    {
        get => _promoteEnabled;
        private set => this.RaiseAndSetIfChanged(ref _promoteEnabled, value);
    }

    // ── Camera ─────────────────────────────────────────────────────────

    private double _camDistance;
    public double CamDistance { get => _camDistance; set => SetCam(ref _camDistance, value, () => _params.UserBulbCameraDistance = value); }

    private double _camThetaDeg;
    public double CamThetaDeg { get => _camThetaDeg; set => SetCam(ref _camThetaDeg, value, () => _params.UserBulbCameraTheta = DegToRad(value)); }

    private double _camPhiDeg;
    public double CamPhiDeg { get => _camPhiDeg; set => SetCam(ref _camPhiDeg, value, () => _params.UserBulbCameraPhi = DegToRad(value)); }

    private double _lightThetaDeg;
    public double LightThetaDeg { get => _lightThetaDeg; set => SetCam(ref _lightThetaDeg, value, () => _params.UserBulbLightTheta = DegToRad(value)); }

    private double _lightPhiDeg;
    public double LightPhiDeg { get => _lightPhiDeg; set => SetCam(ref _lightPhiDeg, value, () => _params.UserBulbLightPhi = DegToRad(value)); }

    // ── Render ─────────────────────────────────────────────────────────

    private int _iterations;
    public int Iterations { get => _iterations; set => SetRender(ref _iterations, Math.Clamp(value, 2, 64), () => _params.UserBulbIterations = _iterations); }

    private int _maxSteps;
    public int MaxSteps { get => _maxSteps; set => SetRender(ref _maxSteps, Math.Clamp(value, 16, 512), () => _params.UserBulbMaxSteps = _maxSteps); }

    private double _epsilon;
    public double Epsilon { get => _epsilon; set => SetRender(ref _epsilon, Math.Clamp(value, 0.00001, 0.1), () => _params.UserBulbEpsilon = _epsilon); }

    private double _bailout;
    public double Bailout { get => _bailout; set => SetRender(ref _bailout, Math.Clamp(value, 1.0, 100.0), () => _params.UserBulbBailout = _bailout); }

    private double _jacobianH;
    public double JacobianH { get => _jacobianH; set => SetRender(ref _jacobianH, Math.Clamp(value, 1e-7, 0.01), () => _params.UserBulbJacobianH = _jacobianH); }

    private double _cullRadius;
    public double CullRadius { get => _cullRadius; set => SetRender(ref _cullRadius, Math.Clamp(value, 0.1, 50.0), () => _params.UserBulbCullRadius = _cullRadius); }

    private int _deModeIndex;
    public int DEModeIndex { get => _deModeIndex; set => SetRender(ref _deModeIndex, Math.Clamp(value, 0, 2), () => _params.UserBulbDEMode = (UserBulbDEModeKind)_deModeIndex); }

    private int _backendIndex;
    public int BackendIndex { get => _backendIndex; set => SetRender(ref _backendIndex, Math.Clamp(value, 0, 1), () => _params.UserBulbBackend = (UserBulbBackendKind)_backendIndex); }

    private int _compilerIndex;
    /// <summary>0 = Roslyn (full C#), 1 = Sandbox (restricted DSL). Toggling triggers recompile.</summary>
    public int CompilerIndex
    {
        get => _compilerIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 1);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _compilerIndex, clamped))
            {
                _params.UserBulbCompiler = (UserBulbCompilerKind)clamped;
                this.RaisePropertyChanged(nameof(IsSandbox));
                if (!_suppressRender) CompileRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>True when the Sandbox DSL compiler is active. Reserved for
    /// future UI affordances; algebra combobox is no longer gated since
    /// Sandbox supports Quat via `qmul`/`qpow`/`qvec`/`.w`.</summary>
    public bool IsSandbox => _compilerIndex == (int)UserBulbCompilerKind.Sandbox;

    /// <summary>Retained for binding compatibility. Sandbox now supports Quat
    /// so the algebra combobox is always enabled.</summary>
    public bool AxisModeComboEnabled => true;

    private int _axisModeIndex;
    public int AxisModeIndex
    {
        get => _axisModeIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, 1);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _axisModeIndex, clamped))
            {
                _params.UserBulbAxisMode = (UserBulbAxisModeKind)clamped;
                this.RaisePropertyChanged(nameof(QuatEnabled));
                this.RaisePropertyChanged(nameof(HintText));
                if (!_suppressRender) CompileRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool QuatEnabled => _axisModeIndex == (int)UserBulbAxisModeKind.Quat;

    // ── Analytic-DE badge (set after each compile by the host) ────────────

    private string _analyticBadgeText = "Numerical only";
    public string AnalyticBadgeText
    {
        get => _analyticBadgeText;
        private set => this.RaiseAndSetIfChanged(ref _analyticBadgeText, value);
    }

    private bool _analyticBadgeOk;
    /// <summary>True when an analytic DE pattern was recognised on the
    /// current source. View uses this to pick green (#64FF64) vs dim grey.</summary>
    public bool AnalyticBadgeOk
    {
        get => _analyticBadgeOk;
        private set => this.RaiseAndSetIfChanged(ref _analyticBadgeOk, value);
    }

    /// <summary>Host calls this after each compile with the calculator's
    /// detected analytic-DE pattern. Pass <paramref name="label"/>=null or
    /// empty for "Numerical only" (numerical-Jacobian DE will be used).</summary>
    public void SetAnalyticBadge(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            AnalyticBadgeText = "Numerical only";
            AnalyticBadgeOk = false;
            return;
        }
        AnalyticBadgeText = label;
        AnalyticBadgeOk = true;
    }

    private double _quatSliceW;
    public double QuatSliceW { get => _quatSliceW; set => SetRender(ref _quatSliceW, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbQuatSliceW = _quatSliceW); }

    // ── Julia ──────────────────────────────────────────────────────────

    private bool _juliaMode;
    public bool JuliaMode { get => _juliaMode; set => SetRender(ref _juliaMode, value, () => _params.UserBulbJuliaMode = _juliaMode); }

    private double _juliaCX;
    public double JuliaCX { get => _juliaCX; set => SetRender(ref _juliaCX, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbJuliaCX = _juliaCX); }

    private double _juliaCY;
    public double JuliaCY { get => _juliaCY; set => SetRender(ref _juliaCY, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbJuliaCY = _juliaCY); }

    private double _juliaCZ;
    public double JuliaCZ { get => _juliaCZ; set => SetRender(ref _juliaCZ, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbJuliaCZ = _juliaCZ); }

    private double _juliaCW;
    public double JuliaCW { get => _juliaCW; set => SetRender(ref _juliaCW, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbJuliaCW = _juliaCW); }

    // ── Color driver ───────────────────────────────────────────────────

    private int _colorDriverIndex;
    public int ColorDriverIndex { get => _colorDriverIndex; set => SetRender(ref _colorDriverIndex, Math.Clamp(value, 0, 5), () => _params.UserBulbColorDriver = (BulbColorDriver)_colorDriverIndex); }

    private double _trapX;
    public double TrapX { get => _trapX; set => SetRender(ref _trapX, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbOrbitTrapX = _trapX); }

    private double _trapY;
    public double TrapY { get => _trapY; set => SetRender(ref _trapY, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbOrbitTrapY = _trapY); }

    private double _trapZ;
    public double TrapZ { get => _trapZ; set => SetRender(ref _trapZ, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbOrbitTrapZ = _trapZ); }

    private int _iterAxis;
    public int IterAxis { get => _iterAxis; set => SetRender(ref _iterAxis, Math.Clamp(value, 0, 2), () => _params.UserBulbIterComponentAxis = _iterAxis); }

    // ── Lighting ───────────────────────────────────────────────────────

    private double _light1;
    public double Light1 { get => _light1; set => SetRender(ref _light1, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbLight1Intensity = _light1); }

    private double _light2;
    public double Light2 { get => _light2; set => SetRender(ref _light2, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbLight2Intensity = _light2); }

    private double _light3;
    public double Light3 { get => _light3; set => SetRender(ref _light3, Math.Clamp(value, -10.0, 10.0), () => _params.UserBulbLight3Intensity = _light3); }

    private int _aoSamples;
    public int AOSamples { get => _aoSamples; set => SetRender(ref _aoSamples, Math.Clamp(value, 0, 16), () => _params.UserBulbAOSamples = _aoSamples); }

    private double _fogDensity;
    public double FogDensity { get => _fogDensity; set => SetRender(ref _fogDensity, Math.Clamp(value, 0.0, 5.0), () => _params.UserBulbFogDensity = _fogDensity); }

    // ── View ───────────────────────────────────────────────────────────

    private double _fovDegrees;
    public double FovDegrees { get => _fovDegrees; set => SetRender(ref _fovDegrees, Math.Clamp(value, 5.0, 170.0), () => _params.UserBulbFovDegrees = _fovDegrees); }

    private bool _clipPlane;
    public bool ClipPlane { get => _clipPlane; set => SetRender(ref _clipPlane, value, () => _params.UserBulbClipPlaneEnabled = _clipPlane); }

    private int _ssIndex;
    public int SSIndex
    {
        get => _ssIndex;
        set => SetRender(ref _ssIndex, Math.Clamp(value, 0, 2),
            () => _params.UserBulbSuperSample = _ssIndex switch { 2 => 4, 1 => 2, _ => 1 });
    }

    // ── Animation ──────────────────────────────────────────────────────

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPlaying, value);
            this.RaisePropertyChanged(nameof(PlayPauseLabel));
        }
    }

    public string PlayPauseLabel => _isPlaying ? "■" : "▶";

    private double _animSpeed = 1.0;
    public double AnimSpeed { get => _animSpeed; set => this.RaiseAndSetIfChanged(ref _animSpeed, Math.Clamp(value, -10.0, 10.0)); }

    private double _animTime;
    public double AnimTime
    {
        get => _animTime;
        set
        {
            double clamped = Math.Clamp(value, -1e6, 1e6);
            this.RaiseAndSetIfChanged(ref _animTime, clamped);
            if (_suppressRender) return;
            _params.UserBulbTime = clamped;
            RenderRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Render-in-flight guard. Host sets to true before kicking off the
    /// long raymarch and calls <see cref="NotifyRenderDone"/> when the
    /// frame uploads. AnimationTick() skips RenderRequested while a frame
    /// is still pending so timer ticks don't cancel each other.
    /// </summary>
    private volatile bool _renderInFlight;
    public void NotifyRenderDone() => _renderInFlight = false;

    /// <summary>
    /// Host invokes this from its own ~30 Hz timer while IsPlaying is true.
    /// Advances t by speed*dt and (if no render is pending) raises
    /// RenderRequested.
    /// </summary>
    public void AnimationTick(double dtSeconds)
    {
        if (!_isPlaying) return;
        _suppressRender = true;
        AnimTime = _params.UserBulbTime + _animSpeed * dtSeconds;
        _suppressRender = false;
        if (_renderInFlight) return;
        _renderInFlight = true;
        RenderRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── Lists (Params + Chain) ─────────────────────────────────────────

    public ObservableCollection<UserBulbParam> Params { get; }
    public ObservableCollection<UserBulbChainStep> Chain { get; }

    // ── Commands ───────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCameraCommand { get; }
    public ReactiveCommand<Unit, Unit> AddParamCommand { get; }
    public ReactiveCommand<Unit, Unit> AddChainCommand { get; }
    public ReactiveCommand<UserBulbParam, Unit> RemoveParamCommand { get; }
    public ReactiveCommand<UserBulbChainStep, Unit> RemoveChainCommand { get; }
    public ReactiveCommand<Unit, Unit> TogglePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportMeshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenHelpCommand { get; }

    /// <summary>Tuple: (docId, anchor, title). View opens HelpViewerView.
    /// Uses an <see cref="EventHandler{T}"/> to stay consistent with the
    /// rest of this VM's host-callback shape.</summary>
    public event EventHandler<(string DocId, string? Anchor, string Title)>? HelpRequested;

    // ── Events ─────────────────────────────────────────────────────────

    public event EventHandler? CompileRequested;
    public event EventHandler? RenderRequested;
    public event EventHandler? PromotionChanged;

    public event EventHandler<NamePromptEventArgs>? NamePromptRequested;
    public event EventHandler<ConfirmEventArgs>? ConfirmDeleteRequested;
    /// <summary>
    /// Fires when Save is about to replace an existing entry with the same
    /// name. Host shows a yes/no overwrite confirm and sets
    /// <see cref="ConfirmEventArgs.Result"/> true to proceed.
    /// </summary>
    public event EventHandler<ConfirmEventArgs>? ConfirmOverwriteRequested;
    public event EventHandler<OpenFileEventArgs>? OpenFilePromptRequested;
    public event EventHandler<SaveFileEventArgs>? SaveFilePromptRequested;
    public event EventHandler<string>? MessageRequested;

    /// <summary>Args: gridN, range, path.</summary>
    public event EventHandler<MeshExportEventArgs>? ExportMeshRequested;

    // ── Public helpers (host-callable) ─────────────────────────────────

    public void TriggerCompile() => CompileRequested?.Invoke(this, EventArgs.Empty);

    public void ShowError(string error)
    {
        StatusMessage = string.IsNullOrEmpty(error) ? "✓ Compiled" : error;
        StatusIsError = !string.IsNullOrEmpty(error);
    }

    public void LoadEquationByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var entry = UserBulbStore.Instance.GetByName(name);
        if (entry is null) return;

        _loadingNamedEquation = true;
        try
        {
            Source = entry.Source;
            _params.UserBulbSource = entry.Source;
            _params.UserBulbName = entry.Name;
        }
        finally { _loadingNamedEquation = false; }

        if (!string.Equals(_selectedSavedName, entry.Name, StringComparison.Ordinal))
        {
            _selectedSavedName = entry.Name;
            this.RaisePropertyChanged(nameof(SelectedSavedName));
        }
        SyncPromote();
        _debounce.Disposable = null;
        CompileRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshSavedList(string? selectName = null)
    {
        SavedNames.Clear();
        foreach (var e in UserBulbStore.Instance.Equations)
            SavedNames.Add(e.Name);

        if (!string.IsNullOrEmpty(selectName) && SavedNames.Contains(selectName!))
        {
            _selectedSavedName = selectName;
            this.RaisePropertyChanged(nameof(SelectedSavedName));
        }
        SyncPromote();
    }

    // ── Internals ──────────────────────────────────────────────────────

    private void SyncPromote()
    {
        if (string.IsNullOrEmpty(_selectedSavedName))
        {
            PromoteEnabled = false;
            _promote = false;
            this.RaisePropertyChanged(nameof(Promote));
            return;
        }
        var entry = UserBulbStore.Instance.GetByName(_selectedSavedName!);
        PromoteEnabled = entry is not null;
        _promote = entry?.Promoted ?? false;
        this.RaisePropertyChanged(nameof(Promote));
    }

    private void SetCam(ref double field, double value, Action apply)
    {
        if (this.RaiseAndSetIfChangedReturnsChanged(ref field, value) && !_suppressRender)
        {
            apply();
            RenderRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetRender<T>(ref T field, T value, Action apply)
    {
        if (this.RaiseAndSetIfChangedReturnsChanged(ref field, value) && !_suppressRender)
        {
            apply();
            RenderRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSave()
    {
        var args = new NamePromptEventArgs("Save bulb equation as:", _selectedSavedName ?? string.Empty);
        NamePromptRequested?.Invoke(this, args);
        if (string.IsNullOrWhiteSpace(args.Result)) return;

        string trimmed = args.Result!.Trim();
        if (UserBulbStore.Instance.GetByName(trimmed) is not null)
        {
            var confirm = new ConfirmEventArgs($"A saved bulb equation named '{trimmed}' already exists. Overwrite?");
            ConfirmOverwriteRequested?.Invoke(this, confirm);
            if (!confirm.Result) return;
        }

        var entry = UserBulbStore.Instance.SaveEquation(trimmed, _source);
        if (entry is null) return;

        _params.UserBulbName = entry.Name;
        RefreshSavedList(entry.Name);
    }

    private void OnDelete()
    {
        if (string.IsNullOrEmpty(_selectedSavedName)) return;
        var args = new ConfirmEventArgs($"Delete saved bulb equation '{_selectedSavedName}'?");
        ConfirmDeleteRequested?.Invoke(this, args);
        if (!args.Result) return;
        UserBulbStore.Instance.Remove(_selectedSavedName!);
        _selectedSavedName = null;
        this.RaisePropertyChanged(nameof(SelectedSavedName));
        RefreshSavedList();
    }

    private void OnImport()
    {
        var args = new OpenFileEventArgs("Import .fbulb", "FracturingFog bulb|*.fbulb;*.json|All files|*.*");
        OpenFilePromptRequested?.Invoke(this, args);
        if (string.IsNullOrEmpty(args.Path)) return;
        var entry = UserBulbStore.Instance.ImportEntry(args.Path!);
        if (entry is null)
        {
            MessageRequested?.Invoke(this, "Import failed (invalid file).");
            return;
        }
        RefreshSavedList(entry.Name);
    }

    private void OnExport()
    {
        if (string.IsNullOrEmpty(_selectedSavedName))
        {
            MessageRequested?.Invoke(this, "Select a saved equation to export.");
            return;
        }
        var args = new SaveFileEventArgs("Export .fbulb", "FracturingFog bulb|*.fbulb", $"{_selectedSavedName}.fbulb");
        SaveFilePromptRequested?.Invoke(this, args);
        if (string.IsNullOrEmpty(args.Path)) return;
        if (!UserBulbStore.Instance.ExportEntry(_selectedSavedName!, args.Path!))
            MessageRequested?.Invoke(this, "Export failed.");
    }

    private void OnResetCamera()
    {
        _suppressRender = true;
        try
        {
            CamDistance   = 3.0;
            CamThetaDeg   = RadToDeg(Math.PI * 0.25);
            CamPhiDeg     = RadToDeg(Math.PI * 0.35);
            LightThetaDeg = RadToDeg(Math.PI * 0.25);
            LightPhiDeg   = RadToDeg(Math.PI * 0.45);
            _params.UserBulbCameraDistance = _camDistance;
            _params.UserBulbCameraTheta = DegToRad(_camThetaDeg);
            _params.UserBulbCameraPhi = DegToRad(_camPhiDeg);
            _params.UserBulbLightTheta = DegToRad(_lightThetaDeg);
            _params.UserBulbLightPhi = DegToRad(_lightPhiDeg);
        }
        finally { _suppressRender = false; }
        RenderRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddParam()
    {
        var p = new UserBulbParam { Name = NextFreeName(), Value = 0, Min = -2, Max = 2 };
        _params.UserBulbParams.Add(p);
        Params.Add(p);
        CompileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoveParam(UserBulbParam p)
    {
        _params.UserBulbParams.Remove(p);
        Params.Remove(p);
        CompileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddChain()
    {
        var s = new UserBulbChainStep
        {
            OutputName = $"s{_params.UserBulbChain.Count}",
            Source = "return z * z + c;"
        };
        _params.UserBulbChain.Add(s);
        Chain.Add(s);
        CompileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoveChain(UserBulbChainStep s)
    {
        _params.UserBulbChain.Remove(s);
        Chain.Remove(s);
        CompileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTogglePlay() => IsPlaying = !_isPlaying;

    private void OnExportMesh()
    {
        var pathArgs = new SaveFileEventArgs("Export bulb mesh", "OBJ mesh|*.obj", "bulb.obj");
        SaveFilePromptRequested?.Invoke(this, pathArgs);
        if (string.IsNullOrEmpty(pathArgs.Path)) return;

        // Host shows the N+range modal; defaults match the legacy dialog.
        var meshArgs = new MeshExportEventArgs(64, 2.0, pathArgs.Path!);
        ExportMeshRequested?.Invoke(this, meshArgs);
    }

    private string NextFreeName()
    {
        var used = new System.Collections.Generic.HashSet<string>();
        foreach (var p in _params.UserBulbParams) used.Add(p.Name);
        for (char c = 'a'; c <= 'z'; c++)
            if (!used.Contains(c.ToString())) return c.ToString();
        for (int i = 0; i < 1000; i++)
            if (!used.Contains($"p{i}")) return $"p{i}";
        return "p";
    }

    private static string HintFor(UserBulbAxisModeKind mode) => mode switch
    {
        UserBulbAxisModeKind.Quat => "Quat Step(Quat z, Quat c, int n) → Quat.   z.W/.X/.Y/.Z available.  Math.* + Quat.* in scope.",
        _                          => "Vec3 Step(Vec3 z, Vec3 c, int n) → Vec3.   z.X/.Y/.Z available.  Math.* + Vec3.* in scope.",
    };

    private const string DefaultSource =
        "// Square-triplex Mandelbulb-lite: a 3D Mandelbrot analogue using\n" +
        "// per-component products. Replace freely.\n" +
        "return new Vec3(\n" +
        "    z.X*z.X - z.Y*z.Y - z.Z*z.Z,\n" +
        "    2*z.X*z.Y,\n" +
        "    2*z.X*z.Z) + c;";

    private static double RadToDeg(double r) => r * 180.0 / Math.PI;
    private static double DegToRad(double d) => d * Math.PI / 180.0;
}

// ── Host-callback arg types ────────────────────────────────────────────

public sealed class NamePromptEventArgs : EventArgs
{
    public NamePromptEventArgs(string caption, string defaultValue) { Caption = caption; DefaultValue = defaultValue; }
    public string Caption { get; }
    public string DefaultValue { get; }
    public string? Result { get; set; }
}

public sealed class ConfirmEventArgs : EventArgs
{
    public ConfirmEventArgs(string message) { Message = message; }
    public string Message { get; }
    public bool Result { get; set; }
}

public sealed class OpenFileEventArgs : EventArgs
{
    public OpenFileEventArgs(string title, string filter) { Title = title; Filter = filter; }
    public string Title { get; }
    public string Filter { get; }
    public string? Path { get; set; }
}

public sealed class SaveFileEventArgs : EventArgs
{
    public SaveFileEventArgs(string title, string filter, string defaultName) { Title = title; Filter = filter; DefaultName = defaultName; }
    public string Title { get; }
    public string Filter { get; }
    public string DefaultName { get; }
    public string? Path { get; set; }
}

public sealed class MeshExportEventArgs : EventArgs
{
    public MeshExportEventArgs(int gridN, double range, string path) { GridN = gridN; Range = range; Path = path; }
    public int GridN { get; }
    public double Range { get; }
    public string Path { get; }
}

internal static class ReactiveObjectExtensions
{
    /// <summary>
    /// Same as <c>RaiseAndSetIfChanged</c> but returns whether the value
    /// actually changed — saves the noisy second EqualityComparer call when
    /// a setter wants to chain "raise + side-effect".
    /// </summary>
    public static bool RaiseAndSetIfChangedReturnsChanged<TObj, TRet>(
        this TObj source,
        ref TRet field,
        TRet value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        where TObj : ReactiveObject
    {
        if (System.Collections.Generic.EqualityComparer<TRet>.Default.Equals(field, value))
            return false;
        source.RaiseAndSetIfChanged(ref field, value, propertyName);
        return true;
    }
}
