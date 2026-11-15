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
//   • Each scene: CPU LA first (the iteration reference), then every candidate, in
//     alternating order on the second pass (as --gpulaprobe). Best of the passes; a
//     route already 3× slower than the scene's best skips its second pass.
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
        public const int Passes = 2;

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
            TimeSpan? budget = null, bool store = true, CancellationToken ct = default, Action<string>? log = null)
        {
            ArgumentNullException.ThrowIfNull(runner);
            scenes ??= DefaultScenes;
            var limit = budget ?? DefaultBudget;
            var clock = Stopwatch.StartNew();
            var timings = new List<DeepRouteTiming>();
            var routes = runner.Candidates.Where(r => r != DeepRouteCandidate.CpuLa).ToList();
            bool complete = true;
            bool OutOfTime() => ct.IsCancellationRequested || clock.Elapsed > limit;

            foreach (var s in scenes.OrderBy(s => s.Zoom))
            {
                if (OutOfTime()) { complete = false; break; }
                // The reference: CPU LA (it also counts as its own first timing).
                var refRun = runner.Run(s, DeepRouteCandidate.CpuLa, ct);
                if (refRun.FellBack || refRun.Iterations == null) { log?.Invoke($"{s.Name}: CPU LA did not run; scene skipped"); continue; }
                var best = new Dictionary<DeepRouteCandidate, double> { [DeepRouteCandidate.CpuLa] = refRun.Ms };
                var dis = new Dictionary<DeepRouteCandidate, double> { [DeepRouteCandidate.CpuLa] = 0 };
                var failed = new HashSet<DeepRouteCandidate>();
                bool sceneDone = true;
                for (int pass = 0; pass < Passes && sceneDone; pass++)
                {
                    var order = pass % 2 == 0 ? routes : Enumerable.Reverse(routes).ToList();
                    if (pass > 0)
                    {
                        // CPU LA again, between the others (alternating order).
                        if (OutOfTime()) { sceneDone = false; break; }
                        var again = runner.Run(s, DeepRouteCandidate.CpuLa, ct);
                        if (!again.FellBack) best[DeepRouteCandidate.CpuLa] = Math.Min(best[DeepRouteCandidate.CpuLa], again.Ms);
                    }
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
