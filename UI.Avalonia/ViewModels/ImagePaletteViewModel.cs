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

public class ImagePaletteViewModel : ViewModelBase
{
    /// <summary>Hook for subclasses that apply a global colour adjustment
    /// (e.g. PaletteBuilder's Temperature/Tint sliders) to the per-row
    /// display. PaletteResultViewModel routes EffectivePalette / EffectiveStops
    /// through here so live slider movement repaints the swatch + gradient
    /// strips. Default = identity.</summary>
    public virtual (byte R, byte G, byte B) AdjustForDisplay((byte R, byte G, byte B) c) => c;

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

    // 0=RGB 1=Lab 2=HSL 3=OkLab  (default Lab=1)
    private int _spaceIndex = 1;
    public int SpaceIndex
    {
        get => _spaceIndex;
        set => this.RaiseAndSetIfChanged(ref _spaceIndex, Math.Clamp(value, 0, 3));
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

    // 0=DeltaE76 1=DeltaE2000
    private int _dedupMetricIndex;
    public int DedupMetricIndex
    {
        get => _dedupMetricIndex;
        set => this.RaiseAndSetIfChanged(ref _dedupMetricIndex, Math.Clamp(value, 0, 1));
    }

    private bool _gammaCorrect;
    public bool GammaCorrect
    {
        get => _gammaCorrect;
        set => this.RaiseAndSetIfChanged(ref _gammaCorrect, value);
    }

    private double _bandwidth = 25.0;
    public double Bandwidth
    {
        get => _bandwidth;
        set => this.RaiseAndSetIfChanged(ref _bandwidth, Math.Clamp(value, 1.0, 100.0));
    }

    private double _dbscanEpsilon = 8.0;
    public double DbscanEpsilon
    {
        get => _dbscanEpsilon;
        set => this.RaiseAndSetIfChanged(ref _dbscanEpsilon, Math.Clamp(value, 0.5, 100.0));
    }

    private int _dbscanMinPts = 20;
    public int DbscanMinPts
    {
        get => _dbscanMinPts;
        set => this.RaiseAndSetIfChanged(ref _dbscanMinPts, Math.Clamp(value, 1, 5000));
    }

    private double _spatialWeight = 0.5;
    public double SpatialWeight
    {
        get => _spatialWeight;
        set => this.RaiseAndSetIfChanged(ref _spatialWeight, Math.Clamp(value, 0.0, 1.0));
    }

    // ── Phase 3 — preprocessing filters ────────────────────────────────

    private bool _excludeTransparent;
    public bool ExcludeTransparent
    {
        get => _excludeTransparent;
        set => this.RaiseAndSetIfChanged(ref _excludeTransparent, value);
    }

    private double _minSaturation;
    public double MinSaturation
    {
        get => _minSaturation;
        set => this.RaiseAndSetIfChanged(ref _minSaturation, Math.Clamp(value, 0.0, 1.0));
    }

    private double _maxSaturation = 1.0;
    public double MaxSaturation
    {
        get => _maxSaturation;
        set => this.RaiseAndSetIfChanged(ref _maxSaturation, Math.Clamp(value, 0.0, 1.0));
    }

    private double _minLightness;
    public double MinLightness
    {
        get => _minLightness;
        set => this.RaiseAndSetIfChanged(ref _minLightness, Math.Clamp(value, 0.0, 1.0));
    }

    private double _maxLightness = 1.0;
    public double MaxLightness
    {
        get => _maxLightness;
        set => this.RaiseAndSetIfChanged(ref _maxLightness, Math.Clamp(value, 0.0, 1.0));
    }

    private double _roiX;
    public double RoiX
    {
        get => _roiX;
        set => this.RaiseAndSetIfChanged(ref _roiX, Math.Clamp(value, 0.0, 1.0));
    }

    private double _roiY;
    public double RoiY
    {
        get => _roiY;
        set => this.RaiseAndSetIfChanged(ref _roiY, Math.Clamp(value, 0.0, 1.0));
    }

    private double _roiWidth;
    public double RoiWidth
    {
        get => _roiWidth;
        set => this.RaiseAndSetIfChanged(ref _roiWidth, Math.Clamp(value, 0.0, 1.0));
    }

    private double _roiHeight;
    public double RoiHeight
    {
        get => _roiHeight;
        set => this.RaiseAndSetIfChanged(ref _roiHeight, Math.Clamp(value, 0.0, 1.0));
    }

    public void ClearRoi()
    {
        RoiX = 0; RoiY = 0; RoiWidth = 0; RoiHeight = 0;
    }

    private bool _useSaliency;
    public bool UseSaliency
    {
        get => _useSaliency;
        set => this.RaiseAndSetIfChanged(ref _useSaliency, value);
    }

    private double _saliencyThreshold = 0.3;
    public double SaliencyThreshold
    {
        get => _saliencyThreshold;
        set => this.RaiseAndSetIfChanged(ref _saliencyThreshold, Math.Clamp(value, 0.0, 1.0));
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
            3 => PaletteColorSpaceKind.OkLab,
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
        DedupMetric = _dedupMetricIndex == 1
            ? DeltaEMetricKind.DeltaE2000
            : DeltaEMetricKind.DeltaE76,
        GammaCorrect = _gammaCorrect,
        Bandwidth = (float)_bandwidth,
        DbscanEpsilon = (float)_dbscanEpsilon,
        DbscanMinPts = _dbscanMinPts,
        SpatialWeight = (float)_spatialWeight,
        ExcludeTransparent = _excludeTransparent,
        AlphaThreshold = 16,
        MinSaturation = (float)_minSaturation,
        MaxSaturation = (float)_maxSaturation,
        MinLightness = (float)_minLightness,
        MaxLightness = (float)_maxLightness,
        RoiX = (float)_roiX,
        RoiY = (float)_roiY,
        RoiWidth = (float)_roiWidth,
        RoiHeight = (float)_roiHeight,
        UseSaliency = _useSaliency,
        SaliencyThreshold = (float)_saliencyThreshold,
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
        var row = new PaletteResultViewModel(result, exclusiveSelect: false, parent: this) { IsSelected = true };
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
/// Mutable per-stop record bound to the Phase 4 stop editor. Position is
/// stored as a double so the NumericUpDown UI gets the right precision;
/// the view-model coerces back to <see cref="PaletteStop"/> when exporting.
/// IsLocked is advisory — present so the UI can render a pin icon, but
/// reorder/edit commands intentionally ignore it (semantics are "visual lock").
/// </summary>
public sealed class EditableStopViewModel : ViewModelBase
{
    public EditableStopViewModel(float position, byte r, byte g, byte b)
    {
        _position = position;
        _r = r; _g = g; _b = b;
    }

    private double _position;
    public double Position
    {
        get => _position;
        set => this.RaiseAndSetIfChanged(ref _position, Math.Clamp(value, 0.0, 1.0));
    }

    private byte _r, _g, _b;
    public byte R { get => _r; set { if (this.RaiseAndSetIfChangedReturnsChanged(ref _r, value)) RaiseColorRelated(); } }
    public byte G { get => _g; set { if (this.RaiseAndSetIfChangedReturnsChanged(ref _g, value)) RaiseColorRelated(); } }
    public byte B { get => _b; set { if (this.RaiseAndSetIfChangedReturnsChanged(ref _b, value)) RaiseColorRelated(); } }

    /// <summary>
    /// Aggregate RGB as an Avalonia.Media.Color — bound TwoWay by the
    /// stop-editor ColorPicker so users can pick visually instead of typing
    /// three byte values. Setter splits back into R/G/B which raise their
    /// own change notifications.
    /// </summary>
    public global::Avalonia.Media.Color Color
    {
        get => global::Avalonia.Media.Color.FromArgb(255, _r, _g, _b);
        set
        {
            bool changed = _r != value.R || _g != value.G || _b != value.B;
            _r = value.R; _g = value.G; _b = value.B;
            if (changed)
            {
                this.RaisePropertyChanged(nameof(R));
                this.RaisePropertyChanged(nameof(G));
                this.RaisePropertyChanged(nameof(B));
                RaiseColorRelated();
            }
        }
    }

    private void RaiseColorRelated()
    {
        this.RaisePropertyChanged(nameof(Color));
        this.RaisePropertyChanged(nameof(Hex));
        this.RaisePropertyChanged(nameof(PreviewBrush));
    }

    public string Hex => $"#{_r:X2}{_g:X2}{_b:X2}";
    public global::Avalonia.Media.IBrush PreviewBrush
        => new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.FromArgb(255, _r, _g, _b));

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set => this.RaiseAndSetIfChanged(ref _isLocked, value);
    }
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

    private ObservableCollection<EditableStopViewModel>? _editableStops;
    /// <summary>
    /// Lazily-built mutable copy of <see cref="Stops"/>. Stop-editor UI binds
    /// to this; callers reading the export-ready palette should read
    /// <see cref="EffectiveStops"/> which returns the edited collection when
    /// <see cref="IsEditing"/> is true and the original snapshot otherwise.
    /// </summary>
    public ObservableCollection<EditableStopViewModel> EditableStops
    {
        get
        {
            if (_editableStops is null)
            {
                _editableStops = new ObservableCollection<EditableStopViewModel>();
                foreach (var s in Stops)
                    AddStopWithHook(new EditableStopViewModel(s.Position, s.R, s.G, s.B));
                _editableStops.CollectionChanged += (_, e) =>
                {
                    if (e.NewItems != null)
                        foreach (EditableStopViewModel s in e.NewItems)
                            HookStop(s);
                    if (e.OldItems != null)
                        foreach (EditableStopViewModel s in e.OldItems)
                            UnhookStop(s);
                    RaiseStopsChanged();
                };
            }
            return _editableStops;
        }
    }

    private void AddStopWithHook(EditableStopViewModel s)
    {
        HookStop(s);
        _editableStops!.Add(s);
    }

    private void HookStop(EditableStopViewModel s)
        => s.PropertyChanged += OnStopPropertyChanged;

    private void UnhookStop(EditableStopViewModel s)
        => s.PropertyChanged -= OnStopPropertyChanged;

    private void OnStopPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // R/G/B/Color/Position all bubble up — collapse to one render notification.
        if (e.PropertyName is nameof(EditableStopViewModel.R)
            or nameof(EditableStopViewModel.G)
            or nameof(EditableStopViewModel.B)
            or nameof(EditableStopViewModel.Color)
            or nameof(EditableStopViewModel.Position))
        {
            RaiseStopsChanged();
        }
    }

