// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
        _deBody = parameters.UserBulbDeBody ?? string.Empty;

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
        _kifsScale    = _params.UserBulbKifsScale;
        _deModeIndex  = (int)_params.UserBulbDEMode;
        _neDEMultiplier = _params.UserBulbNonEscDEMultiplier;
        _neStabilityAxis = _params.UserBulbNonEscStabilityAxis;
        _neStabilityLimit = _params.UserBulbNonEscStabilityLimit;
        _backendIndex = (int)_params.UserBulbBackend;
        // #27 Phase 3 — the raw-C# Roslyn compiler is gone; the Sandbox DSL is
        // the only path. Pin the persisted selector to Sandbox so exports and
        // the (now-retired) UI stay honest, regardless of any legacy value.
        _params.UserBulbCompiler = UserBulbCompilerKind.Sandbox;
        _compilerIndex = (int)UserBulbCompilerKind.Sandbox;
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

        SaveCommand = ReactiveCommand.CreateFromTask(OnSaveAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(OnDeleteAsync,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        ImportCommand = ReactiveCommand.CreateFromTask(OnImportAsync);
        ExportCommand = ReactiveCommand.CreateFromTask(OnExportAsync,
            this.WhenAnyValue(x => x.SelectedSavedName).Select(n => !string.IsNullOrEmpty(n)));
        ResetCameraCommand = ReactiveCommand.Create(OnResetCamera);
        AddParamCommand = ReactiveCommand.Create(OnAddParam);
        AddChainCommand = ReactiveCommand.Create(OnAddChain);
        InsertPrimitiveCommand = ReactiveCommand.Create<UserBulbChainPrimitive>(OnInsertPrimitive);
        LoadHybridCommand = ReactiveCommand.Create<string>(OnLoadHybrid);
        RemoveParamCommand = ReactiveCommand.Create<UserBulbParam>(OnRemoveParam);
        RemoveChainCommand = ReactiveCommand.Create<UserBulbChainStep>(OnRemoveChain);
        TogglePlayCommand = ReactiveCommand.Create(OnTogglePlay);
        ExportMeshCommand = ReactiveCommand.CreateFromTask(OnExportMeshAsync,
            this.WhenAnyValue(x => x.IsExporting, busy => !busy));
        AutoRangeCommand = ReactiveCommand.Create(OnAutoRange,
            this.WhenAnyValue(x => x.IsExporting, busy => !busy));
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

    private string _deBody;
    /// <summary>#281 — optional NonEscaping dr body (Sandbox DSL). Empty → the
    /// runner uses the numerical tangent. Recompiles on edit (debounced with the
    /// step); a parse error surfaces via the status line without breaking a
    /// valid step compile.</summary>
    public string DeBody
    {
        get => _deBody;
        set
        {
            this.RaiseAndSetIfChanged(ref _deBody, value);
            _params.UserBulbDeBody = string.IsNullOrWhiteSpace(_deBody) ? null : _deBody;
            if (_loadingNamedEquation) return;
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
                // #27 Phase 0 — editor content is interactive/trusted; clear any
                // ExternalFile stamp left by a previously-viewed imported region.
                _params.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive;
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
    private double _kifsScale;
    /// <summary>Per-iteration linear scale for the scalar KIFS/Mandelbox DE.
    /// 0 = off (use the DE Mode). Set to the map's scale (e.g. 3 for the
    /// Kaleidoscopic-IFS preset) to render fold+rotation IFS correctly.</summary>
    public double KifsScale { get => _kifsScale; set => SetRender(ref _kifsScale, Math.Clamp(value, 0.0, 20.0), () => _params.UserBulbKifsScale = _kifsScale); }

    private int _deModeIndex;
    public int DEModeIndex { get => _deModeIndex; set => SetRender(ref _deModeIndex, Math.Clamp(value, 0, 3), () => { _params.UserBulbDEMode = (UserBulbDEModeKind)_deModeIndex; this.RaisePropertyChanged(nameof(NonEscapingEnabled)); }); }

    /// <summary>True when DE Mode = NonEscaping (#280). Gates visibility of the
    /// NonEscaping-only controls (DEMultiplier / stability clamp) in the view.</summary>
    public bool NonEscapingEnabled => _deModeIndex == (int)UserBulbDEModeKind.NonEscaping;

    private double _neDEMultiplier;
    /// <summary>#280 — global multiplier on the NonEscaping DE (forum
    /// "DEMultiplier" / "FudgeFactor"). &lt;1 pulls the surface in on pointy
    /// features to suppress overstepping.</summary>
    public double NonEscDEMultiplier { get => _neDEMultiplier; set => SetRender(ref _neDEMultiplier, Math.Clamp(value, 0.01, 4.0), () => _params.UserBulbNonEscDEMultiplier = _neDEMultiplier); }

    private int _neStabilityAxis;
    /// <summary>#280 — component (0=x,1=y,2=z) for the NonEscaping stability
    /// clamp (numeric-overflow guard, not an escape test).</summary>
    public int NonEscStabilityAxis { get => _neStabilityAxis; set => SetRender(ref _neStabilityAxis, Math.Clamp(value, 0, 2), () => _params.UserBulbNonEscStabilityAxis = _neStabilityAxis); }

    private double _neStabilityLimit;
    /// <summary>#280 — magnitude threshold for the NonEscaping stability clamp.</summary>
    public double NonEscStabilityLimit { get => _neStabilityLimit; set => SetRender(ref _neStabilityLimit, Math.Clamp(value, 1.0, 64.0), () => _params.UserBulbNonEscStabilityLimit = _neStabilityLimit); }

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

    private double _animLoopSeconds;
    /// <summary>
    /// Loop length in seconds (in raw t-units, before AnimSpeed scaling).
    /// 0 = no loop (t accumulates without wrap). Positive values cause
    /// AnimationTick to wrap t into [0, LoopSeconds) — useful for periodic
    /// animations driven from sin(t)/cos(t) DSL expressions.
    /// </summary>
    public double AnimLoopSeconds
    {
        get => _animLoopSeconds;
        set => this.RaiseAndSetIfChanged(ref _animLoopSeconds, Math.Clamp(value, 0.0, 600.0));
    }

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
        double next = _params.UserBulbTime + _animSpeed * dtSeconds;
        if (_animLoopSeconds > 0.0)
        {
            double L = _animLoopSeconds;
            next -= L * Math.Floor(next / L);
        }
        // Update the bound t field without letting its setter fire a second
        // (ungated) render — AnimationTick owns the single gated render below.
        _suppressRender = true;
        AnimTime = next;
        _suppressRender = false;
        // The AnimTime setter skips the _params write under _suppressRender, so
        // advance the render-facing time here — otherwise UserBulbTime stays at
        // its initial value, `next` recomputes from a fixed base every tick (t
        // jitters in a tiny range around one dt-step), and the render never sees
        // the advancing time, so nothing animates.
        _params.UserBulbTime = next;
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
    public ReactiveCommand<UserBulbChainPrimitive, Unit> InsertPrimitiveCommand { get; }
    public ReactiveCommand<string, Unit> LoadHybridCommand { get; }
    public ReactiveCommand<UserBulbParam, Unit> RemoveParamCommand { get; }
    public ReactiveCommand<UserBulbChainStep, Unit> RemoveChainCommand { get; }

    /// <summary>Catalog surfaced in the chain editor's "+ Primitive" menu.</summary>
    public IReadOnlyList<UserBulbChainPrimitive> ChainPrimitives => UserBulbChainPrimitives.All;
    public ReactiveCommand<Unit, Unit> TogglePlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportMeshCommand { get; }
    public ReactiveCommand<Unit, Unit> AutoRangeCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenHelpCommand { get; }

    /// <summary>Tuple: (docId, anchor, title). View opens HelpViewerView.
    /// Uses an <see cref="EventHandler{T}"/> to stay consistent with the
    /// rest of this VM's host-callback shape.</summary>
    public event EventHandler<(string DocId, string? Anchor, string Title)>? HelpRequested;

    // ── Events ─────────────────────────────────────────────────────────

    public event EventHandler? CompileRequested;
    public event EventHandler? RenderRequested;
    public event EventHandler? PromotionChanged;

    // #118 — async host prompts. The host handler fills the EventArgs
    // Result/Path and the awaited Task completes when the dialog closes, so the
    // OnXAsync raiser can read the result on the next line. Replaces the former
    // synchronous EventHandler pattern that required a WinForms modal loop.
    public event Func<NamePromptEventArgs, Task>? NamePromptRequested;
    public event Func<ConfirmEventArgs, Task>? ConfirmDeleteRequested;
    /// <summary>
    /// Fires when Save is about to replace an existing entry with the same
    /// name. Host shows a yes/no overwrite confirm and sets
    /// <see cref="ConfirmEventArgs.Result"/> true to proceed.
    /// </summary>
    public event Func<ConfirmEventArgs, Task>? ConfirmOverwriteRequested;
    public event Func<OpenFileEventArgs, Task>? OpenFilePromptRequested;
    public event Func<SaveFileEventArgs, Task>? SaveFilePromptRequested;
    public event EventHandler<string>? MessageRequested;

    /// <summary>Args: gridN, range, path.</summary>
    public event EventHandler<MeshExportEventArgs>? ExportMeshRequested;

    /// <summary>Fires when the user clicks Auto-range. The host probes the DE
    /// bounding extent (using the export-quality Iterations + JacobianH) and
    /// writes it back into <see cref="AutoRangeEventArgs.Result"/>.</summary>
    public event EventHandler<AutoRangeEventArgs>? AutoRangeRequested;

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
            _params.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive; // user's own library

            // Mirror the saved chain into both the params and the bound
            // ObservableCollection so the chain editor reflects what just
            // loaded. Empty/null chain clears any prior chain.
            _params.UserBulbChain.Clear();
            Chain.Clear();
            if (entry.Chain != null)
            {
                foreach (var s in entry.Chain)
                {
                    var clone = s.Clone();
                    _params.UserBulbChain.Add(clone);
                    Chain.Add(clone);
                }
            }

            // Restore the equation's own saved settings (axis/Julia/camera/
            // render budget/params/Time). Legacy entries have no Settings —
            // leave the current knobs untouched (old load behaviour).
            if (entry.Settings != null)
            {
                ApplySnapshotToParams(entry.Settings);
                // #281 — force the DE body from the entry (may be null to clear a
                // stale body from the previously-loaded bulb; nulls are elided in
                // the snapshot so ApplySnapshotToParams can't distinguish absent
                // from "explicitly none").
                _params.UserBulbDeBody = entry.Settings.DeBody;
            }
        }
        finally { _loadingNamedEquation = false; }

        // Push restored settings into the bound VM fields so the editor controls
        // (MaxSteps, camera, Julia, …) reflect what just loaded.
        if (entry.Settings != null)
            SyncMirrorFromParams();

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

    private async Task OnSaveAsync()
    {
        var args = new NamePromptEventArgs("Save bulb equation as:", _selectedSavedName ?? string.Empty);
        if (NamePromptRequested is { } prompt) await prompt(args);
        if (string.IsNullOrWhiteSpace(args.Result)) return;

        string trimmed = args.Result!.Trim();
        if (UserBulbStore.Instance.GetByName(trimmed) is not null)
        {
            var confirm = new ConfirmEventArgs($"A saved bulb equation named '{trimmed}' already exists. Overwrite?");
            if (ConfirmOverwriteRequested is { } ov) await ov(confirm);
            if (!confirm.Result) return;
        }

        // Capture the current render/view/animation settings alongside the
        // source + chain so loading this equation later restores its own knobs.
        // Pass an empty Entry into the snapshot — the entry that owns it is the
        // one being saved; nesting itself would self-reference.
        var settings = BuildSnapshotFromParams(new UserBulbEntry());
        var entry = UserBulbStore.Instance.SaveEquation(trimmed, _source, _params.UserBulbChain, settings);
        if (entry is null) return;

        _params.UserBulbName = entry.Name;
        RefreshSavedList(entry.Name);
    }

    private async Task OnDeleteAsync()
    {
        if (string.IsNullOrEmpty(_selectedSavedName)) return;
        var args = new ConfirmEventArgs($"Delete saved bulb equation '{_selectedSavedName}'?");
        if (ConfirmDeleteRequested is { } confirm) await confirm(args);
        if (!args.Result) return;
        UserBulbStore.Instance.Remove(_selectedSavedName!);
        _selectedSavedName = null;
        this.RaisePropertyChanged(nameof(SelectedSavedName));
        RefreshSavedList();
    }

    private async Task OnImportAsync()
    {
        var args = new OpenFileEventArgs("Import .fbulb", "FracturingFog bulb|*.fbulb;*.json|All files|*.*");
        if (OpenFilePromptRequested is { } pick) await pick(args);
        if (string.IsNullOrEmpty(args.Path)) return;
        var snapshots = UserBulbStore.Instance.ImportSnapshots(args.Path!);
        if (snapshots.Count == 0)
        {
            MessageRequested?.Invoke(this, "Import failed (invalid file).");
            return;
        }

        // A multi-entry file lands every equation in the store; the last one
        // wins the editor so the user sees something rendered rather than an
        // empty selection.
        var snapshot = snapshots[snapshots.Count - 1];

        // Apply snapshot knobs to _params before loading the entry — the
        // load path triggers a recompile and we want the imported axis /
        // Julia / camera state in effect on first render.
        ApplySnapshotToParams(snapshot);
        SyncMirrorFromParams();

        RefreshSavedList(snapshot.Entry!.Name);
        LoadEquationByName(snapshot.Entry.Name);

        if (snapshots.Count > 1)
            MessageRequested?.Invoke(this, $"{snapshots.Count} equations imported.");
    }

    private async Task OnExportAsync()
    {
        if (string.IsNullOrEmpty(_selectedSavedName))
        {
            MessageRequested?.Invoke(this, "Select a saved equation to export.");
            return;
        }
        var entry = UserBulbStore.Instance.GetByName(_selectedSavedName!);
        if (entry is null)
        {
            MessageRequested?.Invoke(this, "Export failed.");
            return;
        }
        var args = new SaveFileEventArgs("Export .fbulb", "FracturingFog bulb|*.fbulb", $"{_selectedSavedName}.fbulb");
        if (SaveFilePromptRequested is { } pick) await pick(args);
        if (string.IsNullOrEmpty(args.Path)) return;
        var snapshot = BuildSnapshotFromParams(entry);
        if (!UserBulbStore.Instance.ExportSnapshot(snapshot, args.Path!))
            MessageRequested?.Invoke(this, "Export failed.");
    }

    private UserBulbSnapshot BuildSnapshotFromParams(UserBulbEntry entry) => new()
    {
        Version          = UserBulbSnapshot.CurrentVersion,
        Entry            = entry,
        AxisMode         = _params.UserBulbAxisMode,
        Compiler         = _params.UserBulbCompiler,
        DEMode           = _params.UserBulbDEMode,
        Backend          = _params.UserBulbBackend,
        QuatSliceW       = _params.UserBulbQuatSliceW,
        JuliaMode        = _params.UserBulbJuliaMode,
        JuliaCX          = _params.UserBulbJuliaCX,
        JuliaCY          = _params.UserBulbJuliaCY,
        JuliaCZ          = _params.UserBulbJuliaCZ,
        JuliaCW          = _params.UserBulbJuliaCW,
        CameraDistance   = _params.UserBulbCameraDistance,
        CameraTheta      = _params.UserBulbCameraTheta,
        CameraPhi        = _params.UserBulbCameraPhi,
        LightTheta       = _params.UserBulbLightTheta,
        LightPhi         = _params.UserBulbLightPhi,
        Light1Intensity  = _params.UserBulbLight1Intensity,
        Light2Intensity  = _params.UserBulbLight2Intensity,
        Light3Intensity  = _params.UserBulbLight3Intensity,
        AOSamples        = _params.UserBulbAOSamples,
        FogDensity       = _params.UserBulbFogDensity,
        ColorDriver      = _params.UserBulbColorDriver,
        OrbitTrapX       = _params.UserBulbOrbitTrapX,
        OrbitTrapY       = _params.UserBulbOrbitTrapY,
        OrbitTrapZ       = _params.UserBulbOrbitTrapZ,
        IterComponentAxis = _params.UserBulbIterComponentAxis,
        Iterations       = _params.UserBulbIterations,
        MaxSteps         = _params.UserBulbMaxSteps,
        Epsilon          = _params.UserBulbEpsilon,
        Bailout          = _params.UserBulbBailout,
        JacobianH        = _params.UserBulbJacobianH,
        CullRadius       = _params.UserBulbCullRadius,
        KifsScale        = _params.UserBulbKifsScale,
        NonEscDEMultiplier   = _params.UserBulbNonEscDEMultiplier,
        NonEscStabilityAxis  = _params.UserBulbNonEscStabilityAxis,
        NonEscStabilityLimit = _params.UserBulbNonEscStabilityLimit,
        DeBody               = _params.UserBulbDeBody,
        FovDegrees       = _params.UserBulbFovDegrees,
        ClipPlaneEnabled = _params.UserBulbClipPlaneEnabled,
        SuperSample      = _params.UserBulbSuperSample,
        Time             = _params.UserBulbTime,
        AnimSpeed        = _animSpeed,
        AnimLoopSeconds  = _animLoopSeconds,
        ExportGridN      = _exportGridN,
        ExportRange      = _exportRange,
        ExportIsoScale   = _exportIsoScale,
        ExportIsoAbsolute = _exportIsoAbsolute,
        ExportSuperSamples = _exportSuperSamples,
        ExportCreaseDegrees = _exportCreaseDegrees,
        Params           = _params.UserBulbParams.ConvertAll(p => p.Clone()),
    };

    private void ApplySnapshotToParams(UserBulbSnapshot s)
    {
        if (s.AxisMode is { } axisMode)          _params.UserBulbAxisMode = axisMode;
        // #27 Phase 3 — ignore any persisted Roslyn selector; the Sandbox DSL
        // is the only compiler now.
        _params.UserBulbCompiler = UserBulbCompilerKind.Sandbox;
        if (s.DEMode is { } deMode)              _params.UserBulbDEMode = deMode;
        if (s.Backend is { } backend)            _params.UserBulbBackend = backend;
        if (s.QuatSliceW is { } qsw)             _params.UserBulbQuatSliceW = qsw;
        if (s.JuliaMode is { } jm)               _params.UserBulbJuliaMode = jm;
        if (s.JuliaCX is { } jcx)                _params.UserBulbJuliaCX = jcx;
        if (s.JuliaCY is { } jcy)                _params.UserBulbJuliaCY = jcy;
        if (s.JuliaCZ is { } jcz)                _params.UserBulbJuliaCZ = jcz;
        if (s.JuliaCW is { } jcw)                _params.UserBulbJuliaCW = jcw;
        if (s.CameraDistance is { } cd)          _params.UserBulbCameraDistance = cd;
        if (s.CameraTheta is { } ct)             _params.UserBulbCameraTheta = ct;
        if (s.CameraPhi is { } cp)               _params.UserBulbCameraPhi = cp;
        if (s.LightTheta is { } lt)              _params.UserBulbLightTheta = lt;
        if (s.LightPhi is { } lp)                _params.UserBulbLightPhi = lp;
        if (s.Light1Intensity is { } l1)         _params.UserBulbLight1Intensity = l1;
        if (s.Light2Intensity is { } l2)         _params.UserBulbLight2Intensity = l2;
        if (s.Light3Intensity is { } l3)         _params.UserBulbLight3Intensity = l3;
        if (s.AOSamples is { } ao)               _params.UserBulbAOSamples = ao;
        if (s.FogDensity is { } fd)              _params.UserBulbFogDensity = fd;
        if (s.ColorDriver is { } cdrv)           _params.UserBulbColorDriver = cdrv;
        if (s.OrbitTrapX is { } tx)              _params.UserBulbOrbitTrapX = tx;
        if (s.OrbitTrapY is { } ty)              _params.UserBulbOrbitTrapY = ty;
        if (s.OrbitTrapZ is { } tz)              _params.UserBulbOrbitTrapZ = tz;
        if (s.IterComponentAxis is { } ica)      _params.UserBulbIterComponentAxis = ica;
        if (s.Iterations is { } iters)           _params.UserBulbIterations = iters;
        if (s.MaxSteps is { } ms)                _params.UserBulbMaxSteps = ms;
        if (s.Epsilon is { } eps)                _params.UserBulbEpsilon = eps;
        if (s.Bailout is { } bo)                 _params.UserBulbBailout = bo;
        if (s.JacobianH is { } jh)               _params.UserBulbJacobianH = jh;
        if (s.CullRadius is { } cr)              _params.UserBulbCullRadius = cr;
        if (s.KifsScale is { } kifs)             _params.UserBulbKifsScale = kifs;
        if (s.NonEscDEMultiplier is { } nem)     _params.UserBulbNonEscDEMultiplier = nem;
        if (s.NonEscStabilityAxis is { } nea)    _params.UserBulbNonEscStabilityAxis = nea;
        if (s.NonEscStabilityLimit is { } nel)   _params.UserBulbNonEscStabilityLimit = nel;
        // DeBody intentionally NOT guarded by the snapshot here — a bulb with no
        // body writes no field (nulls elided), so switching bulbs must be able to
        // CLEAR a stale body. LoadEquationByName force-sets it from the entry.
        if (s.FovDegrees is { } fov)             _params.UserBulbFovDegrees = fov;
        if (s.ClipPlaneEnabled is { } cpe)       _params.UserBulbClipPlaneEnabled = cpe;
        if (s.SuperSample is { } ss)             _params.UserBulbSuperSample = ss;
        if (s.Time is { } t)                     _params.UserBulbTime = t;
        // AnimSpeed / AnimLoopSeconds are VM-only (not in FractalParameters).
        // Set through the public setters so they clamp and raise change
        // notifications for the bound Speed / Loop-s controls.
        if (s.AnimSpeed is { } aspd)             AnimSpeed = aspd;
        if (s.AnimLoopSeconds is { } aloop)      AnimLoopSeconds = aloop;
        // Export knobs are VM-only (no FractalParameters mirror) — set via the
        // public setters so they clamp + notify the bound controls.
        if (s.ExportGridN is { } egn)            ExportGridN = egn;
        if (s.ExportRange is { } erg)            ExportRange = erg;
        if (s.ExportIsoScale is { } eis)         ExportIsoScale = eis;
        if (s.ExportIsoAbsolute is { } eia)      ExportIsoAbsolute = eia;
        if (s.ExportSuperSamples is { } ess)     ExportSuperSamples = ess;
        if (s.ExportCreaseDegrees is { } ecd)    ExportCreaseDegrees = ecd;

        if (s.Params is { Count: > 0 } srcParams)
        {
            _params.UserBulbParams.Clear();
            foreach (var p in srcParams) _params.UserBulbParams.Add(p.Clone());
        }
    }

    /// <summary>
    /// Re-pulls every mirrored property from <see cref="_params"/> and raises
    /// PropertyChanged on each. Used after an Import lands new state — the
    /// individual property setters are bypassed (we wrote straight into
    /// _params), so the view needs an explicit kick. <c>_suppressRender</c>
    /// stays true throughout so no render fires per property — the caller
    /// triggers a single compile/render after the bulk update.
    /// </summary>
    private void SyncMirrorFromParams()
    {
        _suppressRender = true;
        try
        {
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
            _kifsScale    = _params.UserBulbKifsScale;
            _neDEMultiplier = _params.UserBulbNonEscDEMultiplier;
            _neStabilityAxis = _params.UserBulbNonEscStabilityAxis;
            _neStabilityLimit = _params.UserBulbNonEscStabilityLimit;
            _deBody       = _params.UserBulbDeBody ?? string.Empty;
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

            Params.Clear();
            foreach (var p in _params.UserBulbParams) Params.Add(p);
        }
        finally { _suppressRender = false; }

        this.RaisePropertyChanged(nameof(CamDistance));
        this.RaisePropertyChanged(nameof(CamThetaDeg));
        this.RaisePropertyChanged(nameof(CamPhiDeg));
        this.RaisePropertyChanged(nameof(LightThetaDeg));
        this.RaisePropertyChanged(nameof(LightPhiDeg));
        this.RaisePropertyChanged(nameof(Iterations));
        this.RaisePropertyChanged(nameof(MaxSteps));
        this.RaisePropertyChanged(nameof(Epsilon));
        this.RaisePropertyChanged(nameof(Bailout));
        this.RaisePropertyChanged(nameof(JacobianH));
        this.RaisePropertyChanged(nameof(CullRadius));
        this.RaisePropertyChanged(nameof(KifsScale));
        this.RaisePropertyChanged(nameof(NonEscDEMultiplier));
        this.RaisePropertyChanged(nameof(NonEscStabilityAxis));
        this.RaisePropertyChanged(nameof(NonEscStabilityLimit));
        this.RaisePropertyChanged(nameof(NonEscapingEnabled));
        this.RaisePropertyChanged(nameof(DeBody));
        this.RaisePropertyChanged(nameof(DEModeIndex));
        this.RaisePropertyChanged(nameof(BackendIndex));
        this.RaisePropertyChanged(nameof(CompilerIndex));
        this.RaisePropertyChanged(nameof(IsSandbox));
        this.RaisePropertyChanged(nameof(AxisModeIndex));
        this.RaisePropertyChanged(nameof(QuatEnabled));
        this.RaisePropertyChanged(nameof(HintText));
        this.RaisePropertyChanged(nameof(QuatSliceW));
        this.RaisePropertyChanged(nameof(JuliaMode));
        this.RaisePropertyChanged(nameof(JuliaCX));
        this.RaisePropertyChanged(nameof(JuliaCY));
        this.RaisePropertyChanged(nameof(JuliaCZ));
        this.RaisePropertyChanged(nameof(JuliaCW));
        this.RaisePropertyChanged(nameof(ColorDriverIndex));
        this.RaisePropertyChanged(nameof(TrapX));
        this.RaisePropertyChanged(nameof(TrapY));
        this.RaisePropertyChanged(nameof(TrapZ));
        this.RaisePropertyChanged(nameof(IterAxis));
        this.RaisePropertyChanged(nameof(Light1));
        this.RaisePropertyChanged(nameof(Light2));
        this.RaisePropertyChanged(nameof(Light3));
        this.RaisePropertyChanged(nameof(AOSamples));
        this.RaisePropertyChanged(nameof(FogDensity));
        this.RaisePropertyChanged(nameof(FovDegrees));
        this.RaisePropertyChanged(nameof(ClipPlane));
        this.RaisePropertyChanged(nameof(SSIndex));
        this.RaisePropertyChanged(nameof(AnimTime));
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

    private void OnInsertPrimitive(UserBulbChainPrimitive p)
    {
        if (p is null) return;
        var step = p.ToStep();
        // The chain runner threads original pixel z into every step — to
        // compose with whatever the prior step produced, rebind `z` in the
        // primitive body to the prior step's output name.
        if (_params.UserBulbChain.Count > 0)
        {
            string priorName = _params.UserBulbChain[^1].OutputName;
            if (!string.IsNullOrWhiteSpace(priorName))
                step.Source = UserBulbChainPrimitives.RebindZ(step.Source, priorName);
        }
        // Output names must be unique across the chain — uniquify with a
        // numeric suffix when the default collides.
        step.OutputName = UniqueChainName(step.OutputName);
        _params.UserBulbChain.Add(step);
        Chain.Add(step);

        // #113 — a KIFS fold needs the scalar-KIFS DE (its folds are
        // discontinuous; the numerical Jacobian yields a blank / blobby /
        // zero-triangle export). Auto-engage it on the first fold primitive if
        // the user hasn't already dialed a scale in.
        if (p.KifsScale > 0.0 && KifsScale <= 0.0)
        {
            KifsScale = p.KifsScale;
            StatusMessage = $"KIFS Scale set to {p.KifsScale:0.###} — required so the fold DE renders/exports.";
            StatusIsError = false;
        }

        CompileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLoadHybrid(string hybridId)
    {
        List<UserBulbChainStep>? built = hybridId switch
        {
            "mbox+bulb"   => UserBulbChainPrimitives.MandelboxBulbHybrid(),
            "menger+bulb" => UserBulbChainPrimitives.MengerBulbHybrid(),
            _ => null,
        };
        if (built is null) return;

        _params.UserBulbChain.Clear();
        Chain.Clear();
        foreach (var s in built)
        {
            _params.UserBulbChain.Add(s);
            Chain.Add(s);
        }

        // #113 — engage the scalar-KIFS DE for fold-led chains. The numerical
        // Jacobian can't estimate distance across the fold discontinuities
        // (blank / blobby / zero-triangle export); the fold's declared scale
        // drives the running-derivative DE instead.
        double sug = UserBulbChainPrimitives.SuggestedKifsScaleForChain(_params.UserBulbChain);
        if (sug > 0.0)
        {
            KifsScale = sug;
            StatusMessage = $"KIFS Scale set to {sug:0.###} for the fold DE (needed for fold export).";
            StatusIsError = false;
        }

        CompileRequested?.Invoke(this, EventArgs.Empty);
    }

    private string UniqueChainName(string baseName)
    {
        var used = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var s in _params.UserBulbChain) used.Add(s.OutputName);
        if (!used.Contains(baseName)) return baseName;
        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{baseName}{i}";
            if (!used.Contains(candidate)) return candidate;
        }
        return baseName;
    }

    private void OnTogglePlay() => IsPlaying = !_isPlaying;

    private async Task OnExportMeshAsync()
    {
        var pathArgs = new SaveFileEventArgs("Export bulb mesh", "OBJ (smooth)|*.obj|STL (binary)|*.stl", "bulb.obj");
        if (SaveFilePromptRequested is { } pick) await pick(pathArgs);
        if (string.IsNullOrEmpty(pathArgs.Path)) return;

        // #112 — export geometry knobs (grid + range) are export-only; DE
        // quality reuses the panel's existing Iterations + JacobianH (the single
        // source of truth — the render DE and the export DE are the same kernel).
        // For crisp export geometry raise Iterations (native quaternion types use
        // 11–14; render default 8 is blobby) and/or drop JacobianH toward 1e-5.
        var meshArgs = new MeshExportEventArgs(
            ExportGridN, ExportRange, pathArgs.Path!, Iterations, JacobianH,
            ExportIsoScale, ExportIsoAbsolute, ExportSuperSamples, ExportCreaseDegrees);
        // The host runs the marching cubes off-thread and calls NotifyExportDone
        // when finished; gate the buttons meanwhile. Only latch busy when a host
        // is actually listening, else the flag would never clear.
        if (ExportMeshRequested is { } handler)
        {
            IsExporting = true;
            handler(this, meshArgs);
        }
    }

    /// <summary>Host callback: the off-thread mesh export has finished (or
    /// failed) — clear the busy flag so the export buttons re-enable.</summary>
    public void NotifyExportDone() => IsExporting = false;

    private bool _isExporting;
    /// <summary>True while a mesh export runs on the host's background thread.
    /// Disables the export + auto-range buttons and drives the "Exporting…"
    /// status so the user waits instead of force-quitting a busy app.</summary>
    public bool IsExporting
    {
        get => _isExporting;
        private set => this.RaiseAndSetIfChanged(ref _isExporting, value);
    }

    private void OnAutoRange()
    {
        if (AutoRangeRequested is not { } handler)
            return;
        // Reuse the export-quality DE (same Iterations + JacobianH the mesh
        // export uses) so the probed extent matches what will be tessellated.
        var e = new AutoRangeEventArgs(Iterations, JacobianH);
        handler(this, e);
        if (e.Result > 0.0)
        {
            ExportRange = e.Result; // setter clamps + notifies the bound control
            MessageRequested?.Invoke(this, $"Auto-range set to {ExportRange:0.##}.");
        }
        else
        {
            MessageRequested?.Invoke(this,
                "Auto-range found no surface — check the equation compiles and renders.");
        }
    }

    // ── Mesh-export geometry knobs (#112) — export-only; no render equivalent.
    private int _exportGridN = 96;
    /// <summary>Marching-cubes grid resolution per axis. Higher = finer mesh,
    /// cost ~N³. 96 matches the native raymarcher exporter.</summary>
    public int ExportGridN
    {
        get => _exportGridN;
        set => this.RaiseAndSetIfChanged(ref _exportGridN, Math.Clamp(value, 16, 512));
    }

    private double _exportRange = 2.0;
    /// <summary>Object-space half-extent of the sampled cube about the fractal.
    /// Must enclose the set; too small clips, too large wastes resolution.</summary>
    public double ExportRange
    {
        get => _exportRange;
        set => this.RaiseAndSetIfChanged(ref _exportRange, Math.Clamp(value, 0.25, 64.0));
    }

    private double _exportIsoScale = 0.5;
    /// <summary>Marching-cubes iso level. When <see cref="ExportIsoAbsolute"/> is
    /// false this is a fraction of the cell size (iso = step·this); the default
    /// 0.5 sits a half-cell OUTSIDE the true DE≈0 shell, so at coarse grids thin
    /// filaments inflate into fat tubes and gaps fuse into a ball. When absolute,
    /// this is the iso level directly in object-space distance (grid-independent).
    /// Lower toward 0.1–0.25 (or a small absolute distance) to hug the surface and
    /// keep filament detail; raise to bridge gaps if the mesh shatters.</summary>
    public double ExportIsoScale
    {
        get => _exportIsoScale;
        set => this.RaiseAndSetIfChanged(ref _exportIsoScale, Math.Clamp(value, 0.005, 2.0));
    }

    private bool _exportIsoAbsolute;
    /// <summary>When true, <see cref="ExportIsoScale"/> is an absolute object-space
    /// distance (grid-independent surface level); when false it is a fraction of
    /// the cell size (surface level tracks the grid).</summary>
    public bool ExportIsoAbsolute
    {
        get => _exportIsoAbsolute;
        set => this.RaiseAndSetIfChanged(ref _exportIsoAbsolute, value);
    }

    private int _exportSuperSamples = 1;
    /// <summary>Box-average an s×s×s DE stencil per grid corner (1 = single
    /// sample). Antialiases sub-cell filaments into continuous surface instead of
    /// broken tubes. Cost is ~s³× the DE work, so keep it 2–3 on fine grids.</summary>
    public int ExportSuperSamples
    {
        get => _exportSuperSamples;
        set => this.RaiseAndSetIfChanged(ref _exportSuperSamples, Math.Clamp(value, 1, 4));
    }

    private double _exportCreaseDegrees = 180.0;
    /// <summary>Crease angle in degrees. Adjacent faces differing by more than
    /// this keep a hard edge (Mandelbox facets stay crisp) while curved bulb arms
    /// still smooth. 180 (default) smooths everything, like the prior exporter;
    /// ≈30 preserves facets.</summary>
    public double ExportCreaseDegrees
    {
        get => _exportCreaseDegrees;
        set => this.RaiseAndSetIfChanged(ref _exportCreaseDegrees, Math.Clamp(value, 5.0, 180.0));
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
    public MeshExportEventArgs(int gridN, double range, string path, int iterations, double jacobianH,
                               double isoScale, bool isoAbsolute, int superSamples, double creaseDegrees)
    { GridN = gridN; Range = range; Path = path; Iterations = iterations; JacobianH = jacobianH; IsoScale = isoScale; IsoAbsolute = isoAbsolute; SuperSamples = superSamples; CreaseDegrees = creaseDegrees; }
    public int GridN { get; }
    public double Range { get; }
    public string Path { get; }
    // #112 follow-up — marching-cubes iso level. Lower crispens (hugs the true
    // surface, keeps filaments); higher fuses gaps. IsoAbsolute switches IsoScale
    // from a cell-size fraction to an absolute object-space distance.
    public double IsoScale { get; }
    public bool IsoAbsolute { get; }
    // Box-average s×s×s DE stencil per grid corner (1 = single sample) to
    // antialias sub-cell filaments into continuous surface. Cost ~s³×.
    public int SuperSamples { get; }
    // Crease angle (deg): faces differing by more than this keep a hard edge
    // (facets stay sharp). 180 = smooth everything.
    public double CreaseDegrees { get; }
    // #112 — export-specific DE quality (independent of the render's live iter/
    // jacH) so mesh geometry can resolve detail the numerical DE otherwise
    // smooths away.
    public int Iterations { get; }
    public double JacobianH { get; }
}

public sealed class AutoRangeEventArgs : EventArgs
{
    public AutoRangeEventArgs(int iterations, double jacobianH)
    { Iterations = iterations; JacobianH = jacobianH; }
    // Export-quality DE knobs so the probe matches the export sampler.
    public int Iterations { get; }
    public double JacobianH { get; }
    /// <summary>Host writes the probed object-space half-extent here; 0 (the
    /// default) means no surface was found.</summary>
    public double Result { get; set; }
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
