// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using Avalonia.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
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

    // Finding B fix: wheel / key-repeat coalesce. RenderHint.Full fired in
    // rapid succession (multiple wheel ticks within ~50 ms) triggers cancel
    // + re-Calculate per click; at shallow zoom each calc is fast so the
    // queue thrashes through 5-10 redundant frames. Leading-edge fire +
    // trailing coalesce: first Full triggers immediately, subsequent Fulls
    // inside the window arm a trailing timer that fires one final Trigger
    // when the burst settles.
    private readonly System.Threading.Timer _fullCoalesceTimer;
    private const int FullCoalesceWindowMs = 50;
    private long _lastFullEmitTicks;
    private int _fullCoalescePending;

    // Adaptive slider fires RepaintWithAdaptive on every tick, which runs a
    // full histogram-equalization pass against the cached escape buffers.
    // Coalesce rapid drags into one render at ~30 Hz to stop the pipeline
    // thrashing while the user is still moving the slider.
    private readonly System.Threading.Timer _adaptiveRepaintDebounce;
    private const int AdaptiveRepaintDebounceMs = 33;
    // Re-entrancy guard: when a repaint is still running on the threadpool, a
    // newly-fired tick sets _adaptiveRepaintPending instead of starting a
    // second concurrent RepaintWithAdaptive. The in-flight repaint reschedules
    // itself once on completion if a newer value arrived mid-render. Keeps the
    // adaptive sweep paced to actual render capacity instead of piling up.
    private int _adaptiveRepaintBusy;    // 0 = idle, 1 = running
    private int _adaptiveRepaintPending; // 0 = none, 1 = newer value arrived

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
        _input.SelectionBoxChanged += (_, box) =>
        {
            if (box is { } b)
                _renderHost.SetSelectionBox(b.X, b.Y, b.Width, b.Height);
            else
                _renderHost.SetSelectionBox(null, null, null, null);
        };
        _renderHost.FrameCompleted += OnFrameCompleted;
        _renderHost.StatusRequested += OnRenderHostStatusRequested;
        _renderHost.ColorMapChanged += OnRenderHostColorMapChanged;
        _renderHost.RenderCancelled += OnRenderCancelled;
        _overlayContrastLuma = _renderHost.OverlayContrastLuma;

        _panStopDebounce = new System.Threading.Timer(_ =>
        {
            if (_renderHintFastInFlight)
            {
                _renderHintFastInFlight = false;
                _renderHost.Trigger();
            }
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        _adaptiveRepaintDebounce = new System.Threading.Timer(_ =>
        {
            // Re-entrancy guard: if a prior repaint is still running, just mark
            // a pending tick and bail. The running repaint will reschedule us
            // when it finishes. Prevents concurrent ApplyHistogramEqualization
            // passes from clobbering each other during a fast adaptive sweep.
            if (System.Threading.Interlocked.CompareExchange(ref _adaptiveRepaintBusy, 1, 0) != 0)
            {
                System.Threading.Volatile.Write(ref _adaptiveRepaintPending, 1);
                return;
            }
            try
            {
                _renderHost.RepaintWithAdaptive();
            }
            finally
            {
                System.Threading.Volatile.Write(ref _adaptiveRepaintBusy, 0);
                if (System.Threading.Interlocked.Exchange(ref _adaptiveRepaintPending, 0) == 1)
                {
                    _adaptiveRepaintDebounce!.Change(AdaptiveRepaintDebounceMs, System.Threading.Timeout.Infinite);
                }
            }
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

        _fullCoalesceTimer = new System.Threading.Timer(_ =>
        {
            // Trailing edge of a Full burst. If a coalesce was pending, fire
            // one final Trigger so the last wheel/key state is rendered. Reset
            // the emit timestamp so the *next* Full also fires immediately.
            if (System.Threading.Interlocked.Exchange(ref _fullCoalescePending, 0) == 1)
            {
                System.Threading.Volatile.Write(ref _lastFullEmitTicks, 0);
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
        (FractalType.Nova,             "Nova"),
        (FractalType.BuddhaBrot,       "Buddhabrot"),
        (FractalType.Nebulabrot,       "Nebulabrot"),
        (FractalType.AntiBuddhabrot,   "Anti-Buddhabrot"),
        (FractalType.AntiNebulabrot,   "Anti-Nebulabrot"),
        (FractalType.IFS,              "IFS"),
        (FractalType.LSystem,          "L-System"),
        (FractalType.StrangeAttractor, "Strange Attractor"),
        (FractalType.UserEquation,     "User Equation"),
        (FractalType.Mandelbulb,       "Mandelbulb (3D)"),
        (FractalType.Mandelbox,        "Mandelbox (3D)"),
        (FractalType.Kifs,             "KIFS (3D)"),
        (FractalType.Sandbox,          "Sandbox"),
        (FractalType.UserBulb,         "User Bulb (3D)"),
        (FractalType.TearDrop,         "Tear Drop"),
        (FractalType.GeneratedMandelbrotZ2, "Mandelbrot Z² (Generated)"),
        (FractalType.GeneratedMandelbrotZ3, "Mandelbrot Z³ (Generated)"),
        (FractalType.GeneratedMandelbrotZ4, "Mandelbrot Z⁴ (Generated)"),
        (FractalType.GeneratedMandelbrotZ5, "Mandelbrot Z⁵ (Generated)"),
        (FractalType.GeneratedTricorn,      "Tricorn (Generated)"),
        (FractalType.GeneratedBurningShip,  "Burning Ship (Generated)"),
        (FractalType.Magnet1,               "Magnet 1"),
        (FractalType.Magnet2,               "Magnet 2"),
        (FractalType.Glynn,                 "Glynn"),
        (FractalType.Logistic,              "Logistic Bifurcation"),
        (FractalType.Halley,                "Halley"),
        (FractalType.Secant,                "Secant"),
        (FractalType.Spider,                "Spider"),
        (FractalType.QuaternionJulia,       "Quaternion Julia (3D)"),
        (FractalType.QuaternionMandelbrot,  "Quaternion Mandelbrot (3D)"),
        (FractalType.Plasma,                "Plasma (Diamond-Square)"),
        (FractalType.AcidWarp,              "Acid Fog (Palette Cycling)"),
        (FractalType.Flame,                 "Flame (Apophysis)"),
        (FractalType.Apollonian,            "Apollonian Gasket"),
        (FractalType.Kleinian,              "Kleinian Limit Set (3D)"),
        (FractalType.BicomplexMandelbrot,   "Bicomplex Mandelbrot (3D)"),
        (FractalType.Dla,                   "DLA (Brownian Tree)"),
        (FractalType.RandomTile,            "Random Tiling (Bourke)"),
    };

    /// <summary>Category filter applied to the toolbar Type combo via its
    /// right-click sort menu. Mirrors the Region combo's RegionSortMode. A pure
    /// view concern — narrows which entries are listed without changing the
    /// active fractal.</summary>
    public enum FractalTypeFilter { Default, TwoD, ThreeD, User, CalcGen, Promoted }

    private FractalTypeFilter _fractalFilter = FractalTypeFilter.Default;

    /// <summary>User-equation family — the three built-ins backed by editable
    /// equation sources (matches the "User" filter bucket).</summary>
    private static bool IsUserFamily(FractalType t) =>
           t == FractalType.UserEquation
        || t == FractalType.Sandbox
        || t == FractalType.UserBulb;

    /// <summary>CalculatorGen-emitted built-ins (the "Generated …" labels).</summary>
    private static bool IsCalcGen(FractalType t) =>
           t == FractalType.GeneratedMandelbrotZ2
        || t == FractalType.GeneratedMandelbrotZ3
        || t == FractalType.GeneratedMandelbrotZ4
        || t == FractalType.GeneratedMandelbrotZ5
        || t == FractalType.GeneratedTricorn
        || t == FractalType.GeneratedBurningShip;

    private bool MatchesFilter(FractalType t) => _fractalFilter switch
    {
        FractalTypeFilter.TwoD    => !FractalViewState.IsThreeD(t),
        FractalTypeFilter.ThreeD  => FractalViewState.IsThreeD(t),
        FractalTypeFilter.User    => IsUserFamily(t),
        FractalTypeFilter.CalcGen => IsCalcGen(t),
        _                          => true, // Default (Promoted handled separately)
    };

    /// <summary>Rebuild <see cref="FractalEntries"/> from the built-in label
    /// table + the current <see cref="RegisteredFractalCatalog"/> snapshot,
    /// narrowed by the active <see cref="FractalTypeFilter"/>. Call after a user
    /// equation is saved/promoted so the combo picks up the new entry. Preserves
    /// the current selection when it survives the rebuild.</summary>
    public void RebuildFractalEntries()
    {
        // Remember the live pick so a filter flip doesn't drop the active
        // fractal's highlight when it still qualifies for the new bucket.
        var prevType = _selectedFractalEntry?.Type ?? _selectedFractalType;
        var prevPromoted = _selectedFractalEntry?.Promoted;

        FractalEntries.Clear();

        var promoted = RegisteredFractalCatalog.Snapshot();

        if (_fractalFilter == FractalTypeFilter.Promoted)
        {
            // Promoted-only view: just the catalog entries, no built-ins.
            foreach (var r in promoted)
                FractalEntries.Add(FractalTypeEntry.FromPromoted(r));
        }
        else
        {
            foreach (var (t, label) in BuiltInFractalLabels)
                if (MatchesFilter(t))
                    FractalEntries.Add(FractalTypeEntry.BuiltIn(t, label));

            // Promoted equations follow the "— Registered —" divider under the
            // Default view only; the category filters list built-ins alone.
            if (_fractalFilter == FractalTypeFilter.Default && promoted.Count > 0)
            {
                FractalEntries.Add(FractalTypeEntry.Divider());
                foreach (var r in promoted)
                    FractalEntries.Add(FractalTypeEntry.FromPromoted(r));
            }
        }

        // Restore the highlight without re-triggering a render. If the prior
        // pick was filtered out, leave _selectedFractalEntry pointing at it —
        // the combo simply shows no selection until a filter that includes it
        // is chosen; the active fractal is unchanged either way.
        FractalTypeEntry? restore = null;
        foreach (var e in FractalEntries)
        {
            if (e.IsDivider) continue;
            if (prevPromoted != null ? ReferenceEquals(e.Promoted, prevPromoted)
                                     : (e.Promoted == null && e.Type == prevType))
            { restore = e; break; }
        }
        if (restore != null && !ReferenceEquals(_selectedFractalEntry, restore))
        {
            _selectedFractalEntry = restore;
            this.RaisePropertyChanged(nameof(SelectedFractalEntry));
        }
    }

    /// <summary>Build the Type combo's right-click sort/filter menu
    /// (Default / 2D / 3D / User / CalcGen / Promoted). Mirrors
    /// <see cref="FloatingMenuViewModel.BuildRegionSortMenu"/>; each pick flips
    /// the filter and rebuilds the entry list.</summary>
    public IReadOnlyList<ComboMenuItem> BuildFractalTypeSortMenu()
    {
        ComboMenuItem Filter(string header, FractalTypeFilter f) =>
            ComboMenuItem.Item(header, _fractalFilter == f,
                () => { _fractalFilter = f; RebuildFractalEntries(); });

        return new List<ComboMenuItem>
        {
            Filter("Default", FractalTypeFilter.Default),
            ComboMenuItem.Separator,
            Filter("2D",       FractalTypeFilter.TwoD),
            Filter("3D",       FractalTypeFilter.ThreeD),
            Filter("User",     FractalTypeFilter.User),
            Filter("CalcGen",  FractalTypeFilter.CalcGen),
            Filter("Promoted", FractalTypeFilter.Promoted),
        };
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

    /// <summary>Raised when the user picks Acid Fog from the toolbar Type combo
    /// (the genuine setter, not the silent region-recall mirror). The shell
    /// listens to auto-select the animated "Acid Fog Spectrum" theme so cycling
    /// is visible immediately — a plain/static theme would show no motion.</summary>
    public event EventHandler? AcidFogTypeSelected;

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
                // #250 — Acid Warp auto-starts its palette-cycle animation (the
                // classic flowing look). The first time this launch, play the
                // animated "ACID FOG" title card first (which also turns cycling
                // on), then dissolve into the classic ring field.
                if (value == FractalType.AcidWarp)
                {
                    if (AcidWarpIntro.TryConsumeIntro())
                        StartAcidWarpIntro();
                    else
                        PaletteCycleEnabled = true;
                    // Ask the shell to snap to the animated Acid Fog Spectrum
                    // theme (raised AFTER the property change, so the shell's
                    // compat re-filter has already put it in the theme combo).
                    AcidFogTypeSelected?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    // Leaving Acid Warp via the toolbar clears cycling so the
                    // LUT rotation doesn't keep spinning on a non-cycling type,
                    // and stops the auto-VJ ambient loop (Acid Fog only).
                    PaletteCycleEnabled = false;
                    AcidFogAmbientEnabled = false;
                }
                this.RaisePropertyChanged(nameof(IsAcidFogType));
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
        this.RaisePropertyChanged(nameof(IsAcidFogType));
    }

    /// <summary>True when the active fractal type is Acid Fog — gates the
    /// Cycle-adjacent auto-VJ (ambient loop) toolbar affordances.</summary>
    public bool IsAcidFogType => _selectedFractalType == FractalType.AcidWarp;

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
                // #27 Phase 0 — promoted registry is the user's own; trusted.
                // Also resets any stale ExternalFile stamp from a prior region.
                p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive;
                _renderHost.CompileUserEquation(r.Source);
                break;
            case EquationEngine.UserBulb:
                p.UserBulbSource = r.Source;
                p.UserBulbName = r.Name;
                p.UserCodeOrigin = FracturingFog.Security.UserCodeOrigin.Interactive;
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

    private int _gamma;
    /// <summary>Live image gamma in [-100,100]; 0 = neutral. Write-through to
    /// ViewState + post-FX repaint, exactly like Brightness/Contrast. No lock:
    /// gamma has no theme default (themes bake their own PaletteGamma).</summary>
    public int Gamma
    {
        get => _gamma;
        set
        {
            int v = Math.Clamp(value, -100, 100);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _gamma, v))
            {
                ViewState.Gamma = v;
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
                // Adaptive contrast re-runs only the histogram equalization
                // pass against the cached escape buffers — no fresh
                // Calculate() — so the slider feels live (parity with
                // Brightness / Contrast). Mandelbrot only; alt calculators
                // fall back to a post-FX repaint inside RepaintWithAdaptive.
                // Debounced: slider ticks at >30 Hz coalesce into a single
                // RepaintWithAdaptive so the histogram-eq pass doesn't
                // thrash mid-drag.
                _adaptiveRepaintDebounce.Change(AdaptiveRepaintDebounceMs, System.Threading.Timeout.Infinite);
            }
        }
    }

    private bool _bandDither;
    /// <summary>F11 ordered-dither deband (CPU F11a + GPU F11b). Unlike the
    /// post-FX sliders this acts at colorize time, so it needs a full re-render
    /// (Trigger), not a RepaintWithPostFx. The host lifts ViewState.BandDither
    /// into the GradientColorMap statics at render start.</summary>
    public bool BandDither
    {
        get => _bandDither;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _bandDither, value))
            {
                ViewState.BandDither = value;
                _renderHost.Trigger();
            }
        }
    }

    private int _bandDitherStrength = 100;
    /// <summary>Dither amplitude in [0,100]; 100 = full ±0.5-LSB. Live only when
    /// <see cref="BandDither"/> is on; re-renders on change.</summary>
    public int BandDitherStrength
    {
        get => _bandDitherStrength;
        set
        {
            int v = Math.Clamp(value, 0, 100);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _bandDitherStrength, v))
            {
                ViewState.BandDitherStrength = v;
                if (_bandDither) _renderHost.Trigger();
            }
        }
    }

    private bool _alphaPreview;
    /// <summary>F10.5 live per-stop alpha preview. Display-only checkerboard
    /// composite in the host upload path, so a post-FX repaint (no recalc) is
    /// enough to show/hide it.</summary>
    public bool AlphaPreview
    {
        get => _alphaPreview;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _alphaPreview, value))
            {
                ViewState.AlphaPreview = value;
                _renderHost.RepaintWithPostFx();
            }
        }
    }

    private bool _brightnessLocked;
    public bool BrightnessLocked { get => _brightnessLocked; set => this.RaiseAndSetIfChanged(ref _brightnessLocked, value); }

    private bool _contrastLocked;
    public bool ContrastLocked { get => _contrastLocked; set => this.RaiseAndSetIfChanged(ref _contrastLocked, value); }

    private bool _adaptiveLocked;
    public bool AdaptiveLocked { get => _adaptiveLocked; set => this.RaiseAndSetIfChanged(ref _adaptiveLocked, value); }

    private bool _lightingLocked;
    /// <summary>When true, theme selection does NOT overwrite
    /// <c>FractalParameters.Lighting</c> from the theme's bundled
    /// <see cref="LightingFxPresetData"/>. Default false = honour theme presets.
    /// Phase 24.</summary>
    public bool LightingLocked { get => _lightingLocked; set => this.RaiseAndSetIfChanged(ref _lightingLocked, value); }

    private bool _reliefLocked;
    /// <summary>When true, region recall does NOT change the Relief 3D state —
    /// relief stays on/off as the user has it regardless of the region's saved
    /// setting ("Lock Relief 3D"). Default false = relief toggles with region.</summary>
    public bool ReliefLocked { get => _reliefLocked; set => this.RaiseAndSetIfChanged(ref _reliefLocked, value); }

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

    private FracturingFog.Imaging.AsciiWatermarkStyle _asciiWatermarkStyle
        = FracturingFog.Imaging.AsciiWatermarkStyle.Block;
    /// <summary>Glyph style for the ASCII watermark (Terminal Mode + ASCII
    /// export). Changing it repaints so the live terminal reflects it at once.</summary>
    public FracturingFog.Imaging.AsciiWatermarkStyle AsciiWatermarkStyle
    {
        get => _asciiWatermarkStyle;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _asciiWatermarkStyle, value))
            {
                _renderHost.AsciiWatermarkStyle = value;
                _renderHost.RepaintWithPostFx(); // fires FrameBufferChanged → ASCII pump
            }
        }
    }

    private bool _showPerfHud;
    /// <summary>True to blend the perf HUD (phase timings + HW summary)
    /// into the top-left of the uploaded texture. Cheap to leave on.</summary>
    public bool ShowPerfHud
    {
        get => _showPerfHud;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _showPerfHud, value))
            {
                _renderHost.ShowPerfHud = value;
                _renderHost.RepaintWithPostFx();
            }
        }
    }

    /// <summary>T3.1: GPU compute toggle for the SP Mandelbrot path.
    /// Bound to Ctrl+G. Re-reads from the host after assignment so the
    /// UI reflects "didn't engage" when the renderer is not D3D11.</summary>
    public bool UseGpuCompute
    {
        get => _renderHost.UseGpuCompute;
        set
        {
            _renderHost.UseGpuCompute = value;
            this.RaisePropertyChanged(nameof(UseGpuCompute));
            _renderHost.Trigger();
        }
    }

    /// <summary>Slice 4 (#158): GPU relief-raymarch toggle. Flips the shared
    /// <see cref="FractalParameters.Relief2DGpuRaymarch"/> and retriggers.
    /// Bound to Ctrl+Shift+G. Default ON now that the GPU kernel reaches full
    /// ShadingPipeline FX parity; turning it off forces the CPU sphere-trace
    /// (the parity oracle). Inert unless relief raymarch mode is active.</summary>
    public bool ReliefGpuRaymarch
    {
        get => _renderHost.ViewState.FractalParameters.Relief2DGpuRaymarch;
        set
        {
            _renderHost.ViewState.FractalParameters.Relief2DGpuRaymarch = value;
            this.RaisePropertyChanged(nameof(ReliefGpuRaymarch));
            _renderHost.Trigger();
        }
    }

    /// <summary>Clear the perf HUD's rolling buffers + reset GC-rate
    /// baseline. Bound to Shift+H so the user can start a clean capture
    /// when switching regions mid-test.</summary>
    public void ResetPerfStats()
    {
        _renderHost.ResetPerfStats();
        _renderHost.RepaintWithPostFx();
    }

    // ── Custom watermark precedence chain ────────────────────────────────
    //
    // The render host gets one resolved WatermarkDef? — null means "use the
    // default region/theme + auto-contrast". The precedence is:
    //   1. OverrideRegionWatermark + ActiveCustomWatermark → ActiveCustomWatermark
    //   2. RegionEmbeddedWatermark                          → that
    //   3. UseCustomWatermark + ActiveCustomWatermark       → ActiveCustomWatermark
    //   4. → null (default)
    // The shell sets the inputs (toggles, selected name, region jump callback);
    // PushActiveWatermark recomputes + writes to the render host.

    private bool _useCustomWatermark;
    /// <summary>Master toggle. When false, the saved-watermark library is
    /// inert (image/poster/slideshow/video all render the default region/theme
    /// watermark).</summary>
    public bool UseCustomWatermark
    {
        get => _useCustomWatermark;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _useCustomWatermark, value))
            {
                // Flipping the master toggle on with a real selection is a strong
                // intent signal: the user expects to see the custom watermark
                // immediately. Auto-enable ShowWatermark so the overlay actually
                // composites — otherwise the toggle is silently inert until the
                // user also flips Show Watermark (only surfaced via right-click).
                if (value && ActiveCustomWatermark != null && !_showWatermark)
                    ShowWatermark = true;
                PushActiveWatermark();
                if (_showWatermark) _renderHost.RepaintWithPostFx();
            }
        }
    }

    private string? _selectedCustomWatermarkName;
    /// <summary>The library entry currently in scope. Looked up against
    /// UserWatermarkStore on the fly so a fresh save through the editor
    /// reaches the render path on the next repaint.</summary>
    public string? SelectedCustomWatermarkName
    {
        get => _selectedCustomWatermarkName;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _selectedCustomWatermarkName, value))
            {
                // Picking a watermark while the master toggle is on but Show
                // Watermark is off is the same intent-signal case as the toggle
                // setter — auto-enable so the user sees it.
                if (_useCustomWatermark && !_showWatermark && ActiveCustomWatermark != null)
                    ShowWatermark = true;
                PushActiveWatermark();
                if (_showWatermark) _renderHost.RepaintWithPostFx();
            }
        }
    }

    private bool _overrideRegionWatermark;
    /// <summary>FloatingMenu "Override region watermark" — forces the active
    /// custom watermark to win even when the current region carries an
    /// embedded one. Mirrors the existing post-fx override flags.</summary>
    public bool OverrideRegionWatermark
    {
        get => _overrideRegionWatermark;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _overrideRegionWatermark, value))
            {
                PushActiveWatermark();
                if (_showWatermark) _renderHost.RepaintWithPostFx();
            }
        }
    }

    private FracturingFog.Models.WatermarkDef? _regionEmbeddedWatermark;
    /// <summary>Embedded watermark carried by the current region's JSON.
    /// Set by the shell when a region with an EmbeddedWatermark is jumped to;
    /// cleared on region change away from one.</summary>
    public FracturingFog.Models.WatermarkDef? RegionEmbeddedWatermark
    {
        get => _regionEmbeddedWatermark;
        set
        {
            if (!ReferenceEquals(_regionEmbeddedWatermark, value))
            {
                _regionEmbeddedWatermark = value;
                this.RaisePropertyChanged(nameof(RegionEmbeddedWatermark));
                PushActiveWatermark();
                if (_showWatermark) _renderHost.RepaintWithPostFx();
            }
        }
    }

    private FracturingFog.Models.WatermarkDef? _draftWatermark;
    /// <summary>Unsaved watermark currently being edited in the Watermark
    /// Editor. Outranks every other source while the editor is open, and is
    /// deliberately *not* routed through UserWatermarkStore: a draft has not
    /// been saved yet, so a store lookup can never see it. Set to null when
    /// the editor closes to fall back to the normal chain.</summary>
    public FracturingFog.Models.WatermarkDef? DraftWatermark
    {
        get => _draftWatermark;
        set
        {
            _draftWatermark = value;
            this.RaisePropertyChanged(nameof(DraftWatermark));
            // A draft is only visible if the overlay is on at all. Same
            // intent-signal reasoning as the UseCustomWatermark setter: the
            // user is actively editing a watermark, so show it.
            if (value != null && !_showWatermark) ShowWatermark = true;
            PushActiveWatermark();
            if (_showWatermark) _renderHost.RepaintWithPostFx();
        }
    }

    /// <summary>The library entry pointed at by SelectedCustomWatermarkName,
    /// resolved fresh each call so the editor's Save round-trip is visible.
    /// Null when the name is unset or no longer exists.</summary>
    public FracturingFog.Models.WatermarkDef? ActiveCustomWatermark
        => FracturingFog.Models.UserWatermarkStore.Instance.GetByName(_selectedCustomWatermarkName);

    /// <summary>Push the resolved watermark def into the render host. The host
    /// hands it to FractalOverlayCompositor + ImageExport on the next frame.</summary>
    public void PushActiveWatermark()
    {
        if (_draftWatermark != null)
        {
            _renderHost.ActiveWatermark = _draftWatermark;
            return;
        }

        var custom = ActiveCustomWatermark;
        FracturingFog.Models.WatermarkDef? resolved =
            (_overrideRegionWatermark && custom != null) ? custom :
            _regionEmbeddedWatermark != null              ? _regionEmbeddedWatermark :
            (_useCustomWatermark && custom != null)       ? custom :
                                                            null;
        _renderHost.ActiveWatermark = resolved;
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
                // Only capture-from-current when caller hasn't already supplied
                // a target via LockedIterations. The shell handler sets
                // LockedIterations first when the menu Lock checkbox is ticked,
                // so this branch only fires for direct programmatic toggles
                // (e.g. coming back from a region jump with no carried iter).
                if (value && _lockedIterations <= 0)
                    LockedIterations = ViewState.Quality?.ComputeIterations(ViewState.Zoom) ?? 256;
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
        Gamma = 0;
        IterLocked = false;
        _renderHost.Trigger();
    }

    // ── Region pick from outside (theme editor jump, slideshow advance) ───

