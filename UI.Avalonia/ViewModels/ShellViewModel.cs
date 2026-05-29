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
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.Render;
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
        FloatingMenu.SetThemes(_themeService.EnumerateThemeNames());
        FloatingMenu.SetRegions(_themeService.EnumerateRegionNames());
        // Quality combo lives on FloatingMenu but its presets come from
        // QualityPreset.All — the same list MainViewModel already exposes.
        FloatingMenu.SetQualities(QualityPreset.All.Select(q => q.Name));
        FloatingMenu.SetQualitySilent(Main.SelectedQuality?.Name);

        // ── Wire FloatingMenu → MainViewModel / ShellViewModel ───────────
        // Region/Theme picks: forward the name into MainViewModel so the
        // toolbar labels mirror the selection, then ask the host service to
        // actually apply (mutate ViewState for a region, push a new IColorMap
        // for a theme). Without these two calls the combos were label-only —
        // user saw no view change and the symptom looked like flaky bindings.
        FloatingMenu.RegionComboChanged += (_, name) =>
        {
            Main.SetRegionName(name);
            if (string.IsNullOrEmpty(name)) return;
            if (_themeService.ApplyRegion(name, Main.ViewState))
                Main.RenderHost.Trigger();
        };
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
        FloatingMenu.SaveViewClick     += (_, _) =>
        {
            var args = new ThemeMessageEventArgs(
                "Save View as Region",
                "Enter a name for this region (cancel to abort).",
                MessageSeverity.Question)
            { ExpectsConfirmation = true };
            SaveRegionRequested?.Invoke(this, args);
        };
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
            FloatingMenu.SetThemes(_themeService.EnumerateThemeNames());
            FloatingMenu.SetRegions(_themeService.EnumerateRegionNames());
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

        // FrameCompleted: refresh the menu's CX/CY/Zoom/Iter textboxes so
        // the user sees the live values without typing them manually. Skips
        // whichever box currently has focus — that's owned by ViewModelBase
        // consumers in the View layer; for now we just always overwrite.
        Main.RenderHost.FrameCompleted += (_, info) =>
        {
            FloatingMenu.UpdateCoords(
                info.CenterX.ToString("G12", CultureInfo.InvariantCulture),
                info.CenterY.ToString("G12", CultureInfo.InvariantCulture),
                info.Zoom.ToString("G6", CultureInfo.InvariantCulture),
                info.Iterations.ToString(CultureInfo.InvariantCulture));
        };

        ShowFloatingMenuCommand   = ReactiveCommand.Create(() => IsFloatingMenuVisible = !IsFloatingMenuVisible);
        ShowHelpCommand           = ReactiveCommand.Create(ShowHelp);
        ShowColorThemeEditorCommand = ReactiveCommand.Create(ShowColorThemeEditor);
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

    private void ApplyCoordsFromMenu()
    {
        bool changed = false;
        if (double.TryParse(FloatingMenu.CX, NumberStyles.Float, CultureInfo.InvariantCulture, out double cx))
        {
            Main.ViewState.CenterX = cx;
            Main.ViewState.CenterXLo = 0; Main.ViewState.CenterX2 = 0; Main.ViewState.CenterX3 = 0;
            changed = true;
        }
        if (double.TryParse(FloatingMenu.CY, NumberStyles.Float, CultureInfo.InvariantCulture, out double cy))
        {
            Main.ViewState.CenterY = cy;
            Main.ViewState.CenterYLo = 0; Main.ViewState.CenterY2 = 0; Main.ViewState.CenterY3 = 0;
            changed = true;
        }
        if (double.TryParse(FloatingMenu.Zoom, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom)
            && zoom > 0)
        {
            Main.ViewState.Zoom = zoom;
            changed = true;
        }
        if (int.TryParse(FloatingMenu.Iter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iter)
            && iter > 0)
        {
            Main.ViewState.IterLocked = true;
            Main.ViewState.LockedIterations = iter;
            Main.IterLocked = true;
            Main.LockedIterations = iter;
            changed = true;
        }
        if (changed) Main.RenderHost.Trigger();
    }

    public MainViewModel Main { get; }
    public FloatingMenuViewModel FloatingMenu { get; }

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

    // ── Top-level commands ────────────────────────────────────────────────

    public ReactiveCommand<Unit, bool> ShowFloatingMenuCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowHelpCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowColorThemeEditorCommand { get; }

    private void ShowColorThemeEditor()
    {
        if (ColorThemeEditor == null)
        {
            var vm = new ColorThemeEditorViewModel(_themeService,
                initialThemeName: Main.SelectedTheme,
                initialRegionName: Main.SelectedRegion);
            // Wire editor events that affect the main view.
            vm.RegionRequested        += (_, name) => Main.SetRegionName(name);
            vm.EditorThemeSelected    += (_, name) => Main.SetThemeName(name);
            vm.ThemeSavedToLibrary    += (_, _)    => RefreshThemeListsFromService();
            vm.HelpRequested          += (_, _)    => ShowHelp();
            // Preview pipe-through: ColorThemeEditor produces a ColorThemeDef,
            // the host translates it into an IColorMap on its IColorThemeService
            // impl and pushes onto the render host. The actual translation
            // lives outside the VM (host-owned) — we just relay.
            vm.PreviewRequested       += (_, def)  => ColorThemePreviewRequested?.Invoke(this, def);
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
        FloatingMenu.SetThemes(_themeService.EnumerateThemeNames());
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

    /// <summary>Re-pull region names from the service into the menu combo.
    /// Called by the host after a successful import.</summary>
    public void RefreshRegionListsFromService()
    {
        FloatingMenu.SetRegions(_themeService.EnumerateRegionNames());
    }

    public void Dispose()
    {
        Main.Dispose();
    }
}
