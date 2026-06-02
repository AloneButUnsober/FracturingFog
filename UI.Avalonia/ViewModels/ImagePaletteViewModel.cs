// ViewModels/ImagePaletteViewModel.cs
//
// Avalonia port of the legacy WinForms ImagePaletteDialog. Decoupled from
// System.Drawing + the palette-extractor classes via IPaletteExtractionService
// (defined in FracturingFog.Abstractions / FracturingFog.Imaging).
//
// VM responsibilities:
//   • Hold drag/drop or browse-supplied image path + preview bitmap.
//   • Hold extraction option state (method, color count, color space,
//     downsample, exclude-near-black/white, sort, dedup, weighted positions).
//   • Invoke the host service for single-method or compare-all runs.
//   • Surface results as a list of PaletteResultViewModel rows.
//   • Raise BrowseRequested for the file picker, ResultAccepted for the
//     final Apply, and Cancelled for dismissal. UI.Avalonia stays free of
//     OpenFileDialog and System.Drawing.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Media.Imaging;
using FracturingFog.Imaging;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class ImagePaletteViewModel : ViewModelBase
{
    private readonly IPaletteExtractionService _service;

    public ImagePaletteViewModel(IPaletteExtractionService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        MethodNames = new ReadOnlyCollection<string>(new List<string>(service.MethodNames));

        BrowseCommand = ReactiveCommand.Create(() => BrowseRequested?.Invoke(this, EventArgs.Empty));
        ExtractCommand = ReactiveCommand.Create(RunSingle);
        CompareAllCommand = ReactiveCommand.Create(RunCompareAll);
        ApplyCommand = ReactiveCommand.Create(OnApply);
        CancelCommand = ReactiveCommand.Create(OnCancel);
    }

    // ── Image state ────────────────────────────────────────────────────

    private string? _sourcePath;
    public string? SourcePath
    {
        get => _sourcePath;
        private set
        {
            this.RaiseAndSetIfChanged(ref _sourcePath, value);
            this.RaisePropertyChanged(nameof(HasImage));
            this.RaisePropertyChanged(nameof(FileLabel));
        }
    }

    public bool HasImage => !string.IsNullOrEmpty(_sourcePath);

    public string FileLabel => string.IsNullOrEmpty(_sourcePath)
        ? "(no image)"
        : System.IO.Path.GetFileName(_sourcePath);

    private Bitmap? _previewImage;
    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _previewImage, value);
            this.RaisePropertyChanged(nameof(ShowDropHint));
        }
    }

    public bool ShowDropHint => _previewImage is null;

    // ── Options ────────────────────────────────────────────────────────

    public IReadOnlyList<string> MethodNames { get; }

    private int _methodIndex;
    public int MethodIndex
    {
        get => _methodIndex;
        set => this.RaiseAndSetIfChanged(ref _methodIndex, Math.Clamp(value, 0, Math.Max(0, MethodNames.Count - 1)));
    }

    private int _colorCount = 8;
    public int ColorCount
    {
        get => _colorCount;
        set => this.RaiseAndSetIfChanged(ref _colorCount, Math.Clamp(value, 4, 32));
    }

    // 0=RGB 1=Lab 2=HSL  (default Lab=1)
    private int _spaceIndex = 1;
    public int SpaceIndex
    {
        get => _spaceIndex;
        set => this.RaiseAndSetIfChanged(ref _spaceIndex, Math.Clamp(value, 0, 2));
    }

    private int _downsampleMax = 256;
    public int DownsampleMax
    {
        get => _downsampleMax;
        set => this.RaiseAndSetIfChanged(ref _downsampleMax, Math.Clamp(value, 64, 1024));
    }

    // 0=Nearest 1=Hue 2=Lum 3=ClusterSize
    private int _sortIndex;
    public int SortIndex
    {
        get => _sortIndex;
        set => this.RaiseAndSetIfChanged(ref _sortIndex, Math.Clamp(value, 0, 3));
    }

    private double _dedupDeltaE = 2.0;
    public double DedupDeltaE
    {
        get => _dedupDeltaE;
        set => this.RaiseAndSetIfChanged(ref _dedupDeltaE, Math.Clamp(value, 0.0, 30.0));
    }

    private bool _weightedPositions;
    public bool WeightedPositions
    {
        get => _weightedPositions;
        set => this.RaiseAndSetIfChanged(ref _weightedPositions, value);
    }

    private bool _excludeNearBlack;
    public bool ExcludeNearBlack
    {
        get => _excludeNearBlack;
        set => this.RaiseAndSetIfChanged(ref _excludeNearBlack, value);
    }

    private bool _excludeNearWhite;
    public bool ExcludeNearWhite
    {
        get => _excludeNearWhite;
        set => this.RaiseAndSetIfChanged(ref _excludeNearWhite, value);
    }

    // ── Results ────────────────────────────────────────────────────────

    public ObservableCollection<PaletteResultViewModel> Results { get; } = new();

    private PaletteResultViewModel? _selectedResult;
    public PaletteResultViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedResult, value);
            this.RaisePropertyChanged(nameof(CanApply));
        }
    }

    public bool CanApply => _selectedResult?.Stops.Count >= 2;

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    // ── Commands ───────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> BrowseCommand { get; }
    public ReactiveCommand<Unit, Unit> ExtractCommand { get; }
    public ReactiveCommand<Unit, Unit> CompareAllCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    // ── Events (host-wired side effects) ───────────────────────────────

    /// <summary>Fired when the user clicks Browse. Host opens its file picker.</summary>
    public event EventHandler? BrowseRequested;

    /// <summary>Fired with the accepted stops when the user clicks Apply.</summary>
    public event EventHandler<IReadOnlyList<PaletteStop>>? ResultAccepted;

    /// <summary>Fired on Cancel / Esc.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Fired when the VM needs the host to show a message box.</summary>
    public event EventHandler<string>? MessageRequested;

    // ── Host callbacks ─────────────────────────────────────────────────

    /// <summary>
    /// Called by the host after a successful file-picker / drag-drop with the
    /// chosen path + an Avalonia-side preview bitmap (host already decoded the
    /// file once for the picker; passing the bitmap avoids a second decode).
    /// </summary>
    public void SetImage(string path, Bitmap? previewBitmap)
    {
        if (!_service.TryLoadImage(path, out var err))
        {
            MessageRequested?.Invoke(this, err ?? "Failed to load image.");
            return;
        }

        SourcePath = path;
        PreviewImage = previewBitmap;
        Results.Clear();
        SelectedResult = null;
    }

    // ── Internals ──────────────────────────────────────────────────────

    private PaletteExtractionRequest BuildRequest() => new()
    {
        SourcePath = _sourcePath ?? "",
        MethodIndex = _methodIndex,
        ColorCount = _colorCount,
        Space = _spaceIndex switch
        {
            0 => PaletteColorSpaceKind.Rgb,
            2 => PaletteColorSpaceKind.Hsl,
            _ => PaletteColorSpaceKind.Lab,
        },
        DownsampleMaxDim = _downsampleMax,
        ExcludeNearBlack = _excludeNearBlack,
        ExcludeNearWhite = _excludeNearWhite,
        Sort = _sortIndex switch
        {
            1 => StopSortKind.Hue,
            2 => StopSortKind.Luminance,
            3 => StopSortKind.ClusterSize,
            _ => StopSortKind.NearestNeighborChain,
        },
        DedupDeltaE = (float)_dedupDeltaE,
        WeightedPositions = _weightedPositions,
    };

    private void RunSingle()
    {
        if (!HasImage)
        {
            MessageRequested?.Invoke(this, "Drop or browse to an image first.");
            return;
        }

        Results.Clear();
        var result = _service.Extract(BuildRequest());
        if (result.Palette.Count == 0)
        {
            StatusMessage = "No pixels left after filters.";
            SelectedResult = null;
            return;
        }

        StatusMessage = null;
        var row = new PaletteResultViewModel(result, exclusiveSelect: false) { IsSelected = true };
        Results.Add(row);
        SelectedResult = row;
    }

    private void RunCompareAll()
    {
        if (!HasImage)
        {
            MessageRequested?.Invoke(this, "Drop or browse to an image first.");
            return;
        }

        Results.Clear();
        SelectedResult = null;
        var all = _service.ExtractAll(BuildRequest());
        if (all.Count == 0)
        {
            StatusMessage = "No results.";
            return;
        }

        bool any = false;
        foreach (var r in all)
        {
            if (r.Palette.Count == 0) continue;
            Results.Add(new PaletteResultViewModel(r, exclusiveSelect: true, parent: this));
            any = true;
        }
        StatusMessage = any ? null : "No pixels left after filters.";
    }

    /// <summary>
    /// Called by the view when a compare-mode row's RadioButton flips to checked.
    /// Clears IsSelected on the other rows and routes the chosen stops to Apply.
    /// </summary>
    public void SelectResult(PaletteResultViewModel row)
    {
        foreach (var r in Results)
            r.IsSelected = ReferenceEquals(r, row);
        SelectedResult = row;
    }

    private void OnApply()
    {
        if (!CanApply || _selectedResult is null) return;
        ResultAccepted?.Invoke(this, _selectedResult.Stops);
    }

    private void OnCancel() => Cancelled?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// One result row in the comparison grid. Holds the palette + computed stops
