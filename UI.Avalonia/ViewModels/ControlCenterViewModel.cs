// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

using ReactiveUI;

using FracturingFog.Models;
using FracturingFog.UI.Avalonia.Services;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>The six top-level nav groups the Control Center collapses the old
/// ~11 menu sections into (UI-Overhaul-Plan §S1).</summary>
public enum ControlCenterSection
{
    View,
    Explore,
    ColorLight,
    Capture,
    Assets,
    Advanced,
}

/// <summary>A nav-rail entry. <see cref="IsAdvanced"/> items are hidden in
/// beginner mode (the "guiding hand").</summary>
public sealed record ControlCenterNavItem(
    ControlCenterSection Section,
    string Label,
    string Glyph,
    bool IsAdvanced);

/// <summary>
/// Phase S1 shell VM. The Control Center is a re-presentation of the existing
/// <see cref="FloatingMenuViewModel"/> (all commands + state are reused, so the
/// render window and the shell stay in lock-step) into a SplitView nav-rail +
/// sectioned content. Owns only the navigation + beginner/power state; every
/// actual control binds through <see cref="Menu"/>.
/// </summary>
public sealed class ControlCenterViewModel : ViewModelBase
{
    private static readonly ControlCenterNavItem[] AllNav =
    {
        new(ControlCenterSection.View,       "View",          "▦", false),
        new(ControlCenterSection.Explore,    "Explore",       "⌖", false),
        new(ControlCenterSection.ColorLight, "Color & Light", "◐", false),
        new(ControlCenterSection.Capture,    "Capture",       "◉", false),
        new(ControlCenterSection.Assets,     "Assets",        "▤", false),
        new(ControlCenterSection.Advanced,   "Advanced",      "⚙", true),
    };

    private ControlCenterSection _selectedSection = ControlCenterSection.View;
    private bool _isBeginnerMode = true;

    public ControlCenterViewModel(ShellViewModel shell)
    {
        Shell = shell;
        Menu = shell.FloatingMenu;
        Nav = new ObservableCollection<ControlCenterNavItem>();
        ToggleModeCommand = ReactiveCommand.Create(ToggleMode);
        DetachSectionCommand = ReactiveCommand.Create(
            () => DetachRequested?.Invoke(this, _selectedSection));
        GenerateCommandCommand = ReactiveCommand.Create(GenerateCommand);
        CopyCommandCommand = ReactiveCommand.Create(CopyCommand);

        Workspaces = new ObservableCollection<string>();
        SaveWorkspaceCommand   = ReactiveCommand.CreateFromTask(SaveWorkspaceAsync);
        ApplyWorkspaceCommand  = ReactiveCommand.Create(ApplyWorkspace);
        DeleteWorkspaceCommand = ReactiveCommand.CreateFromTask(DeleteWorkspaceAsync);
        ImportWorkspaceCommand = ReactiveCommand.CreateFromTask(ImportWorkspaceAsync);
        ExportWorkspaceCommand = ReactiveCommand.CreateFromTask(ExportWorkspaceAsync);
        RefreshWorkspaces();

        RebuildNav();
    }

    public ReactiveCommand<Unit, Unit> ToggleModeCommand { get; }

    /// <summary>S2 — pop the currently-selected section into its own floating
    /// window (2nd-monitor friendly). The view code-behind opens a
    /// PanelHostWindow hosting a fresh instance of the section's UserControl,
    /// bound to this same VM so the detached copy and the docked copy stay in
    /// lock-step.</summary>
    public ReactiveCommand<Unit, Unit> DetachSectionCommand { get; }

    /// <summary>Raised by <see cref="DetachSectionCommand"/> with the section to
    /// float. Handled in <c>ControlCenterView</c> code-behind (it owns the
    /// Avalonia window + control-instance factory).</summary>
    public event EventHandler<ControlCenterSection>? DetachRequested;

    /// <summary>Human label for a section — reused for the detached window's
    /// title bar so it matches the nav-rail entry.</summary>
    public static string LabelFor(ControlCenterSection section) =>
        AllNav.First(n => n.Section == section).Label;

    /// <summary>The shell VM — sections bind its Show* commands (Params,
    /// ColorGen, Asset Manager, Mini/Toy) that live outside FloatingMenu.</summary>
    public ShellViewModel Shell { get; }

