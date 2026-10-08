// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/AutoExplorer.cs
//
// Interesting-location finder S6 (#1190): beam-search descent on the
// InterestScorer score. Each beam node spawns a Grid×Grid set of sub-views plus
// seeded jitter at ZoomStep× its zoom; the best Beam children (kept apart from
// each other) form the next level, down to Depth levels. Deterministic for a
// seed: the only randomness is System.Random(seed), drawn in a fixed order.
//
// The search never renders itself — the caller supplies a probe delegate that
// renders a small square view of the active family and returns its field
// (Engine/Explore/ExploreProbe in the app and the batch). That keeps this file
// UI- and Engine-free, and lets tests drive it with synthetic fields.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.ViewState;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>A square view: centre and zoom (plane extent
/// <see cref="AutoExplorer.PlaneExtent"/>/zoom across).</summary>
public readonly record struct ExploreView(DeepComplex Center, double Zoom);

/// <summary>Search settings. Every field is tunable.</summary>
public sealed record AutoExploreOptions
{
    public int Seed { get; init; }
    /// <summary>Levels to descend.</summary>
    public int Depth { get; init; } = 6;
    /// <summary>Views kept per level.</summary>
    public int Beam { get; init; } = 3;
    /// <summary>Children per side of the sub-view grid (Grid² per node).</summary>
    public int Grid { get; init; } = 3;
    /// <summary>Extra randomly placed children per node.</summary>
    public int Jitter { get; init; } = 2;
    /// <summary>Jitter children per node on the first level (a wider random
    /// start; "Surprise me" raises it).</summary>
    public int FirstLevelJitter { get; init; } = 2;
    /// <summary>Zoom factor per level.</summary>
    public double ZoomStep { get; init; } = 4;
    /// <summary>Probe render size (square, pixels).</summary>
    public int ProbeSize { get; init; } = 96;
    /// <summary>The family's precision floor: no level goes past this zoom.</summary>
    public double MaxZoom { get; init; } = 1e13;
    /// <summary>A level whose best child scores below this ends the descent.</summary>
    public double MinScore { get; init; } = 0.05;
    /// <summary>Kept views on one level are at least this many child-view
    /// widths apart, so the beam does not collapse onto one spot.</summary>
    public double MinSeparation { get; init; } = 0.5;
    public InterestWeights Weights { get; init; } = InterestWeights.Default;

    public static AutoExploreOptions Default { get; } = new();
}

/// <summary>One scored view in the search tree.</summary>
public sealed record ExploreNode(ExploreView View, InterestScore Score, int Level, ExploreNode? Parent)
{
    /// <summary>The views from the start to this node.</summary>
    public IReadOnlyList<ExploreNode> Path()
    {
        var list = new List<ExploreNode>();
        for (var n = this; n != null; n = n.Parent) list.Add(n);
        list.Reverse();
        return list;
    }
}

public enum ExploreStop
{
    /// <summary>All requested levels were searched.</summary>
    Completed,
    /// <summary>Every child of a level scored below MinScore.</summary>
    DeadEnd,
    /// <summary>The next level would pass MaxZoom.</summary>
    PrecisionLimit,
    /// <summary>The start view could not be probed.</summary>
    NoProbe,
}

/// <summary>Search result. <see cref="Finalists"/> = the last kept level,
/// best first; <see cref="Best"/> is its head (or the start when nothing
/// deeper qualified).</summary>
public sealed record AutoExploreResult(
    ExploreNode? Best, IReadOnlyList<ExploreNode> Finalists, int Probes, ExploreStop Stop);

/// <summary>Progress: level being searched, probes done so far, best score yet.</summary>
public readonly record struct ExploreProgress(int Level, int Probes, double BestScore);

public static class AutoExplorer
{
    /// <summary>Complex-plane width of a zoom-1 view (as ViewCamera).</summary>
    public const double PlaneExtent = 3.5;

    /// <summary>Zoom limit of the probe for families that render in double.</summary>
    public const double DoubleMaxZoom = 1e13;

