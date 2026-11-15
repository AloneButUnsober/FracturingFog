// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/DeepRoute/DeepRouteCalibrator.cs
// #1665 / #1786 (S2) — the calibration pass: time each candidate deep-zoom route on a
// few fixed scenes, check its iterations against CPU LA, and store the winners
// (DeepRouteChooser → DeepRouteStore, S1).
//
//   • Scenes, shallow to deep: the bench-kit "p448" deep minibrot (fractadyne
//     bench-kit, WindySnowOwl, https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit,
//     from rith's FF 0.7.1 benchmark report; see the bibliography) at 1e15 .. its own
//     5e33, which straddles the LA crossover (#1629: 1e30 here), and FF's built-in
//     "3E47 Test" region (every deep pixel rebases there, S1274 / S1630).
//   • Each scene: an untimed CPU LA frame first (the iteration reference), then every
//     candidate, CPU LA included, in alternating order on the second pass (as
//     --gpulaprobe). Best of the passes; a route already 3× slower than the scene's best
//     skips its second pass. (#1789: the reference used to be CPU LA's first timing;
//     the first frame after a scene switch runs slow, which biased CPU LA.)
//   • A run that fell back (the GPU too slow, the route not taken) is not eligible.
//   • A time budget (seconds) and the cancellation token stop the pass early; the
//     chooser then decides what the finished scenes can, the rest keeps the default.
// The runner is injected (IDeepRouteRunner): CalculatorDeepRouteRunner renders real
// frames; tests time fakes.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace FracturingFog
{
    /// <summary>#1786 — a calibration scene: a view (centre limbs), zoom and iteration cap.</summary>
    public sealed record DeepRouteScene(string Name, double[] X, double[] Y, double Zoom, int MaxIter);

    /// <summary>#1786 — one route run: its time and iteration counts, or a fallback.</summary>
    public readonly record struct DeepRouteRun(double Ms, int[]? Iterations, bool FellBack)
    {
        public static DeepRouteRun Fallback => new(double.NaN, null, true);
    }

    /// <summary>#1786 — renders a scene on a route. Implementations: CalculatorDeepRouteRunner
    /// (real frames), test fakes.</summary>
    public interface IDeepRouteRunner
    {
        /// <summary>The routes this machine can run (CPU LA always first: the reference).</summary>
        IReadOnlyList<DeepRouteCandidate> Candidates { get; }
        /// <summary>The GPU's device key (the store's GPU row), or null without a GPU.</summary>
        string? GpuDeviceKey { get; }
        DeepRouteRun Run(DeepRouteScene scene, DeepRouteCandidate route, CancellationToken ct);
    }

    /// <summary>#1786 — what a calibration measured and chose.</summary>
    public sealed record DeepRouteCalibrationResult(
        IReadOnlyList<DeepRouteTiming> Timings, DeepRouteCpuResult Cpu, DeepRouteGpuResult? Gpu,
        string? GpuDeviceKey, bool Complete, double ElapsedMs);

    public static class DeepRouteCalibrator
    {
        /// <summary>Alternating passes per scene, best of each route; a 3x loser stops after one.
        /// #1789: the scalar LA tail's speed depends on the JIT's dynamic-PGO profile: measured
        /// fresh on one scene it matched the 4-lane tail (170 vs 176 ms, p448 3e33, 960x540),
        /// after the shallower scenes it ran 15-40 % slower (220 ms) while the 4-lane tail stayed
        /// at ~175 ms (with PGO off: 260-300 ms vs ~175). The pass, like a session zooming in,
        /// measures the warmed process; more passes do not change that, so three suffice.</summary>
        public const int Passes = 3;

        /// <summary>A route this many times slower than the scene's best after the first
        /// pass skips the second (it cannot win).</summary>
        public const double LoserFactor = 3.0;

        /// <summary>Default wall-clock budget.</summary>
        public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

        private static readonly double[] P448X = [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50, -1.0894092006534645e-66, -5.481175082318642e-83, -1.5340609125118276e-99, 1.4285946495395532e-116];
        private static readonly double[] P448Y = [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50, -4.820603700098655e-67, -3.1189999875481587e-83, 1.2339668272603997e-99, -9.480108552256602e-117];
        private static readonly double[] E47X = [-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51, 0, 0, 0, 0];
        private static readonly double[] E47Y = [-5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55, 0, 0, 0, 0];

        /// <summary>The default scenes, shallow to deep.</summary>
        public static readonly IReadOnlyList<DeepRouteScene> DefaultScenes =
        [
            new("p448 1e15", P448X, P448Y, 1e15, 13440),
            new("p448 1e22", P448X, P448Y, 1e22, 13440),
            new("p448 1e27", P448X, P448Y, 1e27, 13440),
            new("p448 1e31", P448X, P448Y, 1e31, 13440),
            new("p448 3e33", P448X, P448Y, 3e33, 13440),
            new("3E47", E47X, E47Y, 3e47, 20000),
        ];

        /// <summary>Fraction of pixels whose iteration count differs.</summary>
        public static double Disagreement(int[] a, int[] reference)
        {
            if (a.Length != reference.Length || a.Length == 0) return 1.0;
            int d = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != reference[i]) d++;
            return d / (double)a.Length;
        }

        /// <summary>Run the pass. Stores the winners unless <paramref name="store"/> is false.</summary>
        public static DeepRouteCalibrationResult Run(IDeepRouteRunner runner, IReadOnlyList<DeepRouteScene>? scenes = null,
            TimeSpan? budget = null, bool store = true, CancellationToken ct = default, Action<string>? log = null,
            bool warmUp = true)
        {
            ArgumentNullException.ThrowIfNull(runner);
            scenes ??= DefaultScenes;
            var limit = budget ?? DefaultBudget;
            var clock = Stopwatch.StartNew();
            var timings = new List<DeepRouteTiming>();
            // #1789 — CPU LA is timed in the alternation like every route. Its reference run
            // (the first frame of a scene) is not timed: measured, the frame right after a
            // scene switch ran up to ~25 % slow, which, when CPU LA always took that slot,
            // made the 4-lane LA tail look 5-20 % faster than the scalar one (alternated, the
            // two are within 4 %, as #1628 found).
            var routes = runner.Candidates.ToList();
            bool complete = true;
            bool OutOfTime() => ct.IsCancellationRequested || clock.Elapsed > limit;

            // #1789 — an untimed pass over every scene and route first: a fresh process ran
            // its first frames of each code path up to 6x slow (CPU BLA at p448 1e22: 176 ms,
            // then 27), so the first-in-process pass chose differently (LA from 3e33, SIMD
            // off) from the next ones (1e27, SIMD on). Timed after it, every pass measures a
            // warmed process, as a session that has zoomed in is.
            if (warmUp)
                foreach (var s in scenes.OrderBy(s => s.Zoom))
                    foreach (var r in routes)
                    {
                        if (OutOfTime()) break;
                        runner.Run(s, r, ct);
                    }

            foreach (var s in scenes.OrderBy(s => s.Zoom))
            {
                if (OutOfTime()) { complete = false; break; }
                // The reference: CPU LA's iterations (the run is not timed, #1789).
                var refRun = runner.Run(s, DeepRouteCandidate.CpuLa, ct);
                if (refRun.FellBack || refRun.Iterations == null) { log?.Invoke($"{s.Name}: CPU LA did not run; scene skipped"); continue; }
                var best = new Dictionary<DeepRouteCandidate, double>();
                var dis = new Dictionary<DeepRouteCandidate, double>();
                var failed = new HashSet<DeepRouteCandidate>();
                bool sceneDone = true;
                for (int pass = 0; pass < Passes && sceneDone; pass++)
                {
                    var order = pass % 2 == 0 ? routes : Enumerable.Reverse(routes).ToList();
                    foreach (var r in order)
                    {
                        if (failed.Contains(r)) continue;
                        if (pass > 0 && best.TryGetValue(r, out double b) && b > LoserFactor * best.Values.Min()) continue;
                        if (OutOfTime()) { sceneDone = false; break; }
                        var run = runner.Run(s, r, ct);
                        if (run.FellBack || run.Iterations == null) { failed.Add(r); continue; }
                        best[r] = best.TryGetValue(r, out double prev) ? Math.Min(prev, run.Ms) : run.Ms;
                        if (!dis.ContainsKey(r)) dis[r] = Disagreement(run.Iterations, refRun.Iterations);
                    }
                }
                if (!sceneDone) { complete = false; }
                foreach (var (r, ms) in best) timings.Add(new DeepRouteTiming(r, s.Name, s.Zoom, ms, dis[r]));
                foreach (var r in failed) timings.Add(new DeepRouteTiming(r, s.Name, s.Zoom, double.NaN, 1.0));
                log?.Invoke($"{s.Name} ({s.Zoom:0e0}): " + string.Join(", ",
                    best.OrderBy(p => p.Value).Select(p => $"{p.Key} {p.Value:F0} ms ({100 * dis[p.Key]:F2} %)"))
                    + (failed.Count > 0 ? "; fell back: " + string.Join(", ", failed) : ""));
                if (!sceneDone) break;
            }

            string measured = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var (cpu, gpu) = DeepRouteChooser.Choose(timings, measured);
            if (store && timings.Count > 0)
            {
                DeepRouteStore.StoreCpu(cpu);
                if (gpu != null && runner.GpuDeviceKey is { Length: > 0 } key) DeepRouteStore.StoreGpu(key, gpu);
            }
            return new DeepRouteCalibrationResult(timings, cpu, gpu, runner.GpuDeviceKey, complete, clock.Elapsed.TotalMilliseconds);
        }
    }
}
