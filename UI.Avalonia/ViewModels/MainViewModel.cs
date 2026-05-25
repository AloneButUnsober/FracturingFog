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

        _selectedQuality = ViewState.Quality;
        _selectedFractalType = ViewState.FractalType;
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

    public ObservableCollection<QualityPreset> QualityPresets { get; }
    public ObservableCollection<FractalType> FractalTypes { get; }

    // ── Status + cursor ───────────────────────────────────────────────────

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

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
        set => this.RaiseAndSetIfChanged(ref _selectedRegion, value);
    }

    private string? _selectedTheme;
    public string? SelectedTheme
    {
        get => _selectedTheme;
        set => this.RaiseAndSetIfChanged(ref _selectedTheme, value);
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
                _renderHost.Trigger();
            }
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
        _panStopDebounce.Dispose();
    }
}
