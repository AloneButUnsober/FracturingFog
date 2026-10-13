// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ControlCenterViewModel.Heatmap.cs
//
// Explore ▸ Find ▸ Auto-explore ▸ Heatmap (#1240, follow-up to S6 #1190).
// The view is split into a grid; each cell gets its own small probe render
// (Shell.CreateExploreProbe → Engine ExploreProbe) and is scored by
// InterestScorer. The scores are handed to the render host, which blends them
// translucently into the live frame (never into an export). The probes are
// cached, so moving a weight slider or switching the shown term re-scores
// instantly; the probes are re-rendered only when the view changes. The
// tuned weights also drive Auto-explore, and reach the batch through the
// --explore spec (wb / we / wd / dc).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

using ReactiveUI;

using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class ControlCenterViewModel
{
    /// <summary>Heatmap columns (rows follow the frame's aspect).</summary>
    public const int HeatmapCols = 10;
    /// <summary>Probe render size per cell (square, px).</summary>
    public const int HeatmapProbeSize = 48;
    /// <summary>View-change poll for re-probing, ms.</summary>
    public const int HeatmapPollMs = 400;

    public IReadOnlyList<string> HeatmapTerms { get; } = new[] { "Score", "Boundary", "Entropy", "Dimension", "Penalty keep" };

    private bool _showHeatmap;
    private int _heatmapTermIndex;
    private string _heatmapStatus = "";
    private double _wBoundary = InterestWeights.Default.BoundaryWeight;
    private double _wEntropy = InterestWeights.Default.EntropyWeight;
    private double _wDimension = InterestWeights.Default.DimensionWeight;
    private double _dimCentre = InterestWeights.Default.DimensionCentre;

    private CancellationTokenSource? _heatCts;
    private DispatcherTimer? _heatTimer;
    private ProbeField?[]? _heatFields;
    private int _heatColsN, _heatRowsN;
    private (double X, double Y, double Zoom) _heatKey = (double.NaN, double.NaN, double.NaN);
    private (double X, double Y, double Zoom) _heatPending = (double.NaN, double.NaN, double.NaN);
    private bool _heatBusy;

    public ReactiveCommand<Unit, Unit> ResetWeightsCommand { get; private set; } = null!;

    private void InitHeatmap()
        => ResetWeightsCommand = ReactiveCommand.Create(() =>
        {
            var d = InterestWeights.Default;
            (WeightBoundary, WeightEntropy, WeightDimension, DimensionCentre) =
                (d.BoundaryWeight, d.EntropyWeight, d.DimensionWeight, d.DimensionCentre);
        });

    /// <summary>The interest weights as tuned (Auto-explore and the heatmap).</summary>
    public InterestWeights CurrentWeights => InterestWeights.Default with
    {
        BoundaryWeight = _wBoundary,
        EntropyWeight = _wEntropy,
        DimensionWeight = _wDimension,
        DimensionCentre = _dimCentre,
    };

    public bool ShowHeatmap
    {
        get => _showHeatmap;
        set
        {
            if (_showHeatmap == value) return;
            this.RaiseAndSetIfChanged(ref _showHeatmap, value);
            if (value) StartHeatmap(); else StopHeatmap();
        }
    }

    public int HeatmapTermIndex
    {
        get => _heatmapTermIndex;
        set { this.RaiseAndSetIfChanged(ref _heatmapTermIndex, Math.Clamp(value, 0, HeatmapTerms.Count - 1)); Rescore(); }
    }

    public string HeatmapStatus
    {
        get => _heatmapStatus;
        private set => this.RaiseAndSetIfChanged(ref _heatmapStatus, value);
    }

    public double WeightBoundary { get => _wBoundary; set => SetWeight(ref _wBoundary, value, nameof(WeightBoundary), nameof(WeightBoundaryText)); }
    public double WeightEntropy { get => _wEntropy; set => SetWeight(ref _wEntropy, value, nameof(WeightEntropy), nameof(WeightEntropyText)); }
    public double WeightDimension { get => _wDimension; set => SetWeight(ref _wDimension, value, nameof(WeightDimension), nameof(WeightDimensionText)); }
    public double DimensionCentre { get => _dimCentre; set => SetWeight(ref _dimCentre, value, nameof(DimensionCentre), nameof(DimensionCentreText)); }

    public string WeightBoundaryText => _wBoundary.ToString("0.00", CultureInfo.InvariantCulture);
    public string WeightEntropyText => _wEntropy.ToString("0.00", CultureInfo.InvariantCulture);
    public string WeightDimensionText => _wDimension.ToString("0.00", CultureInfo.InvariantCulture);
    public string DimensionCentreText => _dimCentre.ToString("0.00", CultureInfo.InvariantCulture);

    private void SetWeight(ref double field, double value, string name, string textName)
    {
        value = Math.Round(value, 2);
        if (field == value) return;
        field = value;
        this.RaisePropertyChanged(name);
        this.RaisePropertyChanged(textName);
        Rescore();
    }

    private (double X, double Y, double Zoom) ViewKey()
    {
        var vs = Shell.Main.ViewState;
        return (vs.CenterX, vs.CenterY, vs.Zoom);
    }

    private void StartHeatmap()
    {
        _heatTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(HeatmapPollMs), DispatcherPriority.Background, (_, _) => PollHeatmap());
        _heatTimer.Start();
        _ = RecomputeHeatmapAsync();
    }

    private void StopHeatmap()
    {
        _heatTimer?.Stop();
        _heatCts?.Cancel();
        _heatFields = null;
        _heatKey = (double.NaN, double.NaN, double.NaN);
        HeatmapStatus = "";
        var host = Shell.Main.RenderHost;
        host.ExploreHeatmap = null;
        host.RepaintWithPostFx();
    }

    // Re-probe once the view has changed and then held still for one poll.
    private void PollHeatmap()
    {
        if (!_showHeatmap || _heatBusy) return;
        var key = ViewKey();
        if (key == _heatKey) { _heatPending = key; return; }
        if (key != _heatPending) { _heatPending = key; HeatmapStatus = "View changed — re-scoring when it settles…"; return; }
        _ = RecomputeHeatmapAsync();
    }

    private async Task RecomputeHeatmapAsync()
    {
        var main = Shell.Main;
        var vs = main.ViewState;
        if (!AutoExplorer.Supports(vs.FractalType))
        {
            HeatmapStatus = $"No heatmap for {vs.FractalType} (3D, stochastic or not pannable).";
            _heatKey = ViewKey();
            main.RenderHost.ExploreHeatmap = null;
            main.RenderHost.RepaintWithPostFx();
            return;
        }
        var probe = Shell.CreateExploreProbe?.Invoke(HeatmapProbeSize);
        if (probe == null) { HeatmapStatus = "The heatmap needs the render host."; return; }

        _heatCts?.Cancel();
        var cts = new CancellationTokenSource();
        _heatCts = cts;
        _heatBusy = true;
        var key = ViewKey();
        var (w, h) = main.RenderHost.LastPresentedSize;
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        int cols = HeatmapCols, rows = Math.Max(1, (int)Math.Round(HeatmapCols * (double)h / w));
        DeepComplex centre = vs.GetCenter();
        double s = AutoExplorer.PlaneExtent / (Math.Max(w, h) * vs.Zoom);   // plane units per pixel
        double cw = (double)w / cols, ch = (double)h / rows;
        double cellZoom = AutoExplorer.PlaneExtent / (Math.Max(cw, ch) * s);
        HeatmapStatus = $"Scoring {cols}×{rows} cells…";
        try
        {
            var fields = await Task.Run(() =>
            {
                var f = new ProbeField?[cols * rows];
                Parallel.For(0, cols * rows, new ParallelOptions { CancellationToken = cts.Token, MaxDegreeOfParallelism = 4 }, i =>
                {
                    int r = i / cols, c = i % cols;
                    // Screen y runs along +Im (ViewCamera.WorldFromScreen).
                    double ox = ((c + 0.5) * cw - w / 2.0) * s, oy = ((r + 0.5) * ch - h / 2.0) * s;
                    try { f[i] = probe(new ExploreView(centre.Translate(ox, oy), cellZoom), cts.Token); }
                    catch (OperationCanceledException) { throw; }
                    catch { f[i] = null; }
                });
                return f;
            }, cts.Token);
            if (!ReferenceEquals(_heatCts, cts) || !_showHeatmap) return;
            (_heatFields, _heatColsN, _heatRowsN, _heatKey, _heatPending) = (fields, cols, rows, key, key);
            Rescore();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_heatCts, cts)) _heatBusy = false;
        }
    }

    // Score the cached probes with the current weights and show the chosen term.
    private void Rescore()
    {
        if (!_showHeatmap || _heatFields is not { } fields) return;
        var w = CurrentWeights;
        var term = (HeatmapTerm)_heatmapTermIndex;
        var values = new float[fields.Length];
        int best = -1;
        for (int i = 0; i < fields.Length; i++)
        {
            values[i] = fields[i] is { } f ? ExploreHeatmap.Value(InterestScorer.Score(f, w), term, w) : float.NaN;
            if (!float.IsNaN(values[i]) && (best < 0 || values[i] > values[best])) best = i;
        }
        var host = Shell.Main.RenderHost;
        host.ExploreHeatmap = new ExploreHeatmap(_heatColsN, _heatRowsN, values, _heatKey.X, _heatKey.Y, _heatKey.Zoom,
                                                 $"Interest: {HeatmapTerms[_heatmapTermIndex]}");
        host.RepaintWithPostFx();
        HeatmapStatus = best < 0
            ? "No cell could be scored."
            : $"{HeatmapTerms[_heatmapTermIndex]}: best cell {values[best]:0.00} (row {best / _heatColsN + 1}, column {best % _heatColsN + 1}). "
              + $"Weights apply to Auto-explore: --explore … {WeightSpec(w)}";
    }

    private static string WeightSpec(InterestWeights w)
    {
        var spec = AutoExplorer.FormatSpec(AutoExploreOptions.Default with { Weights = w });
        int comma = spec.IndexOf(',');
        return comma < 0 ? "(default weights)" : spec[(comma + 1)..];
    }
}