    /// <summary>
    /// Fires whenever an editable stop's position/colour changes OR a stop
    /// is added/removed/reordered. Strip controls subscribe to this so the
    /// swatch + gradient previews stay in sync with edits as they happen.
    /// </summary>
    public event Action? StopsChanged;

    /// <summary>Public re-raise hook for the parent VM: PaletteBuilder calls
    /// this on every result row when its Temperature/Tint sliders move, so
    /// the swatch + gradient strips repaint with the new adjustment.</summary>
    public void NotifyStopsChanged() => RaiseStopsChanged();

    private void RaiseStopsChanged()
    {
        this.RaisePropertyChanged(nameof(EffectiveStops));
        this.RaisePropertyChanged(nameof(EffectivePalette));
        StopsChanged?.Invoke();
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEditing, value);
            this.RaisePropertyChanged(nameof(EffectiveStops));
            this.RaisePropertyChanged(nameof(EffectivePalette));
            StopsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Swatch-strip projection — when editing, returns colours sourced from
    /// the EditableStops list (in current order) so the row above the
    /// gradient strip mirrors edits. When not editing, returns the original
    /// immutable palette from extraction.
    /// </summary>
    public IReadOnlyList<PaletteSwatch> EffectivePalette
    {
        get
        {
            if (!_isEditing || _editableStops is null)
            {
                if (_parent is null) return Palette;
                var raw = Palette;
                var outArr = new PaletteSwatch[raw.Count];
                for (int i = 0; i < raw.Count; i++)
                {
                    var s = raw[i];
                    var (r, g, b) = _parent.AdjustForDisplay((s.R, s.G, s.B));
                    outArr[i] = new PaletteSwatch(r, g, b, s.Weight);
                }
                return outArr;
            }
            var arr = new PaletteSwatch[_editableStops.Count];
            for (int i = 0; i < _editableStops.Count; i++)
            {
                var e = _editableStops[i];
                var (r, g, b) = _parent?.AdjustForDisplay((e.R, e.G, e.B)) ?? (e.R, e.G, e.B);
                arr[i] = new PaletteSwatch(r, g, b, 1);
            }
            return arr;
        }
    }