#pragma warning disable CS0067 // public API surface; raised by consumers via reflection / future wiring
    public event EventHandler<string>? RegionJumpRequested;
#pragma warning restore CS0067

    /// <summary>Called by ShellViewModel after a region pick lands. The
    /// host service translates the name into a FractalRegion + per-engine
    /// settings (see FractalRegion.LoadRegionFractalParams) and updates the
    /// shared ViewState; this method only mirrors the name into the combo.
    /// </summary>
    public void SetRegionName(string? name) => SelectedRegion = name;

    public void SetThemeName(string? name) => SelectedTheme = name;

    /// <summary>Mirror an externally-applied <see cref="QualityPreset"/>
    /// into the toolbar combo without re-pushing it to ViewState or
    /// re-triggering. Called after a region jump that carries its own
    /// QualityPreset — without this the combo would drift out of sync with
    /// ViewState.Quality, and saves (poster / region) would silently use the
    /// region's quality instead of whatever the combo displayed.</summary>
    public void SetQualitySilent(QualityPreset? preset)
    {
        if (preset == null) return;
        // Region-loaded QualityPreset is a JSON-deserialized instance; the
        // toolbar combo's SelectedItem must reference the QualityPreset.All
        // entry by name so SelectedItem equality holds and the combo paints.
        QualityPreset? match = null;
        foreach (var p in QualityPresets)
        {
            if (string.Equals(p.Name, preset.Name, StringComparison.Ordinal)) { match = p; break; }
        }
        if (match == null) return;
        if (ReferenceEquals(_selectedQuality, match)) return;
        _selectedQuality = match;
        this.RaisePropertyChanged(nameof(SelectedQuality));
    }

    // ── Input plumbing ────────────────────────────────────────────────────

    private void OnInputViewChanged(object? sender, ViewChangedArgs e)
    {
        // Wheel/keyboard zoom can auto-adapt ViewState.Quality (see
        // FractalInputController.AdaptQualityForZoom). Mirror that into the
        // combo so the displayed preset matches what's about to render —
        // and so a save-region right after a zoom captures the live tier.
        if (!ReferenceEquals(_selectedQuality, ViewState.Quality))
            SetQualitySilent(ViewState.Quality);

        switch (e.Hint)
        {
            case RenderHint.Full:
                _panStopDebounce.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

                long now = Environment.TickCount64;
                long prev = System.Threading.Volatile.Read(ref _lastFullEmitTicks);
                long delta = now - prev;
                if (prev != 0 && delta < FullCoalesceWindowMs)
                {
                    // Inside the burst window — defer to a single trailing
                    // Trigger. State is already mutated by the input
                    // controller; we just need to render the final state once.
                    System.Threading.Volatile.Write(ref _fullCoalescePending, 1);
                    _fullCoalesceTimer.Change(FullCoalesceWindowMs, System.Threading.Timeout.Infinite);
                }
                else
                {
                    // First click of a (possibly new) burst — render now.
                    System.Threading.Volatile.Write(ref _lastFullEmitTicks, now);
                    _renderHintFastInFlight = false;
                    _renderHost.Trigger();
                    // Re-arm trailing timer in case more clicks arrive within
                    // the window — the trailing fire then renders the final
                    // accumulated state.
                    _fullCoalesceTimer.Change(FullCoalesceWindowMs, System.Threading.Timeout.Infinite);
                }
                break;
            case RenderHint.Fast:
                // Wave 2.5 — progressive ¼ → ½ → full chain. Each pan / wheel
                // step cancels the in-flight chain and restarts at ¼ res so
                // the user sees feedback within ~one calc of the quarter-res
                // sidecar (~1/16 of full-res cost). The chain self-escalates
                // to a full-quality final stage when input stops; the
                // pan-stop debounce below is kept as a backstop for callers
                // that emit a single Fast without a follow-up Full.
                _renderHintFastInFlight = true;
                _renderHost.Trigger(progressive: true);
                _panStopDebounce.Change(PanStopDebounceMs, System.Threading.Timeout.Infinite);
                break;
        }
    }

    private void OnRenderHostColorMapChanged(object? sender, EventArgs e)
    {
        OverlayContrastLuma = _renderHost.OverlayContrastLuma;
    }

    private RenderFrameInfo? _lastFrameInfo;

    // S-X9c (2026-06-27) — minimum-visible hold for "Calculating…".
    // Shallow renders complete in <20 ms so OnFrameCompleted overwrites
    // the busy string before the next display refresh — user reports
    // never seeing "Calculating…" at all and being unsure whether the
    // app responded to their input. Hold the busy string for at least
    // MinBusyVisibleMs before letting FrameCompleted overwrite it; if
    // the calc finishes faster, queue the frame-info string and apply
    // it on a UI-thread timer when the hold expires. RenderCancelled
    // takes the same path so cancelled fast calcs still show the busy
    // hint briefly.
    private const int MinBusyVisibleMs = 250;
    private readonly System.Diagnostics.Stopwatch _busyClock = new();
    private bool _busyActive;
    private string? _pendingStatusText;
    private System.Threading.Timer? _busyReleaseTimer;

    private void OnRenderHostStatusRequested(object? sender, string text)
    {
        // Render host raises this exactly once per Trigger with
        // "Calculating…". Mark busy, start the clock, push to the bar.
        _busyActive = true;
        _busyClock.Restart();
        _pendingStatusText = null;
        StatusText = text;
    }

    private void ApplyOrDeferStatusText(string text)
    {
        if (!_busyActive)
        {
            StatusText = text;
            return;
        }
        long elapsed = _busyClock.ElapsedMilliseconds;
        if (elapsed >= MinBusyVisibleMs)
        {
            _busyActive = false;
            _busyClock.Stop();
            StatusText = text;
            _pendingStatusText = null;
            return;
        }
        _pendingStatusText = text;
        int remaining = MinBusyVisibleMs - (int)elapsed;
        _busyReleaseTimer ??= new System.Threading.Timer(_ =>
        {
            string? queued = _pendingStatusText;
            if (queued == null) return;
            _pendingStatusText = null;
            _busyActive = false;
            _busyClock.Stop();
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = queued);
        }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _busyReleaseTimer.Change(remaining, System.Threading.Timeout.Infinite);
    }

    private void OnFrameCompleted(object? sender, RenderFrameInfo info)
    {
        // Prefer the calculator's actual precision label (PT, QD-PT,
        // DD-HP4, etc.) when available — collapses to [DD]/[SP] only for
        // legacy calculators that expose just the bool. Lets the user
        // see exactly which deep-zoom path engaged (perturbation, BLA,
        // HP-direct, vectorised DD4, etc.).
        string precTag = !string.IsNullOrEmpty(info.PrecisionLabel)
            ? $"[{info.PrecisionLabel}]"
            : (info.HighPrecisionActive ? "[DD]" : "[SP]");
        string typeTag = $"[{info.FractalType}]";
        string text =
            $"{typeTag}  cx={info.CenterX:G12}  cy={info.CenterY:G12}  " +
            $"zoom={info.Zoom:G6}  iter={info.Iterations}  " +
            $"{precTag}  [{info.ElapsedMs} ms  {info.Width}×{info.Height}]" +
            (info.IterLocked ? "  [ITER LOCKED]" : "");
        // NOTE: the detail-depth limit notice moved OFF the status bar — a long
        // wrapping string there resized the panel and bounced the image edge.
        // It now lives in the render-context overlay (RenderContextOverlay).
        _lastFrameInfo = info;
        ApplyOrDeferStatusText(text);
    }

    // S-X8 (2026-06-27) — RenderHost cancelled the in-flight calc (rapid
    // pan/zoom, deep-Extreme TAA tick beat the prior frame). Without this
    // handler the "Calculating…" string Trigger pushed stays on screen
    // forever. Replay the last good FrameInfo when available so the bar
    // returns to its prior render's geometry; fall back to a blank if no
    // frame has landed yet.
    private void OnRenderCancelled(object? sender, EventArgs e)
    {
        var info = _lastFrameInfo;
        if (info == null)
        {
            ApplyOrDeferStatusText(string.Empty);
            return;
        }
        OnFrameCompleted(this, info.Value);
    }

    // ── #250: animated Acid Warp title card, then dissolve to the rings ──────
    private DispatcherTimer? _acidIntroTimer;

    private void StartAcidWarpIntro()
    {
        var p = ViewState.FractalParameters;
        p.AcidWarpTitleCard = true;
        p.AcidWarpPattern = AcidWarpIntro.ClassicPattern;   // rings behind the wordmark
        p.AcidWarpFrequency = AcidWarpIntro.ClassicFrequency;
        p.AcidWarpCenterX = 0.0;
        p.AcidWarpCenterY = 0.0;
        p.AcidWarpWarpStrength = 0.0;
        p.AcidWarpMorph = false;   // no morphing under the card

        // Animate the card (and the classic look that follows) via palette cycle.
        PaletteCycleEnabled = true;
        _renderHost.Trigger();

        _acidIntroTimer?.Stop();
        _acidIntroTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(3.5), DispatcherPriority.Background, (_, _) =>
            {
                _acidIntroTimer?.Stop();
                _acidIntroTimer = null;
                // Dissolve into the classic ring field; keep cycling.
                AcidWarpIntro.ApplyClassic(ViewState.FractalParameters);
                _renderHost.Trigger();
            });
        _acidIntroTimer.Start();
    }

    // ── #249 / IDEA-1: live palette cycling (animate colour, not camera) ──────
    // A DispatcherTimer advances a rotation phase over wall-clock and pushes it
    // to the render host, which re-maps the field through the rotated LUT. Cheap
    // for the procedural / Acid Warp families; heavier for escape-time types.

    private DispatcherTimer? _paletteCycleTimer;
    private double _paletteCyclePhase;   // turns, wraps mod 1
    private DateTime _paletteCycleLastTick;

    private bool _paletteCycleEnabled;
    /// <summary>Toggle live palette cycling on the current view.</summary>
    public bool PaletteCycleEnabled
    {
        get => _paletteCycleEnabled;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _paletteCycleEnabled, value))
            {
                if (value) StartPaletteCycle();
                else StopPaletteCycle();
            }
        }
    }

    private double _paletteCycleRate = 0.15;
    /// <summary>Palette-cycle speed in LUT turns per second. Default 0.15
    /// (~7 s per full palette sweep).</summary>
    public double PaletteCycleRate
    {
        get => _paletteCycleRate;
        set => this.RaiseAndSetIfChanged(ref _paletteCycleRate, Math.Clamp(value, 0.01, 5.0));
    }

    private void StartPaletteCycle()
    {
        _paletteCycleLastTick = DateTime.UtcNow;
        _paletteCycleTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnPaletteCycleTick);
        _paletteCycleTimer.Start();
    }

    private void StopPaletteCycle()
    {
        _paletteCycleTimer?.Stop();
        _paletteCyclePhase = 0;
        _renderHost.SetLivePaletteRotation(0f);   // clear + repaint at rest
    }

    private void OnPaletteCycleTick(object? sender, EventArgs e)
    {
        // Suspend the recolor while an ambient fade-to-black is presenting its
        // own blended buffers — otherwise SetLivePaletteRotation would repaint
        // the full-bright field and fight the fade.
        if (_acidAmbientFading) return;

        var now = DateTime.UtcNow;
        double dt = (now - _paletteCycleLastTick).TotalSeconds;
        _paletteCycleLastTick = now;
        if (dt <= 0) return;
        if (dt > 0.25) dt = 0.25;                 // guard a stalled dispatcher

        _paletteCyclePhase += _paletteCycleRate * dt;
        _paletteCyclePhase -= Math.Floor(_paletteCyclePhase);
        _renderHost.SetLivePaletteRotation((float)_paletteCyclePhase);
    }

    // ── #251 / IDEA-6: Acid Fog auto-VJ ambient loop ─────────────────────────
    // Hold a pattern while the palette cycles, then fade-to-black and advance to
    // the next pattern from a shuffled, non-repeating playlist. The pure timing
    // lives in AcidWarpAmbientDirector; this wires it to the render host: a 50 ms
    // DispatcherTimer ticks the director and, on an advance, drives a stepped
    // fade-to-black on the outgoing frame before swapping the pattern in.

    private DispatcherTimer? _acidAmbientTimer;
    private AcidWarpAmbientDirector? _acidAmbientDirector;
    private DateTime _acidAmbientLastTick;

    // Fade state machine. _acidAmbientFading gates the palette-cycle recolor so
    // it does not overwrite the fade's blended buffers. A transition is:
    //   FadingOut  — ramp the outgoing frame down to black
    //   WaitingFrame — pattern swapped + recompute triggered; hold black until
    //                  the new field's frame uploads
    //   FadingIn   — ramp the new field up from black
    private enum AcidFadePhase { None, FadingOut, WaitingFrame, FadingIn }
    private volatile bool _acidAmbientFading;
    private AcidFadePhase _acidAmbientPhase;
    private uint[]? _acidAmbientFadeFrom;   // outgoing frame (FadingOut)
    private uint[]? _acidAmbientFadeTo;     // incoming field (FadingIn)
    private int _acidAmbientFadeW, _acidAmbientFadeH;
    private int _acidAmbientFadeStep;
    private int _acidAmbientWaitTicks;
    private volatile bool _acidAmbientFrameReady;
    private EventHandler? _acidAmbientFrameHandler;
    private int _acidAmbientPendingPattern;
    private const int AcidAmbientFadeSteps = 10;   // ~500 ms at the 50 ms tick
    private const int AcidAmbientWaitTimeoutTicks = 60; // ~3 s recompute guard

    private bool _acidFogAmbientEnabled;
    /// <summary>Toggle the Acid Fog auto-VJ ambient loop. Enabling it also turns
    /// on palette cycling (the loop holds each pattern while the colour cycles).
    /// No-op unless the active type is Acid Fog.</summary>
    public bool AcidFogAmbientEnabled
    {
        get => _acidFogAmbientEnabled;
        set
        {
            if (!this.RaiseAndSetIfChangedReturnsChanged(ref _acidFogAmbientEnabled, value))
                return;
            if (value) StartAcidAmbient();
            else StopAcidAmbient();
        }
    }

    private bool _acidFogAmbientLocked;
    /// <summary>Lock the ambient loop: freeze pattern advancement while the
    /// colour keeps cycling (classic Acid Warp "lock field, cycle colour").</summary>
    public bool AcidFogAmbientLocked
    {
        get => _acidFogAmbientLocked;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _acidFogAmbientLocked, value)
                && _acidAmbientDirector != null)
                _acidAmbientDirector.Locked = value;
        }
    }

    private double _acidFogAmbientHoldSeconds = 6.0;
    /// <summary>Per-pattern hold before the loop auto-advances (seconds).</summary>
    public double AcidFogAmbientHoldSeconds
    {
        get => _acidFogAmbientHoldSeconds;
        set
        {
            double v = Math.Clamp(value, 0.5, 120.0);
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _acidFogAmbientHoldSeconds, v)
                && _acidAmbientDirector != null)
                _acidAmbientDirector.HoldMs = (int)(v * 1000);
        }
    }

    // #262 / Audio-Reactive Phase 3 — beat-lock the auto-VJ loop.
    /// <summary>Host getter for the live audio modulation source. Set by the
    /// bootstrap; null when no audio backend.</summary>
    public Func<FracturingFog.Audio.IAudioModulationSource?>? GetAudioModulationSource { get; set; }

    /// <summary>Host hook to ensure audio capture is running. Set by the bootstrap.</summary>
    public Action? EnsureAudioModulationStarted { get; set; }

    /// <summary>#277 — host hook to re-evaluate audio-capture demand and start or
    /// stop capture accordingly. Idempotent; call after any audio-reactive toggle
    /// (on or off) so a File source doesn't keep playing after toggle-off. Set by
    /// the bootstrap.</summary>
    public Action? ReconcileAudioCapture { get; set; }

    private long _lastAmbientDownbeatSeen;
    private bool _acidFogAmbientBeatSync;
    /// <summary>When true (and the ambient loop is running), pattern advances are
    /// driven by the music's downbeats instead of the hold clock, and the palette
    /// cycle rate tracks the detected BPM. Turning it on spins up audio capture.
    /// A downbeat advance fires even while <see cref="AcidFogAmbientLocked"/>.</summary>
    public bool AcidFogAmbientBeatSync
    {
        get => _acidFogAmbientBeatSync;
        set
        {
            if (!this.RaiseAndSetIfChangedReturnsChanged(ref _acidFogAmbientBeatSync, value))
                return;
            // #277 — start (on) or stop (off) capture per overall demand.
            ReconcileAudioCapture?.Invoke();
            if (value)
            {
                // Baseline the edge counter so we don't fire on stale history.
                _lastAmbientDownbeatSeen = GetAudioModulationSource?.Invoke()?.DownbeatCount ?? 0;
            }
        }
    }

    // #264 / Audio-Reactive Phase 5 — view "breathing" (zoom-pulse + shake).
    private ViewBreatheAnimator? _viewBreathe;
    private bool _audioViewBreathe;
    /// <summary>When true, the view zoom-pulses (and optionally shakes) with the
    /// music: a render-gated <see cref="ViewBreatheAnimator"/> registers on the
    /// shared bus and writes a transient overlay the render host applies per
    /// frame. The base centre / zoom are never mutated, so navigation stays exact.
    /// Turning it on spins up audio capture; off snaps the view back to base.
    /// Shallow-zoom only (suppressed past 1e6).</summary>
    public bool AudioViewBreathe
    {
        get => _audioViewBreathe;
        set
        {
            if (!this.RaiseAndSetIfChangedReturnsChanged(ref _audioViewBreathe, value))
                return;

            // #277 — reconcile capture first: on turns it on (so the source below
            // is live), off stops it when no other consumer still wants audio.
            ReconcileAudioCapture?.Invoke();

            var bus = AnimationBusHost.Bus;
            if (value)
            {
                var src = GetAudioModulationSource?.Invoke();
                if (src != null && bus != null)
                {
                    // Fresh animator each enable so it binds the current source.
                    _viewBreathe = new ViewBreatheAnimator(src, ViewState) { IsEnabled = true };
                    bus.Register(_viewBreathe);
                    bus.Refresh();
                }
            }
            else
            {
                if (_viewBreathe != null && bus != null)
                {
                    _viewBreathe.IsEnabled = false;
                    bus.UnregisterPermanent(_viewBreathe);
                    bus.Refresh();
                }
                _viewBreathe?.ResetOverlay();
                _viewBreathe = null;
                // Snap the view back to the base (no more ticks will fire).
                _renderHost.Trigger();
            }
        }
    }

    /// <summary>Manual "next" — advance to the next pattern now, even when locked.</summary>
    public void AcidFogAmbientNext() => _acidAmbientDirector?.RequestNext();

    private ReactiveCommand<Unit, Unit>? _acidFogAmbientNextCommand;
    /// <summary>Toolbar "Next ▸" binding for the auto-VJ manual advance.</summary>
    public ReactiveCommand<Unit, Unit> AcidFogAmbientNextCommand
        => _acidFogAmbientNextCommand ??= ReactiveCommand.Create(AcidFogAmbientNext);

    private void StartAcidAmbient()
    {
        // Ambient is an Acid Fog behaviour; ignore the toggle on other types.
        if (_selectedFractalType != FractalType.AcidWarp)
        {
            _acidFogAmbientEnabled = false;
            this.RaisePropertyChanged(nameof(AcidFogAmbientEnabled));
            return;
        }

        var playlist = new AcidWarpPlaylist(
            new Random().Next,
            FractalParameters.AcidWarpPatternCount,
            startWithClassic: true);
        _acidAmbientDirector = new AcidWarpAmbientDirector(
            playlist, (int)(_acidFogAmbientHoldSeconds * 1000))
        {
            Locked = _acidFogAmbientLocked,
        };

        // Show the first pattern immediately (no fade for the entry), cycling on.
        var p = ViewState.FractalParameters;
        p.AcidWarpTitleCard = false;
        p.AcidWarpMorph = false;
        p.AcidWarpPattern = _acidAmbientDirector.CurrentPattern;
        PaletteCycleEnabled = true;
        _renderHost.Trigger();

        // #262 — baseline the beat-sync edge counter so the first downbeat after
        // start advances, not a stale one accrued before the loop began.
        if (_acidFogAmbientBeatSync)
        {
            ReconcileAudioCapture?.Invoke();
            _lastAmbientDownbeatSeen = GetAudioModulationSource?.Invoke()?.DownbeatCount ?? 0;
        }

        _acidAmbientLastTick = DateTime.UtcNow;
        _acidAmbientTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnAcidAmbientTick);
        _acidAmbientTimer.Start();
    }

    private void StopAcidAmbient()
    {
        _acidAmbientTimer?.Stop();
        _acidAmbientDirector = null;
        _acidAmbientFading = false;
        _acidAmbientPhase = AcidFadePhase.None;
        _acidAmbientFadeFrom = null;
        _acidAmbientFadeTo = null;
        if (_acidAmbientFrameHandler != null)
        {
            _renderHost.AnimationFrameUploaded -= _acidAmbientFrameHandler;
            _acidAmbientFrameHandler = null;
        }
    }

    private void OnAcidAmbientTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        int dtMs = (int)(now - _acidAmbientLastTick).TotalMilliseconds;
        _acidAmbientLastTick = now;
        if (dtMs <= 0) dtMs = 50;

        // Mid-transition: keep stepping the fade, ignore advance timing.
        if (_acidAmbientFading) { StepAcidFade(); return; }

        var d = _acidAmbientDirector;
        if (d == null) return;

        // #262 — beat-lock: advance on the downbeat and slave the palette cycle
        // rate to BPM, instead of the wall-clock hold. Falls back to the hold
        // clock when audio is inactive so a dropped signal doesn't freeze the loop.
        if (_acidFogAmbientBeatSync
            && GetAudioModulationSource?.Invoke() is { IsActive: true } mod)
        {
            var f = mod.Sample();
            if (f.Bpm > 0)
                PaletteCycleRate = Math.Clamp(f.Bpm / 240.0, 0.05, 2.0);

            long dc = mod.DownbeatCount;
            if (dc > _lastAmbientDownbeatSeen)
            {
                _lastAmbientDownbeatSeen = dc;
                d.RequestNext();                       // advance on the bar
            }
            if (d.Tick(0)) BeginAcidFade(d.CurrentPattern); // dt=0: no hold accrual
            return;
        }

        if (d.Tick(dtMs)) BeginAcidFade(d.CurrentPattern);
    }

    // Snapshot the live frame and begin fading it to black; once black, the
    // pattern swaps in and the recomputed field fades back up (fade-out → hold
    // black for the recompute → fade-in), a symmetric fade-to-black crossfade.
    private void BeginAcidFade(int targetPattern)
    {
        _acidAmbientFadeFrom = _renderHost.SnapshotFrame(out _acidAmbientFadeW, out _acidAmbientFadeH);
        _acidAmbientPendingPattern = targetPattern;
        _acidAmbientFadeStep = 0;
        // No frame to fade (cold view) — swap immediately.
        if (_acidAmbientFadeFrom == null || _acidAmbientFadeFrom.Length == 0
            || _acidAmbientFadeW <= 0 || _acidAmbientFadeH <= 0)
        {
            CommitAcidPattern(targetPattern);
            return;
        }
        _acidAmbientPhase = AcidFadePhase.FadingOut;
        _acidAmbientFading = true;
    }

    private void StepAcidFade()
    {
        switch (_acidAmbientPhase)
        {
            case AcidFadePhase.FadingOut: StepFadeOut(); break;
            case AcidFadePhase.WaitingFrame: StepWaitForFrame(); break;
            case AcidFadePhase.FadingIn: StepFadeIn(); break;
            default: _acidAmbientFading = false; break;
        }
    }

    private void StepFadeOut()
    {
        var from = _acidAmbientFadeFrom;
        int w = _acidAmbientFadeW, h = _acidAmbientFadeH;
        if (from == null || w <= 0 || h <= 0)
        {
            _acidAmbientFading = false;
            _acidAmbientPhase = AcidFadePhase.None;
            CommitAcidPattern(_acidAmbientPendingPattern);
            return;
        }

        _acidAmbientFadeStep++;
        if (_acidAmbientFadeStep >= AcidAmbientFadeSteps)
        {
            // Reached black — swap the pattern in, trigger the recompute, and
            // wait (holding black) for the new field's frame to upload.
            PresentAcidBlack(w, h);
            _acidAmbientFadeFrom = null;
            _acidAmbientFrameReady = false;
            _acidAmbientWaitTicks = 0;
            _acidAmbientFrameHandler = (_, _) =>
            {
                _acidAmbientFrameReady = true;
                if (_acidAmbientFrameHandler != null)
                {
                    _renderHost.AnimationFrameUploaded -= _acidAmbientFrameHandler;
                    _acidAmbientFrameHandler = null;
                }
            };
            _renderHost.AnimationFrameUploaded += _acidAmbientFrameHandler;
            _acidAmbientPhase = AcidFadePhase.WaitingFrame;
            CommitAcidPattern(_acidAmbientPendingPattern);
            return;
        }

        float ia = 1f - _acidAmbientFadeStep / (float)AcidAmbientFadeSteps; // → 0
        _renderHost.PresentBuffer(ScaleAcid(from, w * h, ia), w, h);
    }

    private void StepWaitForFrame()
    {
        _acidAmbientWaitTicks++;
        bool timedOut = _acidAmbientWaitTicks >= AcidAmbientWaitTimeoutTicks;
        if (!_acidAmbientFrameReady && !timedOut)
        {
            // Hold black while the new field computes.
            PresentAcidBlack(_acidAmbientFadeW, _acidAmbientFadeH);
            return;
        }

        // New field is on screen (bright) — grab it as the fade-in target, then
        // cover with black so the fade-in starts from black, not a pop.
        _acidAmbientFadeTo = _renderHost.SnapshotFrame(out _acidAmbientFadeW, out _acidAmbientFadeH);
        if (_acidAmbientFadeTo == null || _acidAmbientFadeW <= 0 || _acidAmbientFadeH <= 0)
        {
            // Nothing to fade in — just resume (the live field is already shown).
            _acidAmbientFading = false;
            _acidAmbientPhase = AcidFadePhase.None;
            return;
        }
        PresentAcidBlack(_acidAmbientFadeW, _acidAmbientFadeH);
        _acidAmbientFadeStep = 0;
        _acidAmbientPhase = AcidFadePhase.FadingIn;
    }

    private void StepFadeIn()
    {
        var to = _acidAmbientFadeTo;
        int w = _acidAmbientFadeW, h = _acidAmbientFadeH;
        if (to == null || w <= 0 || h <= 0)
        {
            _acidAmbientFading = false;
            _acidAmbientPhase = AcidFadePhase.None;
            return;
        }

        _acidAmbientFadeStep++;
        if (_acidAmbientFadeStep >= AcidAmbientFadeSteps)
        {
            // Full brightness — present the field exactly and hand back to the
            // palette-cycle tick (which resumes recolouring from here).
            _renderHost.PresentBuffer(to, w, h);
            _acidAmbientFadeTo = null;
            _acidAmbientFading = false;
            _acidAmbientPhase = AcidFadePhase.None;
            return;
        }

        float a = _acidAmbientFadeStep / (float)AcidAmbientFadeSteps; // 0 → 1
        _renderHost.PresentBuffer(ScaleAcid(to, w * h, a), w, h);
    }

    // Scale RGB toward black by factor f (0..1), preserving opaque alpha.
    private static uint[] ScaleAcid(uint[] src, int n, float f)
    {
        var blend = new uint[src.Length];
        for (int i = 0; i < n; i++)
        {
            uint o = src[i];
            byte r = (byte)(((o >> 16) & 0xFF) * f);
            byte g = (byte)(((o >> 8) & 0xFF) * f);
            byte b = (byte)((o & 0xFF) * f);
            blend[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
        return blend;
    }

    private void PresentAcidBlack(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        var black = new uint[w * h];
        for (int i = 0; i < black.Length; i++) black[i] = 0xFF000000u;
        _renderHost.PresentBuffer(black, w, h);
    }

    private void CommitAcidPattern(int pattern)
    {
        ViewState.FractalParameters.AcidWarpPattern = pattern;
        _renderHost.Trigger();
    }

    public void Dispose()
    {
        _acidIntroTimer?.Stop();
        _acidIntroTimer = null;
        _acidAmbientTimer?.Stop();
        _acidAmbientTimer = null;
        _paletteCycleTimer?.Stop();
        _paletteCycleTimer = null;
        _input.ViewChanged -= OnInputViewChanged;
        _renderHost.FrameCompleted -= OnFrameCompleted;
        _renderHost.StatusRequested -= OnRenderHostStatusRequested;
        _renderHost.ColorMapChanged -= OnRenderHostColorMapChanged;
        _renderHost.RenderCancelled -= OnRenderCancelled;
        _panStopDebounce.Dispose();
        _adaptiveRepaintDebounce.Dispose();
        _fullCoalesceTimer.Dispose();
        _busyReleaseTimer?.Dispose();
    }
}
