// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using System.Runtime.InteropServices;
using global::Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

    // ── ROI grid overlay ───────────────────────────────────────────────

    private bool _showGrid;
    public bool ShowGrid
    {
        get => _showGrid;
        set => this.RaiseAndSetIfChanged(ref _showGrid, value);
    }

    private int _gridRows = 3;
    public int GridRows
    {
        get => _gridRows;
        set => this.RaiseAndSetIfChanged(ref _gridRows, Math.Clamp(value, 1, 16));
    }

    private int _gridCols = 3;
    public int GridCols
    {
        get => _gridCols;
        set => this.RaiseAndSetIfChanged(ref _gridCols, Math.Clamp(value, 1, 16));
    }

    private bool _snapToGrid;
    public bool SnapToGrid
    {
        get => _snapToGrid;
        set => this.RaiseAndSetIfChanged(ref _snapToGrid, value);
    }

    private int _sourcePixelWidth;
    public int SourcePixelWidth
    {
        get => _sourcePixelWidth;
        private set => this.RaiseAndSetIfChanged(ref _sourcePixelWidth, value);
    }

    private int _sourcePixelHeight;
    public int SourcePixelHeight
    {
        get => _sourcePixelHeight;
        private set => this.RaiseAndSetIfChanged(ref _sourcePixelHeight, value);
    }

    // ── Results ────────────────────────────────────────────────────────

    public ObservableCollection<PaletteResultViewModel> Results { get; } = new();

    private PaletteResultViewModel? _selectedResult;
    public PaletteResultViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (_selectedResult is not null)
                _selectedResult.StopsChanged -= RecomputeAdvisories;
            this.RaiseAndSetIfChanged(ref _selectedResult, value);
            if (_selectedResult is not null)
                _selectedResult.StopsChanged += RecomputeAdvisories;
            this.RaisePropertyChanged(nameof(CanApply));
            RecomputeAdvisories();
        }
    }

    public bool CanApply => _selectedResult?.Stops.Count >= 2;

    // ── Colour advisor (roadmap S10.6, #392 — the first S10 core surfaced) ──

    /// <summary>Gentle, dismissible colour advisories for the selected palette —
    /// CVD-collapse, shadow-crush, histogram-waste, cycle-seam — from
    /// <see cref="ColorAdvisor.Review"/>. The view paints these #FFCC00 (never red,
    /// the colourblind-safe advisory convention). Recomputed whenever the selection
    /// changes or its stops are edited.</summary>
    public ObservableCollection<PaletteAdviceItem> Advisories { get; } = new();

    /// <summary>True when the selected palette has at least one advisory.</summary>
    public bool HasAdvisories => Advisories.Count > 0;

    /// <summary>Header line for the advisor panel — a count, or an all-clear.</summary>
    public string AdvisorySummary => _selectedResult is null
        ? "Select a palette to review."
        : Advisories.Count == 0
            ? "No colour issues found."
            : $"{Advisories.Count} advisory{(Advisories.Count == 1 ? "" : "(s)")} for this palette.";

    private void RecomputeAdvisories()
    {
        Advisories.Clear();
        var result = _selectedResult;
        if (result is not null)
        {
            var eff = result.EffectiveStops;
            var stops = new List<(byte r, byte g, byte b)>(eff.Count);
            foreach (var s in eff) stops.Add((s.R, s.G, s.B));
            if (stops.Count >= 2)
                foreach (var advice in ColorAdvisor.Review(stops))
                    Advisories.Add(new PaletteAdviceItem(advice));
        }
        this.RaisePropertyChanged(nameof(HasAdvisories));
        this.RaisePropertyChanged(nameof(AdvisorySummary));
        RecomputeCvdPreview();
        RecomputeKeyColors();
        RecomputeHarmony();
        Recompute3DPreview();
        RecomputeLooks();
        RecomputeLiveFractal();
    }

    // ── Palette + CVD preview on the LIVE fractal (roadmap S10-LW.3, #392/#694) ──

    private IPaletteViewParamService? _viewParamService;
    /// <summary>Optional host service exposing the current fractal view's per-pixel
    /// palette parameter (roadmap S10-LW.1, #690). Set by the host after construction;
    /// null in the standalone tool (no live render) — the live-fractal preview then stays
    /// empty. Re-tinting is local, so no re-render is triggered.</summary>
    public IPaletteViewParamService? ViewParamService
    {
        get => _viewParamService;
        set { _viewParamService = value; RecomputeLiveFractal(); }
    }

    /// <summary>The current fractal view re-tinted through the selected palette, and the
    /// same view under each colour-vision deficiency — the palette live on the real
    /// fractal (roadmap S10-LW.3). Empty when no view-param service / view is available.</summary>
    public ObservableCollection<LabeledImage> LiveFractalViews { get; } = new();

    public bool HasLiveFractal => LiveFractalViews.Count > 0;

    private void RecomputeLiveFractal()
    {
        LiveFractalViews.Clear();

        var svc = _viewParamService;
        var eff = _selectedResult?.EffectiveStops;
        if (svc is not null && eff is { Count: >= 2 } && svc.TryGetViewParam(out var sample) && sample.HasData)
        {
            // Anchor stops (ascending position) for perceptual re-tint.
            var anchors = new PerceptualRamp.Stop[eff.Count];
            for (int i = 0; i < eff.Count; i++)
                anchors[i] = new PerceptualRamp.Stop(eff[i].Position, eff[i].R, eff[i].G, eff[i].B);

            // Downsample so the preview grid is cheap regardless of render resolution.
            const int TargetMax = 200;
            int stride = Math.Max(1, (Math.Max(sample.Width, sample.Height) + TargetMax - 1) / TargetMax);

            LiveFractalViews.Add(new LabeledImage("This palette", Retint(sample, anchors, stride, null)));
            LiveFractalViews.Add(new LabeledImage("Deuteranopia", Retint(sample, anchors, stride, CvdType.Deutan)));
            LiveFractalViews.Add(new LabeledImage("Protanopia", Retint(sample, anchors, stride, CvdType.Protan)));
            LiveFractalViews.Add(new LabeledImage("Tritanopia", Retint(sample, anchors, stride, CvdType.Tritan)));
            LiveFractalViews.Add(new LabeledImage("Monochromacy", Retint(sample, anchors, stride, CvdType.Monochromacy)));
        }

        this.RaisePropertyChanged(nameof(HasLiveFractal));
    }

    /// <summary>Re-tint the view's per-pixel parameter through the palette (perceptually,
    /// in OkLab), optionally CVD-simulating each pixel, into a downsampled bitmap. No
    /// re-render — a palette change never changes geometry.</summary>
    private static WriteableBitmap Retint(
        PaletteViewSample sample, PerceptualRamp.Stop[] anchors, int stride, CvdType? cvd)
    {
        int ow = Math.Max(1, sample.Width / stride);
        int oh = Math.Max(1, sample.Height / stride);
        var buf = new int[ow * oh];
        for (int oy = 0; oy < oh; oy++)
        {
            int sy = Math.Min(oy * stride, sample.Height - 1);
            for (int ox = 0; ox < ow; ox++)
            {
                int sx = Math.Min(ox * stride, sample.Width - 1);
                float t = sample.T[sy * sample.Width + sx];
                var (r, g, b) = PerceptualRamp.SampleOkLab(anchors, t);
                if (cvd is { } type) (r, g, b) = CvdSimulation.Simulate(r, g, b, type);
                buf[oy * ow + ox] = unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b));
            }
        }

        var bmp = new WriteableBitmap(new PixelSize(ow, oh), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
            for (int y = 0; y < oh; y++)
                Marshal.Copy(buf, y * ow, IntPtr.Add(fb.Address, y * fb.RowBytes), ow);
        return bmp;
    }

    // ── "Looks" — scene colour scripts (roadmap S10.10, #392) ──

    /// <summary>The scene "looks": a look derived from the current palette
    /// (<see cref="SceneLooks.FromRamp"/>) followed by the built-in
    /// <see cref="SceneLooks.Catalog"/>. Each pairs a ramp with a material preset and
    /// palette-drawn lights — the on-brand way the palette reaches into 3D.</summary>
    public ObservableCollection<LookRowVm> Looks { get; } = new();

    public bool HasLooks => Looks.Count > 0;

    private void RecomputeLooks()
    {
        Looks.Clear();

        // A look derived from the current palette, first (when one is selected).
        var eff = _selectedResult?.EffectiveStops;
        if (eff is { Count: >= 1 })
        {
            var stops = new List<(byte r, byte g, byte b)>(eff.Count);
            foreach (var s in eff) stops.Add((s.R, s.G, s.B));
            Looks.Add(new LookRowVm(SceneLooks.FromRamp("From this palette", stops)));
        }

        foreach (var look in SceneLooks.Catalog)
            Looks.Add(new LookRowVm(look));

        this.RaisePropertyChanged(nameof(HasLooks));
    }

    // ── 3D-facing previews: shaded gamut / fog / relief (roadmap S10.7–S10.9, #392) ──

    /// <summary>Shaded-gamut rows (roadmap S10.7): each palette colour swept full-shadow →
    /// lit → specular, so the artist sees how it reads once 3D lighting hits it.</summary>
    public ObservableCollection<LabeledSwatchRow> ShadedGamutRows { get; } = new();

    private string _shadedGamutSummary = "";
    public string ShadedGamutSummary
    {
        get => _shadedGamutSummary;
        private set => this.RaiseAndSetIfChanged(ref _shadedGamutSummary, value);
    }

    /// <summary>The palette as volumetric fog / god-rays over a dark backdrop (S10.8).</summary>
    public ObservableCollection<ISolidColorBrush> FogRamp { get; } = new();

    /// <summary>A fog-optimised sub-ramp — the palette's brighter reach, luminance-ascending (S10.8).</summary>
    public ObservableCollection<ISolidColorBrush> FogSubRamp { get; } = new();

    private string _fogSummary = "";
    public string FogSummary
    {
        get => _fogSummary;
        private set => this.RaiseAndSetIfChanged(ref _fogSummary, value);
    }

    private string _reliefSummary = "";
    /// <summary>Relief-legibility verdict (S10.9): does the ramp's lightness read as raised 3D?</summary>
    public string ReliefSummary
    {
        get => _reliefSummary;
        private set => this.RaiseAndSetIfChanged(ref _reliefSummary, value);
    }

    /// <summary>The luminance-locked repair ramp (S10.9): hue/chroma kept, lightness made
    /// monotonic so relief reads as form.</summary>
    public ObservableCollection<ISolidColorBrush> ReliefLockedRamp { get; } = new();

    public bool Has3DPreview => ShadedGamutRows.Count > 0;

    private void Recompute3DPreview()
    {
        ShadedGamutRows.Clear();
        FogRamp.Clear();
        FogSubRamp.Clear();
        ReliefLockedRamp.Clear();
        ShadedGamutSummary = "";
        FogSummary = "";
        ReliefSummary = "";

        var result = _selectedResult;
        var eff = result?.EffectiveStops;
        if (eff is { Count: >= 2 })
        {
            var stops = new List<(byte r, byte g, byte b)>(eff.Count);
            foreach (var s in eff) stops.Add((s.R, s.G, s.B));

            // S10.7 — shaded gamut (shadow → lit → specular), one row per stop.
            int crush = 0, blow = 0;
            foreach (var sw in ShadedGamut.AnalyzeRamp(stops, 7))
            {
                var brushes = new ISolidColorBrush[sw.Sweep.Length];
                for (int i = 0; i < sw.Sweep.Length; i++)
                    brushes[i] = new SolidColorBrush(Color.FromRgb(sw.Sweep[i].r, sw.Sweep[i].g, sw.Sweep[i].b));
                string flags = (sw.CrushesInShadow ? "  crushes in shadow" : "") + (sw.BlowsInSpecular ? "  blows in specular" : "");
                ShadedGamutRows.Add(new LabeledSwatchRow(
                    $"#{sw.Albedo.r:X2}{sw.Albedo.g:X2}{sw.Albedo.b:X2}{flags}", brushes));
                if (sw.CrushesInShadow) crush++;
                if (sw.BlowsInSpecular) blow++;
            }
            ShadedGamutSummary = crush == 0 && blow == 0
                ? "Every colour holds its separation shadow to specular."
                : $"{crush} stop(s) crush in shadow, {blow} blow in specular under 3D lighting.";

            // S10.8 — fog / god-rays over a dark backdrop + a fog-optimised sub-ramp.
            foreach (var (r, g, b) in FogPalettePreview.FogSweep(stops, 24, 10, 10, 20))
                FogRamp.Add(new SolidColorBrush(Color.FromRgb(r, g, b)));
            foreach (var packed in FogPalettePreview.FogOptimizedSubRamp(stops, 16))
                FogSubRamp.Add(BrushFromPacked(packed));
            bool washes = FogPalettePreview.WashesOut(stops, 10, 10, 20, out float span);
            FogSummary = washes
                ? $"Washes out as fog — no visible haze gradient (ΔE {span:0.###}). Try the fog sub-ramp."
                : $"Reads as fog / god-rays (haze ΔE span {span:0.###}).";

            // S10.9 — relief legibility + the luminance-locked repair.
            var rep = ReliefLegibility.Analyze(stops);
            ReliefSummary = rep.ReadsAsRelief
                ? $"Reads as raised 3D relief (lightness monotonic, spread {rep.LuminanceSpread:0.###})."
                : rep.NonMonotonic
                    ? $"Flattens relief — lightness reverses {rep.Reversals} time(s). Use the luminance-locked ramp."
                    : $"Flattens relief — too little lightness range (spread {rep.LuminanceSpread:0.###}). Use the luminance-locked ramp.";
            foreach (var packed in ReliefLegibility.LockLuminance(stops, 24))
                ReliefLockedRamp.Add(BrushFromPacked(packed));
        }

        this.RaisePropertyChanged(nameof(Has3DPreview));

        static ISolidColorBrush BrushFromPacked(uint p) =>
            new SolidColorBrush(Color.FromRgb((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF)));
    }

    // ── Harmony + generation in perceptual space (roadmap S10.4, #392) ──

    /// <summary>Harmony schemes (complementary / triadic / analogous / split / tetradic,
    /// computed in OKLCH) around the palette's most chromatic stop — one row of swatches
    /// per scheme, base colour first.</summary>
    public ObservableCollection<LabeledSwatchRow> HarmonySchemes { get; } = new();

    /// <summary>The IQ cosine "rainbow" ramp (<see cref="CosinePalette"/>) — the compact,
    /// GPU-friendly generator the ColorGen DSL uses.</summary>
    public ObservableCollection<ISolidColorBrush> CosineRamp { get; } = new();

    /// <summary>A chroma.js-style Bézier ramp threaded through the palette's own stops in
    /// OkLab, lightness-corrected so lightness rises monotonically (<see cref="BezierRamp"/>).</summary>
    public ObservableCollection<ISolidColorBrush> BezierPaletteRamp { get; } = new();

    public bool HasHarmony => HarmonySchemes.Count > 0;

    private void RecomputeHarmony()
    {
        HarmonySchemes.Clear();
        CosineRamp.Clear();
        BezierPaletteRamp.Clear();

        var result = _selectedResult;
        var eff = result?.EffectiveStops;
        if (eff is { Count: >= 2 })
        {
            // Harmony base = the palette's most chromatic stop (the colour worth
            // building a scheme around).
            var baseStop = eff[0];
            float bestC = -1f;
            foreach (var s in eff)
            {
                var (L, a, b2) = PerceptualRamp.RgbToOkLab(s.R, s.G, s.B);
                float c = PerceptualRamp.OkLabToOklch(L, a, b2).C;
                if (c > bestC) { bestC = c; baseStop = s; }
            }

            foreach (var (name, scheme) in new[]
            {
                ("Complementary", ColorHarmony.Scheme.Complementary),
                ("Triadic", ColorHarmony.Scheme.Triadic),
                ("Analogous", ColorHarmony.Scheme.Analogous),
                ("Split-complementary", ColorHarmony.Scheme.SplitComplementary),
                ("Tetradic", ColorHarmony.Scheme.Tetradic),
            })
            {
                var set = ColorHarmony.Harmony(baseStop.R, baseStop.G, baseStop.B, scheme);
                var brushes = new ISolidColorBrush[set.Length];
                for (int i = 0; i < set.Length; i++)
                    brushes[i] = new SolidColorBrush(Color.FromRgb(set[i].r, set[i].g, set[i].b));
                HarmonySchemes.Add(new LabeledSwatchRow(name, brushes));
            }

            // IQ cosine rainbow (fixed coefficients — the ColorGen idiom).
            var (ca, cb, cc, cd) = CosinePalette.Rainbow;
            foreach (var packed in CosinePalette.Emit(ca, cb, cc, cd, 24))
                CosineRamp.Add(BrushFromPacked(packed));

            // Bézier through the palette's stops, lightness-corrected.
            var controls = new (byte, byte, byte)[eff.Count];
            for (int i = 0; i < eff.Count; i++) controls[i] = (eff[i].R, eff[i].G, eff[i].B);
            foreach (var packed in BezierRamp.Emit(controls, 24, lightnessCorrect: true))
                BezierPaletteRamp.Add(BrushFromPacked(packed));
        }

        this.RaisePropertyChanged(nameof(HasHarmony));

        static ISolidColorBrush BrushFromPacked(uint p) =>
            new SolidColorBrush(Color.FromRgb((byte)((p >> 16) & 0xFF), (byte)((p >> 8) & 0xFF), (byte)(p & 0xFF)));
    }

    // ── Key colours: dominant / accent + lightness ramp (roadmap S10.5, #392) ──

    private ISolidColorBrush? _dominantBrush;
    /// <summary>The selected palette's dominant (heaviest) swatch as a brush, or null
    /// when nothing is selected. From <see cref="PaletteExtractionCore.Classify"/> over
    /// the extraction's weighted swatches.</summary>
    public ISolidColorBrush? DominantBrush
    {
        get => _dominantBrush;
        private set => this.RaiseAndSetIfChanged(ref _dominantBrush, value);
    }

    private string _dominantLabel = "";
    public string DominantLabel
    {
        get => _dominantLabel;
        private set => this.RaiseAndSetIfChanged(ref _dominantLabel, value);
    }

    private ISolidColorBrush? _accentBrush;
    /// <summary>The most chromatic non-dominant swatch — the pop colour to pair with the
    /// dominant workhorse.</summary>
    public ISolidColorBrush? AccentBrush
    {
        get => _accentBrush;
        private set => this.RaiseAndSetIfChanged(ref _accentBrush, value);
    }

    private string _accentLabel = "";
    public string AccentLabel
    {
        get => _accentLabel;
        private set => this.RaiseAndSetIfChanged(ref _accentLabel, value);
    }

    /// <summary>The palette's swatches re-ordered by ascending OkLab lightness — a ramp
    /// reads as a curve, not a bag (roadmap S10.5).</summary>
    public ObservableCollection<ISolidColorBrush> LightnessRamp { get; } = new();

    /// <summary>True once a palette is selected (drives the panel's empty state).</summary>
    public bool HasKeyColors => _dominantBrush is not null;

    private void RecomputeKeyColors()
    {
        LightnessRamp.Clear();
        var result = _selectedResult;
        // Dominant/accent are extraction characteristics — read the raw, weighted
        // extraction swatches (EffectivePalette drops weights to 1), not the edited stops.
        var swatches = result?.Palette;
        if (swatches is null || swatches.Count == 0)
        {
            DominantBrush = null;
            AccentBrush = null;
            DominantLabel = "";
            AccentLabel = "";
            this.RaisePropertyChanged(nameof(HasKeyColors));
            return;
        }

        var clusters = new List<PaletteCluster>(swatches.Count);
        long total = 0;
        foreach (var s in swatches)
        {
            clusters.Add(new PaletteCluster(s.R, s.G, s.B, s.Weight));
            total += s.Weight;
        }

        var pal = PaletteExtractionCore.Classify(clusters);
        DominantBrush = Brush(pal.Dominant);
        AccentBrush = Brush(pal.Accent);
        DominantLabel = Label("Dominant", pal.Dominant, total);
        AccentLabel = Label("Accent", pal.Accent, total);
        foreach (var c in pal.Ramp)
            LightnessRamp.Add(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)));

        this.RaisePropertyChanged(nameof(HasKeyColors));

        static ISolidColorBrush Brush(PaletteCluster c) => new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));

        static string Label(string role, PaletteCluster c, long total)
        {
            string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            return total > 0
                ? $"{role}  {hex}  ·  {100.0 * c.Weight / total:0.#}%"
                : $"{role}  {hex}";
        }
    }

    // ── Colourblind side-by-side preview (roadmap S10.2, #392) ──────────

    /// <summary>The selected palette rendered as it appears to normal vision and
    /// under each colour-vision deficiency (deutan / protan / tritan / full
    /// monochromacy), via <see cref="CvdSimulation.Simulate"/> — the CVD-first
    /// differentiator, side by side. Recomputed with the advisor on selection /
    /// stop edits.</summary>
    public ObservableCollection<CvdPreviewRow> CvdPreview { get; } = new();

    /// <summary>True once a palette with ≥2 stops is selected (drives the preview
    /// panel's empty-state text).</summary>
    public bool HasCvdPreview => CvdPreview.Count > 0;

    private void RecomputeCvdPreview()
    {
        CvdPreview.Clear();
        var result = _selectedResult;
        if (result is not null)
        {
            var eff = result.EffectiveStops;
            if (eff.Count >= 2)
            {
                // Normal vision first, then each deficiency at full severity.
                CvdPreview.Add(BuildCvdRow("Normal vision", eff, null));
                CvdPreview.Add(BuildCvdRow("Deuteranopia (green-weak)", eff, CvdType.Deutan));
                CvdPreview.Add(BuildCvdRow("Protanopia (red-weak)", eff, CvdType.Protan));
                CvdPreview.Add(BuildCvdRow("Tritanopia (blue-weak)", eff, CvdType.Tritan));
                CvdPreview.Add(BuildCvdRow("Monochromacy", eff, CvdType.Monochromacy));
            }
        }
        this.RaisePropertyChanged(nameof(HasCvdPreview));
    }

    private static CvdPreviewRow BuildCvdRow(
        string label, IReadOnlyList<PaletteStop> stops, CvdType? type)
    {
        var brushes = new ISolidColorBrush[stops.Count];
        for (int i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            var (r, g, b) = type is null
                ? (s.R, s.G, s.B)
                : CvdSimulation.Simulate(s.R, s.G, s.B, type.Value);
            brushes[i] = new SolidColorBrush(Color.FromRgb(r, g, b));
        }
        return new CvdPreviewRow(label, brushes);
    }

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
        SourcePixelWidth = previewBitmap?.PixelSize.Width ?? 0;
        SourcePixelHeight = previewBitmap?.PixelSize.Height ?? 0;
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
            // Project the swatch strip from EffectiveStops so it shows the SAME
            // colours, in the SAME order, whether or not the editor is open — the
            // deduped, position-sorted stop set that also feeds the gradient strip
            // directly below and the export. Previously the non-edit strip rendered
            // the raw extraction-order Palette (a different order, and with the
            // pre-dedup duplicates), so toggling Edit visibly reordered the swatches
            // and confused the artist. EffectiveStops already applies the parent's
            // display adjustment in both states, so don't re-apply it here.
            var stops = EffectiveStops;
            var arr = new PaletteSwatch[stops.Count];
            for (int i = 0; i < stops.Count; i++)
            {
                var s = stops[i];
                arr[i] = new PaletteSwatch(s.R, s.G, s.B, 1);
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

/// <summary>One colour advisory row for binding (roadmap S10.6, #392). Wraps a
/// <see cref="ColorAdvice"/> as display-friendly text; the view paints every row the
/// same colourblind-safe advisory colour (#FFCC00), so <see cref="IsWarn"/> only drives
/// weight / an optional glyph, never a red/green cue.</summary>
public sealed class PaletteAdviceItem
{
    public PaletteAdviceItem(ColorAdvice advice)
    {
        Kind = advice.Kind.ToString();
        Message = advice.Message;
        IsWarn = advice.Severity == AdviceSeverity.Warn;
    }

    public string Kind { get; }
    public string Message { get; }
    public bool IsWarn { get; }

    /// <summary>A neutral severity glyph (never a colour cue): a filled dot for a
    /// warning, a hollow one for info.</summary>
    public string Glyph => IsWarn ? "◆" : "◇";
}

/// <summary>One row of the colourblind side-by-side preview (roadmap S10.2, #392):
/// a label plus the palette's swatches as they appear under one vision type. The view
/// binds each brush straight onto a swatch cell's background.</summary>
public sealed class CvdPreviewRow
{
    public CvdPreviewRow(string label, IReadOnlyList<ISolidColorBrush> swatches)
    {
        Label = label;
        Swatches = swatches;
    }

    public string Label { get; }
    public IReadOnlyList<ISolidColorBrush> Swatches { get; }
}

/// <summary>A labelled swatch row for binding (roadmap S10.4, #392) — a scheme name plus
/// its generated colours as brushes. Used by the harmony panel.</summary>
public sealed class LabeledSwatchRow
{
    public LabeledSwatchRow(string label, IReadOnlyList<ISolidColorBrush> swatches)
    {
        Label = label;
        Swatches = swatches;
    }

    public string Label { get; }
    public IReadOnlyList<ISolidColorBrush> Swatches { get; }
}

/// <summary>A labelled preview image for binding (roadmap S10-LW.3, #694) — the live
/// fractal re-tinted through the palette, under one vision type.</summary>
public sealed class LabeledImage
{
    public LabeledImage(string label, Bitmap image)
    {
        Label = label;
        Image = image;
    }

    public string Label { get; }
    public Bitmap Image { get; }
}

/// <summary>One scene "look" for binding (roadmap S10.10, #392): the ramp as swatches,
/// the material preset as text, and the key / sky (and optional emission) tints as
/// brushes.</summary>
public sealed class LookRowVm
{
    public LookRowVm(Look look)
    {
        Name = look.Name;
        var ramp = new ISolidColorBrush[look.Ramp.Count];
        for (int i = 0; i < look.Ramp.Count; i++)
        {
            var (r, g, b) = look.Ramp[i];
            ramp[i] = new SolidColorBrush(Color.FromRgb(r, g, b));
        }
        Ramp = ramp;
        MaterialText = $"roughness {look.Material.Roughness:0.00}  ·  metallic {look.Material.Metallic:0.00}";
        KeyBrush = Brush(look.Lighting.KeyTint);
        SkyBrush = Brush(look.Lighting.SkyTint);
        EmissionBrush = look.EmissionTint is { } e ? Brush(e) : null;
        HasEmission = EmissionBrush is not null;

        static ISolidColorBrush Brush((byte r, byte g, byte b) c) => new SolidColorBrush(Color.FromRgb(c.r, c.g, c.b));
    }

    public string Name { get; }
    public IReadOnlyList<ISolidColorBrush> Ramp { get; }
    public string MaterialText { get; }
    public ISolidColorBrush KeyBrush { get; }
    public ISolidColorBrush SkyBrush { get; }
    public ISolidColorBrush? EmissionBrush { get; }
    public bool HasEmission { get; }
}