/// + selection state; the view binds an ItemsControl to a list of these.
/// </summary>
public sealed class PaletteResultViewModel : ViewModelBase
{
    private readonly ImagePaletteViewModel? _parent;

    public PaletteResultViewModel(PaletteExtractionResult result, bool exclusiveSelect)
        : this(result, exclusiveSelect, parent: null) { }

    public PaletteResultViewModel(PaletteExtractionResult result, bool exclusiveSelect, ImagePaletteViewModel? parent)
    {
        Name = result.MethodName;
        Palette = result.Palette;
        Stops = result.Stops;
        ExclusiveSelect = exclusiveSelect;
        _parent = parent;
    }

    public string Name { get; }
    public IReadOnlyList<PaletteSwatch> Palette { get; }
    public IReadOnlyList<PaletteStop> Stops { get; }

    /// <summary>True in compare-all mode (show radio button), false in single-extract.</summary>
    public bool ExclusiveSelect { get; }

    /// <summary>Inverse of ExclusiveSelect for XAML clarity.</summary>
    public bool ShowTitleLabel => !ExclusiveSelect;

    public string SubtitleText => $"{Name}   —   {Palette.Count} swatches";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!this.RaiseAndSetIfChangedReturnsChanged(ref _isSelected, value)) return;
            // In Compare-All mode the radio binds IsChecked → IsSelected. Notify
            // the parent so it can mirror the pick into SelectedResult (which
            // gates the Apply button via CanApply).
            if (value && ExclusiveSelect) _parent?.SelectResult(this);
        }
    }
}