    /// <summary>The shared FloatingMenu VM most sections bind through.</summary>
    public FloatingMenuViewModel Menu { get; }

    // ── Window-arrangement workspaces (#433 slice 3 — #471) ──────────────────
    //
    // Save the current window layout as a named preset, recall it, and
    // import/export single-preset files. The View section binds the droplist +
    // buttons here. The host wires the three Func delegates (name prompt + file
    // pickers) to its AvaloniaDialogs helpers, since UI.Avalonia can't reference
    // the Hosting layer directly.

    /// <summary>Saved workspace names for the droplist.</summary>
    public ObservableCollection<string> Workspaces { get; }

    private string? _selectedWorkspace;
    public string? SelectedWorkspace
    {
        get => _selectedWorkspace;
        set => this.RaiseAndSetIfChanged(ref _selectedWorkspace, value);
    }

    public ReactiveCommand<Unit, Unit> SaveWorkspaceCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyWorkspaceCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteWorkspaceCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportWorkspaceCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportWorkspaceCommand { get; }

    /// <summary>Host prompt for a workspace name on Save (default suggested).
    /// Returns null on cancel.</summary>
    public Func<string, Task<string?>>? WorkspaceNamePromptRequested;

    /// <summary>Host open-file picker for Import. Returns the chosen path or null.</summary>
    public Func<Task<string?>>? WorkspaceImportPathRequested;

    /// <summary>Host save-file picker for Export (default filename). Returns the
    /// chosen path or null.</summary>
    public Func<string, Task<string?>>? WorkspaceExportPathRequested;

    /// <summary>Host confirm dialog for Delete. Returns true to proceed.</summary>
    public Func<string, Task<bool>>? WorkspaceDeleteConfirmRequested;

    /// <summary>Reload the droplist from the library, preserving the current
    /// selection when it still exists (else falling back to the active preset).</summary>
    public void RefreshWorkspaces()
    {
        var file = WorkspaceLayoutLibrary.Load();
        var keep = SelectedWorkspace;

        Workspaces.Clear();
        foreach (var w in file.Layouts) Workspaces.Add(w.Name);

        SelectedWorkspace =
            keep != null && Workspaces.Contains(keep) ? keep
            : (file.ActiveName != null && Workspaces.Contains(file.ActiveName) ? file.ActiveName
            : Workspaces.FirstOrDefault());
    }

    private async Task SaveWorkspaceAsync()
    {
        string suggested = SelectedWorkspace ?? $"Workspace {Workspaces.Count + 1}";
        string? name = WorkspaceNamePromptRequested is { } prompt
            ? await prompt(suggested)
            : suggested;
        if (string.IsNullOrWhiteSpace(name)) return;

        var layout = WorkspaceService.Capture(name!, Shell);
        var file = WorkspaceLayoutLibrary.Load();
        WorkspaceLayoutLibrary.Upsert(file, layout);

        RefreshWorkspaces();
        SelectedWorkspace = name;
    }

    private void ApplyWorkspace()
    {
        if (string.IsNullOrWhiteSpace(SelectedWorkspace)) return;
        var file = WorkspaceLayoutLibrary.Load();
        var layout = WorkspaceLayoutLibrary.Get(file, SelectedWorkspace!);
        if (layout != null) WorkspaceService.Restore(layout, Shell);
    }