    /// <summary>Families the explorer runs on: 2D, pannable and deterministic.
    /// 3D families, the stochastic splat renderers (Buddhabrot family, Flame)
    /// and the non-pannable generators (Plasma, Acid Warp, DLA, Random Tile)
    /// are excluded.</summary>
    public static bool Supports(FractalType t)
        => !FractalViewState.IsThreeD(t) && t switch
        {
            FractalType.BuddhaBrot or FractalType.Nebulabrot
                or FractalType.AntiBuddhabrot or FractalType.AntiNebulabrot
                or FractalType.DualBuddhabrot or FractalType.Flame
                or FractalType.Plasma or FractalType.AcidWarp
                or FractalType.Dla or FractalType.RandomTile => false,
            _ => true,
        };

    /// <summary>The deepest zoom the probe resolves for a family: Mandelbrot
    /// carries the octuple-double centre (to the Extreme tier's 1e100); every
    /// other family's probe centre is a double.</summary>
    public static double MaxZoomFor(FractalType t)
        => t == FractalType.Mandelbrot ? QualityPreset.Extreme.ZoomMax : DoubleMaxZoom;

    public static AutoExploreResult Run(
        ExploreView start, AutoExploreOptions o,
        Func<ExploreView, CancellationToken, ProbeField?> probe,
        IProgress<ExploreProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(probe);
        var rng = new Random(o.Seed);
        int probes = 0;

        var rootField = probe(start, ct);
        probes++;
        if (rootField == null)
            return new AutoExploreResult(null, Array.Empty<ExploreNode>(), probes, ExploreStop.NoProbe);
        var root = new ExploreNode(start, InterestScorer.Score(rootField, o.Weights), 0, null);

        var beam = new List<ExploreNode> { root };
        var stop = ExploreStop.Completed;
        for (int level = 1; level <= SMath.Max(0, o.Depth); level++)
        {
            ct.ThrowIfCancellationRequested();
            double childZoom = beam[0].View.Zoom * o.ZoomStep;
            if (childZoom > o.MaxZoom) { stop = ExploreStop.PrecisionLimit; break; }

            var pool = new List<ExploreNode>();
            int jitter = level == 1 ? SMath.Max(o.Jitter, o.FirstLevelJitter) : o.Jitter;
            foreach (var parent in beam)
            {
                foreach (var view in Children(parent.View, o, jitter, rng))
                {
                    ct.ThrowIfCancellationRequested();
                    var f = probe(view, ct);
                    probes++;
                    if (f == null) continue;
                    pool.Add(new ExploreNode(view, InterestScorer.Score(f, o.Weights), level, parent));
                }
                progress?.Report(new ExploreProgress(level, probes,
                    pool.Count > 0 ? pool.Max(p => p.Score.Total) : 0));
            }

            var next = Select(pool, o);
            if (next.Count == 0 || next[0].Score.Total < o.MinScore) { stop = ExploreStop.DeadEnd; break; }
            beam = next;
        }

        beam.Sort(ByScore);
        return new AutoExploreResult(beam[0], beam, probes, stop);
    }

    /// <summary>The child views of <paramref name="v"/>: the cell centres of a
    /// Grid×Grid split of the square view, then <paramref name="jitter"/>
    /// uniformly random centres inside it, all at ZoomStep× the zoom.</summary>
    public static IEnumerable<ExploreView> Children(ExploreView v, AutoExploreOptions o, int jitter, Random rng)
    {
        double extent = PlaneExtent / v.Zoom;
        double zoom = v.Zoom * o.ZoomStep;
        int g = SMath.Max(1, o.Grid);
        for (int j = 0; j < g; j++)
            for (int i = 0; i < g; i++)
                yield return new ExploreView(
                    v.Center.Translate((i - (g - 1) / 2.0) * extent / g, (j - (g - 1) / 2.0) * extent / g), zoom);
        for (int k = 0; k < jitter; k++)
        {
            double dx = (rng.NextDouble() - 0.5) * extent;
            double dy = (rng.NextDouble() - 0.5) * extent;
            yield return new ExploreView(v.Center.Translate(dx, dy), zoom);
        }
    }

    // Best first, then the earlier-generated child (stable, so deterministic).
    private static int ByScore(ExploreNode a, ExploreNode b) => b.Score.Total.CompareTo(a.Score.Total);

