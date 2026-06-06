// ViewModels/PaletteBuilderViewModel.cs
//
// Extends ImagePaletteViewModel with:
//   • Generic Export-by-format-id command (replaces single GeneratePdf).
//   • Preset save / load / delete via PresetStore.
//   • Recent-files MRU exposed for the UI menu.
//   • Auto-extract debounced re-run on option change (Phase 0.3).
//
// The base VM owns extraction state, commands, and option properties; this
// subclass only layers app-level concerns (persistence + side-effect commands).

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;

using FracturingFog.Imaging;
using FracturingFog.UI.Avalonia.ViewModels;
using PaletteBuilder.Models;
using PaletteBuilder.Services;
using ReactiveUI;

namespace PaletteBuilder.ViewModels;

public class PaletteBuilderViewModel : ImagePaletteViewModel
{
    private readonly PresetStore _store;
    private readonly IPaletteExtractionService _serviceRef;

    public PaletteBuilderViewModel(IPaletteExtractionService service)
        : this(service, new PresetStore()) { }

    public PaletteBuilderViewModel(IPaletteExtractionService service, PresetStore store) : base(service)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _serviceRef = service;

        ExportCommand = ReactiveCommand.Create<string>(OnExport);
        SavePresetCommand = ReactiveCommand.Create<string>(OnSavePreset);
        LoadPresetCommand = ReactiveCommand.Create<string>(OnLoadPreset);
        DeletePresetCommand = ReactiveCommand.Create<string>(OnDeletePreset);

        ExportFormats = new ReadOnlyCollection<ExportFormatVm>(
            ExporterRegistry.Exporters
                .Select(e => new ExportFormatVm(e.Id, e.DisplayName, e.Extension))
                .ToList());

        RefreshPresetNames();
        RefreshRecentFiles();
        WireAutoExtract();