    private async Task DeleteWorkspaceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedWorkspace)) return;
        string name = SelectedWorkspace!;

        if (WorkspaceDeleteConfirmRequested is { } confirm && !await confirm(name))
            return;

        var file = WorkspaceLayoutLibrary.Load();
        WorkspaceLayoutLibrary.Delete(file, name);
        SelectedWorkspace = null;
        RefreshWorkspaces();
    }

    private async Task ImportWorkspaceAsync()
    {
        if (WorkspaceImportPathRequested is not { } pick) return;
        string? path = await pick();
        if (string.IsNullOrWhiteSpace(path)) return;

        var file = WorkspaceLayoutLibrary.Load();
        var names = WorkspaceLayoutLibrary.Import(file, path!);
        RefreshWorkspaces();
        if (names.Count > 0) SelectedWorkspace = names[^1];
    }

    private async Task ExportWorkspaceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedWorkspace)) return;
        if (WorkspaceExportPathRequested is not { } pick) return;

        string name = SelectedWorkspace!;
        string? path = await pick(name + ".json");
        if (string.IsNullOrWhiteSpace(path)) return;

        var file = WorkspaceLayoutLibrary.Load();
        WorkspaceLayoutLibrary.Export(file, name, path!);
    }

    /// <summary>Nav-rail entries, filtered by <see cref="IsBeginnerMode"/>.</summary>
    public ObservableCollection<ControlCenterNavItem> Nav { get; }

    public ControlCenterSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSection, value);
            RaiseSectionFlags();
        }
    }

    /// <summary>Beginner mode hides advanced nav groups + advanced sub-controls
    /// (the diagnostic bypasses, remote server/cluster, source-equation editors).
    /// Power mode reveals everything.</summary>
    public bool IsBeginnerMode
    {
        get => _isBeginnerMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBeginnerMode, value);
            this.RaisePropertyChanged(nameof(IsPowerMode));
            this.RaisePropertyChanged(nameof(ModeButtonLabel));
            this.RaisePropertyChanged(nameof(ModeHint));
            RebuildNav();
        }
    }

    public bool IsPowerMode => !_isBeginnerMode;

    /// <summary>Label for the beginner/power toggle button (shows the mode a
    /// click switches TO).</summary>
    public string ModeButtonLabel => _isBeginnerMode ? "Power mode" : "Beginner mode";

    /// <summary>Sub-label under the mode toggle describing the CURRENT mode.</summary>
    public string ModeHint => _isBeginnerMode ? "Essentials only" : "All tools shown";

    public void ToggleMode() => IsBeginnerMode = !IsBeginnerMode;

    // Section-visibility flags for the content ContentControl. Cheaper than a
    // converter and keeps the XAML declarative.
    public bool IsViewSection       => _selectedSection == ControlCenterSection.View;
    public bool IsExploreSection    => _selectedSection == ControlCenterSection.Explore;
    public bool IsColorLightSection => _selectedSection == ControlCenterSection.ColorLight;
    public bool IsCaptureSection    => _selectedSection == ControlCenterSection.Capture;
    public bool IsAssetsSection     => _selectedSection == ControlCenterSection.Assets;
    public bool IsAdvancedSection   => _selectedSection == ControlCenterSection.Advanced;

    private void RaiseSectionFlags()
    {
        this.RaisePropertyChanged(nameof(IsViewSection));
        this.RaisePropertyChanged(nameof(IsExploreSection));
        this.RaisePropertyChanged(nameof(IsColorLightSection));
        this.RaisePropertyChanged(nameof(IsCaptureSection));
        this.RaisePropertyChanged(nameof(IsAssetsSection));
        this.RaisePropertyChanged(nameof(IsAdvancedSection));
    }

    private void RebuildNav()
    {
        IEnumerable<ControlCenterNavItem> visible =
            _isBeginnerMode ? AllNav.Where(n => !n.IsAdvanced) : AllNav;

        Nav.Clear();
        foreach (var n in visible) Nav.Add(n);

        // If the current section was just hidden (switched to beginner while on
        // Advanced), fall back to the first visible group.
        if (Nav.All(n => n.Section != _selectedSection))
            SelectedSection = Nav[0].Section;
    }

    // ── CLI Command Builder (#361, slice of #64) ──────────────────────────────
    // Reads the live 2D configuration off the shell's MainViewModel and emits a
    // copy/paste `--batch` command that reproduces the current poster. MVP: 2D
    // image path only. Fx families with no batch flag (lighting / relief / etc.)
    // are not represented — #362 adds the gap-detection warning.

    public ReactiveCommand<Unit, Unit> GenerateCommandCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommandCommand { get; }

    private int _commandWidth = 1920;
    /// <summary>Poster width the generated command targets. Defaults to the
    /// batch default (1920); the live viewport size is unrelated to output size.</summary>
    public int CommandWidth
    {
        get => _commandWidth;
        set => this.RaiseAndSetIfChanged(ref _commandWidth, value < 1 ? 1 : value);
    }

    private int _commandHeight = 1080;
    /// <summary>Poster height the generated command targets (batch default 1080).</summary>
    public int CommandHeight
    {
        get => _commandHeight;
        set => this.RaiseAndSetIfChanged(ref _commandHeight, value < 1 ? 1 : value);
    }

    private bool _useFullExePath;
    /// <summary>When checked, lead the command with the full path to the running
    /// FracturingFog executable instead of the bare "FracturingFog" name.
    /// Default off. Toggling re-emits the command if one is already shown.</summary>
    public bool UseFullExePath
    {
        get => _useFullExePath;
        set
        {
            if (this.RaiseAndSetIfChangedReturnsChanged(ref _useFullExePath, value)
                && GeneratedCommand.Length > 0)
                GenerateCommand();   // keep the shown command in sync with the toggle
        }
    }

    /// <summary>The command leader: bare "FracturingFog" by default, or the full
    /// path to the running executable when <see cref="UseFullExePath"/> is on.
    /// Falls back to the bare name when the process path is unavailable.</summary>
    private string ResolveExecutableName()
    {
        if (!_useFullExePath) return "FracturingFog";
        string? path = System.Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? "FracturingFog" : path;
    }

    private string _generatedCommand = "";
    /// <summary>The last-generated command string, bound to a read-only field.</summary>
    public string GeneratedCommand
    {
        get => _generatedCommand;
        private set => this.RaiseAndSetIfChanged(ref _generatedCommand, value);
    }

    private string _commandGapWarning = "";
    /// <summary>Human-readable list of live fx the generated command cannot
    /// reproduce (#362). Empty when the 2D config is fully expressible. Bound to
    /// a yellow banner (#FFCC00 — colour-blind-safe, never red).</summary>
    public string CommandGapWarning
    {
        get => _commandGapWarning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _commandGapWarning, value);
            this.RaisePropertyChanged(nameof(HasCommandGaps));
        }
    }

    /// <summary>True when the last generate found unrepresented fx.</summary>
    public bool HasCommandGaps => _commandGapWarning.Length > 0;

    private void GenerateCommand()
    {
        var main = Shell.Main;
        var vs = main.ViewState;

        // Effective iteration count: honour an explicit lock / region override,
        // else the quality preset's zoom-derived count — matching what the live
        // calculator uses, so the poster does not drift.
        int iter = vs.IterLocked ? vs.LockedIterations
                 : vs.PreferredIterations > 0 ? vs.PreferredIterations
                 : vs.Quality?.ComputeIterations(vs.Zoom) ?? 0;

        var fp = vs.FractalParameters;

        var snap = new FracturingFog.Cli.BatchCommandSnapshot
        {
            ExecutableName = ResolveExecutableName(),
            Fractal     = main.SelectedFractalType,
            CenterX     = vs.CenterX,
            CenterY     = vs.CenterY,
            Zoom        = vs.Zoom,
            Iterations  = iter,
            ThemeName   = main.SelectedTheme ?? FracturingFog.Batch.BatchDefaults.ThemeName,
            QualityName = main.SelectedQuality?.Name ?? FracturingFog.Batch.BatchDefaults.QualityName,
            Width       = CommandWidth,
            Height      = CommandHeight,
            Brightness  = vs.Brightness,
            Contrast    = vs.Contrast,
            HistogramEq = vs.HistogramEq,
            InteriorAlpha = fp?.InteriorAlpha ?? 255,   // #363 — now emitted as a flag
            ViewTransform  = vs.ViewTransform,           // S2 (#389) — output tonemap
            ViewExposureEv = vs.ViewExposureEv,
            Parameters  = fp,

            // Relief core knobs (#363 — Tier-1). Emitted when relief is on.
            ReliefEnabled        = fp?.Relief2DEnabled ?? false,
            ReliefRaymarch       = fp?.Relief2DRaymarch ?? false,
            ReliefHeight         = fp?.Relief2DHeightScale ?? 1.0,
            ReliefDetailGain     = fp?.Relief2DDetailGain ?? 1.0,      // #518
            ReliefDetailRadius   = fp?.Relief2DDetailRadius ?? 0,      // #518
            ReliefHeightGamma    = fp?.Relief2DHeightGamma ?? 1.0,     // #518
            ReliefStrength       = fp?.Relief2DStrength ?? 1.0,
            ReliefLightAzimuth   = fp?.Relief2DLightAzimuthDeg ?? 135.0,
            ReliefLightElevation = fp?.Relief2DLightElevationDeg ?? 30.0,
            ReliefShadow         = fp?.Relief2DShadowStrength ?? 0.6,
            ReliefAbsolute       = fp?.Relief2DAbsolute ?? false,
            ReliefCameraAzimuth  = fp?.Relief2DCameraAzimuthDeg ?? 0.0,
            ReliefCameraElevation = fp?.Relief2DCameraElevationDeg ?? 45.0,
            ReliefCameraFov      = fp?.Relief2DCameraFovDeg ?? 50.0,
            ReliefCameraZoom     = fp?.Relief2DCameraZoom ?? 1.0,
            ReliefCameraOrtho    = fp?.Relief2DCameraOrthographic ?? false,
            ReliefFarDetail      = fp?.Relief2DFarDetail ?? 1.0,      // #520
            ReliefDofAperture    = fp?.Relief2DDofApertureRadius ?? 0.0,   // S3 (#389)
            ReliefDofFocus       = fp?.Relief2DDofFocusDistance ?? 0.0,
            ReliefFroxel         = fp?.Relief2DFroxelVolumetrics ?? false,   // S6 (#408)
            ReliefFroxelQuality  = fp?.Relief2DFroxelQuality ?? FracturingFog.Models.FroxelQuality.Balanced,   // S6 (#408)
            FogLightMask         = fp?.Lighting.VolumeLightMask ?? 0x7,      // S6 (#408)
            Transmission         = fp?.Lighting.Transmission ?? 0.0,        // S5 (#406)
            Ior                  = fp?.Lighting.Ior ?? 1.5,
            AbsorptionDistance   = fp?.Lighting.AbsorptionDistance ?? 1.0,
            AbsorptionColor      = fp?.Lighting.AbsorptionColor ?? 0xFFFFFFFFu,
            GlassInternalMarch   = fp?.Lighting.RefractInternalMarch ?? false,
            ReliefDenoiseIterations  = fp?.Relief2DDenoiseIterations ?? 0,   // S4 (#389)
            ReliefDenoiseColorSigma  = fp?.Relief2DDenoiseColorSigma ?? 0.10,
            ReliefDenoiseNormalSigma = fp?.Relief2DDenoiseNormalSigma ?? 0.30,
            ReliefDenoiseDepthSigma  = fp?.Relief2DDenoiseDepthSigma ?? 0.20,
            ReliefDenoiseAdaptiveSupersample = fp?.Relief2DDenoiseAdaptiveSupersample ?? false,   // S4 (#402)
            ReliefMotionBlur         = fp?.Relief2DMotionBlurStrength ?? 0.0,   // S1 (#398)
            ReliefMotionBlurSamples  = fp?.Relief2DMotionBlurSamples ?? 8,
            ReliefIsolate        = fp?.Relief2DIsolate ?? false,
            ReliefIsolateByDetail = fp?.Relief2DIsolateByDetail ?? true,
            ReliefIsolateThreshold = fp?.Relief2DDetailThreshold ?? 0.6,
            ReliefIsolateByColor = fp?.Relief2DIsolateByColor ?? false,
            ReliefIsolateColors  = fp?.Relief2DDropColorsCsv ?? "",
            ReliefIsolateTolerance = fp?.Relief2DColorTolerance ?? 0.12,

            // Fidelity-gap inputs (#362). These live fx have no 2D batch flag.
            ThemeIsUnsaved      = string.IsNullOrWhiteSpace(main.SelectedTheme),
            StereoActive        = fp != null && fp.Lighting.StereoMode != FracturingFog.Rendering.Lighting.StereoMode.Off,
            DomainWarpActive    = fp?.DomainWarpEnabled ?? false,   // #363 — now emitted as flags
            DomainWarpStrength  = fp?.DomainWarpStrength ?? 0.0,
            DomainWarpFrequency = fp?.DomainWarpFrequency ?? 1.0,
        };

        var report = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(snap);
        GeneratedCommand = report.Command;
        CommandGapWarning = report.HasGaps
            ? "Not represented (rendered output will differ): " + string.Join("; ", report.Gaps)
            : "";
    }

    private void CopyCommand()
    {
        if (string.IsNullOrEmpty(GeneratedCommand)) GenerateCommand();
        if (!string.IsNullOrEmpty(GeneratedCommand))
            Shell.RequestCopyToClipboard(GeneratedCommand);
    }
}
