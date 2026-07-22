// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using FracturingFog.Audio;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.UI.Avalonia.Slideshow;
using FracturingFog.UI.Avalonia.ViewModels.Animation;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly IColorThemeService _themeService;
    private readonly IPaletteExtractionService? _paletteService;
    private readonly IHelpContentProvider _helpProvider;

    /// <summary>Asset Manager sources (Sub-goal A). Injected by the host because
    /// the IAssetSource adapters live in Engine, which UI.Avalonia doesn't
    /// reference. Null/empty when the host wires no sources.</summary>
    private readonly System.Collections.Generic.IReadOnlyList<FracturingFog.Abstractions.Assets.IAssetSource> _assetSources;

    /// <summary>True while the host window is in borderless multi-monitor
    /// span mode. Toggled by the FloatingMenu Span button.</summary>
    private bool _isSpanning;

    /// <summary>Avalonia slideshow cycler. Lazily created on first Start.</summary>
    private SlideshowEngine? _slideshow;

    /// <summary>Live recorder when the active slideshow config has
    /// <c>RecordSlideshow</c> on. Null otherwise. Disposed (and the folder
    /// surfaced via <see cref="SlideshowRecordingReady"/>) when the engine
    /// signals Stopped.</summary>
    private FracturingFog.UI.Avalonia.Slideshow.ISlideshowFrameRecorder? _slideshowRecorder;
    private string? _slideshowRecordPreset;

    /// <summary>Host-supplied factory: builds an <see cref="ISlideshowFrameRecorder"/>
    /// for a given temp folder + dimensions. Null = recording not available
    /// (e.g. legacy WinForms host); the engine will skip the sink and the
    /// settings checkbox becomes a no-op at runtime.</summary>
    public Func<string, int, int, FracturingFog.UI.Avalonia.Slideshow.ISlideshowFrameRecorder>?
        SlideshowRecorderFactory { get; set; }

    /// <summary>Host-supplied hook invoked when an audio-reactive slideshow
    /// starts. The host (main WinExe) owns the AudioEngine lifecycle; this
    /// callback should start the engine if not already running and return
    /// its live <see cref="IBeatSource"/>. Null when no audio backend is
    /// wired (Avalonia-only test hosts) — slideshow falls back to plain
    /// wall-clock timing in that case.</summary>
    public Func<IBeatSource?>? StartAudioReactive { get; set; }

    /// <summary>Companion to <see cref="StartAudioReactive"/>: host stops
    /// the AudioEngine when the slideshow ends.</summary>
    public Action? StopAudioReactive { get; set; }

    /// <summary>Beat-skip cadence pushed onto the SlideshowEngine when an
    /// audio-reactive slideshow starts. Host loads from
    /// <c>AudioSettingsStore</c>; <c>(8, 32)</c> matches the legacy default.</summary>
    public Func<(int BeatsPerTheme, int BeatsPerRegion)>? GetAudioBeatCadence { get; set; }

    /// <summary>Raised on the UI thread after a recorded slideshow stops.
    /// Host listens to prompt Convert / Save / Cancel. <c>FolderPath</c> is
    /// the PNG-sequence directory; <c>EncodePreset</c> matches a name in
    /// <c>FfmpegEncoder.Preset</c>.</summary>
    public event EventHandler<SlideshowRecordingReadyEventArgs>? SlideshowRecordingReady;

    /// <summary>Video Zoom engine — the same concrete object as the render
    /// host (FractalRenderHost implements both IFractalRenderHost and
    /// IVideoZoomController). Null only if the host doesn't implement it.</summary>
    private readonly IVideoZoomController? _video;

    public ShellViewModel(
        IFractalRenderHost renderHost,
        IFractalInputController input,
        IColorThemeService themeService,
        IHelpContentProvider helpProvider,
        IPaletteExtractionService? paletteService = null,
        System.Collections.Generic.IReadOnlyList<FracturingFog.Abstractions.Assets.IAssetSource>? assetSources = null)
    {
        if (renderHost == null) throw new ArgumentNullException(nameof(renderHost));
        if (input == null) throw new ArgumentNullException(nameof(input));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _helpProvider = helpProvider ?? throw new ArgumentNullException(nameof(helpProvider));
        _paletteService = paletteService;
        _assetSources = assetSources ?? System.Array.Empty<FracturingFog.Abstractions.Assets.IAssetSource>();

        Main = new MainViewModel(renderHost, input);

        // Animation Roadmap Phase 3b — app-scoped animation bus for
        // region-attached animations. Initialised once; the JumpToRegion path
        // below populates its dynamic animator set per recall. Render-completion
        // released on every FrameCompleted from the host, so the gate fires
        // regardless of which UI surface kicked the render.
        AnimationBusHost.Initialize(() => renderHost.Trigger());
        renderHost.FrameCompleted += (_, _) =>
            AnimationBusHost.Bus?.NotifyRenderCompleted();
        FloatingMenu = new FloatingMenuViewModel();
        // Hand the menu the theme service so its Region / Theme combos can
        // group + sort + right-click-filter themselves (parity with the
        // WinForms combos). AttachThemeService performs the initial fill.
        FloatingMenu.AttachThemeService(_themeService);
        // Seed the menu's compat-fractal-type mirror so the
        // "Compatible with {type}" menu entry shows the right name at startup
        // and ByFractalCompat (if selected later) filters against the live type.
        FloatingMenu.SetCompatFractalType(Main.SelectedFractalType);
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
        FloatingMenu.EditWatermarkClick += (_, _) => ShowWatermarkEditor();
        FloatingMenu.EditAnimationClick += (_, _) => ShowAnimationEditor();
        FloatingMenu.EditSceneClick += (_, _) => ShowSceneEditor();
        FloatingMenu.WatermarkChanged += (_, name) => Main.SelectedCustomWatermarkName = name;
        FloatingMenu.UseCustomWatermarkChanged += (_, v) => Main.UseCustomWatermark = v;
        FloatingMenu.OverrideRegionWatermarkChanged += (_, v) => Main.OverrideRegionWatermark = v;
        FloatingMenu.ShowWatermarkChanged += (_, v) => Main.ShowWatermark = v;
        FloatingMenu.ColorThemeChanged  += (_, name) =>
        {
            Main.SetThemeName(name);
            if (string.IsNullOrEmpty(name)) return;
            _themeService.ApplyTheme(name);
            // ApplyTheme already calls RepaintWithPostFx; nothing else needed.

            // Phase 24 — bundled Lighting & FX preset. When the active theme
            // carries a non-null LightingPreset and the user hasn't locked
            // their lighting, snap FractalParameters.Lighting to the bundle
            // and kick a recompute (lighting affects shading, not just the
            // post-FX pass that ApplyTheme already retriggered).
            if (!Main.LightingLocked
                && _themeService.TryGetThemeLightingPreset(name, out var preset))
            {
                Main.ViewState.FractalParameters.Lighting = preset;
                // Wave 4.3 — preset-apply bypasses the VM EnvironmentName
                // setter, so kick the HDRI preload here too.
                if (!string.IsNullOrWhiteSpace(preset.EnvironmentName))
                    FracturingFog.Rendering.Lighting.HdriProbe.Preload?.Invoke(preset.EnvironmentName);
                Main.RenderHost.Trigger();
            }
        };
        FloatingMenu.ResetClick        += (_, _) => Main.ResetViewCommand.Execute().Subscribe();
        FloatingMenu.HelpClick         += (_, _) => ShowHelp();
        FloatingMenu.EditThemeClick    += (_, _) => ShowColorThemeEditor();
        FloatingMenu.ServerClick       += (_, _) => ShowServerAdmin();
        FloatingMenu.ClientClick       += (_, _) => ShowFFClient();
        FloatingMenu.BrightnessSlide   += (_, v) => Main.Brightness = v;
        FloatingMenu.ContrastSlide     += (_, v) => Main.Contrast = v;
        FloatingMenu.AdaptiveSlide     += (_, v) => Main.Adaptive = v;
        FloatingMenu.GammaSlide        += (_, v) => Main.Gamma = v;
        FloatingMenu.BandDitherToggle          += (_, v) => Main.BandDither = v;
        FloatingMenu.BandDitherStrengthSlide   += (_, v) => Main.BandDitherStrength = v;
        FloatingMenu.AlphaPreviewToggle        += (_, v) => Main.AlphaPreview = v;
        // Phase 24 — mirror the lighting-lock checkbox into MainViewModel so
        // the theme-change handler below can consult it. Phase 24b extends
        // the same pattern to Brightness / Contrast / Adaptive — previously
        // the FloatingMenu state never reached Main and the checkboxes were
        // dead UI.
        FloatingMenu.LightingLockedChanged += (_, v) => Main.LightingLocked = v;
        FloatingMenu.BrightnessLockedChanged += (_, v) => Main.BrightnessLocked = v;
        FloatingMenu.ContrastLockedChanged += (_, v) => Main.ContrastLocked = v;
        FloatingMenu.AdaptiveLockedChanged += (_, v) => Main.AdaptiveLocked = v;

        // Phase 9b/24b — "Save Lighting → Theme" snapshots the active
        // FractalParameters.Lighting block as the selected user theme's
        // bundled LightingPreset. Built-in / algorithmic themes are not in
        // the user library and the service returns false on those —
        // surface a friendly status hint in that case so the user knows
        // the click registered. The selected theme name is whichever entry
        // is currently in the FloatingMenu combo (mirrored by ColorThemeChanged).
        FloatingMenu.SaveLightingToThemeClick += (_, themeName) =>
        {
            if (string.IsNullOrWhiteSpace(themeName)
                || themeName.StartsWith("—", StringComparison.Ordinal))
            {
                Main.SetStatus("Pick a user theme first.");
                return;
            }
            var lighting = Main.ViewState.FractalParameters.Lighting;
            bool ok = _themeService.SaveLightingPresetToTheme(themeName, in lighting);
            Main.SetStatus(ok
                ? $"Lighting saved to theme: {themeName}"
                : $"Cannot save lighting to '{themeName}' — built-in or unknown theme.");
        };

        // ── Newly-wired controls (#53) ───────────────────────────────────
        // Close menu — flip the visibility flag the MainWindow binds to.
        FloatingMenu.CloseClick        += (_, _) => IsFloatingMenuVisible = false;

        // Close program — bubble up so the host (bootstrap) can shut the
        // application down through the right Avalonia lifetime API.
        FloatingMenu.CloseProgramClick += (_, _) => CloseProgramRequested?.Invoke(this, EventArgs.Empty);

        // Grid checkbox in the menu mirrors the toolbar toggle.
        FloatingMenu.GridToggled       += (_, v) => Main.ShowGrid = v;
        FloatingMenu.BypassAccelerationToggled += (_, v) =>
        {
            Main.RenderHost.MandelbrotDisableAcceleration = v;
            RebuildWindowTitle();
            Main.RenderHost.Trigger();
        };
        FloatingMenu.BypassSeriesApproximationToggled += (_, v) =>
        {
            Main.RenderHost.MandelbrotDisableSeriesApproximation = v;
            RebuildWindowTitle();
            Main.RenderHost.Trigger();
        };
        FloatingMenu.BypassDdBlaToggled += (_, v) =>
        {
            Main.RenderHost.MandelbrotDisableDdBla = v;
            RebuildWindowTitle();
            Main.RenderHost.Trigger();
        };
        FloatingMenu.BypassRebasingToggled += (_, v) =>
        {
            Main.RenderHost.MandelbrotAllowPtRebasing = !v;   // checked = bypass = off
            RebuildWindowTitle();
            Main.RenderHost.Trigger();
        };

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

        // Mirror auto-quality changes from the input controller (wheel/key
        // zoom past a tier's ZoomMax) into the menu's quality combo. The
        // toolbar combo is bound directly so it picks up SelectedQuality
        // automatically; the floating menu has its own ComboBox that needs
        // a SetQualitySilent push to avoid feedback through QualityChanged.
        Main.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(MainViewModel.SelectedQuality))
                FloatingMenu.SetQualitySilent(Main.SelectedQuality?.Name);
            // Fractal-type pick is a discrete nav — record it so Backspace
            // can return to the prior fractal + view.
            if (ev.PropertyName == nameof(MainViewModel.SelectedFractalType)
             || ev.PropertyName == nameof(MainViewModel.SelectedFractalEntry))
                RecordNavChange();
            // Push the active fractal type into the FloatingMenu so a
            // ByFractalCompat theme combo re-filters as the user switches
            // fractal type.
            if (ev.PropertyName == nameof(MainViewModel.SelectedFractalType))
                FloatingMenu.SetCompatFractalType(Main.SelectedFractalType);
            // Mirror watermark master toggle so the menu checkbox stays in
            // sync with the auto-enable from MainViewModel.UseCustomWatermark
            // and with right-click toggles outside the menu.
            if (ev.PropertyName == nameof(MainViewModel.ShowWatermark))
                FloatingMenu.SetShowWatermarkSilent(Main.ShowWatermark);
        };

        // Seed the menu mirror so the checkbox reflects the persisted state
        // on first open instead of always defaulting to unchecked.
        FloatingMenu.SetShowWatermarkSilent(Main.ShowWatermark);

        // "Go" button: parse the four coord textboxes and apply.
        FloatingMenu.GoClick           += (_, _) => ApplyCoordsFromMenu();

        // Iteration lock toggle in the menu maps onto MainViewModel state.
        // Set LockedIterations FIRST so the IterLocked setter sees the user-
        // typed iter count and skips its capture-current-iter fallback —
        // otherwise the first render after the tick would use the auto-calc
        // iter and FrameCompleted would push that stale value back into the
        // menu textbox, masking the lock.
        FloatingMenu.IterLockChanged   += (_, e) =>
        {
            if (e.Locked && e.CurrentIter > 0) Main.LockedIterations = e.CurrentIter;
            Main.IterLocked = e.Locked;
        };

        // Screenshot — host saves the most-recent BGRA buffer to disk.
        FloatingMenu.ScreenshotClick   += (_, _) => ScreenshotRequested?.Invoke(this, EventArgs.Empty);

        // Wallpaper — host renders an offscreen image sized to the union of
        // every connected monitor's pixel bounds, regardless of the current
        // window state. Works around the GNOME/Wayland limitation where Span
        // mode cannot overlay the shell's top bar + dock across monitors.
        FloatingMenu.WallpaperClick    += (_, _) => WallpaperScreenshotRequested?.Invoke(this, EventArgs.Empty);

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

        // General application settings — host pops the Avalonia AppSettings
        // dialog seeded from persisted AnimationSettings, saves on OK.
        FloatingMenu.AppSettingsClick += (_, _) => AppSettingsRequested?.Invoke(this, EventArgs.Empty);

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
        // (WindowDecorations / position / size) and restores it on exit.
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
                FormatLimbs(s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3,
                            s.CenterX4, s.CenterX5, s.CenterX6, s.CenterX7),
                FormatLimbs(s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3,
                            s.CenterY4, s.CenterY5, s.CenterY6, s.CenterY7),
                info.Zoom.ToString("G6", CultureInfo.InvariantCulture),
                info.Iterations.ToString(CultureInfo.InvariantCulture),
                FloatingMenu.ActiveCoordField);

            // Pan/zoom settle → nav history. Each completed frame resets a
            // ~700ms debounce; when the user stops moving, RecordNavChange
            // captures the final view so Backspace can return to it. The
            // dedup inside RecordNavChange ignores idle re-renders that
            // didn't actually move the view.
            _navSettleDebounce?.Change(NavSettleDebounceMs, global::System.Threading.Timeout.Infinite);
        };
        _navSettleDebounce = new global::System.Threading.Timer(
            _ => global::Avalonia.Threading.Dispatcher.UIThread.Post(RecordNavChange),
            null, global::System.Threading.Timeout.Infinite, global::System.Threading.Timeout.Infinite);

        ShowFloatingMenuCommand   = ReactiveCommand.Create(() => IsFloatingMenuVisible = !IsFloatingMenuVisible);
        ShowHelpCommand           = ReactiveCommand.Create(ShowHelp);
        ShowColorThemeEditorCommand = ReactiveCommand.Create(ShowColorThemeEditor);
        ShowControlCenterCommand    = ReactiveCommand.Create(ShowControlCenter);
        ShowRegionEditorCommand   = ReactiveCommand.Create(ShowRegionEditor);
        ShowAssetManagerCommand   = ReactiveCommand.Create(ShowAssetManager);
        ShowSceneEditorCommand    = ReactiveCommand.Create(() => ShowSceneEditor());
        ShowColorGenEditorCommand = ReactiveCommand.Create(
            () => OpenColorGenEditorRequested?.Invoke(this, EventArgs.Empty));
        ShowFractalParamsCommand  = ReactiveCommand.Create(
            () => FractalParamsRequested?.Invoke(this, EventArgs.Empty));
        ShowLightingFxCommand     = ReactiveCommand.Create(
            () => LightingFxRequested?.Invoke(this, EventArgs.Empty));

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
        TogglePostFxHudCommand = ReactiveCommand.Create(() => IsPostFxHudVisible = !IsPostFxHudVisible);
        ToggleMiniModeCommand  = ReactiveCommand.Create(() => IsMiniMode = !IsMiniMode);
        ToggleToyModeCommand   = ReactiveCommand.Create(() => IsToyMode  = !IsToyMode);

        // Push live view-state into the MiniMap VM on every frame so the
        // indicator tracks the user's pan/zoom. Mirrors legacy MainForm's
        // _miniMapPanel.RefreshIndicator() call sites.
        //
        // FrameCompleted is raised on TaskScheduler.Default (background
        // thread) — assigning the VM properties directly would raise
        // PropertyChanged off-UI-thread, and MiniMapControl.InvalidateVisual
        // silently no-ops when called outside the UI thread, leaving the
        // reticle frozen. Marshal to the UI thread the same way the
        // MiniDepth indicator refresh does (MainWindow.ConfigureMiniDepth).
        Main.RenderHost.FrameCompleted += (_, info) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                MiniMap.ActiveType = Main.ViewState.FractalType;
                MiniMap.CenterX = info.CenterX;
                MiniMap.CenterY = info.CenterY;
                MiniMap.HostZoom = info.Zoom;
            });
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
        // Emit full multi-limb centre in the same pipe form the menu textbox
        // already accepts on paste. Past zoom ~1e15 the Hi limb alone is
        // below pixel scale, so emitting only Hi would collapse adjacent
        // pixels to identical coords on round-trip → user-visible block
        // pixelation when pasting the copied value back. Pipe form keeps
        // every DD/QD/OD limb intact through clipboard.
        return string.Format(CultureInfo.InvariantCulture,
            "CX = {0}\nCY = {1}\nZoom = {2:G6}",
            FormatLimbs(s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3,
                        s.CenterX4, s.CenterX5, s.CenterX6, s.CenterX7),
            FormatLimbs(s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3,
                        s.CenterY4, s.CenterY5, s.CenterY6, s.CenterY7),
            s.Zoom);
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

        // A Video-type preset runs on the zoom engine, so the context-menu
        // "Slideshow" toggle must also be able to STOP a running video
        // slideshow — otherwise the button is a no-op mid-run.
        if (IsVideoRunning)
        {
            StopVideo();
            return;
        }

        // Context-menu + Floating Menu "Slideshow" buttons honour the user's
        // active saved preset — RecordSlideshow, AdaptiveSweep, AudioReactive,
        // filters etc. were unreachable when this path constructed a fresh
        // default config. Falls back to defaults when the library load fails
        // (corrupt JSON / first run) so the toggle still works.
        SlideshowConfig active;
        try { active = SlideshowConfigLibrary.GetActive(SlideshowConfigLibrary.Load()); }
        catch { active = new SlideshowConfig(); }

        // Honour the preset's Type. Video routes to the zoom engine; Image and
        // Animation both run on the CPU cross-fade cycler. Without this branch
        // the context-menu / Floating Menu Slideshow buttons always ran the
        // image cycler, so a saved Video preset (e.g. "Deep Forrest Path Video")
        // rendered as a static fractal image instead of a video zoom (#45).
        if (active.Type == SlideshowType.Video)
            StartVideoSlideshowFromConfig(active);
        else
            StartSlideshowWithConfig(active);
    }

    /// <summary>Start the image slideshow from an explicit in-memory
    /// <see cref="SlideshowConfig"/>. Used by the host when the user clicked
    /// Start in the unified dialog with unsaved edits — drives the run from
    /// the dialog's working copy without touching the on-disk preset.</summary>
    public void StartSlideshowFromConfig(SlideshowConfig config)
    {
        if (config == null) return;
        if (_slideshow is { IsRunning: true }) return;
        // Route by type so a Video preset never falls through to the image
        // cycler (which would render it as a static frame — #45).
        if (config.Type == SlideshowType.Video)
        {
            StartVideoSlideshowFromConfig(config);
            return;
        }
        StartSlideshowWithConfig(config);
    }

    private void StartSlideshowWithConfig(SlideshowConfig activeConfig)
    {
        var settings = activeConfig.Timing;

        if (_slideshow == null)
        {
            _slideshow = new SlideshowEngine(Main.RenderHost, _themeService, settings)
            {
                LockRegion = _slideshowLockRegion,
                FocusRegion = _slideshowFocusRegion,
            };
            _slideshow.Stopped += (_, _) =>
            {
                IsSlideshowVcrVisible = false;
                FloatingMenu.SlideshowButtonText = "Slideshow";
                this.RaisePropertyChanged(nameof(IsSlideshowRunning));
                FinalizeSlideshowRecording();
                // Detach beat source + tell the host to spin down its
                // AudioEngine. Detach BEFORE StopAudioReactive so the engine
                // doesn't deliver one last late beat into a stopped slideshow.
                if (_slideshow != null)
                {
                    _slideshow.BeatSource = null;
                    // Null the sink before restoring Adaptive — any in-flight
                    // sweep tick still queued on the dispatcher becomes a
                    // no-op instead of clobbering the restored value.
                    _slideshow.AdaptiveValueSink = null;
                }
                try { StopAudioReactive?.Invoke(); } catch { /* host failure must not block VCR */ }
                // Restore the pre-slideshow Adaptive value. Posted onto the
                // dispatcher so it lands after any pending sweep tick that
                // raced the Stopped event.
                if (_adaptivePreSweepValue >= 0)
                {
                    int restore = _adaptivePreSweepValue;
                    _adaptivePreSweepValue = -1;
                    Dispatcher.UIThread.Post(() => FloatingMenu.Adaptive = restore);
                }
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
                // When the slideshow honours each region's embedded watermark,
                // push the lookup into MainViewModel so the precedence resolver
                // re-emits the active watermark for the next frame. Otherwise
                // clear so leftover region embedded state doesn't persist past
                // a slideshow run that disabled per-region branding.
                Main.RegionEmbeddedWatermark = settings.UseRegionWatermark
                    ? _themeService.GetRegionEmbeddedWatermark(regionName)
                    : null;
            });
            _slideshow.ThemeApplied += (_, themeName) => Dispatcher.UIThread.Post(() =>
            {
                Main.SetThemeName(themeName);
                FloatingMenu.SetThemeSilent(themeName);
            });
        }

        // Push fresh settings onto an existing engine instance too — _slideshow
        // is constructed once and reused across toggles, so without this any
        // user changes to TotalDisplayMsPerRegion / FadeSteps / fade durations
        // would never reach the running loop.
        _slideshow.ApplySettings(settings);
        _slideshow.Config = activeConfig;
        // Snapshot the live Adaptive value so the Stopped handler can put it
        // back; only when sweep will actually run, else leave -1 (skip restore).
        _adaptivePreSweepValue = activeConfig.AdaptiveSweep is { Enabled: true }
            ? FloatingMenu.Adaptive
            : -1;
        _slideshow.AdaptiveValueSink = v => Dispatcher.UIThread.Post(() => FloatingMenu.Adaptive = v);

        // Push the Post-FX snapshot before kicking the loop so the first leg
        // already renders with the preset's look. Adaptive Sweep will override
        // the Adaptive value per-tick when enabled.
        if (activeConfig.PostFx.Enabled && activeConfig.PostFx.Values != null)
        {
            var v = activeConfig.PostFx.Values;
            if (v.TryGetValue("brightness", out var b)) FloatingMenu.Brightness = (int)Math.Round(b);
            if (v.TryGetValue("contrast", out var c)) FloatingMenu.Contrast = (int)Math.Round(c);
            if (v.TryGetValue("adaptive", out var a)) FloatingMenu.Adaptive = (int)Math.Round(a);
        }

        // Bring up the PNG-sequence recorder before Start so the very first
        // fade frame is captured. Dimensions snapped from the current render
        // host; a Resize mid-run will be ignored (recorder is fixed-size).
        StartSlideshowRecordingIfRequested(activeConfig);

        // Audio-reactive wiring: ask the host to spin up its AudioEngine and
        // hand back a live IBeatSource. The engine's OnBeat then flips
        // skip-flags per BeatsPerTheme / BeatsPerRegion and drives the
        // adaptive-sweep tick rate from BPM. Both hooks null when the host
        // doesn't carry an audio backend — slideshow falls back to plain
        // wall-clock timing in that case.
        if (activeConfig.AudioReactive)
        {
            try
            {
                if (GetAudioBeatCadence != null)
                {
                    var cadence = GetAudioBeatCadence();
                    _slideshow.BeatsPerTheme = cadence.BeatsPerTheme;
                    _slideshow.BeatsPerRegion = cadence.BeatsPerRegion;
                }
                var src = StartAudioReactive?.Invoke();
                _slideshow.BeatSource = src;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ShellViewModel] AudioReactive start failed: {ex.Message}");
                _slideshow.BeatSource = null;
            }
        }
        else
        {
            _slideshow.BeatSource = null;
        }

        SlideshowVcr.SetPaused(false);
        IsSlideshowVcrVisible = true;
        FloatingMenu.SlideshowButtonText = "Stop";
        _slideshow.Start();
        this.RaisePropertyChanged(nameof(IsSlideshowRunning));
    }

    private void StartSlideshowRecordingIfRequested(SlideshowConfig cfg)
    {
        // Always clear stale state first — re-entering Start without a Stop
        // would otherwise leak the previous writer.
        DisposeSlideshowRecorder();

        if (_slideshow == null) return;
        if (!cfg.Timing.RecordSlideshow) { _slideshow.FrameSink = null; return; }

        var factory = SlideshowRecorderFactory;
        if (factory == null)
        {
            Console.Error.WriteLine("[ShellViewModel] Slideshow record requested but no recorder factory is wired.");
            _slideshow.FrameSink = null;
            return;
        }

        _slideshowRecordPreset = string.IsNullOrWhiteSpace(cfg.Timing.RecordEncodePreset)
            ? "HighQualityH264Mp4" : cfg.Timing.RecordEncodePreset;

        // Lazily build the writer on the FIRST frame the engine actually
        // emits. Sizing off SnapshotHostFrame here was unreliable — the host
        // buffer is empty before the very first interactive render, and the
        // recorder's fixed dimensions would mismatch every subsequent fade
        // frame, silently dropping all of them and yielding an empty capture
        // (which the Stopped handler then cleans up without prompting).
        _slideshow.FrameSink = (buf, fw, fh) =>
        {
            var rec = _slideshowRecorder;
            if (rec == null)
            {
                if (fw < 2 || fh < 2) return;
                string root = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "FracturingFog",
                    "slideshow-rec",
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                try { System.IO.Directory.CreateDirectory(root); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ShellViewModel] Slideshow record dir create failed: {ex.Message}");
                    return;
                }
                try { rec = factory(root, fw, fh); _slideshowRecorder = rec; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ShellViewModel] Slideshow recorder init failed: {ex.Message}");
                    return;
                }
            }
            // Engine reuses its blend array between steps — sink copies
            // before returning so this is safe.
            if (fw != rec.Width || fh != rec.Height) return;
            try { rec.WriteFrame(buf); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ShellViewModel] Slideshow frame write failed: {ex.Message}");
            }
        };
    }

    private void FinalizeSlideshowRecording()
    {
        var rec = _slideshowRecorder;
        var preset = _slideshowRecordPreset ?? "HighQualityH264Mp4";
        if (_slideshow != null) _slideshow.FrameSink = null;
        if (rec == null)
        {
            DisposeSlideshowRecorder();
            return;
        }

        string folder = rec.Sink;
        int frames = rec.FrameCount;
        int w = rec.Width, h = rec.Height;
        try { rec.Dispose(); } catch { }
        _slideshowRecorder = null;
        _slideshowRecordPreset = null;

        // Empty capture (user stopped before any frame landed) — clean up the
        // temp folder ourselves and don't bother the user with a dialog.
        if (frames <= 0)
        {
            try { System.IO.Directory.Delete(folder, recursive: true); } catch { }
            return;
        }

        SlideshowRecordingReady?.Invoke(this,
            new SlideshowRecordingReadyEventArgs(folder, frames, preset, w, h));
    }

    private void DisposeSlideshowRecorder()
    {
        var rec = _slideshowRecorder;
        _slideshowRecorder = null;
        _slideshowRecordPreset = null;
        if (rec != null) { try { rec.Dispose(); } catch { } }
    }

    private (uint[] Buffer, int W, int H) SnapshotHostFrame()
    {
        try
        {
            var b = Main.RenderHost.SnapshotFrame(out int w, out int h);
            return (b, w, h);
        }
        catch { return (Array.Empty<uint>(), 0, 0); }
    }

    private void ApplyCoordsFromMenu()
    {
        RecordNavChange();
        bool changed = false;
        // Coord fields accept pipe-separated limbs so deep-zoom regions
        // (Hi, Lo, Lo2, Lo3 in DD/QD format) can be pasted in directly:
        //   "-1.9918151296901943|-7.821983681126658E-17"
        // A single value (no pipe) sets the Hi limb and zeros the rest.
        //
        // Skip any field whose current text matches what the host last
        // pushed via UpdateCoords — that means the user didn't touch it,
        // and re-parsing the FormatLimbs G29 string round-trips through
        // decimal sum / split which can't reconstruct the original Lo/Lo2/Lo3
        // limbs exactly. At deep zoom that drifts the centre by a visible
        // fraction of a pixel on Go.
        if (FloatingMenu.CX != FloatingMenu.LastPushedCX
            && TryParseLimbs(FloatingMenu.CX,
                out double cxHi, out double cxLo, out double cxL2, out double cxL3,
                out double cxL4, out double cxL5, out double cxL6, out double cxL7))
        {
            Main.ViewState.CenterX = cxHi;
            Main.ViewState.CenterXLo = cxLo; Main.ViewState.CenterX2 = cxL2; Main.ViewState.CenterX3 = cxL3;
            Main.ViewState.CenterX4 = cxL4; Main.ViewState.CenterX5 = cxL5;
            Main.ViewState.CenterX6 = cxL6; Main.ViewState.CenterX7 = cxL7;
            changed = true;
        }
        if (FloatingMenu.CY != FloatingMenu.LastPushedCY
            && TryParseLimbs(FloatingMenu.CY,
                out double cyHi, out double cyLo, out double cyL2, out double cyL3,
                out double cyL4, out double cyL5, out double cyL6, out double cyL7))
        {
            Main.ViewState.CenterY = cyHi;
            Main.ViewState.CenterYLo = cyLo; Main.ViewState.CenterY2 = cyL2; Main.ViewState.CenterY3 = cyL3;
            Main.ViewState.CenterY4 = cyL4; Main.ViewState.CenterY5 = cyL5;
            Main.ViewState.CenterY6 = cyL6; Main.ViewState.CenterY7 = cyL7;
            changed = true;
        }
        if (FloatingMenu.Zoom != FloatingMenu.LastPushedZoom
            && double.TryParse(FloatingMenu.Zoom, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom)
            && zoom > 0)
        {
            Main.ViewState.Zoom = zoom;
            changed = true;
        }
        if (FloatingMenu.Iter != FloatingMenu.LastPushedIter
            && int.TryParse(FloatingMenu.Iter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iter)
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
    private static string FormatLimbs(double hi, double lo, double l2, double l3,
                                       double l4 = 0.0, double l5 = 0.0,
                                       double l6 = 0.0, double l7 = 0.0)
    {
        // Pick the highest non-zero limb so the format never carries trailing
        // zero limbs (avoids surfacing meaningless precision for shallow zooms).
        // Wave 2.11 — OD limbs 4..7 join the same scan; the format scales
        // automatically when zoom > 1e50 once the pan-zoom path populates them.
        int n = 1;
        if (l7 != 0.0) n = 8;
        else if (l6 != 0.0) n = 7;
        else if (l5 != 0.0) n = 6;
        else if (l4 != 0.0) n = 5;
        else if (l3 != 0.0) n = 4;
        else if (l2 != 0.0) n = 3;
        else if (lo != 0.0) n = 2;

        // Any-extra-limb path (n >= 2): the Lo (and L2..L7) limbs carry
        // precision past decimal's ~29-digit cap. DD pair is ~31 digits,
        // QD chain is ~62 digits, OD chain is ~124 digits; any case loses
        // bottom limb data through the G29 sum + textbox round-trip and
        // collapses the centre to ~29 digits permanently on the next Go.
        // Emit pipe-delimited limbs whenever any low limb is non-zero so
        // every limb survives the display + parse.
        //
        // Pipe form is uglier than a single decimal string but is the
        // only honest representation of multi-limb precision in a UI
        // textbox. Shallow (n=1) coords keep the readable decimal form.
        if (n >= 2)
        {
            var limbs = new double[] { hi, lo, l2, l3, l4, l5, l6, l7 };
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(limbs[i].ToString("G17", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        try
        {
            decimal acc = (decimal)hi;
            // Single limb (n == 1) — plain "G29" prints up to decimal's
            // full 29-digit precision without scientific notation for
            // everyday Mandelbrot coords. No precision loss possible
            // because Hi alone fits well inside decimal's range.
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
        => TryParseLimbs(s, out hi, out lo, out l2, out l3, out _, out _, out _, out _);

    private static bool TryParseLimbs(string? s,
        out double hi, out double lo, out double l2, out double l3,
        out double l4, out double l5, out double l6, out double l7)
    {
        hi = lo = l2 = l3 = l4 = l5 = l6 = l7 = 0.0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split('|');
        if (parts.Length > 1)
        {
            // Pipe-delimited (legacy) — each segment is a plain double.
            // Wave 2.11 — accept up to 8 limbs for OD precision past zoom 1e50.
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out hi))
                return false;
            if (parts.Length > 1) double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lo);
            if (parts.Length > 2) double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l2);
            if (parts.Length > 3) double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l3);
            if (parts.Length > 4) double.TryParse(parts[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l4);
            if (parts.Length > 5) double.TryParse(parts[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l5);
            if (parts.Length > 6) double.TryParse(parts[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l6);
            if (parts.Length > 7) double.TryParse(parts[7].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out l7);
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

    // ── Phase S1 Control Center shell ─────────────────────────────────────
    // Re-presents FloatingMenu + this shell's Show* commands into a nav-rail.
    // Lazily built on first open; shares FloatingMenu so the render window and
    // the shell stay in lock-step.
    private ControlCenterViewModel? _controlCenter;
    public ControlCenterViewModel? ControlCenter
    {
        get => _controlCenter;
        private set => this.RaiseAndSetIfChanged(ref _controlCenter, value);
    }

    private void ShowControlCenter()
    {
        ControlCenter ??= new ControlCenterViewModel(this);
        IsControlCenterVisible = !IsControlCenterVisible;
    }

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

    private WatermarkEditorViewModel? _watermarkEditor;
    public WatermarkEditorViewModel? WatermarkEditor
    {
        get => _watermarkEditor;
        private set => this.RaiseAndSetIfChanged(ref _watermarkEditor, value);
    }

    /// <summary>Animation Roadmap Phase 3c. Lazily-constructed VM for the
    /// Animation Editor dialog; null until the first ShowAnimationEditor call.</summary>
    private AnimationEditorViewModel? _animationEditor;
    public AnimationEditorViewModel? AnimationEditor
    {
        get => _animationEditor;
        private set => this.RaiseAndSetIfChanged(ref _animationEditor, value);
    }

    /// <summary>Animation Roadmap Sub-goal B. Lazily-constructed VM for the
    /// Region Editor dialog; rebuilt per Show so it always targets the
    /// currently-selected region.</summary>
    private RegionEditorViewModel? _regionEditor;
    public RegionEditorViewModel? RegionEditor
    {
        get => _regionEditor;
        private set => this.RaiseAndSetIfChanged(ref _regionEditor, value);
    }

    /// <summary>Scene Engine Roadmap Phase S5. Lazily-constructed VM for the
    /// Scene Editor dialog; null until the first ShowSceneEditor call.</summary>
    private SceneEditorViewModel? _sceneEditor;
    public SceneEditorViewModel? SceneEditor
    {
        get => _sceneEditor;
        private set => this.RaiseAndSetIfChanged(ref _sceneEditor, value);
    }

    /// <summary>Asset Manager dialog (Sub-goal A); built once on first Show.</summary>
    private AssetManagerViewModel? _assetManager;
    public AssetManagerViewModel? AssetManager
    {
        get => _assetManager;
        private set => this.RaiseAndSetIfChanged(ref _assetManager, value);
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

    private ClusterDashboardViewModel? _clusterDashboard;
    public ClusterDashboardViewModel? ClusterDashboard
    {
        get => _clusterDashboard;
        private set => this.RaiseAndSetIfChanged(ref _clusterDashboard, value);
    }

    private JobListViewModel? _jobList;
    public JobListViewModel? JobList
    {
        get => _jobList;
        private set => this.RaiseAndSetIfChanged(ref _jobList, value);
    }

    private JobDetailViewModel? _jobDetail;
    public JobDetailViewModel? JobDetail
    {
        get => _jobDetail;
        private set => this.RaiseAndSetIfChanged(ref _jobDetail, value);
    }

    private WorkerDetailViewModel? _workerDetail;
    public WorkerDetailViewModel? WorkerDetail
    {
        get => _workerDetail;
        private set => this.RaiseAndSetIfChanged(ref _workerDetail, value);
    }

    private MasterConfigViewModel? _masterConfig;
    public MasterConfigViewModel? MasterConfig
    {
        get => _masterConfig;
        private set => this.RaiseAndSetIfChanged(ref _masterConfig, value);
    }

    // ── Window visibility flags (bound to Window.IsVisible) ──────────────

    private bool _isFloatingMenuVisible;
    public bool IsFloatingMenuVisible
    {
        get => _isFloatingMenuVisible;
        set => this.RaiseAndSetIfChanged(ref _isFloatingMenuVisible, value);
    }

    private bool _isControlCenterVisible;
    public bool IsControlCenterVisible
    {
        get => _isControlCenterVisible;
        set => this.RaiseAndSetIfChanged(ref _isControlCenterVisible, value);
    }

    /// <summary>Render-window always-on-top. MainWindow mirrors this onto its
    /// Window.Topmost; the Control Center + the context-menu "On Top" item both
    /// drive it so the two stay in sync.</summary>
    private bool _isRenderTopmost;
    public bool IsRenderTopmost
    {
        get => _isRenderTopmost;
        set => this.RaiseAndSetIfChanged(ref _isRenderTopmost, value);
    }

    private bool _isColorThemeEditorVisible;
    public bool IsColorThemeEditorVisible
    {
        get => _isColorThemeEditorVisible;
        set => this.RaiseAndSetIfChanged(ref _isColorThemeEditorVisible, value);
    }

    private bool _isWatermarkEditorVisible;
    public bool IsWatermarkEditorVisible
    {
        get => _isWatermarkEditorVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _isWatermarkEditorVisible, value);
            // Dropping the editor drops its unsaved draft, so the overlay falls
            // back to the saved-library chain instead of pinning whatever was
            // last typed. Done here rather than on CloseRequested because the
            // editor can also be dismissed by the shell without raising it.
            if (!value) Main.DraftWatermark = null;
        }
    }

    private bool _isAnimationEditorVisible;
    public bool IsAnimationEditorVisible
    {
        get => _isAnimationEditorVisible;
        set => this.RaiseAndSetIfChanged(ref _isAnimationEditorVisible, value);
    }

    private bool _isSceneEditorVisible;
    public bool IsSceneEditorVisible
    {
        get => _isSceneEditorVisible;
        set => this.RaiseAndSetIfChanged(ref _isSceneEditorVisible, value);
    }

    private bool _isRegionEditorVisible;
    public bool IsRegionEditorVisible
    {
        get => _isRegionEditorVisible;
        set => this.RaiseAndSetIfChanged(ref _isRegionEditorVisible, value);
    }

    private bool _isAssetManagerVisible;
    public bool IsAssetManagerVisible
    {
        get => _isAssetManagerVisible;
        set => this.RaiseAndSetIfChanged(ref _isAssetManagerVisible, value);
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

    private bool _isClusterDashboardVisible;
    public bool IsClusterDashboardVisible
    {
        get => _isClusterDashboardVisible;
        set => this.RaiseAndSetIfChanged(ref _isClusterDashboardVisible, value);
    }

    private bool _isJobListVisible;
    public bool IsJobListVisible
    {
        get => _isJobListVisible;
        set => this.RaiseAndSetIfChanged(ref _isJobListVisible, value);
    }

    private bool _isJobDetailVisible;
    public bool IsJobDetailVisible
    {
        get => _isJobDetailVisible;
        set => this.RaiseAndSetIfChanged(ref _isJobDetailVisible, value);
    }

    private bool _isWorkerDetailVisible;
    public bool IsWorkerDetailVisible
    {
        get => _isWorkerDetailVisible;
        set => this.RaiseAndSetIfChanged(ref _isWorkerDetailVisible, value);
    }

    private bool _isMasterConfigVisible;
    public bool IsMasterConfigVisible
    {
        get => _isMasterConfigVisible;
        set => this.RaiseAndSetIfChanged(ref _isMasterConfigVisible, value);
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
        string diag = BuildDiagnosticSuffix();
        WindowTitle = $"{_programName}{ver}{ren}{diag}";
    }

    private string BuildDiagnosticSuffix()
    {
        var sb = new System.Text.StringBuilder();
        if (Main.RenderHost.MandelbrotDisableAcceleration) sb.Append("  [ACCEL OFF]");
        if (Main.RenderHost.MandelbrotDisableSeriesApproximation) sb.Append("  [SA OFF]");
        if (Main.RenderHost.MandelbrotDisableDdBla) sb.Append("  [DD-BLA OFF]");
        if (!Main.RenderHost.MandelbrotAllowPtRebasing) sb.Append("  [REBASE OFF]");
        return sb.ToString();
    }

    /// <summary>Toggle BLA + SA bypass on the legacy MandelbrotCalculator HP
    /// path. Used to isolate deep-zoom precision regressions. Title gains a
    /// <c>[ACCEL OFF]</c> suffix when on. Retriggers the current frame.
    /// Drives the menu checkbox; menu event handler does the actual flag +
    /// trigger so both paths stay in lockstep.</summary>
    public void ToggleMandelbrotAcceleration()
        => FloatingMenu.BypassAcceleration = !FloatingMenu.BypassAcceleration;

    /// <summary>Toggle SA prelude bypass on the legacy MandelbrotCalculator HP
    /// path (BLA still applies). Used to isolate SA-induced artefacts vs BLA
    /// errors. Title gains a <c>[SA OFF]</c> suffix when on.</summary>
    public void ToggleMandelbrotSeriesApproximation()
        => FloatingMenu.BypassSeriesApproximation = !FloatingMenu.BypassSeriesApproximation;

    /// <summary>Toggle DD-precision BLA bypass — when on, the legacy single-
    /// precision BLA table runs (pre-Wave-2.10 behaviour). Title gains a
    /// <c>[DD-BLA OFF]</c> suffix while on.</summary>
    public void ToggleMandelbrotDdBla()
        => FloatingMenu.BypassDdBla = !FloatingMenu.BypassDdBla;

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
    public ReactiveCommand<Unit, Unit> ShowControlCenterCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowRegionEditorCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowAssetManagerCommand { get; }
    /// <summary>Scene Engine Roadmap Phase S5 — opens the Scene Editor.</summary>
    public ReactiveCommand<Unit, Unit> ShowSceneEditorCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowColorGenEditorCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowFractalParamsCommand { get; }

    /// <summary>S2 — opens the standalone Volumetric Lighting &amp; FX panel
    /// (the Lighting/FX block on its own, not buried inside Fractal Params).</summary>
    public ReactiveCommand<Unit, Unit> ShowLightingFxCommand { get; }

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

    // Captured FloatingMenu.Adaptive value at slideshow start. Restored when
    // the engine stops so the user's pre-slideshow Adaptive setting comes
    // back instead of leaving the slider stuck at whatever value the
    // adaptive-sweep loop landed on (forced Loop=true under audio-reactive
    // means the sweep parks mid-cycle on Stop). -1 = no sweep this run, skip restore.
    private int _adaptivePreSweepValue = -1;

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

    private bool _slideshowFocusRegion = true;
    /// <summary>Mirror of SlideshowEngine.FocusRegion. Defaults true (Region
    /// Focus, 3 themes/region) to match legacy MainForm._slideshowFocusRegion.
    /// Menu label shows the *next* action: true → "More Colors", false →
    /// "More Regions".</summary>
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

    // ── Post-FX HUD overlay (S2) ────────────────────────────────────────
    // A borderless brightness/contrast/adaptive strip tethered to a render-
    // window corner, bound to FloatingMenu (same props the Control Center
    // Post-FX panel uses). Host wires the tether in MainWindow.SyncPostFxHud.
    private bool _isPostFxHudVisible;
    public bool IsPostFxHudVisible
    {
        get => _isPostFxHudVisible;
        set => this.RaiseAndSetIfChanged(ref _isPostFxHudVisible, value);
    }

    public ReactiveCommand<Unit, bool> TogglePostFxHudCommand { get; private set; } = null!;

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

    // ── Toy Mode ────────────────────────────────────────────────────────
    // Tighter than Mini Mode: borderless, no toolbar, no status bar, on
    // top, and left-click-drag moves the window (pan is sacrificed). Lives
    // alongside Mini Mode but is mutually exclusive — entering Toy exits
    // Mini and vice versa (the host handles the switch).
    private bool _isToyMode;
    public bool IsToyMode
    {
        get => _isToyMode;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _isToyMode, value))
                ToyModeToggleRequested?.Invoke(this, value);
        }
    }

    public event EventHandler<bool>? ToyModeToggleRequested;

    public ReactiveCommand<Unit, bool> ToggleToyModeCommand { get; private set; } = null!;

    /// <summary>Apply a region jump: relabel the watermark, mutate ViewState
    /// via the host service, mirror the resulting fractal type into the toolbar
    /// (without snapping its centre/zoom), then trigger a render. Shared by the
    /// FloatingMenu region combo and the Color Theme Editor's region pick so
    /// both paths actually move the view instead of only relabelling it.</summary>
    private void JumpToRegion(string? name)
    {
        RecordNavChange();
        Main.SetRegionName(name);
        // Mirror any watermark embedded in this region into MainViewModel so
        // the precedence resolver routes it through to the next frame. Null
        // when the region doesn't exist or doesn't carry one.
        Main.RegionEmbeddedWatermark = _themeService.GetRegionEmbeddedWatermark(name ?? string.Empty);
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
            // Phase 10b — per-region LightingOverride. Same precedence as the
            // theme preset (Phase 24): honour LightingLocked, then apply.
            // The override "wins" the race against the theme preset because
            // it runs after the region jump — themes follow region jumps in
            // most user flows, and a region's lighting tuning is more
            // specific than a theme's. Bit-identical when LightingOverride
            // is null on the region (the common case).
            if (!Main.LightingLocked
                && _themeService.TryGetRegionLightingOverride(name, out var lightOverride))
            {
                Main.ViewState.FractalParameters.Lighting = lightOverride;
                // Wave 4.3 — preset-apply bypasses the VM EnvironmentName
                // setter, so kick the HDRI preload here too.
                if (!string.IsNullOrWhiteSpace(lightOverride.EnvironmentName))
                    FracturingFog.Rendering.Lighting.HdriProbe.Preload?.Invoke(lightOverride.EnvironmentName);
            }
            // Animation Roadmap Phase 3b — region's attached animation, if
            // any. Wipes the prior dynamic animator set even when this region
            // has no animation attached (silent transition off). Bus starts
            // its dispatcher timer on Refresh inside LoadRegionAnimation.
            var attachedAnimName = _themeService.GetRegionAnimationName(name);
            var attachedAnim = string.IsNullOrEmpty(attachedAnimName)
                ? null
                : _themeService.GetAnimation(attachedAnimName);
            AnimationBusHost.LoadRegionAnimation(
                attachedAnim,
                Main.ViewState.FractalParameters);
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
            vm.ThemeSavedToLibrary    += (_, _)    => { RefreshThemeListsFromService(); RefreshAssetManagerIfVisible(); };
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
                if (!Main.AdaptiveLocked)
                {
                    int adaptive = def.Adaptive ?? 0;
                    bool changed = adaptive != Main.Adaptive;
                    Main.Adaptive = adaptive;
                    // ApplyColorMap (above) just rewrote the framebuffer with a
                    // pure palette pass, dropping the prior histogram-eq layer.
                    // The Adaptive setter only schedules a re-apply on a value
                    // change, so when the user touches another editor field
                    // while Adaptive is non-zero the visible result drops back
                    // to non-adaptive until they toggle the checkbox. Force
                    // the histogram-eq pass to re-run on every preview.
                    if (!changed && adaptive > 0)
                        Main.RenderHost.RepaintWithAdaptive();
                }
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
            vm.FromImageRequested      += (_, args) => FromImageRequested?.Invoke(this, args);
            vm.SaveFileRequested       += (_, args) => SaveFileRequested?.Invoke(this, args);
            vm.MessageRequested        += (_, args) => MessageRequested?.Invoke(this, args);
            vm.ImportPaletteRequested  += (_, args) => ImportPaletteRequested?.Invoke(this, args);
            vm.ExportPaletteRequested  += (_, args) => ExportPaletteRequested?.Invoke(this, args);
            vm.SampleColorRequested    += (_, args) => SampleColorRequested?.Invoke(this, args);
            vm.UnsavedChangesPromptRequested += (_, args) => UnsavedChangesPromptRequested?.Invoke(this, args);
            ColorThemeEditor = vm;
        }
        IsColorThemeEditorVisible = true;
    }

    /// <summary>Open the Watermark Editor dialog. Public so the Poster dialog
    /// (hosted in Hosting/AvaloniaDialogs) can route its "Edit Watermark…"
    /// button through the same code path as the FloatingMenu button.</summary>
    public void ShowWatermarkEditor() => ShowWatermarkEditorInternal();

    private void ShowWatermarkEditorInternal()
    {
        if (WatermarkEditor == null)
        {
            var vm = new WatermarkEditorViewModel(initialWatermarkName: Main.SelectedCustomWatermarkName);
            // Live preview pipe: hand the edited def itself to MainViewModel as
            // the draft. This used to discard `def` and call PushActiveWatermark,
            // which re-reads UserWatermarkStore by name — so nothing unsaved
            // could ever render, and a brand-new watermark (no name selected
            // yet) failed the guard outright. Hence "save, then toggle Use
            // Custom Watermark off/on to see it".
            vm.PreviewRequested += (_, def) => Main.DraftWatermark = def;
            vm.PreviewCancelled += (_, _) => Main.DraftWatermark = null;
            vm.WatermarkSavedToLibrary += (_, name) =>
            {
                FloatingMenu.SetWatermarks(UserWatermarkStore.Instance.EnumerateNames());
                FloatingMenu.SetWatermarkSilent(name);
                Main.SelectedCustomWatermarkName = name;
                RefreshAssetManagerIfVisible();
            };
            vm.WatermarkDeletedFromLibrary += (_, name) =>
            {
                FloatingMenu.SetWatermarks(UserWatermarkStore.Instance.EnumerateNames());
                if (string.Equals(Main.SelectedCustomWatermarkName, name, StringComparison.OrdinalIgnoreCase))
                    Main.SelectedCustomWatermarkName = null;
                RefreshAssetManagerIfVisible();
            };
            vm.HelpRequested += (_, _) => ShowHelp();
            vm.CloseRequested += (_, _) => IsWatermarkEditorVisible = false;
            vm.MessageRequested += (_, args) => MessageRequested?.Invoke(this, args);
            vm.ImportRequested += (_, _) => AssetJsonImportRequested?.Invoke(this,
                new AssetJsonImportEventArgs(
                    FracturingFog.Abstractions.Assets.AssetKind.Watermark, "Import Watermarks"));
            WatermarkEditor = vm;
        }
        IsWatermarkEditorVisible = true;
        // The VM raises its first PreviewRequested from its own constructor,
        // before the handler above exists, so seed the draft here — otherwise
        // the overlay shows nothing until the first keystroke.
        if (WatermarkEditor != null && WatermarkEditor.LivePreview)
            Main.DraftWatermark = WatermarkEditor.BuildDef();
    }

    /// <summary>Animation Roadmap Phase 3c — open the Animation Editor.
    /// Modeless, lives alongside the existing editors. The preview target is
    /// the live FractalParameters record so Live Preview / Preview push
    /// onto the same params the renderer reads.</summary>
    public void ShowAnimationEditor()
    {
        if (AnimationEditor == null)
        {
            var vm = new AnimationEditorViewModel(
                _themeService,
                Main.ViewState.FractalParameters);
            vm.AnimationSavedToLibrary += (_, _) =>
            {
                // No FloatingMenu animation dropdown today — the Save Region
                // dialog picks up the new entry on its next open via
                // EnumerateAnimationNames(). Hook stays here for the future
                // SlideshowSettings animation filter UI.
                RefreshAssetManagerIfVisible();
            };
            vm.CloseRequested += (_, _) => IsAnimationEditorVisible = false;
            vm.MessageRequested += (_, args) => MessageRequested?.Invoke(this, args);
            vm.ImportRequested += (_, _) => AssetJsonImportRequested?.Invoke(this,
                new AssetJsonImportEventArgs(
                    FracturingFog.Abstractions.Assets.AssetKind.Animation, "Import Animations"));
            AnimationEditor = vm;
        }
        IsAnimationEditorVisible = true;
    }

    /// <summary>Scene Engine Roadmap Phase S5 — open the Scene Editor. Built once
    /// (retargeted by name via <paramref name="initialSceneName"/> on later
    /// opens). Preview applies a single shot's region / theme / animation to the
    /// live view; sequenced multi-shot playback with camera motion + transitions
    /// is S6.</summary>
    public void ShowSceneEditor(string? initialSceneName = null)
    {
        if (SceneEditor == null)
        {
            var vm = new SceneEditorViewModel(_themeService);
            vm.SceneSavedToLibrary   += (_, _) => RefreshAssetManagerIfVisible();
            vm.SceneDeletedFromLibrary += (_, _) => RefreshAssetManagerIfVisible();
            vm.PreviewShotRequested  += (_, shot) => PreviewSceneShot(shot);
            vm.PlaySceneRequested    += (_, scene) => PlayScene(scene);
            vm.ExportSceneRequested  += (_, args) => ExportSceneRequested?.Invoke(this, args);
            vm.StopPreviewRequested  += (_, _) => StopScenePreview();
            vm.CloseRequested        += (_, _) => IsSceneEditorVisible = false;
            vm.MessageRequested      += (_, args) => MessageRequested?.Invoke(this, args);
            vm.ImportRequested       += (_, _) => AssetJsonImportRequested?.Invoke(this,
                new AssetJsonImportEventArgs(
                    FracturingFog.Abstractions.Assets.AssetKind.Scene, "Import Scenes"));
            SceneEditor = vm;
        }
        if (!string.IsNullOrEmpty(initialSceneName)) SceneEditor.SelectedScene = initialSceneName;
        IsSceneEditorVisible = true;
    }

    /// <summary>Apply one scene shot to the live view for the editor's per-shot
    /// Preview: jump to its region (if any), set its theme override (if any), and
    /// push its param-animation onto the shared bus (if any). A static framing
    /// preview — the keyframed camera plays only under S6 scene playback.</summary>
    private void PreviewSceneShot(FracturingFog.Abstractions.Animation.SceneShot shot)
    {
        if (shot == null) return;
        if (!string.IsNullOrEmpty(shot.RegionName))
        {
            JumpToRegion(shot.RegionName);
            FloatingMenu.SetRegionSilent(shot.RegionName);
        }
        else if (Main.SelectedFractalType != shot.FractalType)
        {
            // Region-free shot: switch the live view to the shot's fractal type
            // so Preview shows the right set (see ApplySceneSample).
            Main.SelectedFractalType = shot.FractalType;
        }
        if (!string.IsNullOrEmpty(shot.ThemeName))
        {
            Main.SetThemeName(shot.ThemeName);
            FloatingMenu.SetThemeSilent(shot.ThemeName);
        }
        var anim = string.IsNullOrEmpty(shot.AnimationName)
            ? null
            : _themeService.GetAnimation(shot.AnimationName!);
        AnimationBusHost.LoadRegionAnimation(anim, Main.ViewState.FractalParameters);
    }

    /// <summary>Stop any scene-preview param animation + scene playback
    /// (companion to <see cref="PreviewSceneShot"/> / <see cref="PlayScene"/>).</summary>
    private void StopScenePreview()
    {
        StopScene();
        AnimationBusHost.LoadRegionAnimation(null, Main.ViewState.FractalParameters);
    }

    // ── Scene playback (S6) ──────────────────────────────────────────────────
    // Realtime, cut-sequenced playback: a dispatcher clock walks the S6
    // SceneTimeline; on each shot boundary the shot is applied to the live view
    // and its camera + param motion is loaded onto the shared animation bus,
    // which advances it under the same render-completion gate + ceiling as every
    // other track. Cross-fade / light-sweep / param-morph *compositing* between
    // shots (blend two rendered frames) is the offline frame-locked path's job
    // (S7) — running two 3D raymarchers live would breach the resource cap — so
    // realtime playback cuts between shots. The timeline already supplies the
    // blend factor for S7 to consume.

    private DispatcherTimer? _sceneTimer;
    private FracturingFog.Abstractions.Animation.SceneTimeline? _sceneTimeline;
    private FracturingFog.Abstractions.Animation.SceneData? _scenePlaying;
    private double _sceneClock;
    private int _sceneCurrentEntry = -1;
    private DateTime _sceneLastTick;

    /// <summary>Start realtime playback of <paramref name="scene"/> on the live
    /// view. Loops at the end. A no-op (with a friendly message) for a scene
    /// with no playable shots.</summary>
    public void PlayScene(FracturingFog.Abstractions.Animation.SceneData scene)
    {
        if (scene == null) return;
        var timeline = FracturingFog.Abstractions.Animation.SceneTimeline.Build(scene);
        if (timeline.IsEmpty)
        {
            MessageRequested?.Invoke(this, new ThemeMessageEventArgs(
                "Play Scene", "This scene has no shots with a positive duration to play.",
                MessageSeverity.Warning));
            return;
        }

        StopScene();
        _scenePlaying = scene;
        _sceneTimeline = timeline;
        _sceneClock = 0;
        _sceneCurrentEntry = -1;
        _sceneLastTick = DateTime.UtcNow;

        // Apply the opening shot immediately so playback starts on-frame.
        ApplySceneSample(timeline.Sample(0));

        _sceneTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnSceneTick);
        _sceneTimer.Start();
    }

    /// <summary>Stop scene playback and clear its bus animators. Safe to call
    /// when nothing is playing.</summary>
    public void StopScene()
    {
        _sceneTimer?.Stop();
        _sceneTimer = null;
        _sceneTimeline = null;
        _scenePlaying = null;
        _sceneCurrentEntry = -1;
    }

    private void OnSceneTick(object? sender, EventArgs e)
    {
        var tl = _sceneTimeline;
        if (tl == null || tl.IsEmpty) { StopScene(); return; }

        var now = DateTime.UtcNow;
        double dt = (now - _sceneLastTick).TotalSeconds;
        _sceneLastTick = now;
        if (dt <= 0) return;
        if (dt > 0.25) dt = 0.25; // guard against a stalled dispatcher jump

        _sceneClock += dt;
        if (tl.TotalDuration > 0) _sceneClock %= tl.TotalDuration; // loop

        ApplySceneSample(tl.Sample(_sceneClock));
    }

    /// <summary>Apply a timeline sample: when the active shot changes, jump the
    /// live view to it and (re)load its camera + param animation onto the bus.
    /// Intra-shot motion is driven by the bus, not here.</summary>
    private void ApplySceneSample(FracturingFog.Abstractions.Animation.SceneSample sample)
    {
        if (sample.CurrentEntry == _sceneCurrentEntry) return; // same shot — bus drives it
        _sceneCurrentEntry = sample.CurrentEntry;

        var scene = _scenePlaying;
        if (scene == null || sample.OriginalIndex < 0 || sample.OriginalIndex >= scene.Shots.Count) return;
        var shot = scene.Shots[sample.OriginalIndex];

        if (!string.IsNullOrEmpty(shot.RegionName))
        {
            JumpToRegion(shot.RegionName);
            FloatingMenu.SetRegionSilent(shot.RegionName);
        }
        else
        {
            // Region-free shot (built-in scenes): the shot names a bare fractal
            // type. Switch the live view to it — otherwise playback stays on
            // whatever type the toolbar last showed and the shot's camera drives
            // the wrong set (e.g. the "Box" shot never appears until the user
            // hand-picks Mandelbox). The full setter snaps a fractal-appropriate
            // default framing; the shot's camera track then takes over on the bus.
            if (Main.SelectedFractalType != shot.FractalType)
                Main.SelectedFractalType = shot.FractalType;
        }
        if (!string.IsNullOrEmpty(shot.ThemeName))
        {
            Main.SetThemeName(shot.ThemeName);
            FloatingMenu.SetThemeSilent(shot.ThemeName);
        }

        // Per-shot tone-map override (S8) — pin it on the live params after the
        // region jump so it survives the shot; null inherits the region's.
        if (shot.ToneMap is { } tm)
        {
            var fx = Main.ViewState.FractalParameters.Lighting;
            fx.ToneMap = tm;
            Main.ViewState.FractalParameters.Lighting = fx;
        }

        var anim = string.IsNullOrEmpty(shot.AnimationName)
            ? null
            : _themeService.GetAnimation(shot.AnimationName!);

        // This shot's exact global start — seeds the S8 global-track sweep so it
        // continues mid-timeline across the cut instead of restarting.
        double shotStart = 0.0;
        var tl = _sceneTimeline;
        if (tl != null && sample.CurrentEntry >= 0 && sample.CurrentEntry < tl.Entries.Count)
            shotStart = tl.Entries[sample.CurrentEntry].StartTime;

        AnimationBusHost.LoadSceneShot(shot, anim, Main.ViewState.FractalParameters,
            scene.GlobalTracks, shotStart);
    }

    /// <summary>Animation Roadmap Sub-goal B — open the Region Editor for the
    /// currently-selected region. Metadata-only edit (geometry preserved);
    /// built-in regions clone into a new user region on save. The VM is rebuilt
    /// each call so it targets whatever region is selected now.</summary>
    public void ShowRegionEditor() => ShowRegionEditor(null);

    /// <summary>Open the Region Editor for an explicit region name. Null falls
    /// back to the toolbar / menu selection (the render-surface "Edit Region…"
    /// path). The Asset Manager (A2) passes the row's name directly.</summary>
    public void ShowRegionEditor(string? targetName)
    {
        string? name = targetName ?? FloatingMenu.SelectedRegion ?? Main.SelectedRegion;
        // FloatingMenu placeholder / header rows start with "—" and aren't
        // real regions — treat those as "nothing selected".
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("—", StringComparison.Ordinal))
        {
            MessageRequested?.Invoke(this, new ThemeMessageEventArgs(
                "Edit Region", "Select a region to edit first.", MessageSeverity.Info));
            return;
        }

        var model = _themeService.GetRegionForEdit(name);
        if (model == null)
        {
            MessageRequested?.Invoke(this, new ThemeMessageEventArgs(
                "Edit Region", $"Region \"{name}\" could not be loaded.", MessageSeverity.Warning));
            return;
        }

        // Live-view provider powers the editor's "Capture current view" (R3):
        // re-snap the region's stored geometry from the current camera on save.
        var vm = new RegionEditorViewModel(_themeService, model, () => Main.ViewState);
        vm.RegionSavedToLibrary += (_, savedName) =>
        {
            // Refresh the region combo (honours the active sort + type filter)
            // and select the saved name so the toolbar reflects the edit /
            // rename / clone immediately.
            FloatingMenu.RefreshRegions();
            FloatingMenu.SetRegionSilent(savedName);
            Main.SetRegionName(savedName);
            RefreshAssetManagerIfVisible();
        };
        vm.CloseRequested   += (_, _)    => IsRegionEditorVisible = false;
        vm.MessageRequested += (_, args) => MessageRequested?.Invoke(this, args);
        RegionEditor = vm;
        IsRegionEditorVisible = true;
    }

    /// <summary>Animation Roadmap Sub-goal A — open the read-only Asset Manager
    /// (phase A1). Built once; RefreshAssets on each Show so it reflects saves
    /// made since it was first opened.</summary>
    public void ShowAssetManager()
    {
        if (AssetManager == null)
        {
            var vm = new AssetManagerViewModel(_assetSources);
            vm.CloseRequested  += (_, _) => IsAssetManagerVisible = false;
            vm.OpenRequested   += (_, e) => EditAsset(e.Kind, e.Name);
            vm.ExportRequested += (_, e) => AssetBundleExportRequested?.Invoke(this, e);
            vm.ImportRequested += (_, _) => AssetBundleImportRequested?.Invoke(this, EventArgs.Empty);
            AssetManager = vm;
        }
        else
        {
            AssetManager.RefreshAssets();
        }
        IsAssetManagerVisible = true;
    }

    /// <summary>Asset Manager A2 — route a row to the type's own editor. Four
    /// types have shell-owned modeless editors that accept a name and are
    /// retargeted here directly (Region / Colour theme / Animation / Watermark).
    /// The source editors (User equation / Sandbox / UserBulb) and Slideshow
    /// configs are opened by the host — UI.Avalonia can't reach those open
    /// paths — via <see cref="AssetHostEditorRequested"/>.</summary>
    private void EditAsset(FracturingFog.Abstractions.Assets.AssetKind kind, string name)
    {
        switch (kind)
        {
            case FracturingFog.Abstractions.Assets.AssetKind.Region:
                ShowRegionEditor(name);
                break;

            case FracturingFog.Abstractions.Assets.AssetKind.ColorTheme:
                ShowColorThemeEditor();
                if (ColorThemeEditor != null) ColorThemeEditor.SelectedTheme = name;
                break;

            case FracturingFog.Abstractions.Assets.AssetKind.Animation:
                ShowAnimationEditor();
                if (AnimationEditor != null) AnimationEditor.SelectedAnimation = name;
                break;

            case FracturingFog.Abstractions.Assets.AssetKind.Watermark:
                ShowWatermarkEditor();
                if (WatermarkEditor != null) WatermarkEditor.SelectedWatermark = name;
                break;

            case FracturingFog.Abstractions.Assets.AssetKind.Scene:
                ShowSceneEditor(name);
                break;

            case FracturingFog.Abstractions.Assets.AssetKind.SlideshowConfig:
                // Make the clicked preset active so the Slideshow Settings
                // dialog (host-owned, opened via the shared event) opens on it.
                try
                {
                    var file = FracturingFog.Models.SlideshowConfigLibrary.Load();
                    file.ActiveName = name;
                    FracturingFog.Models.SlideshowConfigLibrary.Save(file);
                }
                catch { /* non-fatal — dialog just opens on the prior active */ }
                SlideshowSettingsRequested?.Invoke(this, EventArgs.Empty);
                break;

            default:
                // Source editors (UserEquation / SandboxEquation / UserBulb) edit
                // live params in host-owned windows UI.Avalonia can't reach.
                AssetHostEditorRequested?.Invoke(this,
                    new AssetHostEditorEventArgs(kind, name));
                break;
        }
    }

    /// <summary>Live refresh (Asset Manager deferred item): when an editor saves
    /// or deletes while the manager is open, re-enumerate the middle list so the
    /// change shows without a manual Refresh. No-op when the manager is hidden —
    /// the next Show re-enumerates anyway.</summary>
    private void RefreshAssetManagerIfVisible()
    {
        if (IsAssetManagerVisible) AssetManager?.RefreshAssets();
    }

    /// <summary>Raised for asset types whose editors the host owns (source
    /// editors + slideshow). The host (AvaloniaShellBootstrap) subscribes and
    /// opens the matching editor window.</summary>
    public event EventHandler<AssetHostEditorEventArgs>? AssetHostEditorRequested;

    /// <summary>Raised with an assembled Asset Manager export bundle (A3). The
    /// host shows a save picker and writes the zip bytes.</summary>
    public event EventHandler<AssetExportEventArgs>? AssetBundleExportRequested;

    /// <summary>Raised when the Asset Manager wants to import a bundle (A3). The
    /// host shows an open picker + overwrite prompt, reads the bytes, and calls
    /// <see cref="ImportAssetBundle"/> back with the result.</summary>
    public event EventHandler? AssetBundleImportRequested;

    /// <summary>Host entry point for bundle import: hands the read bytes to the
    /// live Asset Manager VM (which owns the source roster + the zip parse) and
    /// returns the per-entry tally for the host to report. No-op tally when the
    /// manager isn't open.</summary>
    public AssetImportSummary ImportAssetBundle(byte[] zipBytes, bool overwrite)
        => AssetManager?.ImportBundle(zipBytes, overwrite) ?? new AssetImportSummary();

    /// <summary>Raised when an editor wants to import assets of its own kind
    /// from a JSON file. The host shows an open picker + overwrite prompt,
    /// reads the text, and calls <see cref="ImportAssetsFromJson"/> back with
    /// the result.</summary>
    public event EventHandler<AssetJsonImportEventArgs>? AssetJsonImportRequested;

    /// <summary>Host entry point for a single-kind JSON import (the per-editor
    /// Import buttons). Routes every entry in the file through the kind's own
    /// <see cref="FracturingFog.Abstractions.Assets.IAssetSource"/> — the same
    /// importer the Asset Manager's bundle uses, so a file exported from either
    /// place lands identically — then refreshes the kind's editor list.
    ///
    /// Unlike <see cref="ImportAssetBundle"/> this needs no open Asset Manager:
    /// the shell holds the source roster directly.</summary>
    public AssetImportSummary ImportAssetsFromJson(
        FracturingFog.Abstractions.Assets.AssetKind kind, string json, bool overwrite)
    {
        var summary = new AssetImportSummary();

        FracturingFog.Abstractions.Assets.IAssetSource? source = null;
        foreach (var s in _assetSources)
            if (s.Kind == kind) { source = s; break; }
        if (source == null)
        {
            summary.Unreadable = true;
            return summary;
        }

        var entries = FracturingFog.Abstractions.Assets.AssetJsonFile.SplitEntries(json);
        if (entries.Count == 0)
        {
            // Empty vs malformed is indistinguishable after the split; both mean
            // "nothing usable in this file", which Describe() reports as such.
            summary.Unreadable = true;
            return summary;
        }

        foreach (var entry in entries)
            summary.Tally(source.ImportJson(entry, overwrite).Status);

        RefreshEditorListFor(kind);
        RefreshAssetManagerIfVisible();
        return summary;
    }

    // Post-import list refresh for the editor that raised the import. Only the
    // shell-owned editors with an Import button are wired; the host-owned source
    // editors (UserEquation / Sandbox / UserBulb) import against their store
    // singletons directly and refresh themselves.
    private void RefreshEditorListFor(FracturingFog.Abstractions.Assets.AssetKind kind)
    {
        switch (kind)
        {
            case FracturingFog.Abstractions.Assets.AssetKind.Scene:
                SceneEditor?.RefreshSceneNames();
                break;
            case FracturingFog.Abstractions.Assets.AssetKind.Animation:
                AnimationEditor?.RefreshAnimationNames();
                break;
            case FracturingFog.Abstractions.Assets.AssetKind.Watermark:
                WatermarkEditor?.RefreshWatermarkNames();
                // Imported watermarks are selectable from the main menu too.
                FloatingMenu.SetWatermarks(UserWatermarkStore.Instance.EnumerateNames());
                break;
        }
    }

    private void ShowFFClient()
    {
        if (FFClient == null)
        {
            FFClient = new FFClientViewModel(_themeService);
            // Close => hide via the shell visibility flag (same pattern as the
            // sibling floating windows). The view is a UserControl now and can
            // no longer self-close.
            FFClient.CloseRequested += (_, _) => IsFFClientVisible = false;
        }
        // Mirror MainViewModel's active custom watermark in so the form's
        // "Send custom watermark" checkbox has something to send.
        FFClient.ActiveWatermark = Main.ActiveCustomWatermark;
        IsFFClientVisible = true;
    }

    private void ShowServerAdmin()
    {
        if (ServerAdmin == null)
        {
            ServerAdmin = new ServerAdminViewModel();
            // Close => hide via the shell visibility flag (same pattern as the
            // sibling cluster windows). The view no longer self-closes now that
            // it is a UserControl hosted by MainWindow.SyncServerAdmin.
            ServerAdmin.CloseRequested += (_, _) => IsServerAdminVisible = false;
            // SAVM exposes a "Cluster Dashboard…" button; bounce that through
            // the shell so MainWindow's SyncClusterDashboard handles the
            // window lifecycle on the same visibility-flag pattern as the
            // other floating windows.
            ServerAdmin.OpenClusterDashboardRequested += (_, _) => ShowClusterDashboard();
            // D-5e — sibling launcher for the live cluster-knob editor.
            ServerAdmin.OpenMasterConfigRequested     += (_, _) => ShowMasterConfig();
        }
        IsServerAdminVisible = true;
    }

    private void ShowMasterConfig()
    {
        if (MasterConfig == null)
        {
            MasterConfig = new MasterConfigViewModel();
            MasterConfig.CloseRequested += (_, _) => IsMasterConfigVisible = false;
        }
        IsMasterConfigVisible = true;
    }

    private void ShowClusterDashboard()
    {
        if (ClusterDashboard == null)
        {
            ClusterDashboard = new ClusterDashboardViewModel();
            ClusterDashboard.CloseRequested      += (_, _)       => IsClusterDashboardVisible = false;
            // D-5c — dashboard surfaces two drill-in points: "All Jobs"
            // opens the full paginated list, and per-row "Detail" opens
            // the tile-map view scoped to one jobId. Both route through
            // the shell so MainWindow's SyncJobList / SyncJobDetail
            // handles the window lifecycle on the same flag pattern.
            ClusterDashboard.OpenJobListRequested  += (_, _)        => ShowJobList();
            ClusterDashboard.OpenJobDetailRequested += (_, jobId)   => ShowJobDetail(jobId);
            // D-5d — per-worker drill-in from the workers grid Open button.
            ClusterDashboard.OpenWorkerDetailRequested += (_, workerId) => ShowWorkerDetail(workerId);
        }
        IsClusterDashboardVisible = true;
    }

    private void ShowWorkerDetail(string workerId)
    {
        if (string.IsNullOrEmpty(workerId)) return;
        if (WorkerDetail == null)
        {
            WorkerDetail = new WorkerDetailViewModel(workerId);
            WorkerDetail.CloseRequested += (_, _) => IsWorkerDetailVisible = false;
        }
        else
        {
            // Single-instance window like JobDetailView: swap target id; the
            // setter clears live state + immediate-polls so the operator sees
            // fresh data without waiting for the 5 s timer.
            WorkerDetail.WorkerId = workerId;
        }
        IsWorkerDetailVisible = true;
    }

    private void ShowJobList()
    {
        if (JobList == null)
        {
            JobList = new JobListViewModel();
            JobList.CloseRequested          += (_, _)     => IsJobListVisible = false;
            // Same drill-in path from the list view as from the dashboard.
            JobList.OpenJobDetailRequested  += (_, jobId) => ShowJobDetail(jobId);
        }
        IsJobListVisible = true;
    }

    private void ShowJobDetail(string jobId)
    {
        if (string.IsNullOrEmpty(jobId)) return;
        if (JobDetail == null)
        {
            JobDetail = new JobDetailViewModel(jobId);
            JobDetail.CloseRequested += (_, _) => IsJobDetailVisible = false;
        }
        else
        {
            // Single-instance window: swap target jobId. The setter clears
            // tile/worker collections + kicks an immediate poll so the
            // operator sees fresh data without waiting for the 2 s timer.
            JobDetail.JobId = jobId;
        }
        IsJobDetailVisible = true;
    }

    private void ShowHelp()
    {
        // Toggle so the same command (right-click "Help…", F1) flips the
        // floating help window on/off rather than only opening it.
        if (IsHelpVisible) { IsHelpVisible = false; return; }
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

    /// <summary>ColorGen menu/toolbar entry was invoked. Host opens the
    /// dedicated ColorGenEditorView (single-instance modeless window).</summary>
    public event EventHandler? OpenColorGenEditorRequested;

    /// <summary>Editor wants to open the ImagePalette dialog. Host owns the
    /// extraction pipeline + the System.Drawing bridge; it pops the view,
    /// runs extraction, then fills <see cref="ThemeFromImageEventArgs.Stops"/>
    /// before returning.</summary>
    public event EventHandler<ThemeFromImageEventArgs>? FromImageRequested;

    /// <summary>Editor wants to save a file (JSON theme export or C# class).
    /// Host pops a SaveFileDialog and writes the content.</summary>
    public event EventHandler<ThemeSaveFileEventArgs>? SaveFileRequested;

    /// <summary>Scene Engine Roadmap S8 polish — the Scene Editor asks the host
    /// to render + encode a scene offline (the host picks the output path and
    /// drives the Engine's SceneVideoRenderer).</summary>
    public event EventHandler<SceneExportEventArgs>? ExportSceneRequested;

    /// <summary>Editor or other child VM wants to show a MessageBox.</summary>
    public event EventHandler<ThemeMessageEventArgs>? MessageRequested;

    /// <summary>Editor wants to import a palette file. Host pops an
    /// OpenFilePicker, parses the file, shows the Add/Replace prompt, and
    /// fills the args' Colors + Result before completing.</summary>
    public event EventHandler<ThemeImportPaletteEventArgs>? ImportPaletteRequested;

    /// <summary>Editor wants to export the current stops as a palette file.
    /// Host pops a SaveFilePicker and writes the format keyed off the
    /// chosen extension.</summary>
    public event EventHandler<ThemeExportPaletteEventArgs>? ExportPaletteRequested;

    /// <summary>Editor wants the user to pick a screen pixel (eyedropper).
    /// Host installs the global mouse hook and fills PickedR/G/B before
    /// completing. PickedR null indicates the user cancelled.</summary>
    public event EventHandler<ThemeSampleColorEventArgs>? SampleColorRequested;

    /// <summary>Color Theme Editor raises this when dirty and the user picks
    /// a different theme or tries to close the window. Host shows the modal
    /// Save/Discard/Cancel prompt and signals the args' completion.</summary>
    public event EventHandler<UnsavedChangesPromptEventArgs>? UnsavedChangesPromptRequested;

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

    /// <summary>Render a wallpaper-sized image at the virtual-screen union of
    /// every connected monitor, regardless of the current window state. Host
    /// reads the screen bounds off the active Window, then runs
    /// <c>PosterRenderer</c> offscreen at the computed dimensions. Use this on
    /// Linux/GNOME where Span mode cannot overlay the shell's top bar + dock.</summary>
    public event EventHandler? WallpaperScreenshotRequested;

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

    /// <summary>Open the general application-settings dialog. Host seeds it
    /// from the persisted AnimationSettings and writes back on OK.</summary>
    public event EventHandler? AppSettingsRequested;

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
#pragma warning disable CS0067 // raised by host subscribing via reflection / future wiring
    public event EventHandler<(int Width, int Height)>? ResizeRequested;
#pragma warning restore CS0067

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

    /// <summary>User asked for the standalone Volumetric Lighting &amp; FX panel.
    /// Host pops <c>LightingFxDialog</c> bound to a <c>FractalParamsViewModel</c>
    /// over the shared ViewState — the Lighting/FX block is type-independent, so
    /// this stays open across fractal-type changes (unlike Fractal Params).</summary>
    public event EventHandler? LightingFxRequested;

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

    /// <summary>Build a <see cref="VideoZoomRequest"/> from the unified
    /// <see cref="SlideshowConfig"/> + active <see cref="VideoSettingsConfig"/>
    /// and start the auto video slideshow. Pushes adaptive-sweep schedule onto
    /// the controller so per-leg ramps fire as requested.</summary>
    /// <summary>True while the video engine is running (single-shot or slideshow).</summary>
    public bool IsVideoRunning => _video is { IsRunning: true };

    /// <summary>Stop the running video engine (single-shot or slideshow). No-op when idle.</summary>
    public void StopVideo() => _video?.Stop();

    public void StartVideoSlideshowFromConfig(SlideshowConfig config)
    {
        if (_video == null || config == null) return;
        if (_video.IsRunning) return;

        var v = config.Video ?? new VideoSettingsConfig();
        double secs = v.SecondsPerLeg > 0 ? v.SecondsPerLeg : 30.0;

        var req = new VideoZoomRequest
        {
            IsSlideshow = true,
            Seconds = secs,
            SlideshowSecondsOverride = secs,
            IsConstantRate = v.ConstantRate,
            IsReverse = v.Reverse,
            TaaSmoothing = v.TaaSmoothing,
            BandDither = v.BandDither,
            BandDitherStrength = v.BandDitherStrength,
            IterCapMode = v.IterCapMode,
            UseRegionWatermark = config.Timing.UseRegionWatermark,
            ThemeFadeEnabled = v.ThemeFadeEnabled,
            ThemesPerLeg = v.ThemesPerLeg,
            EnableAnimations = config.EnableAnimations,
            IncludedAnimations = config.IncludedAnimations,
            FilterAnimations = config.FilterAnimations,
            RandomizeAnimationsByFractalType = config.RandomizeAnimationsByFractalType,
            // Region / theme restrictions — without these the video slideshow
            // cycled the whole library, ignoring a preset that pinned one
            // region + one theme (#45).
            IncludedRegions = config.IncludedRegions,
            IncludedColorThemes = config.IncludedColorThemes,
            FilterFractalTypes = config.FilterFractalTypes,
            FilterQualityPresets = config.FilterQualityPresets,
        };

        _video.VideoSweepConfig = config.AdaptiveSweep;
        _video.VideoAdaptiveValueSink = val => Dispatcher.UIThread.Post(() => FloatingMenu.Adaptive = val);

        if (config.PostFx.Enabled && config.PostFx.Values != null)
        {
            var pv = config.PostFx.Values;
            if (pv.TryGetValue("brightness", out var b)) FloatingMenu.Brightness = (int)Math.Round(b);
            if (pv.TryGetValue("contrast", out var c)) FloatingMenu.Contrast = (int)Math.Round(c);
            if (pv.TryGetValue("adaptive", out var a)) FloatingMenu.Adaptive = (int)Math.Round(a);
        }

        StartVideoFromRequest(req);
    }

    /// <summary>Re-pull region names from the service into the menu combo.
    /// Called by the host after a successful import.</summary>
    public void RefreshRegionListsFromService()
    {
        FloatingMenu.RefreshRegions();
    }

    // ── Nav history (Backspace = go back) ───────────────────────────────
    //
    // Captures discrete navigations (region jump, coord Go, fractal-type
    // change, reset, pan/zoom settle) into a stack of the last 10 view
    // states so Backspace pops the previous one and restores it. Post-hoc
    // model: every observed nav records the just-displayed state as the
    // history entry — the "current" cache holds what we last recorded so
    // we can push it before overwriting with the new state.
    private sealed record NavSnapshot(
        double Cx, double CxLo, double Cx2, double Cx3,
        double Cy, double CyLo, double Cy2, double Cy3,
        double Zoom,
        global::FracturingFog.FractalType Type,
        string? QualityName,
        bool IterLocked,
        int LockedIterations,
        string? RegionName);

    private const int MaxNavHistory = 10;
    private const int NavSettleDebounceMs = 700;
    private readonly System.Collections.Generic.LinkedList<NavSnapshot> _navHistory = new();
    private NavSnapshot? _navLastSettled;
    private bool _navigatingBack;
    private global::System.Threading.Timer? _navSettleDebounce;

    private NavSnapshot CaptureNavSnapshot()
    {
        var s = Main.ViewState;
        return new NavSnapshot(
            s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3,
            s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3,
            s.Zoom, s.FractalType, s.Quality?.Name,
            s.IterLocked, s.LockedIterations,
            Main.SelectedRegion);
    }

    /// <summary>Record the current view as the most-recent settled
    /// navigation. The previous "settled" entry rolls into the history stack
    /// so Backspace can pop back to it. No-op while a back-restore is in
    /// flight (prevents the restore itself from polluting history).</summary>
    public void RecordNavChange()
    {
        if (_navigatingBack) return;
        var current = CaptureNavSnapshot();
        if (_navLastSettled != null && !_navLastSettled.Equals(current))
        {
            _navHistory.AddFirst(_navLastSettled);
            while (_navHistory.Count > MaxNavHistory) _navHistory.RemoveLast();
        }
        _navLastSettled = current;
    }

    /// <summary>Pop the previous nav state and apply it. Returns false when
    /// the history is empty (Backspace becomes a no-op at startup).</summary>
    public bool GoBack()
    {
        if (_navHistory.Count == 0) return false;
        var snap = _navHistory.First!.Value;
        _navHistory.RemoveFirst();
        ApplyNavSnapshot(snap);
        return true;
    }

    private void ApplyNavSnapshot(NavSnapshot snap)
    {
        _navigatingBack = true;
        try
        {
            var s = Main.ViewState;
            s.CenterX = snap.Cx; s.CenterXLo = snap.CxLo; s.CenterX2 = snap.Cx2; s.CenterX3 = snap.Cx3;
            s.CenterY = snap.Cy; s.CenterYLo = snap.CyLo; s.CenterY2 = snap.Cy2; s.CenterY3 = snap.Cy3;
            s.Zoom = snap.Zoom;
            s.FractalType = snap.Type;
            s.IterLocked = snap.IterLocked;
            s.LockedIterations = snap.LockedIterations;
            if (!string.IsNullOrEmpty(snap.QualityName))
            {
                var q = QualityPreset.FromName(snap.QualityName);
                if (q != null)
                {
                    s.Quality = q;
                    Main.SetQualitySilent(q);
                    FloatingMenu.SetQualitySilent(q.Name);
                }
            }
            Main.SetFractalTypeSilent(snap.Type);
            Main.SetRegionName(snap.RegionName);
            FloatingMenu.SetRegionSilent(snap.RegionName);
            Main.SetIterLockSilent(snap.IterLocked, snap.LockedIterations);
            FloatingMenu.SetIterLockSilent(snap.IterLocked, snap.LockedIterations);
            _navLastSettled = snap;
            Main.RenderHost.Trigger();
        }
        finally { _navigatingBack = false; }
    }

    public void Dispose()
    {
        Main.Dispose();
    }
}