    private static List<ExploreNode> Select(List<ExploreNode> pool, AutoExploreOptions o)
    {
        var sorted = pool.Select((n, i) => (n, i))
                         .OrderByDescending(t => t.n.Score.Total).ThenBy(t => t.i)
                         .Select(t => t.n).ToList();
        var kept = new List<ExploreNode>();
        foreach (var n in sorted)
        {
            if (kept.Count >= SMath.Max(1, o.Beam)) break;
            double minDist = o.MinSeparation * PlaneExtent / n.View.Zoom;
            bool tooClose = kept.Any(k => Distance(k.View.Center, n.View.Center) < minDist);
            if (!tooClose) kept.Add(n);
        }
        return kept;
    }

    private static double Distance(DeepComplex a, DeepComplex b)
    {
        double dx = OdExact.Sub(a.Re, b.Re).X0, dy = OdExact.Sub(a.Im, b.Im).X0;
        return SMath.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Parse a batch <c>--explore</c> spec: comma-separated
    /// <c>key=value</c> pairs (seed, depth, beam, grid, jitter, start, step,
    /// probe, minscore). Unknown keys and bad values are errors.</summary>
    public static bool TryParseSpec(string? spec, out AutoExploreOptions options, out string? error)
    {
        options = AutoExploreOptions.Default;
        error = null;
        if (string.IsNullOrWhiteSpace(spec)) return true;
        var o = AutoExploreOptions.Default;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) { error = $"'{raw}' is not key=value"; return false; }
            string key = raw[..eq].Trim().ToLowerInvariant(), val = raw[(eq + 1)..].Trim();
            bool okInt = int.TryParse(val, System.Globalization.NumberStyles.Integer, inv, out int iv);
            bool okDbl = double.TryParse(val, System.Globalization.NumberStyles.Float, inv, out double dv);
            switch (key)
            {
                case "seed" when okInt: o = o with { Seed = iv }; break;
                case "depth" when okInt && iv is >= 1 and <= 64: o = o with { Depth = iv }; break;
                case "beam" when okInt && iv is >= 1 and <= 32: o = o with { Beam = iv }; break;
                case "grid" when okInt && iv is >= 1 and <= 8: o = o with { Grid = iv }; break;
                case "jitter" when okInt && iv is >= 0 and <= 64: o = o with { Jitter = iv }; break;
                case "start" when okInt && iv is >= 0 and <= 256: o = o with { FirstLevelJitter = iv }; break;
                case "step" when okDbl && dv is >= 1.5 and <= 1000: o = o with { ZoomStep = dv }; break;
                case "probe" when okInt && iv is >= 16 and <= 512: o = o with { ProbeSize = iv }; break;
                case "minscore" when okDbl && dv is >= 0 and <= 1: o = o with { MinScore = dv }; break;
                case "seed" or "depth" or "beam" or "grid" or "jitter" or "start" or "step" or "probe" or "minscore":
                    error = $"bad value for {key}: '{val}' (seed int; depth 1..64; beam 1..32; grid 1..8; "
                          + "jitter 0..64; start 0..256; step 1.5..1000; probe 16..512; minscore 0..1)";
                    return false;
                default:
                    error = $"unknown key '{key}' (seed, depth, beam, grid, jitter, start, step, probe, minscore)";
                    return false;
            }
        }
        options = o;
        return true;
    }

    /// <summary>The canonical spec for <paramref name="o"/>: only the keys that
    /// differ from the defaults (seed always), so parse → format round-trips.</summary>
    public static string FormatSpec(AutoExploreOptions o)
    {
        var d = AutoExploreOptions.Default;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var parts = new List<string> { "seed=" + o.Seed.ToString(inv) };
        if (o.Depth != d.Depth) parts.Add("depth=" + o.Depth.ToString(inv));
        if (o.Beam != d.Beam) parts.Add("beam=" + o.Beam.ToString(inv));
        if (o.Grid != d.Grid) parts.Add("grid=" + o.Grid.ToString(inv));
        if (o.Jitter != d.Jitter) parts.Add("jitter=" + o.Jitter.ToString(inv));
        if (o.FirstLevelJitter != d.FirstLevelJitter) parts.Add("start=" + o.FirstLevelJitter.ToString(inv));
        if (o.ZoomStep != d.ZoomStep) parts.Add("step=" + o.ZoomStep.ToString("R", inv));
        if (o.ProbeSize != d.ProbeSize) parts.Add("probe=" + o.ProbeSize.ToString(inv));
        if (o.MinScore != d.MinScore) parts.Add("minscore=" + o.MinScore.ToString("R", inv));
        return string.Join(",", parts);
    }
}