        UndoCommand = ReactiveCommand.Create(DoUndo, this.WhenAnyValue(x => x.CanUndo));
        RedoCommand = ReactiveCommand.Create(DoRedo, this.WhenAnyValue(x => x.CanRedo));
        WireUndoSnapshots();
    }

    private void WireUndoSnapshots()
    {
        // Push an undo snapshot whenever a user-tunable option changes —
        // throttled so a slider drag becomes one snapshot, not 80.
        var options = Observable.Merge(
            this.WhenAnyValue(x => x.MethodIndex).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.ColorCount).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.SpaceIndex).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.DownsampleMax).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.SortIndex).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.DedupDeltaE).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.WeightedPositions).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.ExcludeNearBlack).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.ExcludeNearWhite).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.DedupMetricIndex).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.GammaCorrect).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.Bandwidth).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.DbscanEpsilon).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.DbscanMinPts).Select(_ => System.Reactive.Unit.Default),
            this.WhenAnyValue(x => x.SpatialWeight).Select(_ => System.Reactive.Unit.Default));

        options
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => PushUndoSnapshot());
    }

    // ── Export ─────────────────────────────────────────────────────────

    public IReadOnlyList<ExportFormatVm> ExportFormats { get; }

    private ExportFormatVm? _selectedExportFormat;
    public ExportFormatVm? SelectedExportFormat
    {
        get => _selectedExportFormat ?? ExportFormats.FirstOrDefault();
        set => this.RaiseAndSetIfChanged(ref _selectedExportFormat, value);
    }

    public ReactiveCommand<string, Unit> ExportCommand { get; }

    // ── Picker mode ────────────────────────────────────────────────────
    //
    // When the standalone PaletteBuilder.exe owns the window, PickerMode
    // stays false: the main menu, presets, Export controls, status bar are
    // all visible and the bottom row offers "Export… / Close".
    //
    // When the main FracturingFog host opens MainWindow as the "From Image…"
    // picker dialog, PickerMode is true: the host wants the dialog to
    // return a chosen palette via the existing ApplyCommand /
    // ResultAccepted contract. Menu / Export / Status bar collapse;
    // bottom row offers "Apply / Cancel" instead.
    //
    // Two derived booleans (PickerMode + StandaloneMode = !PickerMode) are
    // exposed so XAML can use them as IsVisible bindings without needing a
    // value converter — Avalonia bindings only accept a path string.

    private bool _pickerMode;
    public bool PickerMode
    {
        get => _pickerMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _pickerMode, value);
            this.RaisePropertyChanged(nameof(StandaloneMode));
        }
    }

    public bool StandaloneMode => !_pickerMode;

    /// <summary>
    /// Fired when the user clicks Export with a chosen format id. Host listens,
    /// shows a save-file picker, and calls back into <see cref="PerformExport"/>.
    /// </summary>
    public event EventHandler<ExportRequestedEventArgs>? ExportRequested;

    private void OnExport(string formatId)
    {
        if (!CanApply || SelectedResult is null) return;
        var fmt = ExporterRegistry.FindById(formatId);
        if (fmt is null) return;
        // Honour Temperature/Tint sliders — exporters receive the adjusted
        // swatches the user actually sees on screen.
        var swatches = GetAdjustedSwatches();
        ExportRequested?.Invoke(this, new ExportRequestedEventArgs(fmt, swatches, SelectedResult.Stops, SelectedResult.Name, SourcePath));
    }

    /// <summary>
    /// Host calls this after picking a save path. Runs the exporter inline so
    /// errors propagate to the caller's try/catch.
    /// </summary>
    public static void PerformExport(IPaletteExporter exporter,
                                     string path,
                                     IReadOnlyList<(byte R, byte G, byte B)> swatches,
                                     IReadOnlyList<PaletteStop> stops,
                                     PaletteExportContext? context)
    {
        exporter.Export(path, swatches, stops, context);
    }

    // ── Presets ────────────────────────────────────────────────────────

    public ObservableCollection<string> PresetNames { get; } = new();

    public ReactiveCommand<string, Unit> SavePresetCommand { get; }
    public ReactiveCommand<string, Unit> LoadPresetCommand { get; }
    public ReactiveCommand<string, Unit> DeletePresetCommand { get; }

    public ExtractionPreset CaptureCurrentAsPreset(string name) => new()
    {
        Name = name,
        MethodIndex = MethodIndex,
        ColorCount = ColorCount,
        SpaceIndex = SpaceIndex,
        DownsampleMax = DownsampleMax,
        SortIndex = SortIndex,
        DedupDeltaE = DedupDeltaE,
        WeightedPositions = WeightedPositions,
        ExcludeNearBlack = ExcludeNearBlack,
        ExcludeNearWhite = ExcludeNearWhite,
        DedupMetricIndex = DedupMetricIndex,
        GammaCorrect = GammaCorrect,
        Bandwidth = Bandwidth,
        DbscanEpsilon = DbscanEpsilon,
        DbscanMinPts = DbscanMinPts,
        SpatialWeight = SpatialWeight,
        ExcludeTransparent = ExcludeTransparent,
        MinSaturation = MinSaturation,
        MaxSaturation = MaxSaturation,
        MinLightness = MinLightness,
        MaxLightness = MaxLightness,
        UseSaliency = UseSaliency,
        SaliencyThreshold = SaliencyThreshold,
    };

    public void ApplyPreset(ExtractionPreset preset)
    {
        if (preset == null) return;
        MethodIndex = preset.MethodIndex;
        ColorCount = preset.ColorCount;
        SpaceIndex = preset.SpaceIndex;
        DownsampleMax = preset.DownsampleMax;
        SortIndex = preset.SortIndex;
        DedupDeltaE = preset.DedupDeltaE;
        WeightedPositions = preset.WeightedPositions;
        ExcludeNearBlack = preset.ExcludeNearBlack;
        ExcludeNearWhite = preset.ExcludeNearWhite;
        DedupMetricIndex = preset.DedupMetricIndex;
        GammaCorrect = preset.GammaCorrect;
        Bandwidth = preset.Bandwidth;
        DbscanEpsilon = preset.DbscanEpsilon;
        DbscanMinPts = preset.DbscanMinPts;
        SpatialWeight = preset.SpatialWeight;
        ExcludeTransparent = preset.ExcludeTransparent;
        MinSaturation = preset.MinSaturation;
        MaxSaturation = preset.MaxSaturation;
        MinLightness = preset.MinLightness;
        MaxLightness = preset.MaxLightness;
        UseSaliency = preset.UseSaliency;
        SaliencyThreshold = preset.SaliencyThreshold;
    }

    private void OnSavePreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.SavePreset(CaptureCurrentAsPreset(name));
        RefreshPresetNames();
    }

    private void OnLoadPreset(string name)
    {
        var p = _store.LoadPreset(name);
        if (p != null) ApplyPreset(p);
    }

    private void OnDeletePreset(string name)
    {
        if (_store.DeletePreset(name)) RefreshPresetNames();
    }

    private void RefreshPresetNames()
    {
        PresetNames.Clear();
        foreach (var n in _store.ListPresetNames()) PresetNames.Add(n);
    }

    // ── Recent files ───────────────────────────────────────────────────

    public ObservableCollection<string> RecentFilePaths { get; } = new();

    public void NotifyImageLoaded(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _store.PushRecent(path);
        RefreshRecentFiles();
    }

    private void RefreshRecentFiles()
    {
        RecentFilePaths.Clear();
        foreach (var p in _store.LoadRecent().Paths) RecentFilePaths.Add(p);
    }

    // ── Phase 7.2 — undo/redo ──────────────────────────────────────────

    private readonly Stack<ExtractionPreset> _undo = new();
    private readonly Stack<ExtractionPreset> _redo = new();
    private bool _suspendSnapshot;

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>? UndoCommand { get; private set; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit>? RedoCommand { get; private set; }

    private bool _canUndo;
    public bool CanUndo
    {
        get => _canUndo;
        private set => this.RaiseAndSetIfChanged(ref _canUndo, value);
    }

    private bool _canRedo;
    public bool CanRedo
    {
        get => _canRedo;
        private set => this.RaiseAndSetIfChanged(ref _canRedo, value);
    }

    private void PushUndoSnapshot()
    {
        if (_suspendSnapshot) return;
        _undo.Push(CaptureCurrentAsPreset("snapshot"));
        if (_undo.Count > 50) // cap memory
        {
            var trimmed = _undo.ToArray();
            _undo.Clear();
            for (int i = trimmed.Length - 50; i >= 0; i--) _undo.Push(trimmed[i]);
        }
        _redo.Clear();
        UpdateUndoRedoFlags();
    }

    private void UpdateUndoRedoFlags()
    {
        CanUndo = _undo.Count > 0;
        CanRedo = _redo.Count > 0;
    }

    private void DoUndo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(CaptureCurrentAsPreset("redo"));
        _suspendSnapshot = true;
        try { ApplyPreset(_undo.Pop()); } finally { _suspendSnapshot = false; }
        UpdateUndoRedoFlags();
    }

    private void DoRedo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(CaptureCurrentAsPreset("undo"));
        _suspendSnapshot = true;
        try { ApplyPreset(_redo.Pop()); } finally { _suspendSnapshot = false; }
        UpdateUndoRedoFlags();
    }

    // ── Phase 7.3 — hex paste seed palette ─────────────────────────────

    /// <summary>
    /// Parse a comma/space/newline-separated list of `#RRGGBB` (or `RRGGBB`)
    /// strings and inject as a synthetic palette result, bypassing extraction.
    /// </summary>
    public void SeedFromHexList(string hexCsv)
    {
        var colors = new List<(byte, byte, byte)>();
        var parts = hexCsv.Split(new[] { ',', ';', ' ', '\t', '\n', '\r' },
                                 StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            string s = raw.Trim().TrimStart('#');
            if (s.Length == 3)
                s = "" + s[0] + s[0] + s[1] + s[1] + s[2] + s[2];
            if (s.Length != 6) continue;
            if (!byte.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)) continue;
            if (!byte.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)) continue;
            if (!byte.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b)) continue;
            colors.Add((r, g, b));
        }
        if (colors.Count == 0) return;

        // Build a synthetic PaletteExtractionResult so existing result-row UI
        // and exporters keep working unchanged.
        var swatches = new PaletteSwatch[colors.Count];
        var stops = new PaletteStop[colors.Count];
        for (int i = 0; i < colors.Count; i++)
        {
            var (r, g, b) = colors[i];
            swatches[i] = new PaletteSwatch(r, g, b, 1);
            float pos = colors.Count == 1 ? 0f : (float)i / (colors.Count - 1);
            stops[i] = new PaletteStop(pos, r, g, b);
        }
        var result = new PaletteExtractionResult
        {
            MethodName = "Pasted hex",
            Palette = swatches,
            Stops = stops,
        };

        Results.Clear();
        var row = new FracturingFog.UI.Avalonia.ViewModels.PaletteResultViewModel(result, exclusiveSelect: false, parent: this) { IsSelected = true };
        Results.Add(row);
        SelectedResult = row;
        StatusBarText = $"Pasted {colors.Count} colors";
    }

    // ── Phase 7.7 — status bar ─────────────────────────────────────────

    private string _statusBarText = "Ready";
    public string StatusBarText
    {
        get => _statusBarText;
        set => this.RaiseAndSetIfChanged(ref _statusBarText, value);
    }

    /// <summary>Snapshot ExtractAll output for the PDF comparison-page export path.</summary>
    public IReadOnlyList<(string Method, IReadOnlyList<(byte R, byte G, byte B)> Swatches, IReadOnlyList<PaletteStop> Stops)>
        RunAllForExport()
    {
        if (!HasImage) return Array.Empty<(string, IReadOnlyList<(byte, byte, byte)>, IReadOnlyList<PaletteStop>)>();
        var request = BuildExportRequest();
        var all = _serviceRef.ExtractAll(request);
        var rows = new List<(string, IReadOnlyList<(byte R, byte G, byte B)>, IReadOnlyList<PaletteStop>)>(all.Count);
        foreach (var r in all)
        {
            var sw = new List<(byte, byte, byte)>(r.Palette.Count);
            foreach (var p in r.Palette) sw.Add((p.R, p.G, p.B));
            // Temp/tint applied to each method consistently.
            var adjusted = PaletteAdjustments.ApplyAll(sw, Temperature, Tint);
            rows.Add((r.MethodName, adjusted, r.Stops));
        }
        return rows;
    }

    /// <summary>
    /// Build a PaletteExtractionRequest reflecting current option state.
    /// Mirrors BuildRequest on the base VM but accessible from this class.
    /// </summary>
    private PaletteExtractionRequest BuildExportRequest()
    {
        return new PaletteExtractionRequest
        {
            SourcePath = SourcePath ?? "",
            MethodIndex = MethodIndex,
            ColorCount = ColorCount,
            Space = SpaceIndex switch
            {
                0 => PaletteColorSpaceKind.Rgb,
                2 => PaletteColorSpaceKind.Hsl,
                3 => PaletteColorSpaceKind.OkLab,
                _ => PaletteColorSpaceKind.Lab,
            },
            DownsampleMaxDim = DownsampleMax,
            ExcludeNearBlack = ExcludeNearBlack,
            ExcludeNearWhite = ExcludeNearWhite,
            Sort = SortIndex switch
            {
                1 => StopSortKind.Hue,
                2 => StopSortKind.Luminance,
                3 => StopSortKind.ClusterSize,
                _ => StopSortKind.NearestNeighborChain,
            },
            DedupDeltaE = (float)DedupDeltaE,
            WeightedPositions = WeightedPositions,
            DedupMetric = DedupMetricIndex == 1 ? DeltaEMetricKind.DeltaE2000 : DeltaEMetricKind.DeltaE76,
            GammaCorrect = GammaCorrect,
            Bandwidth = (float)Bandwidth,
            DbscanEpsilon = (float)DbscanEpsilon,
            DbscanMinPts = DbscanMinPts,
            SpatialWeight = (float)SpatialWeight,
            ExcludeTransparent = ExcludeTransparent,
            MinSaturation = (float)MinSaturation,
            MaxSaturation = (float)MaxSaturation,
            MinLightness = (float)MinLightness,
            MaxLightness = (float)MaxLightness,
            RoiX = (float)RoiX,
            RoiY = (float)RoiY,
            RoiWidth = (float)RoiWidth,
            RoiHeight = (float)RoiHeight,
            UseSaliency = UseSaliency,
            SaliencyThreshold = (float)SaliencyThreshold,
        };
    }

    public void NotifyExtractCompleted(long elapsedMs, int swatchCount, string? methodName)
    {
        StatusBarText = methodName is null
            ? $"Extract took {elapsedMs} ms → {swatchCount} swatches"
            : $"{methodName}: {elapsedMs} ms → {swatchCount} swatches";
    }

    // ── Phase 4 — palette adjustments ──────────────────────────────────

    private double _temperature;
    public double Temperature
    {
        get => _temperature;
        set
        {
            this.RaiseAndSetIfChanged(ref _temperature, Math.Clamp(value, -1.0, 1.0));
            NotifyAdjustmentsChanged();
        }
    }

    private double _tint;
    public double Tint
    {
        get => _tint;
        set
        {
            this.RaiseAndSetIfChanged(ref _tint, Math.Clamp(value, -1.0, 1.0));
            NotifyAdjustmentsChanged();
        }
    }

    public override (byte R, byte G, byte B) AdjustForDisplay((byte R, byte G, byte B) c)
        => PaletteBuilder.Services.PaletteAdjustments.Apply(c, _temperature, _tint);

    private void NotifyAdjustmentsChanged()
    {
        foreach (var r in Results) r.NotifyStopsChanged();
    }

    // 0=sRGB 1=Lab 2=OkLab — gradient interpolation space for previews + PDF.
    private int _gradientInterpolationIndex;
    public int GradientInterpolationIndex
    {
        get => _gradientInterpolationIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            this.RaiseAndSetIfChanged(ref _gradientInterpolationIndex, clamped);

            // Update shared static for PDF exporter…
            FracturingFog.Imaging.PaletteExtraction.GradientRenderSettings.Space = clamped switch
            {
                1 => FracturingFog.Imaging.PaletteExtraction.GradientInterpolationSpace.Lab,
                2 => FracturingFog.Imaging.PaletteExtraction.GradientInterpolationSpace.OkLab,
                _ => FracturingFog.Imaging.PaletteExtraction.GradientInterpolationSpace.Srgb,
            };

            // …and register a sampler delegate so the Avalonia GradientStripControl
            // (which can't see the extraction project) can call into our sampler.
            FracturingFog.UI.Avalonia.Controls.GradientRenderHook.Sampler = clamped == 0
                ? null   // sRGB → fall back to Avalonia native gradient brush
                : (stops, t) => FracturingFog.Imaging.PaletteExtraction.GradientInterpolation.Sample(
                    stops, t,
                    FracturingFog.Imaging.PaletteExtraction.GradientRenderSettings.Space);
        }
    }

    /// <summary>Returns the selected palette w/ Temperature + Tint applied (export-ready).</summary>
    public IReadOnlyList<(byte R, byte G, byte B)> GetAdjustedSwatches()
    {
        if (SelectedResult is null) return Array.Empty<(byte, byte, byte)>();
        // Honour Phase 4 stop edits when the editor is engaged on this row.
        var src = SelectedResult.EffectiveStops;
        var raw = new List<(byte, byte, byte)>(src.Count);
        foreach (var s in src) raw.Add((s.R, s.G, s.B));
        var dedup = new List<(byte, byte, byte)>(raw.Count);
        foreach (var c in raw) if (!dedup.Contains(c)) dedup.Add(c);
        return PaletteAdjustments.ApplyAll(dedup, Temperature, Tint);
    }

    // ── Auto-extract (Phase 0.3) ───────────────────────────────────────

    private bool _autoExtract;
    public bool AutoExtract
    {
        get => _autoExtract;
        set => this.RaiseAndSetIfChanged(ref _autoExtract, value);
    }

    private void WireAutoExtract()
    {
        // Throttle every option change to a single trailing re-extract.
        // Only fires when AutoExtract is on and the user has loaded an image.
        // 15 option props total — split into two WhenAnyValue groups (12-tuple
        // is the ReactiveUI cap) and merge so any change re-triggers.
        var coreChanges = this.WhenAnyValue(
                x => x.MethodIndex,
                x => x.ColorCount,
                x => x.SpaceIndex,
                x => x.DownsampleMax,
                x => x.SortIndex,
                x => x.DedupDeltaE,
                x => x.WeightedPositions,
                x => x.ExcludeNearBlack,
                x => x.ExcludeNearWhite,
                x => x.DedupMetricIndex,
                x => x.GammaCorrect,
                (_, _, _, _, _, _, _, _, _, _, _) => System.Reactive.Unit.Default);

        var extraChanges = this.WhenAnyValue(
                x => x.Bandwidth,
                x => x.DbscanEpsilon,
                x => x.DbscanMinPts,
                x => x.SpatialWeight,
                (_, _, _, _) => System.Reactive.Unit.Default);

        var filterChanges = this.WhenAnyValue(
                x => x.ExcludeTransparent,
                x => x.MinSaturation,
                x => x.MaxSaturation,
                x => x.MinLightness,
                x => x.MaxLightness,
                x => x.UseSaliency,
                x => x.SaliencyThreshold,
                (_, _, _, _, _, _, _) => System.Reactive.Unit.Default);

        var roiChanges = this.WhenAnyValue(
                x => x.RoiX,
                x => x.RoiY,
                x => x.RoiWidth,
                x => x.RoiHeight,
                (_, _, _, _) => System.Reactive.Unit.Default);

        // Temperature/Tint are display-only — explicitly NOT in the
        // auto-extract trigger set (re-running k-means on a temp tweak
        // would be wasteful and visually confusing).
        var optionChanges = Observable.Merge(coreChanges, extraChanges, filterChanges, roiChanges).Skip(1);

        optionChanges
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (AutoExtract && HasImage)
                    ExtractCommand.Execute().Subscribe(_ => { }, _ => { });
            });
    }
}

/// <summary>One entry in the Export-format dropdown.</summary>
public sealed class ExportFormatVm
{
    public ExportFormatVm(string id, string displayName, string extension)
    {
        Id = id;
        DisplayName = displayName;
        Extension = extension;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Extension { get; }

    public override string ToString() => DisplayName;
}

public sealed class ExportRequestedEventArgs : EventArgs
{
    public ExportRequestedEventArgs(IPaletteExporter exporter,
                                    IReadOnlyList<(byte R, byte G, byte B)> swatches,
                                    IReadOnlyList<PaletteStop> stops,
                                    string methodName,
                                    string? sourceImagePath)
    {
        Exporter = exporter;
        Swatches = swatches;
        Stops = stops;
        MethodName = methodName;
        SourceImagePath = sourceImagePath;
    }

    public IPaletteExporter Exporter { get; }
    public IReadOnlyList<(byte R, byte G, byte B)> Swatches { get; }
    public IReadOnlyList<PaletteStop> Stops { get; }
    public string MethodName { get; }
    public string? SourceImagePath { get; }
}
