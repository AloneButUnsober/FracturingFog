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

        ShowFloatingMenuCommand   = ReactiveCommand.Create(() => IsFloatingMenuVisible = !IsFloatingMenuVisible);
        ShowHelpCommand           = ReactiveCommand.Create(ShowHelp);
        ShowColorThemeEditorCommand = ReactiveCommand.Create(ShowColorThemeEditor);
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

    private void RefreshThemeListsFromService()
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

    public void Dispose()
    {
        Main.Dispose();
    }
}
