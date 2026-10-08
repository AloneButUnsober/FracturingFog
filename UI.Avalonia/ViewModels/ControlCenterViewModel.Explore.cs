// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ControlCenterViewModel.Explore.cs
//
// Explore ▸ Find ▸ Auto-explore — interesting-location finder S6 (#1190).
// Theory-free, so it runs on every 2D family: score small probe renders
// (InterestScorer) and beam-search downward (AutoExplorer).
//   "Surprise me": a fresh random seed from the family's home view.
//   "Descend":     from the current view.
// The probe renders through the host (Shell.CreateExploreProbe → Engine
// ExploreProbe, the poster calculators). Results are plain navigation (centre,
// zoom, quality), which the Command builder already captures from the live
// view; the batch equivalent is --explore seed=…,depth=…,beam=….

using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;

using Avalonia.Threading;

using ReactiveUI;

using FracturingFog.Abstractions.Explore;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>One auto-explore finalist in the Find group's candidate list.</summary>
public sealed class ExploreCandidateItem
{
    public ExploreCandidateItem(string label, string tip, ReactiveCommand<Unit, Unit> go)
    { Label = label; Tip = tip; Go = go; }

    public string Label { get; }
    public string Tip { get; }
    public ReactiveCommand<Unit, Unit> Go { get; }
}

public sealed partial class ControlCenterViewModel
{
    /// <summary>First-level random children for "Surprise me" (a wide random start).</summary>
    public const int SurpriseStartJitter = 16;

    private int _exploreDepth = AutoExploreOptions.Default.Depth;
    private int _exploreBeam = AutoExploreOptions.Default.Beam;
    private int _descendSeed = 1;

    public ReactiveCommand<Unit, Unit> SurpriseMeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DescendCommand { get; private set; } = null!;

    /// <summary>Levels to descend (each ×<see cref="AutoExploreOptions.ZoomStep"/> zoom).</summary>
    public int ExploreDepth
    {
        get => _exploreDepth;
        set => this.RaiseAndSetIfChanged(ref _exploreDepth, Math.Clamp(value, 1, 16));
    }

    /// <summary>Views kept per level.</summary>
    public int ExploreBeam
    {
        get => _exploreBeam;
        set => this.RaiseAndSetIfChanged(ref _exploreBeam, Math.Clamp(value, 1, 8));
    }

    /// <summary>The last run's finalists, best first.</summary>
    public ObservableCollection<ExploreCandidateItem> ExploreCandidates { get; } = new();

    public bool HasExploreCandidates => ExploreCandidates.Count > 0;

    private void InitExplore()
    {
        var idle = this.WhenAnyValue(x => x.IsFinderBusy, busy => !busy);
        SurpriseMeCommand = ReactiveCommand.CreateFromTask(() => ExploreAsync(surprise: true), idle);
        DescendCommand    = ReactiveCommand.CreateFromTask(() => ExploreAsync(surprise: false), idle);
        ExploreCandidates.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(HasExploreCandidates));
    }

    private Task ExploreAsync(bool surprise)
    {
        var main = Shell.Main;
        var vs = main.ViewState;
        var type = vs.FractalType;
        if (!AutoExplorer.Supports(type))
        {
            FinderStatus = $"Auto-explore runs on 2D families; {type} is 3D, stochastic or not pannable.";
            return Task.CompletedTask;
        }
        var opts = AutoExploreOptions.Default with
        {
            Depth = ExploreDepth,
            Beam = ExploreBeam,
            MaxZoom = AutoExplorer.MaxZoomFor(type),
        };
        var probe = Shell.CreateExploreProbe?.Invoke(opts.ProbeSize);
        if (probe == null)
        {
            FinderStatus = "Auto-explore needs the render host (not available).";
            return Task.CompletedTask;
        }

        ExploreView start;
        if (surprise)
        {
            var home = new FracturingFog.ViewState.FractalViewState();
            home.SnapToFractalDefault(type);
            start = new ExploreView(new FFMath.DeepComplex(home.CenterX, home.CenterY), home.Zoom);
            opts = opts with { Seed = Random.Shared.Next(1, int.MaxValue), FirstLevelJitter = SurpriseStartJitter };
        }
        else
        {
            start = new ExploreView(vs.GetCenter(), vs.Zoom);
            opts = opts with { Seed = _descendSeed++ };
        }
        var quality = vs.Quality;
        string spec = AutoExplorer.FormatSpec(opts);
        FinderStatus = $"Exploring {type} ({spec})…";

        return RunFinderAsync(async ct =>
        {
            var progress = new Progress<ExploreProgress>(p =>
            {
                if (IsFinderBusy)
                    FinderStatus = $"Exploring… level {p.Level}/{opts.Depth} · {p.Probes} probes · best {p.BestScore:F2}";
            });
            var result = await Task.Run(() => AutoExplorer.Run(start, opts, probe, progress, ct), ct);
            if (result.Best == null)
            {
                FinderStatus = $"Auto-explore could not render a probe of {type} here.";
                return;
            }

            ExploreCandidates.Clear();
            int k = 0;
            foreach (var n in result.Finalists)
            {
                var view = n.View;
                ExploreCandidates.Add(new ExploreCandidateItem(
                    $"#{++k}  score {n.Score.Total:F2} · zoom {Fmt(view.Zoom)}",
                    n.Score.ToString(),
                    ReactiveCommand.Create(() => { GoToExploreView(view, quality); })));
            }
            GoToExploreView(result.Best.View, quality);

            string why = result.Stop switch
            {
                ExploreStop.DeadEnd        => $" Stopped at level {result.Best.Level}: nothing deeper scored.",
                ExploreStop.PrecisionLimit => $" Stopped at level {result.Best.Level}: the next level passes this family's precision.",
                _                          => "",
            };
            FinderStatus = $"Score {result.Best.Score.Total:F2} at zoom {Fmt(result.Best.View.Zoom)} "
                         + $"({result.Probes} probes, --explore {spec}).{why} Backspace returns.";
        });
    }

    // Navigate to an explored view: nav history, centre, zoom, tier promotion.
    private void GoToExploreView(ExploreView v, FracturingFog.Models.QualityPreset baseQuality)
    {
        var q = MinibrotJump.QualityFor(baseQuality, v.Zoom);
        ApplyPlan(new MinibrotJumpPlan(v.Center, v.Zoom, q, 0, 0, false));
    }
}
