// ViewModels/ShellViewModel.cs
//
// Step E of the Phase 2.3 MainForm cut plan. Top-level composition VM the
// Avalonia MainWindow.axaml binds to. Owns:
//
//   • MainViewModel              — state + render-host orchestration
//   • FloatingMenuViewModel      — main floating control panel
//   • Lazy dialog VMs            — ColorThemeEditor, FloatingHelp,
//                                   FractalParams, UserEquation, UserBulb,
//                                   ImagePalette, Sandbox, AudioSettings,
//                                   SlideshowSettings
//
// Host-provided services (constructed by the WinExe bootstrapper and
// passed in):
//
//   • IFractalRenderHost          — the render host that owns the renderer
//                                    and 11 calculators
//   • IFractalInputController     — input controller mutating the view state
//   • IColorThemeService          — bridge to ColorPalette + UserColorThemeLibrary
//   • IPaletteExtractionService   — bridge to BitmapSampler + KMeans / etc.
//   • IHelpContentProvider        — bridge to FloatingHelp's static text +
//                                    DXGI / D3D11 enumeration
//
// The ShellViewModel never touches System.Drawing or Vortice directly;
// it talks only to the interfaces above + the child VMs.

using System;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Threading;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.UI.Avalonia.Slideshow;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly IColorThemeService _themeService;
    private readonly IPaletteExtractionService? _paletteService;
    private readonly IHelpContentProvider _helpProvider;

    /// <summary>True while the host window is in borderless multi-monitor
    /// span mode. Toggled by the FloatingMenu Span button.</summary>
    private bool _isSpanning;

    /// <summary>Avalonia slideshow cycler. Lazily created on first Start.</summary>
    private SlideshowEngine? _slideshow;

    /// <summary>Video Zoom engine — the same concrete object as the render
    /// host (FractalRenderHost implements both IFractalRenderHost and
    /// IVideoZoomController). Null only if the host doesn't implement it.</summary>
    private readonly IVideoZoomController? _video;

    public ShellViewModel(
        IFractalRenderHost renderHost,
        IFractalInputController input,
        IColorThemeService themeService,
        IHelpContentProvider helpProvider,
        IPaletteExtractionService? paletteService = null)
    {
        if (renderHost == null) throw new ArgumentNullException(nameof(renderHost));
        if (input == null) throw new ArgumentNullException(nameof(input));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _helpProvider = helpProvider ?? throw new ArgumentNullException(nameof(helpProvider));
        _paletteService = paletteService;

        Main = new MainViewModel(renderHost, input);
        FloatingMenu = new FloatingMenuViewModel();
        // Hand the menu the theme service so its Region / Theme combos can
        // group + sort + right-click-filter themselves (parity with the
        // WinForms combos). AttachThemeService performs the initial fill.
        FloatingMenu.AttachThemeService(_themeService);
        // Quality combo lives on FloatingMenu but its presets come from
        // QualityPreset.All — the same list MainViewModel already exposes.
        FloatingMenu.SetQualities(QualityPreset.All.Select(q => q.Name));
        FloatingMenu.SetQualitySilent(Main.SelectedQuality?.Name);
        // Dimensions combo population + ResolutionChanged → ResizeRequested
        // is handled by the host bootstrap (UI.Avalonia has no reference to
        // the main project's ResolutionDimensions table).

        // ── Wire FloatingMenu → MainViewModel / ShellViewModel ───────────
        // Region/Theme picks: forward the name into MainViewModel so the
        // toolbar labels mirror the selection, then ask the host service to
        // actually apply (mutate ViewState for a region, push a new IColorMap
        // for a theme). Without these two calls the combos were label-only —
        // user saw no view change and the symptom looked like flaky bindings.
        FloatingMenu.RegionComboChanged += (_, name) => JumpToRegion(name);
        FloatingMenu.ColorThemeChanged  += (_, name) =>
        {
            Main.SetThemeName(name);
            if (string.IsNullOrEmpty(name)) return;
            _themeService.ApplyTheme(name);
            // ApplyTheme already calls RepaintWithPostFx; nothing else needed.
        };
        FloatingMenu.ResetClick        += (_, _) => Main.ResetViewCommand.Execute().Subscribe();
        FloatingMenu.HelpClick         += (_, _) => ShowHelp();
        FloatingMenu.EditThemeClick    += (_, _) => ShowColorThemeEditor();
        FloatingMenu.ServerClick       += (_, _) => ShowServerAdmin();
        FloatingMenu.ClientClick       += (_, _) => ShowFFClient();
        FloatingMenu.BrightnessSlide   += (_, v) => Main.Brightness = v;
        FloatingMenu.ContrastSlide     += (_, v) => Main.Contrast = v;
        FloatingMenu.AdaptiveSlide     += (_, v) => Main.Adaptive = v;

        // ── Newly-wired controls (#53) ───────────────────────────────────
        // Close menu — flip the visibility flag the MainWindow binds to.
        FloatingMenu.CloseClick        += (_, _) => IsFloatingMenuVisible = false;

        // Close program — bubble up so the host (bootstrap) can shut the
        // application down through the right Avalonia lifetime API.
        FloatingMenu.CloseProgramClick += (_, _) => CloseProgramRequested?.Invoke(this, EventArgs.Empty);

        // Grid checkbox in the menu mirrors the toolbar toggle.
        FloatingMenu.GridToggled       += (_, v) => Main.ShowGrid = v;

        // Status-bar visibility flag the MainWindow status row binds to.
        FloatingMenu.StatusBarToggled  += (_, v) => IsStatusBarVisible = v;

        // Copy CX / CY / Zoom / Iter to system clipboard via the host so
        // UI.Avalonia stays free of TopLevel.Clipboard plumbing here.
        FloatingMenu.CopyCoordsClick   += (_, _) =>
        {
            string text = FormatCoords(Main.ViewState);
            CopyToClipboardRequested?.Invoke(this, text);
        };

        // Save / Delete current region: bubble up so the host can pop a
        // small name-prompt + confirmation modal, then ask IColorThemeService
        // to persist. Host signals back via the args.Completion TCS pattern
        // so the editor never blocks the dispatcher.
        FloatingMenu.SaveViewClick     += (_, _) => TriggerSaveView();
        FloatingMenu.DeleteRegionClick += (_, _) =>
        {
            if (string.IsNullOrEmpty(Main.SelectedRegion)) return;
            var args = new ThemeMessageEventArgs(
                "Delete Region",
                $"Delete user region \"{Main.SelectedRegion}\"? This cannot be undone.",
                MessageSeverity.Question)
            { ExpectsConfirmation = true };
            DeleteRegionRequested?.Invoke(this, (args, Main.SelectedRegion!));
        };

        // Reload themes — pull current names back from the service in case
        // the user edited the JSON file underneath us.
        FloatingMenu.ReloadThemesClick += (_, _) =>
        {
            FloatingMenu.RefreshThemes();
            FloatingMenu.RefreshRegions();
        };

        // Quality combo on the menu drives MainViewModel; MainViewModel's
        // SelectedQuality setter calls Trigger() so a quality change kicks
        // a fresh render.
        FloatingMenu.QualityChanged    += (_, name) =>
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                var q = QualityPreset.FromName(name);
                if (q != null) Main.SelectedQuality = q;
            }
            catch { /* unknown quality name — ignore */ }
        };

        // "Go" button: parse the four coord textboxes and apply.
        FloatingMenu.GoClick           += (_, _) => ApplyCoordsFromMenu();

        // Iteration lock toggle in the menu maps onto MainViewModel state.
        FloatingMenu.IterLockChanged   += (_, e) =>
        {
            Main.IterLocked = e.Locked;
            if (e.Locked && e.CurrentIter > 0) Main.LockedIterations = e.CurrentIter;
        };

        // Screenshot — host saves the most-recent BGRA buffer to disk.
        FloatingMenu.ScreenshotClick   += (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty);

        // Export / Import user regions — host pops a file picker then asks
        // IColorThemeService to serialize / merge. After an import the host
        // refreshes the region combo so new entries show without a restart.
        FloatingMenu.ExportRegionsClick += (_, _) => ExportRegionsRequested?.Invoke(this, EventArgs.Empty);
        FloatingMenu.ImportRegionsClick += (_, _) => ImportRegionsRequested?.Invoke(this, EventArgs.Empty);

        // Flip — mirror the view across the real axis by negating every CY
        // limb (Hi + 3 low limbs) so deep-zoom precision survives. Re-parsing
        // the textbox would drop the low limbs, so we mutate the view state
        // directly and retrigger.
        FloatingMenu.FlipClick         += (_, _) => FlipVertical();

        // Slideshow settings — host pops the ported Avalonia dialog seeded
        // from the persisted SlideshowSettings, then writes back on OK.
        FloatingMenu.SlideshowSettingsClick += (_, _) => SlideshowSettingsRequested?.Invoke(this, EventArgs.Empty);

        // Export / Import / Delete user colour themes — same shape as the
        // region IO above. Export/Import bubble to a file picker on the host;
        // Delete confirms against the currently-selected theme then asks the
        // service. Built-in themes aren't deletable (service returns false).
        FloatingMenu.ExportThemeClick += (_, _) => ExportThemesRequested?.Invoke(this, EventArgs.Empty);
        FloatingMenu.ImportThemeClick += (_, _) => ImportThemesRequested?.Invoke(this, EventArgs.Empty);
        FloatingMenu.DeleteThemeClick += (_, _) =>
        {
            if (string.IsNullOrEmpty(Main.SelectedTheme)) return;
            var args = new ThemeMessageEventArgs(
                "Delete Theme",
                $"Delete user theme \"{Main.SelectedTheme}\"? This cannot be undone.",
                MessageSeverity.Question)
            { ExpectsConfirmation = true };
            DeleteThemeRequested?.Invoke(this, (args, Main.SelectedTheme!));
        };

        // Span — toggle borderless multi-monitor fullscreen. This VM owns the
        // intent + button label; the host owns the actual Window geometry
        // (SystemDecorations / position / size) and restores it on exit.
        FloatingMenu.SpanClick += (_, _) =>
        {
            _isSpanning = !_isSpanning;
            FloatingMenu.SpanButtonText = _isSpanning ? "Back" : "Span";
            SpanToggleRequested?.Invoke(this, _isSpanning);
        };

        // Poster — host pops the poster-size dialog, then runs the shared
        // PosterRenderer offscreen at the chosen resolution and saves to disk.
        FloatingMenu.PosterClick += (_, _) => PosterRequested?.Invoke(this, EventArgs.Empty);

        // Slideshow — toggle the Avalonia cycler (region + theme hard-cuts).
        // The ported VCR panel drives pause / skip / stop while it runs.
        // The VCR transport is shared between the native region/theme cycler
        // and the video slideshow. Each handler routes to whichever is active:
        // the video controller takes precedence when its slideshow is running
        // (Stop ends the run; SkipRegion/SkipTheme both advance the leg; the
        // video slideshow has no pause).
        SlideshowVcr = new SlideshowVcrViewModel();
        SlideshowVcr.PlayPauseClicked += (_, _) =>
        {
            if (_video is { IsSlideshowRunning: true }) return;
            _slideshow?.TogglePause();
            SlideshowVcr.SetPaused(_slideshow?.IsPaused ?? false);
        };
        SlideshowVcr.StopClicked += (_, _) =>
        {
            if (_video is { IsSlideshowRunning: true }) _video.Stop();
            else _slideshow?.Stop();
        };
        SlideshowVcr.SkipRegionClicked += (_, _) =>
        {
            if (_video is { IsSlideshowRunning: true }) _video.SkipLeg();
            else _slideshow?.SkipRegion();
        };
        SlideshowVcr.SkipThemeClicked += (_, _) =>
        {
            if (_video is { IsSlideshowRunning: true }) _video.SkipLeg();
            else _slideshow?.SkipTheme();
        };

        FloatingMenu.SlideshowClick += (_, _) => ToggleSlideshow();

        // ── Video Zoom (#64) ─────────────────────────────────────────────
        // The Video button toggles: while a single-shot zoom or the video
        // slideshow runs, it stops; otherwise it asks the host to pop the
        // dialog (host owns ShowVideoAsync — main-project FormHelpers / region
        // library / ffmpeg lookups). Engine events fire on a background thread,
        // so every VM mutation is marshalled to the UI thread.
        _video = renderHost as IVideoZoomController;
        FloatingMenu.VideoClick += (_, _) =>
        {
            if (_video is { IsRunning: true }) _video.Stop();
            else VideoRequested?.Invoke(this, EventArgs.Empty);
        };
        if (_video != null)
        {
            _video.StatusChanged += (_, text) =>
                Dispatcher.UIThread.Post(() => Main.SetStatus(text));
            _video.Stopped += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                {
                    FloatingMenu.VideoButtonText = "Video";
                    IsSlideshowVcrVisible = false;
                });
        }

        // FrameCompleted: refresh the menu's CX/CY/Zoom/Iter textboxes so
        // the user sees the live values without typing them manually. Skips
        // whichever box currently has focus — that's owned by ViewModelBase
        // consumers in the View layer; for now we just always overwrite.
        Main.RenderHost.FrameCompleted += (_, info) =>
        {
            // Surface DD/QD limbs in the menu as Hi|Lo[|Lo2|Lo3] when the
            // view state carries any non-zero low limb. The textbox already
            // accepts the same format for input, so copy-paste round-trips
            // a deep-zoom region without losing precision.
            var s = Main.ViewState;
            FloatingMenu.UpdateCoords(
                FormatLimbs(s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3),
                FormatLimbs(s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3),
                info.Zoom.ToString("G6", CultureInfo.InvariantCulture),
                info.Iterations.ToString(CultureInfo.InvariantCulture));
        };

        ShowFloatingMenuCommand   = ReactiveCommand.Create(() => IsFloatingMenuVisible = !IsFloatingMenuVisible);
        ShowHelpCommand           = ReactiveCommand.Create(ShowHelp);
        ShowColorThemeEditorCommand = ReactiveCommand.Create(ShowColorThemeEditor);
        ShowFractalParamsCommand  = ReactiveCommand.Create(
            () => FractalParamsRequested?.Invoke(this, EventArgs.Empty));

        // Context-menu commands. Toolbar / status / grid / watermark are
        // simple flag flips; the rest delegate to the existing private
        // handlers + event raisers so a right-click reaches the same code
        // as the FloatingMenu buttons.
        ToggleToolbarCommand   = ReactiveCommand.Create(() => IsToolbarVisible = !IsToolbarVisible);
        ToggleStatusBarCommand = ReactiveCommand.Create(() => IsStatusBarVisible = !IsStatusBarVisible);
        ToggleGridCommand      = ReactiveCommand.Create(() => Main.ShowGrid = !Main.ShowGrid);
        ToggleWatermarkCommand = ReactiveCommand.Create(() => Main.ShowWatermark = !Main.ShowWatermark);
        ToggleSpanCommand      = ReactiveCommand.Create(() =>
        {
            _isSpanning = !_isSpanning;
            FloatingMenu.SpanButtonText = _isSpanning ? "Back" : "Span";
            SpanToggleRequested?.Invoke(this, _isSpanning);
        });
        ToggleSlideshowCommand = ReactiveCommand.Create(ToggleSlideshow);
        ToggleVideoCommand     = ReactiveCommand.Create(() =>
        {
            if (_video is { IsRunning: true }) _video.Stop();
            else VideoRequested?.Invoke(this, EventArgs.Empty);
        });
        SaveRegionCommand      = ReactiveCommand.Create(TriggerSaveView);
        ScreenshotCommand      = ReactiveCommand.Create(
            () => ScreenshotRequested?.Invoke(this, EventArgs.Empty));

        // Slideshow control commands (right-click context menu). The
        // checkbox/text state for the items is read off SlideshowLockRegion
        // + SlideshowFocusRegion at menu-open time.
        ToggleSlideshowLockRegionCommand = ReactiveCommand.Create(() =>
        {
            SlideshowLockRegion = !SlideshowLockRegion;
        });
        ToggleMiniMapCommand = ReactiveCommand.Create(() => IsMiniMapVisible = !IsMiniMapVisible);
        ToggleMiniDepthCommand = ReactiveCommand.Create(() => IsMiniDepthVisible = !IsMiniDepthVisible);
        ToggleMiniModeCommand  = ReactiveCommand.Create(() => IsMiniMode = !IsMiniMode);

        // Push live view-state into the MiniMap VM on every frame so the
        // indicator tracks the user's pan/zoom. Mirrors legacy MainForm's
        // _miniMapPanel.RefreshIndicator() call sites.
        Main.RenderHost.FrameCompleted += (_, info) =>
        {
            MiniMap.ActiveType = Main.ViewState.FractalType;
            MiniMap.CenterX = info.CenterX;
            MiniMap.CenterY = info.CenterY;
            MiniMap.HostZoom = info.Zoom;
        };
        MiniMap.NavigationRequested += (_, pt) =>
        {
            var s = Main.ViewState;
            s.CenterX = pt.X; s.CenterXLo = 0; s.CenterX2 = 0; s.CenterX3 = 0;
            s.CenterY = pt.Y; s.CenterYLo = 0; s.CenterY2 = 0; s.CenterY3 = 0;
            Main.RenderHost.Trigger();
        };
        ToggleSlideshowFocusCommand = ReactiveCommand.Create(() =>
        {
            SlideshowFocusRegion = !SlideshowFocusRegion;
        });
    }

    private static string FormatCoords(FracturingFog.ViewState.FractalViewState s)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "CX = {0:G12}\nCY = {1:G12}\nZoom = {2:G6}",
            s.CenterX, s.CenterY, s.Zoom);
    }

    /// <summary>Mirror the view across the real axis: negate all four CY
    /// limbs so deep-zoom precision survives, mirror the menu CY textbox,
    /// then retrigger. No-op when already on the axis.</summary>
    private void FlipVertical()
    {
        var s = Main.ViewState;
        if (s.CenterY == 0.0 && s.CenterYLo == 0.0 && s.CenterY2 == 0.0 && s.CenterY3 == 0.0)
            return;

        s.CenterY  = -s.CenterY;
        s.CenterYLo = -s.CenterYLo;
        s.CenterY2 = -s.CenterY2;
        s.CenterY3 = -s.CenterY3;

        FloatingMenu.CY = s.CenterY.ToString("G12", CultureInfo.InvariantCulture);
        Main.RenderHost.Trigger();
    }

    /// <summary>Route a command-level keyboard shortcut forwarded by the
    /// window (M / T / R / V / Escape). Pan/zoom/3D-camera keys are owned by
    /// the input controller; these UI commands have no home there, so the
    /// window hands them here. Mirrors the universal shortcuts from the
    /// WinForms <c>MainForm.OnKeyDown</c>. Returns true if consumed.</summary>
    public bool HandleCommandKey(InputKey key)
    {
        switch (key)
        {
            case InputKey.M:                       // toggle floating menu
                IsFloatingMenuVisible = !IsFloatingMenuVisible;
                return true;
            case InputKey.T:                       // open colour-theme editor
                ShowColorThemeEditor();
                return true;
            case InputKey.R:                       // reset view
                Main.ResetViewCommand.Execute().Subscribe();
                return true;
            case InputKey.V:                       // save current view as region
                TriggerSaveView();
                return true;
            case InputKey.Escape:                  // exit span, else stop a run
                if (_isSpanning)
                {
                    _isSpanning = false;
                    FloatingMenu.SpanButtonText = "Span";
                    SpanToggleRequested?.Invoke(this, false);
                    return true;
                }
                if (_video is { IsRunning: true }) { _video.Stop(); return true; }
                if (_slideshow is { IsRunning: true }) { _slideshow.Stop(); return true; }
                return false;
        }
        return false;
    }

    /// <summary>Bubble a "save current view as a named region" request up to
    /// the host (which pops the name-prompt modal). Shared by the FloatingMenu
    /// Save-View button and the V keyboard shortcut.</summary>
    private void TriggerSaveView()
    {
        var args = new ThemeMessageEventArgs(
            "Save View as Region",
            "Enter a name for this region (cancel to abort).",
            MessageSeverity.Question)
        { ExpectsConfirmation = true };
        SaveRegionRequested?.Invoke(this, args);
    }

    /// <summary>Start or stop the Avalonia slideshow cycler. Shows / hides the
    /// VCR panel and lazily constructs the engine on first run.</summary>
    private void ToggleSlideshow()
    {
        if (_slideshow is { IsRunning: true })
        {
            _slideshow.Stop();
            return;
        }

        if (_slideshow == null)
        {
            _slideshow = new SlideshowEngine(Main.RenderHost, _themeService, new SlideshowSettings())
            {
                LockRegion = _slideshowLockRegion,
                FocusRegion = _slideshowFocusRegion,
            };
            _slideshow.Stopped += (_, _) =>
            {
                IsSlideshowVcrVisible = false;
                this.RaisePropertyChanged(nameof(IsSlideshowRunning));
            };
            // Mirror engine-driven region jumps into the toolbar combos so the
            // displayed region name + quality preset match what's actually
            // being rendered (and what future region saves will capture).
            _slideshow.RegionApplied += (_, regionName) => Dispatcher.UIThread.Post(() =>
            {
                Main.SetRegionName(regionName);
                Main.SetFractalTypeSilent(Main.ViewState.FractalType);
                Main.SetQualitySilent(Main.ViewState.Quality);
                FloatingMenu.SetRegionSilent(regionName);
                FloatingMenu.SetQualitySilent(Main.SelectedQuality?.Name);
            });
            _slideshow.ThemeApplied += (_, themeName) => Dispatcher.UIThread.Post(() =>
            {
                Main.SetThemeName(themeName);
                FloatingMenu.SetThemeSilent(themeName);
            });
        }

        SlideshowVcr.SetPaused(false);
        IsSlideshowVcrVisible = true;
        _slideshow.Start();
        this.RaisePropertyChanged(nameof(IsSlideshowRunning));
    }

    private void ApplyCoordsFromMenu()
    {
        bool changed = false;
        // Coord fields accept pipe-separated limbs so deep-zoom regions
        // (Hi, Lo, Lo2, Lo3 in DD/QD format) can be pasted in directly:
        //   "-1.9918151296901943|-7.821983681126658E-17"
        // A single value (no pipe) sets the Hi limb and zeros the rest.
        if (TryParseLimbs(FloatingMenu.CX, out double cxHi, out double cxLo, out double cxL2, out double cxL3))
        {
            Main.ViewState.CenterX = cxHi;
            Main.ViewState.CenterXLo = cxLo; Main.ViewState.CenterX2 = cxL2; Main.ViewState.CenterX3 = cxL3;
            changed = true;
        }
        if (TryParseLimbs(FloatingMenu.CY, out double cyHi, out double cyLo, out double cyL2, out double cyL3))
        {
            Main.ViewState.CenterY = cyHi;
            Main.ViewState.CenterYLo = cyLo; Main.ViewState.CenterY2 = cyL2; Main.ViewState.CenterY3 = cyL3;
            changed = true;
        }
        if (double.TryParse(FloatingMenu.Zoom, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom)
            && zoom > 0)
        {
            Main.ViewState.Zoom = zoom;
            changed = true;
        }
        if (int.TryParse(FloatingMenu.Iter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iter)
            && iter > 0 && Main.IterLocked)
        {
            // "Go" never enables the lock (parity with legacy OnGoClick); it
            // only refreshes the held iteration count when the lock is already
            // on. When unlocked the render stays adaptive — flip the lock
            // checkbox to pin a fixed count.
            Main.LockedIterations = iter;
            changed = true;
        }
        if (changed) Main.RenderHost.Trigger();
    }

    // Display DD/QD limbs as a single high-precision decimal string by
    // default (UI-gap #16) — far more readable than the pipe-delimited limb
    // format, and round-trips through `TryParseLimbs` because that parser
    // still accepts long decimals. Pipe-delimited input remains supported on
    // paste / manual entry, so external tools that emit "Hi|Lo|Lo2|Lo3" keep
    // working.
    //
    // Sum limbs in `decimal` (~28-29 sig digits, exact double conversion).
    // This covers a full DD limb pair (Hi+Lo, ~31 digits) reliably; the L2/L3
    // tail is still summed but precision past 28 digits is lost — the same
    // limit that bounds the pipe-format paste path. Falls back to the limb
    // string when any limb is outside decimal range (e.g. denormals beyond
    // ±7.9e28) so we never lose information silently.
    private static string FormatLimbs(double hi, double lo, double l2, double l3)
    {
        // Pick the highest non-zero limb so the format never carries trailing
        // zero limbs (avoids surfacing meaningless precision for shallow zooms).
        int n = 1;
        if (l3 != 0.0) n = 4;
        else if (l2 != 0.0) n = 3;
        else if (lo != 0.0) n = 2;

        try
        {
            decimal acc = (decimal)hi;
            if (n >= 2) acc += (decimal)lo;
            if (n >= 3) acc += (decimal)l2;
            if (n >= 4) acc += (decimal)l3;
            // "G29" prints up to decimal's full 29-digit precision without
            // scientific notation for everyday Mandelbrot coords.
            return acc.ToString("G29", CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            // Fall through to the pipe-delimited path so no precision is lost.
        }

        string h = hi.ToString("G17", CultureInfo.InvariantCulture);
        if (n == 1) return h;
        string p1 = lo.ToString("G17", CultureInfo.InvariantCulture);
        if (n == 2) return $"{h}|{p1}";
        string p2 = l2.ToString("G17", CultureInfo.InvariantCulture);
        if (n == 3) return $"{h}|{p1}|{p2}";
        string p3 = l3.ToString("G17", CultureInfo.InvariantCulture);
        return $"{h}|{p1}|{p2}|{p3}";
    }

    // Parse a coordinate field. Accepts three input shapes (UI-gap #16):
    //   1. Pipe-delimited limbs:  "Hi|Lo|Lo2|Lo3"  (any 1–4 segments)
    //   2. Plain numeric:         "-1.99181512969"  → Hi only
    //   3. Long decimal string:   "-1.9918151296901943521..." (> ~17 sig digits)
    //      decoded into Hi/Lo (and Lo2/Lo3 when input is precise enough) so
    //      pasting an external high-precision coordinate doesn't truncate to
    //      double precision. .NET `decimal` carries ~28 sig digits, which
    //      covers a full DD limb pair (Hi+Lo, ~31 digits) reliably; Lo2/Lo3
    //      capture whatever precision is still in the decimal residual.
    // Missing limbs default to zero. Returns true when at least Hi parsed.
    private static bool TryParseLimbs(string? s, out double hi, out double lo, out double l2, out double l3)
    {
        hi = lo = l2 = l3 = 0.0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('|');
        if (parts.Length > 1)
        {
            // Pipe-delimited (legacy) — each segment is a plain double.
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hi))
                return false;
            if (parts.Length > 1) double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lo);
            if (parts.Length > 2) double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l2);
            if (parts.Length > 3) double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l3);
            return true;
        }

        string single = parts[0].Trim();

        // Long high-precision string path: peel into limbs via `decimal`.
        // `decimal` parsing rounds at ~28-29 sig digits rather than failing,
        // so even strings longer than that produce a sensible Hi/Lo split.
        if (decimal.TryParse(single, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal m))
        {
            hi = (double)m;
            try { m -= (decimal)hi; } catch (OverflowException) { return true; }
            lo = (double)m;
            try { m -= (decimal)lo; } catch (OverflowException) { return true; }
            l2 = (double)m;
            try { m -= (decimal)l2; } catch (OverflowException) { return true; }
            l3 = (double)m;
            return true;
        }

        // Fallback: plain double for inputs outside `decimal` range
        // (NaN, infinity, magnitudes above 7.9e28, etc.).
        return double.TryParse(single, NumberStyles.Float, CultureInfo.InvariantCulture, out hi);
    }

    public MainViewModel Main { get; }
    public FloatingMenuViewModel FloatingMenu { get; }

    /// <summary>VCR transport for the running slideshow. Shown only while
    /// <see cref="IsSlideshowVcrVisible"/> is true.</summary>
    public SlideshowVcrViewModel SlideshowVcr { get; }

    private bool _isSlideshowVcrVisible;
    public bool IsSlideshowVcrVisible
    {
        get => _isSlideshowVcrVisible;
        set => this.RaiseAndSetIfChanged(ref _isSlideshowVcrVisible, value);
    }

    // ── Lazy dialog VMs ───────────────────────────────────────────────────

    private ColorThemeEditorViewModel? _colorThemeEditor;
    public ColorThemeEditorViewModel? ColorThemeEditor
    {
        get => _colorThemeEditor;
        private set => this.RaiseAndSetIfChanged(ref _colorThemeEditor, value);
    }

    private FloatingHelpViewModel? _help;
    public FloatingHelpViewModel? Help
    {
        get => _help;
        private set => this.RaiseAndSetIfChanged(ref _help, value);
    }

    // ── Phase 3 dialogs ──────────────────────────────────────────────────

    private FFClientViewModel? _ffClient;
    public FFClientViewModel? FFClient
    {
        get => _ffClient;
        private set => this.RaiseAndSetIfChanged(ref _ffClient, value);
    }

    private ServerAdminViewModel? _serverAdmin;
    public ServerAdminViewModel? ServerAdmin
    {
        get => _serverAdmin;
        private set => this.RaiseAndSetIfChanged(ref _serverAdmin, value);
    }

    // ── Window visibility flags (bound to Window.IsVisible) ──────────────

    private bool _isFloatingMenuVisible;
    public bool IsFloatingMenuVisible
    {
        get => _isFloatingMenuVisible;
        set => this.RaiseAndSetIfChanged(ref _isFloatingMenuVisible, value);
    }

    private bool _isColorThemeEditorVisible;
    public bool IsColorThemeEditorVisible
    {
        get => _isColorThemeEditorVisible;
        set => this.RaiseAndSetIfChanged(ref _isColorThemeEditorVisible, value);
    }

    private bool _isHelpVisible;
    public bool IsHelpVisible
    {
        get => _isHelpVisible;
        set => this.RaiseAndSetIfChanged(ref _isHelpVisible, value);
    }

    private bool _isStatusBarVisible = true;
    /// <summary>Bound to the MainWindow status row's IsVisible. Toggled by
    /// the Status checkbox on FloatingMenu.</summary>
    public bool IsStatusBarVisible
    {
        get => _isStatusBarVisible;
        set => this.RaiseAndSetIfChanged(ref _isStatusBarVisible, value);
    }

    private bool _isToolbarVisible = true;
    /// <summary>Bound to the MainWindow toolbar row's IsVisible. Toggled by
    /// the Toolbar context-menu item.</summary>
    public bool IsToolbarVisible
    {
        get => _isToolbarVisible;
        set => this.RaiseAndSetIfChanged(ref _isToolbarVisible, value);
    }

    private bool _isFFClientVisible;
    public bool IsFFClientVisible
    {
        get => _isFFClientVisible;
        set => this.RaiseAndSetIfChanged(ref _isFFClientVisible, value);
    }

    private bool _isServerAdminVisible;
    public bool IsServerAdminVisible
    {
        get => _isServerAdminVisible;
        set => this.RaiseAndSetIfChanged(ref _isServerAdminVisible, value);
    }

    // ── Window title (program name + version + renderer description) ────
    // Mirrors legacy MainForm: "{ProgramName} v{ProgramVersion} — {renderer}".
    // Bootstrap sets ProgramName/ProgramVersion from HostHelpContentProvider
    // (which reads assembly version), then composes the renderer suffix once
    // the IFractalRenderer is up.

    private string _programName = "Fracturing Fog";
    public string ProgramName
    {
        get => _programName;
        set { this.RaiseAndSetIfChanged(ref _programName, value); RebuildWindowTitle(); }
    }

    private string _programVersion = "";
    public string ProgramVersion
    {
        get => _programVersion;
        set { this.RaiseAndSetIfChanged(ref _programVersion, value); RebuildWindowTitle(); }
    }

    private string _rendererDescription = "";
    public string RendererDescription
    {
        get => _rendererDescription;
        set { this.RaiseAndSetIfChanged(ref _rendererDescription, value); RebuildWindowTitle(); }
    }

    private string _windowTitle = "Fracturing Fog";
    public string WindowTitle
    {
        get => _windowTitle;
        private set => this.RaiseAndSetIfChanged(ref _windowTitle, value);
    }

    private void RebuildWindowTitle()
    {
        string ver = string.IsNullOrEmpty(_programVersion) ? "" : $" v{_programVersion}";
        string ren = string.IsNullOrEmpty(_rendererDescription) ? "" : $"  —  {_rendererDescription}";
        WindowTitle = $"{_programName}{ver}{ren}";
    }

    // ── Local server indicator (status bar dot) ──────────────────────────

    private string _localServerIndicator = "● Server: off";
    public string LocalServerIndicator
    {
        get => _localServerIndicator;
        set => this.RaiseAndSetIfChanged(ref _localServerIndicator, value);
    }

    private string _localServerBrush = "#666666";
    public string LocalServerBrush
    {
        get => _localServerBrush;
        set => this.RaiseAndSetIfChanged(ref _localServerBrush, value);
    }

    private DispatcherTimer? _serverPingTimer;
    public void StartServerPing(int defaultPort)
    {
        if (_serverPingTimer != null) return;
        // Async probe — the sync overload would Wait(500ms) on the
        // dispatcher every tick when the server is down, freezing the
        // UI thread.
        _serverPingTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) =>
        {
            bool up = await FracturingFog.Server.ServerInstanceProbe.IsListeningAsync("127.0.0.1", defaultPort).ConfigureAwait(true);
            LocalServerIndicator = up ? $"● Server: running ({defaultPort})" : "● Server: off";
            LocalServerBrush = up ? "#5DD27B" : "#666666";
        });
        _serverPingTimer.Start();
        // Fire one immediate probe so the indicator isn't grey for 5 s on launch.
        _ = Task.Run(async () =>
        {
            bool up0 = await FracturingFog.Server.ServerInstanceProbe.IsListeningAsync("127.0.0.1", defaultPort).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                LocalServerIndicator = up0 ? $"● Server: running ({defaultPort})" : "● Server: off";
                LocalServerBrush = up0 ? "#5DD27B" : "#666666";
            });
        });
    }

    // ── Top-level commands ────────────────────────────────────────────────

    public ReactiveCommand<Unit, bool> ShowFloatingMenuCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHelpCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowColorThemeEditorCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFractalParamsCommand { get; }

    // Context-menu commands (right-click on render surface).
    public ReactiveCommand<Unit, bool> ToggleToolbarCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleStatusBarCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleGridCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleWatermarkCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSpanCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSlideshowCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleVideoCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveRegionCommand { get; }
    public ReactiveCommand<Unit, Unit> ScreenshotCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSlideshowLockRegionCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleSlideshowFocusCommand { get; }

    private bool _slideshowLockRegion;
    /// <summary>Mirror of SlideshowEngine.LockRegion — when true the cycler
    /// pins the current region and rotates only themes. Setter forwards to
    /// the engine when running so toggles take effect mid-slideshow.</summary>
    public bool SlideshowLockRegion
    {
        get => _slideshowLockRegion;
        set
        {
            this.RaiseAndSetIfChanged(ref _slideshowLockRegion, value);
            if (_slideshow != null) _slideshow.LockRegion = value;
        }
    }

    private bool _slideshowFocusRegion;
    /// <summary>Mirror of SlideshowEngine.FocusRegion — true = "More Regions"
    /// (1 theme/region), false = "More Colors" (default 3 themes/region).</summary>
    public bool SlideshowFocusRegion
    {
        get => _slideshowFocusRegion;
        set
        {
            this.RaiseAndSetIfChanged(ref _slideshowFocusRegion, value);
            if (_slideshow != null) _slideshow.FocusRegion = value;
        }
    }

    /// <summary>True while the Avalonia slideshow cycler is running. Drives
    /// enable state for the slideshow-specific context-menu items.</summary>
    public bool IsSlideshowRunning => _slideshow is { IsRunning: true };

    // ── MiniMap overlay (UI-gap #10) ─────────────────────────────────────
    // The MiniMap VM holds the thumbnail bitmap + the current view centre/
    // zoom so the indicator paints over the right pixel. The host renders
    // the thumbnail offscreen (see AvaloniaShellBootstrap.RenderMiniMap)
    // and pushes it in via SetThumbnail.
    public MiniMapViewModel MiniMap { get; } = new();

    private bool _isMiniMapVisible;
    public bool IsMiniMapVisible
    {
        get => _isMiniMapVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMiniMapVisible, value);
            if (value) MiniMapVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Fires when MiniMap is shown so the host can kick a thumbnail
    /// render. Host watches via FrameCompleted for ongoing centre/zoom
    /// updates after the initial render.</summary>
    public event EventHandler? MiniMapVisibilityChanged;

    public ReactiveCommand<Unit, bool> ToggleMiniMapCommand { get; private set; } = null!;

    // ── MiniDepth overlay (UI-gap #11) ──────────────────────────────────
    private bool _isMiniDepthVisible;
    public bool IsMiniDepthVisible
    {
        get => _isMiniDepthVisible;
        set => this.RaiseAndSetIfChanged(ref _isMiniDepthVisible, value);
    }

    public ReactiveCommand<Unit, bool> ToggleMiniDepthCommand { get; private set; } = null!;

    /// <summary>Host-supplied palette sampler. Returns the packed ARGB color
    /// for a smooth-iteration index against the active IColorMap. Used by
    /// MiniDepthControl to draw a theme-coloured gradient strip. Bootstrap
    /// sets this once at startup; null means MiniDepth falls back to the
    /// built-in HSV ramp.</summary>
    public Func<int, uint>? SamplePaletteColor { get; set; }

    /// <summary>Host-supplied current swatch colour (packed ARGB). MiniDepth
    /// uses it to pick a high-contrast indicator colour over the gradient.</summary>
    public Func<uint>? GetCurrentSwatchArgb { get; set; }

    // ── Mini Mode (UI-gap #12) ──────────────────────────────────────────
    // Mini Mode shrinks the host window to a small borderless always-on-top
    // panel that keeps the fractal visible while the user works elsewhere.
    // Toolbar + status bar are hidden; prior window geometry restores on
    // exit. The host (MainWindow code-behind) owns the actual Window
    // mutation — ShellViewModel just signals via MiniModeToggleRequested
    // so UI.Avalonia stays free of Window.WindowState/Decorations APIs.
    private bool _isMiniMode;
    public bool IsMiniMode
    {
        get => _isMiniMode;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _isMiniMode, value))
                MiniModeToggleRequested?.Invoke(this, value);
        }
    }

    /// <summary>Fires when the user toggles Mini Mode. Bool payload is the
    /// target state — true to enter mini mode (shrink + borderless +
    /// topmost), false to restore the prior geometry.</summary>
    public event EventHandler<bool>? MiniModeToggleRequested;

    public ReactiveCommand<Unit, bool> ToggleMiniModeCommand { get; private set; } = null!;

    /// <summary>Apply a region jump: relabel the watermark, mutate ViewState
    /// via the host service, mirror the resulting fractal type into the toolbar
    /// (without snapping its centre/zoom), then trigger a render. Shared by the
    /// FloatingMenu region combo and the Color Theme Editor's region pick so
    /// both paths actually move the view instead of only relabelling it.</summary>
    private void JumpToRegion(string? name)
    {
        Main.SetRegionName(name);
        if (string.IsNullOrEmpty(name)) return;
        if (_themeService.ApplyRegion(name, Main.ViewState))
        {
            // ApplyRegion sets ViewState.FractalType directly (it owns the
            // region's centre/zoom, so it bypasses the SelectedFractalType
            // setter which would SnapToFractalDefault and clobber them).
            // Mirror the type into the toolbar combo without snapping.
            Main.SetFractalTypeSilent(Main.ViewState.FractalType);
            // Regions with a saved QualityPreset overwrite ViewState.Quality
            // in ApplyRegion. Mirror that into the toolbar + FloatingMenu
            // Quality combos so the UI doesn't drift out of sync with the
            // value future saves (poster / region) will actually use.
            Main.SetQualitySilent(Main.ViewState.Quality);
            FloatingMenu.SetQualitySilent(Main.SelectedQuality?.Name);
            Main.RenderHost.Trigger();
        }
    }

    private void ShowColorThemeEditor()
    {
        if (ColorThemeEditor == null)
        {
            var vm = new ColorThemeEditorViewModel(_themeService,
                initialThemeName: Main.SelectedTheme,
                initialRegionName: Main.SelectedRegion);
            // Wire editor events that affect the main view.
            // Region pick must actually move the view (mutate ViewState +
            // render), not just relabel the watermark — share the same jump
            // the FloatingMenu region combo uses, then mirror the pick into
            // the menu combo so the toolbar reflects it.
            vm.RegionRequested        += (_, name) => { JumpToRegion(name); FloatingMenu.SetRegionSilent(name); };
            vm.EditorThemeSelected    += (_, name) => Main.SetThemeName(name);
            vm.ThemeSavedToLibrary    += (_, _)    => RefreshThemeListsFromService();
            vm.HelpRequested          += (_, _)    => ShowHelp();
            // Preview pipe-through: ColorThemeEditor produces a ColorThemeDef,
            // the host translates it into an IColorMap on its IColorThemeService
            // impl and pushes onto the render host. The actual translation
            // lives outside the VM (host-owned) — we just relay.
            vm.PreviewRequested       += (_, def)  =>
            {
                ColorThemePreviewRequested?.Invoke(this, def);
                // Post-FX defaults (Brightness / Contrast / Adaptive) aren't
                // part of the IColorMap — push them through the MainViewModel
                // setters so ViewState + the repaint/recalc stay in sync.
                // Mirrors legacy MainForm.ApplyThemePostFx: a null field resets
                // the value to neutral 0; a locked slider is left untouched so
                // the user can pin a preferred value across theme edits.
                if (!Main.BrightnessLocked) Main.Brightness = def.Brightness ?? 0;
                if (!Main.ContrastLocked)   Main.Contrast   = def.Contrast   ?? 0;
                if (!Main.AdaptiveLocked)   Main.Adaptive   = def.Adaptive   ?? 0;
            };
            // Real-time Post-FX (UI-gap #18 follow-up): the editor's
            // Brightness/Contrast/Adaptive sliders raise LivePostFxChanged
            // immediately, bypassing the 150ms preview debounce. Push the
            // current values straight into MainViewModel so the rendered
            // image responds while the user is still dragging.
            vm.LivePostFxChanged += (_, _) =>
            {
                if (!Main.BrightnessLocked && vm.UseBrightness) Main.Brightness = vm.Brightness;
                if (!Main.ContrastLocked   && vm.UseContrast)   Main.Contrast   = vm.Contrast;
                if (!Main.AdaptiveLocked   && vm.UseAdaptive)   Main.Adaptive   = vm.Adaptive;
            };
            // From-image flow currently raised by the editor when "From
            // Image…" is clicked. The host implements IPaletteExtractionService
            // and pops the ImagePaletteView; the editor consumes the returned
            // stops itself. UI.Avalonia stays free of System.Drawing.
            vm.FromImageRequested     += (_, args) => FromImageRequested?.Invoke(this, args);
            vm.SaveFileRequested      += (_, args) => SaveFileRequested?.Invoke(this, args);
            vm.MessageRequested       += (_, args) => MessageRequested?.Invoke(this, args);
            ColorThemeEditor = vm;
        }
        IsColorThemeEditorVisible = true;
    }

    private void ShowFFClient()
    {
        if (FFClient == null)
            FFClient = new FFClientViewModel(_themeService);
        IsFFClientVisible = true;
    }

    private void ShowServerAdmin()
    {
        if (ServerAdmin == null)
            ServerAdmin = new ServerAdminViewModel();
        IsServerAdminVisible = true;
    }

    private void ShowHelp()
    {
        if (Help == null)
        {
            var vm = new FloatingHelpViewModel(_helpProvider);
            vm.LinkRequested += (_, url) => LinkRequested?.Invoke(this, url);
            vm.CloseRequested += (_, _) => IsHelpVisible = false;
            Help = vm;
        }
        IsHelpVisible = true;
    }

    /// <summary>Re-pull theme names from the service into the menu combo.
    /// Called after the editor saves, or by the host after import/delete.</summary>
    public void RefreshThemeListsFromService()
    {
        FloatingMenu.RefreshThemes();
    }

    // ── Host-handled events (forwarded up from child VMs) ────────────────

    /// <summary>Color theme editor produced a new ColorThemeDef preview.
    /// Host translates into IColorMap and pushes onto the render host.</summary>
    public event EventHandler<ColorThemeDef>? ColorThemePreviewRequested;

    /// <summary>Editor wants to open the ImagePalette dialog. Host owns the
    /// extraction pipeline + the System.Drawing bridge; it pops the view,
    /// runs extraction, then fills <see cref="ThemeFromImageEventArgs.Stops"/>
    /// before returning.</summary>
    public event EventHandler<ThemeFromImageEventArgs>? FromImageRequested;

    /// <summary>Editor wants to save a file (JSON theme export or C# class).
    /// Host pops a SaveFileDialog and writes the content.</summary>
    public event EventHandler<ThemeSaveFileEventArgs>? SaveFileRequested;

    /// <summary>Editor or other child VM wants to show a MessageBox.</summary>
    public event EventHandler<ThemeMessageEventArgs>? MessageRequested;

    /// <summary>Help VM wants the host to open a URL in the system browser.</summary>
    public event EventHandler<string>? LinkRequested;

    /// <summary>FloatingMenu's "Close Program" was clicked. Host shuts the
    /// application down via the appropriate Avalonia lifetime API.</summary>
    public event EventHandler? CloseProgramRequested;

    /// <summary>Copy text to the system clipboard. Host owns the
    /// TopLevel.Clipboard call; payload is the string to copy.</summary>
    public event EventHandler<string>? CopyToClipboardRequested;

    /// <summary>Save the current view as a new user region. Host prompts
    /// the user for a name (via the message dialog), then asks
    /// <see cref="IColorThemeService"/> to persist. Args carry the
    /// confirmation TCS pattern.</summary>
    public event EventHandler<ThemeMessageEventArgs>? SaveRegionRequested;

    /// <summary>Delete an existing user region. Args carry the confirmation
    /// prompt + the region name to delete.</summary>
    public event EventHandler<(ThemeMessageEventArgs Confirm, string Name)>? DeleteRegionRequested;

    /// <summary>Save the most-recent rendered frame to a PNG. Host pops a
    /// SaveFilePicker and writes the BGRA buffer.</summary>
    public event EventHandler? ScreenshotRequested;

    /// <summary>Export user-defined regions to a JSON bundle. Host pops a
    /// SaveFilePicker then calls IColorThemeService.ExportUserRegionsToFile.</summary>
    public event EventHandler? ExportRegionsRequested;

    /// <summary>Import regions from a JSON bundle. Host pops an OpenFilePicker
    /// then calls IColorThemeService.ImportRegionsFromFile and refreshes the
    /// region combo via <see cref="RefreshRegionListsFromService"/>.</summary>
    public event EventHandler? ImportRegionsRequested;

    /// <summary>Open the slideshow-settings dialog. Host seeds it from the
    /// persisted SlideshowSettings and writes back on OK.</summary>
    public event EventHandler? SlideshowSettingsRequested;

    /// <summary>Export user-defined colour themes to a JSON file. Host pops a
    /// SaveFilePicker then calls IColorThemeService.ExportUserThemesToFile.</summary>
    public event EventHandler? ExportThemesRequested;

    /// <summary>Import colour themes from a JSON file. Host pops an
    /// OpenFilePicker then calls IColorThemeService.ImportThemesFromFile and
    /// refreshes the theme combo via <see cref="RefreshThemeListsFromService"/>.</summary>
    public event EventHandler? ImportThemesRequested;

    /// <summary>Delete an existing user theme. Args carry the confirmation
    /// prompt + the theme name to delete.</summary>
    public event EventHandler<(ThemeMessageEventArgs Confirm, string Name)>? DeleteThemeRequested;

    /// <summary>Toggle borderless multi-monitor fullscreen. The bool payload
    /// is true to enter span mode, false to restore the prior window geometry.
    /// Host owns the Avalonia Window manipulation.</summary>
    public event EventHandler<bool>? SpanToggleRequested;

    /// <summary>FloatingMenu's Dimensions combo picked a new render size.
    /// Host resizes the MainWindow to (Width, Height). No-op when the
    /// requested size exceeds the working area — host clamps as needed.</summary>
    public event EventHandler<(int Width, int Height)>? ResizeRequested;

    /// <summary>Render a high-resolution poster. Host pops the poster-size
    /// dialog + a SaveFilePicker, then runs the shared PosterRenderer.</summary>
    public event EventHandler? PosterRequested;

    /// <summary>User clicked the Video button (and nothing is running). Host
    /// pops the Avalonia VideoDialog; on OK it calls back into
    /// <see cref="StartVideoFromRequest"/> with the collected request.</summary>
    public event EventHandler? VideoRequested;

    /// <summary>User clicked the fractal-type Params button. Host pops the
    /// Avalonia <c>FractalParamsView</c> seeded from the shared ViewState's
    /// <c>FractalParameters</c> + active <c>FractalType</c>, and re-renders on
    /// each live change. Mirrors the legacy WinForms FractalParamsDialog.</summary>
    public event EventHandler? FractalParamsRequested;

    /// <summary>Begin a video zoom / slideshow from a request the host
    /// collected via the dialog. Sets the button label + (slideshow) shows the
    /// VCR transport, then drives the engine. Called on the UI thread.</summary>
    public void StartVideoFromRequest(VideoZoomRequest request)
    {
        if (_video == null || request == null) return;
        if (_video.IsRunning) return;

        FloatingMenu.VideoButtonText = "Stop";
        if (request.IsSlideshow)
        {
            SlideshowVcr.SetPaused(false);
            IsSlideshowVcrVisible = true;
            _video.StartSlideshow(request);
        }
        else
        {
            _video.StartVideo(request);
        }
    }

    /// <summary>Re-pull region names from the service into the menu combo.
    /// Called by the host after a successful import.</summary>
    public void RefreshRegionListsFromService()
    {
        FloatingMenu.RefreshRegions();
    }

    public void Dispose()
    {
        Main.Dispose();
    }
}