    /// <summary>
    /// Returns the edited stops when IsEditing is on, otherwise the original
    /// snapshot from extraction. Caller (export pipeline) reads this.
    /// </summary>
    public IReadOnlyList<PaletteStop> EffectiveStops
    {
        get
        {
            if (!_isEditing || _editableStops is null)
            {
                if (_parent is null) return Stops;
                var raw = Stops;
                var outArr = new PaletteStop[raw.Count];
                for (int i = 0; i < raw.Count; i++)
                {
                    var s = raw[i];
                    var (r, g, b) = _parent.AdjustForDisplay((s.R, s.G, s.B));
                    outArr[i] = new PaletteStop(s.Position, r, g, b);
                }
                return outArr;
            }
            var arr = new PaletteStop[_editableStops.Count];
            for (int i = 0; i < _editableStops.Count; i++)
            {
                var e = _editableStops[i];
                var (r, g, b) = _parent?.AdjustForDisplay((e.R, e.G, e.B)) ?? (e.R, e.G, e.B);
                arr[i] = new PaletteStop((float)e.Position, r, g, b);
            }
            return arr;
        }
    }

    public void MoveStopUp(int index)
    {
        var list = EditableStops;
        if (index <= 0 || index >= list.Count) return;
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(index - 1, item);
    }

    public void MoveStopDown(int index)
    {
        var list = EditableStops;
        if (index < 0 || index >= list.Count - 1) return;
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(index + 1, item);
    }

    public void RemoveStop(int index)
    {
        var list = EditableStops;
        if (index < 0 || index >= list.Count) return;
        list.RemoveAt(index);
    }

    /// <summary>
    /// Redistribute all stop positions evenly across [0,1] — handy after
    /// reordering when the original positions no longer make sense.
    /// </summary>
    public void NormalizePositions()
    {
        var list = EditableStops;
        int n = list.Count;
        if (n == 0) return;
        if (n == 1) { list[0].Position = 0; return; }
        for (int i = 0; i < n; i++)
            list[i].Position = (double)i / (n - 1);
    }

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
