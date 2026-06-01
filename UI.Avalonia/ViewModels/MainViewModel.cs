// ViewModels/MainViewModel.cs
//
// Step D of the Phase 2.3 MainForm cut plan. Thin facade over the three
// shared engines:
//
//   • FractalViewState         — POCO holding every render input
//   • IFractalInputController  — translates neutral pointer/key events
//                                into view-state mutations + render hints
//   • IFractalRenderHost       — owns the renderer + every calculator
//
// The VM:
//   • Wires ViewChanged from the input controller → Trigger / TriggerFast
//     on the render host (Full vs Fast hint).
//   • Mirrors the host's FrameCompleted into a status string the toolbar
//     can bind to.
//   • Exposes Brightness / Contrast / Adaptive (and their lock flags) as
//     observable properties, writes through to the view state, and calls
//     RepaintWithPostFx so the change is visible without a recalc.
//   • Exposes Reset / quality-pick / fractal-type-pick commands.
//   • Tracks the active region + theme name so the toolbar + floating
//     menu combos can share the same selection.
//
// Dialog ownership (color-theme editor, floating help, etc.) is NOT here.
// That belongs to ShellViewModel (step E).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using FracturingFog;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.ViewState;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IFractalRenderHost _renderHost;
    private readonly IFractalInputController _input;
    private readonly System.Threading.Timer _panStopDebounce;
    private const int PanStopDebounceMs = 300;
    private bool _renderHintFastInFlight;

    public MainViewModel(IFractalRenderHost renderHost, IFractalInputController input)
    {
        _renderHost = renderHost ?? throw new ArgumentNullException(nameof(renderHost));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        ViewState = renderHost.ViewState;

        QualityPresets = new ObservableCollection<QualityPreset>(QualityPreset.All);
        FractalTypes = new ObservableCollection<FractalType>(
            (FractalType[])Enum.GetValues(typeof(FractalType)));
        FractalEntries = new ObservableCollection<FractalTypeEntry>();
        RebuildFractalEntries();

        _selectedQuality = ViewState.Quality;
        _selectedFractalType = ViewState.FractalType;
        _selectedFractalEntry = FindEntryForType(_selectedFractalType);
        _brightness = ViewState.Brightness;
        _contrast = ViewState.Contrast;
        _adaptive = ViewState.HistogramEq;
        _iterLocked = ViewState.IterLocked;
        _lockedIterations = ViewState.LockedIterations;

        ResetViewCommand = ReactiveCommand.Create(ResetView);

        _input.ViewChanged += OnInputViewChanged;
        _input.StatusRequested += (_, msg) => StatusText = msg.Text;
        _input.CursorRequested += (_, req) => CursorRequest = req;
        _renderHost.FrameCompleted += OnFrameCompleted;
        _renderHost.StatusRequested += (_, txt) => StatusText = txt;
        _renderHost.ColorMapChanged += OnRenderHostColorMapChanged;
        _overlayContrastLuma = _renderHost.OverlayContrastLuma;

        _panStopDebounce = new System.Threading.Timer(_ =>
        {
            if (_renderHintFastInFlight)
            {
                _renderHintFastInFlight = false;
                _renderHost.Trigger();
            }
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public FractalViewState ViewState { get; }
    public IFractalInputController Input => _input;
    public IFractalRenderHost RenderHost => _renderHost;

    private byte _overlayContrastLuma = 255;
    /// <summary>Pre-sampled luminance of the active colour map's middle band,
    /// mirrored from the render host. The overlay control binds here so it
    /// can pick a contrast-aware ink colour (white on dark, near-black on
    /// light) without UI.Avalonia needing to see the main-project
    /// <c>IColorMap</c> type. Re-read after every
    /// <see cref="IFractalRenderHost.ColorMapChanged"/>.</summary>
    public byte OverlayContrastLuma
    {
        get => _overlayContrastLuma;
        private set => this.RaiseAndSetIfChanged(ref _overlayContrastLuma, value);
    }

    public ObservableCollection<QualityPreset> QualityPresets { get; }
    public ObservableCollection<FractalType> FractalTypes { get; }

    /// <summary>Toolbar Type combo entries: hard-coded FractalType values
    /// followed by a "— Registered —" divider + every promoted equation from
    /// <see cref="RegisteredFractalCatalog"/>. Drives the new combo binding
    /// so saved equations are selectable without opening a dialog.</summary>
    public ObservableCollection<FractalTypeEntry> FractalEntries { get; }

    private static readonly (FractalType Type, string Label)[] BuiltInFractalLabels =
    {
        (FractalType.Mandelbrot,       "Mandelbrot"),
        (FractalType.Julia,            "Julia"),
        (FractalType.BurningShip,      "Burning Ship"),
        (FractalType.Tricorn,          "Tricorn"),
        (FractalType.Multibrot,        "Multibrot"),
        (FractalType.Phoenix,          "Phoenix"),
        (FractalType.Newton,           "Newton"),
        (FractalType.BuddhaBrot,       "Buddhabrot"),
        (FractalType.IFS,              "IFS"),
        (FractalType.LSystem,          "L-System"),
        (FractalType.StrangeAttractor, "Strange Attractor"),
        (FractalType.UserEquation,     "User Equation"),
        (FractalType.Mandelbulb,       "Mandelbulb (3D)"),
        (FractalType.Sandbox,          "Sandbox"),
        (FractalType.UserBulb,         "User Bulb (3D)"),
        (FractalType.TearDrop,         "Tear Drop"),
        (FractalType.GeneratedMandelbrotZ2, "Mandelbrot Z² (Generated)"),
        (FractalType.GeneratedMandelbrotZ3, "Mandelbrot Z³ (Generated)"),
        (FractalType.GeneratedMandelbrotZ4, "Mandelbrot Z⁴ (Generated)"),
        (FractalType.GeneratedMandelbrotZ5, "Mandelbrot Z⁵ (Generated)"),
        (FractalType.GeneratedTricorn,      "Tricorn (Generated)"),
        (FractalType.GeneratedBurningShip,  "Burning Ship (Generated)"),
    };

    /// <summary>Rebuild <see cref="FractalEntries"/> from the built-in label
    /// table + the current <see cref="RegisteredFractalCatalog"/> snapshot.
    /// Call after a user equation is saved/promoted so the combo picks up
    /// the new entry.</summary>
    public void RebuildFractalEntries()
    {
        FractalEntries.Clear();
        foreach (var (t, label) in BuiltInFractalLabels)
            FractalEntries.Add(FractalTypeEntry.BuiltIn(t, label));

        var promoted = RegisteredFractalCatalog.Snapshot();
        if (promoted.Count > 0)
        {
            FractalEntries.Add(FractalTypeEntry.Divider());
            foreach (var r in promoted)
                FractalEntries.Add(FractalTypeEntry.FromPromoted(r));
        }
    }

    private FractalTypeEntry? FindEntryForType(FractalType type)
    {
        foreach (var e in FractalEntries)
            if (!e.IsDivider && e.Promoted == null && e.Type == type) return e;
        return null;
    }

    // ── Status + cursor ───────────────────────────────────────────────────

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>Public passthrough so the shell + host (video / slideshow
    /// engines) can push status text without exposing the private setter.</summary>
    public void SetStatus(string text) => StatusText = text;

    private InputCursorRequest _cursorRequest = new(InputCursor.Default);
    public InputCursorRequest CursorRequest
    {
        get => _cursorRequest;
        private set => this.RaiseAndSetIfChanged(ref _cursorRequest, value);
    }

    // ── Selection mirrors ─────────────────────────────────────────────────

    private string? _selectedRegion;
    public string? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _selectedRegion, value))
            {
                // Push to render host so the watermark sees the new label
                // on the next composited frame.
                _renderHost.RegionName = value;
                if (_showWatermark) _renderHost.RepaintWithPostFx();
            }
        }
    }

    private string? _selectedTheme;
    public string? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _selectedTheme, value))
            {
                _renderHost.ThemeName = value;
                if (_showWatermark) _renderHost.RepaintWithPostFx();
            }
        }
    }

    private QualityPreset _selectedQuality;
    public QualityPreset SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _selectedQuality, value) && value != null)
            {
                ViewState.Quality = value;
                _renderHost.Trigger();
            }
        }
    }

    private FractalType _selectedFractalType;
    public FractalType SelectedFractalType
    {
        get => _selectedFractalType;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _selectedFractalType, value))
            {
                ViewState.FractalType = value;
                // Snap to a fractal-appropriate default view. Without this,
                // switching from Mandelbrot to Burning Ship / Tricorn / etc.
                // would inherit the Mandelbrot centre+zoom and put the entire
                // image inside the new set → every pixel hits MAX_ITER and
                // the calc takes minutes, looking like a UI lockup.
                ViewState.SnapToFractalDefault(value);
                var entry = FindEntryForType(value);
                if (entry != null && !ReferenceEquals(_selectedFractalEntry, entry))
                {
                    _selectedFractalEntry = entry;
                    this.RaisePropertyChanged(nameof(SelectedFractalEntry));
                }
                _renderHost.Trigger();
            }
        }
    }

    /// <summary>Mirror the active fractal type into the toolbar combo without
    /// snapping the view or re-triggering. Used after a region jump set
    /// ViewState.FractalType directly — region jumps own their centre/zoom, so
    /// the SnapToFractalDefault in the normal setter must be bypassed.</summary>
    public void SetFractalTypeSilent(FractalType type)
    {
        if (_selectedFractalType != type)
        {
            _selectedFractalType = type;
            this.RaisePropertyChanged(nameof(SelectedFractalType));
        }
        var entry = FindEntryForType(type);
        if (entry != null && !ReferenceEquals(_selectedFractalEntry, entry))
        {
            _selectedFractalEntry = entry;
            this.RaisePropertyChanged(nameof(SelectedFractalEntry));
        }
    }

    private FractalTypeEntry? _selectedFractalEntry;
    /// <summary>Toolbar Type combo binding target. Distinguishes built-in
    /// FractalType picks from promoted RegisteredFractal picks: the latter
    /// also loads the equation source into FractalParameters and recompiles
    /// the appropriate engine before switching FractalType. Selecting the
    /// "— Registered —" divider is bounced back to the prior entry.</summary>
    public FractalTypeEntry? SelectedFractalEntry
    {
        get => _selectedFractalEntry;
        set
        {
            if (value == null) return;
            if (value.IsDivider)
            {
                // Revert combo to whatever the canonical entry is.
                var revert = FindEntryForType(_selectedFractalType);
                if (revert != null && !ReferenceEquals(_selectedFractalEntry, revert))
                {
                    _selectedFractalEntry = revert;
                    this.RaisePropertyChanged(nameof(SelectedFractalEntry));
                }
                return;
            }
            if (ReferenceEquals(_selectedFractalEntry, value)) return;
            _selectedFractalEntry = value;
            this.RaisePropertyChanged(nameof(SelectedFractalEntry));

            if (value.Promoted != null)
            {
                ApplyPromoted(value.Promoted);
                if (_selectedFractalType != value.Type)
                {
                    _selectedFractalType = value.Type;
                    ViewState.FractalType = value.Type;
                    ViewState.SnapToFractalDefault(value.Type);
                    this.RaisePropertyChanged(nameof(SelectedFractalType));
                }
                _renderHost.Trigger();
            }
            else
            {
                // Built-in entry — go through the SelectedFractalType setter
                // so the snap-to-default + retrigger path stays in one place.
                SelectedFractalType = value.Type;
            }
        }
    }

    private void ApplyPromoted(RegisteredFractal r)
    {
        var p = ViewState.FractalParameters;
        switch (r.Engine)
        {
            case EquationEngine.Sandbox:
                p.SandboxSource = r.Source;
                p.SandboxName = r.Name;
                _renderHost.CompileSandbox(r.Source);
                break;
            case EquationEngine.UserEquation:
                p.UserEquationSource = r.Source;
                p.UserEquationName = r.Name;
                _renderHost.CompileUserEquation(r.Source);
                break;
            case EquationEngine.UserBulb:
                p.UserBulbSource = r.Source;
                p.UserBulbName = r.Name;
                _renderHost.CompileUserBulb(r.Source);
                break;
        }
    }

    // ── Post-FX (write-through to view state, repaint without recalc) ─────

    private int _brightness;
    public int Brightness
    {
        get => _brightness;
        set
        {
            int v = Math.Clamp(value, -100, 100);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _brightness, v))
            {
                ViewState.Brightness = v;
                _renderHost.RepaintWithPostFx();
            }
        }
    }

    private int _contrast;
    public int Contrast
    {
        get => _contrast;
        set
        {
            int v = Math.Clamp(value, -100, 100);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _contrast, v))
            {
                ViewState.Contrast = v;
                _renderHost.RepaintWithPostFx();
            }
        }
    }

    private int _adaptive;
    public int Adaptive
    {
        get => _adaptive;
        set
        {
            int v = Math.Clamp(value, 0, 100);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _adaptive, v))
            {
                ViewState.HistogramEq = v;
                // Adaptive contrast lives on the calculator's escape buffer,
                // so it requires a fresh calculation, not just a re-upload.
                _renderHost.Trigger();
            }
        }
    }

    private bool _brightnessLocked;
    public bool BrightnessLocked { get => _brightnessLocked; set => this.RaiseAndSetIfChanged(ref _brightnessLocked, value); }

    private bool _contrastLocked;
    public bool ContrastLocked { get => _contrastLocked; set => this.RaiseAndSetIfChanged(ref _contrastLocked, value); }

    private bool _adaptiveLocked;
    public bool AdaptiveLocked { get => _adaptiveLocked; set => this.RaiseAndSetIfChanged(ref _adaptiveLocked, value); }

    // ── Overlay toggles ───────────────────────────────────────────────────

    private bool _showGrid;
    /// <summary>True to blend the Cartesian grid + axis labels into the
    /// uploaded texture. Writes through to the render host so the next
    /// repaint includes the overlay.</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _showGrid, value))
            {
                _renderHost.ShowGrid = value;
                _renderHost.RepaintWithPostFx();
            }
        }
    }

    private bool _showWatermark;
    /// <summary>True to blend the region/theme + program/version watermark
    /// into the lower-right corner of the uploaded texture.</summary>
    public bool ShowWatermark
    {
        get => _showWatermark;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _showWatermark, value))
            {
                _renderHost.ShowWatermark = value;
                _renderHost.RepaintWithPostFx();
            }
        }
    }

    // ── Iter lock ─────────────────────────────────────────────────────────

    private bool _iterLocked;
    public bool IterLocked
    {
        get => _iterLocked;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _iterLocked, value))
            {
                ViewState.IterLocked = value;
                if (value && _lockedIterations <= 0)
                {
                    // Capture current iter count as the lock target.
                    LockedIterations = ViewState.Quality?.ComputeIterations(ViewState.Zoom) ?? 256;
                }
                ViewState.LockedIterations = _lockedIterations;
                _renderHost.Trigger();
            }
        }
    }

    /// <summary>Mirror an iter-lock state that was already applied to the shared
    /// ViewState (e.g. by a region jump) into the VM without re-triggering a
    /// render or recomputing the lock target. Keeps the toolbar/menu checkbox
    /// in sync with what the render is actually doing.</summary>
    public void SetIterLockSilent(bool locked, int lockedIterations)
    {
        if (lockedIterations > 0 && lockedIterations != _lockedIterations)
        {
            _lockedIterations = Math.Max(64, lockedIterations);
            this.RaisePropertyChanged(nameof(LockedIterations));
        }
        if (locked != _iterLocked)
        {
            _iterLocked = locked;
            this.RaisePropertyChanged(nameof(IterLocked));
        }
    }

    private int _lockedIterations;
    public int LockedIterations
    {
        get => _lockedIterations;
        set
        {
            int v = Math.Max(64, value);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _lockedIterations, v))
            {
                ViewState.LockedIterations = v;
                if (_iterLocked) _renderHost.Trigger();
            }
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ResetViewCommand { get; }

    private void ResetView()
    {
        ViewState.ResetView();
        Brightness = 0;
        Contrast = 0;
        Adaptive = 0;
        IterLocked = false;
        _renderHost.Trigger();
    }

    // ── Region pick from outside (theme editor jump, slideshow advance) ───

    public event EventHandler<string>? RegionJumpRequested;

    /// <summary>Called by ShellViewModel after a region pick lands. The
    /// host service translates the name into a FractalRegion + per-engine
    /// settings (see FractalRegion.LoadRegionFractalParams) and updates the
    /// shared ViewState; this method only mirrors the name into the combo.
    /// </summary>
    public void SetRegionName(string? name) => SelectedRegion = name;

    public void SetThemeName(string? name) => SelectedTheme = name;

    // ── Input plumbing ────────────────────────────────────────────────────

    private void OnInputViewChanged(object? sender, ViewChangedArgs e)
    {
        switch (e.Hint)
        {
            case RenderHint.Full:
                _renderHintFastInFlight = false;
                _panStopDebounce.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                _renderHost.Trigger();
                break;
            case RenderHint.Fast:
                _renderHintFastInFlight = true;
                _renderHost.TriggerFast();
                _panStopDebounce.Change(PanStopDebounceMs, System.Threading.Timeout.Infinite);
                break;
        }
    }

    private void OnRenderHostColorMapChanged(object? sender, EventArgs e)
    {
        OverlayContrastLuma = _renderHost.OverlayContrastLuma;
    }

    private void OnFrameCompleted(object? sender, RenderFrameInfo info)
    {
        // Mirrors the legacy status string in MainForm.TriggerCalculation.
        string precTag = info.HighPrecisionActive ? "[DD]" : "[SP]";
        string typeTag = $"[{info.FractalType}]";
        StatusText =
            $"{typeTag}  cx={info.CenterX:G12}  cy={info.CenterY:G12}  " +
            $"zoom={info.Zoom:G6}  iter={info.Iterations}  " +
            $"{precTag}  [{info.ElapsedMs} ms  {info.Width}×{info.Height}]" +
            (info.IterLocked ? "  [ITER LOCKED]" : "");
    }

    public void Dispose()
    {
        _input.ViewChanged -= OnInputViewChanged;
        _renderHost.FrameCompleted -= OnFrameCompleted;
        _renderHost.ColorMapChanged -= OnRenderHostColorMapChanged;
        _panStopDebounce.Dispose();
    }
}
