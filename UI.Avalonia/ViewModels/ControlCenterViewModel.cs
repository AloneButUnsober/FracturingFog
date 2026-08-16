// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;

using ReactiveUI;

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
            Parameters  = fp,

            // Fidelity-gap inputs (#362). These live fx have no 2D batch flag.
            ThemeIsUnsaved      = string.IsNullOrWhiteSpace(main.SelectedTheme),
            ReliefEnabled       = fp?.Relief2DEnabled ?? false,
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
