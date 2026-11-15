// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/DeepRoute/DeepRouteChooser.cs
// #1665 / #1785 (S1) — pick the deep-zoom route's winners from timed runs.
//
// Pure: the calibration pass (#1786, S2) times each candidate route on a few fixed
// scenes and measures its iteration disagreement against CPU LA (as --gpulaprobe
// does); this turns those runs into the DeepRouteStore rows. A route that disagrees
// past MaxDisagreement on any scene is not eligible anywhere: a fast wrong route
// must never win.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FracturingFog
{
    /// <summary>#1665 — the candidate deep routes the calibration times.</summary>
    public enum DeepRouteCandidate
    {
        /// <summary>The CPU BLA / PT rows (LA off).</summary>
        CpuBla,
        /// <summary>CPU LA stages, scalar tail.</summary>
        CpuLa,
        /// <summary>CPU LA stages, 4-lane AVX2 tail (#1628).</summary>
        CpuLaSimd,
        /// <summary>GPU SA / BLA, the fp64 kernel.</summary>
        GpuSaFp64,
        /// <summary>GPU deep perturbation, the float-exp / scaled-fp32 kernel (#1331).</summary>
        GpuFloatExp,
        /// <summary>GPU LA stages, fp64 (#1630).</summary>
        GpuLaFp64,
    }

    /// <summary>#1665 — one timed run: a route on a scene, its best time and its
    /// fraction of pixels whose iteration count differs from CPU LA's.</summary>
    public readonly record struct DeepRouteTiming(DeepRouteCandidate Route, string Scene, double Zoom, double Ms, double Disagreement);

    public static class DeepRouteChooser
    {
        /// <summary>The iteration disagreement any route may show on a scene and stay
        /// eligible (the S1630 GPU-vs-CPU LA bound).</summary>
        public const double MaxDisagreement = 0.02;

        /// <summary>#1786 — a GPU route may also disagree up to this many times the CPU
        /// evaluators' own disagreement on the scene (the noise floor: CPU BLA and CPU LA
        /// differ by ~0.2-4 % of pixels on the p448 scenes, measured).</summary>
        public const double FloorFactor = 1.5;

        /// <summary>#1786 — the gain a measured choice needs to replace today's default
        /// (LA SIMD on, GPU deep on, GPU LA on, float-exp over fp64): noise must not flip
        /// a default (the #1339 shape tuner keeps 3 %).</summary>
        public const double MinGain = 0.05;

        private static bool IsCpu(DeepRouteCandidate r) => r is DeepRouteCandidate.CpuBla or DeepRouteCandidate.CpuLa or DeepRouteCandidate.CpuLaSimd;

        /// <summary>The routes that ran with a finite time on every scene and, for a GPU
        /// route, within max(<see cref="MaxDisagreement"/>, <see cref="FloorFactor"/> × the
        /// CPU evaluators' disagreement on that scene). The CPU evaluators (BLA rows, LA,
        /// LA SIMD) are the shipped references and define that floor: they are eligible
        /// whatever they disagree by.</summary>
        public static IReadOnlySet<DeepRouteCandidate> Eligible(IEnumerable<DeepRouteTiming> runs)
        {
            var all = runs.ToList();
            var floor = all.Where(t => IsCpu(t.Route) && double.IsFinite(t.Ms)).GroupBy(t => t.Scene)
                .ToDictionary(g => g.Key, g => g.Max(t => t.Disagreement));
            bool Ok(DeepRouteTiming t) => double.IsFinite(t.Ms) && t.Ms > 0
                // (A CPU evaluator always passes: the floor is the largest of their own disagreements.)
                && t.Disagreement <= Math.Max(MaxDisagreement, FloorFactor * floor.GetValueOrDefault(t.Scene));
            return all.GroupBy(t => t.Route).Where(g => g.All(Ok)).Select(g => g.Key).ToHashSet();
        }

        // a beats b by MinGain
        private static bool Beats(double a, double b) => a < b * (1 - MinGain);

        /// <summary>The store rows from the runs. Fields stay null where the runs
        /// can't decide (the default then applies).</summary>
        public static (DeepRouteCpuResult Cpu, DeepRouteGpuResult? Gpu) Choose(IEnumerable<DeepRouteTiming> runs, string measured)
        {
            var all = runs.ToList();
            var ok = Eligible(all);
            var scenes = all.Select(t => (t.Scene, t.Zoom)).Distinct().OrderBy(s => s.Zoom).ToList();
            double? Ms(DeepRouteCandidate r, string scene)
                => ok.Contains(r) && all.FirstOrDefault(t => t.Route == r && t.Scene == scene) is { Ms: > 0 } t ? t.Ms : null;

            // LA tail: SIMD where it is eligible and faster in total over the scenes both ran.
            bool? laSimd = null;
            if (ok.Contains(DeepRouteCandidate.CpuLa) && ok.Contains(DeepRouteCandidate.CpuLaSimd))
            {
                var both = scenes.Where(s => Ms(DeepRouteCandidate.CpuLa, s.Scene) != null && Ms(DeepRouteCandidate.CpuLaSimd, s.Scene) != null).ToList();
                if (both.Count > 0)
                    laSimd = Beats(both.Sum(s => Ms(DeepRouteCandidate.CpuLaSimd, s.Scene)!.Value), both.Sum(s => Ms(DeepRouteCandidate.CpuLa, s.Scene)!.Value));
            }
            var la = laSimd == true ? DeepRouteCandidate.CpuLaSimd : DeepRouteCandidate.CpuLa;

            // LA crossover: the shallowest scene from which LA beats BLA on it and every
            // deeper scene; +inf when LA wins on none (or not on the deepest).
            double? laMin = null;
            var cmp = scenes.Where(s => Ms(la, s.Scene) != null && Ms(DeepRouteCandidate.CpuBla, s.Scene) != null).ToList();
            if (cmp.Count > 0)
            {
                laMin = double.PositiveInfinity;
                for (int i = cmp.Count - 1; i >= 0; i--)
                {
                    if (Ms(la, cmp[i].Scene)!.Value < Ms(DeepRouteCandidate.CpuBla, cmp[i].Scene)!.Value) laMin = cmp[i].Zoom;
                    else break;
                }
            }
            else if (ok.Contains(la) && !ok.Contains(DeepRouteCandidate.CpuBla) && scenes.Count > 0)
                laMin = scenes[0].Zoom;   // only LA is eligible: LA wherever measured

            var cpu = new DeepRouteCpuResult { LaMinZoom = laMin, LaSimd = laSimd, Measured = measured };

            // GPU: the fp64-or-float-exp choice, GPU LA over GPU SA / BLA, then GPU deep
            // over the CPU's best route, per scene, totalled over scenes both ran.
            var gpuRoutes = new[] { DeepRouteCandidate.GpuSaFp64, DeepRouteCandidate.GpuFloatExp, DeepRouteCandidate.GpuLaFp64 };
            if (!all.Any(t => !IsCpu(t.Route))) return (cpu, null);

            double Total(DeepRouteCandidate r, IEnumerable<(string Scene, double Zoom)> on) => on.Sum(s => Ms(r, s.Scene) ?? double.NaN);

            GpuDeepPrecision? precision = null;
            bool fp64 = ok.Contains(DeepRouteCandidate.GpuSaFp64), fx = ok.Contains(DeepRouteCandidate.GpuFloatExp);
            if (fp64 && fx)
            {
                var both = scenes.Where(s => Ms(DeepRouteCandidate.GpuSaFp64, s.Scene) != null && Ms(DeepRouteCandidate.GpuFloatExp, s.Scene) != null).ToList();
                precision = Beats(Total(DeepRouteCandidate.GpuFloatExp, both), Total(DeepRouteCandidate.GpuSaFp64, both)) ? GpuDeepPrecision.Fp32 : GpuDeepPrecision.Double;
            }
            else if (fp64) precision = GpuDeepPrecision.Double;
            else if (fx) precision = GpuDeepPrecision.Fp32;
            var plain = precision == GpuDeepPrecision.Fp32 ? DeepRouteCandidate.GpuFloatExp : DeepRouteCandidate.GpuSaFp64;

            bool? gpuLa = null;
            if (ok.Contains(DeepRouteCandidate.GpuLaFp64) && ok.Contains(plain))
            {
                var both = scenes.Where(s => Ms(DeepRouteCandidate.GpuLaFp64, s.Scene) != null && Ms(plain, s.Scene) != null).ToList();
                if (both.Count > 0) gpuLa = Beats(Total(DeepRouteCandidate.GpuLaFp64, both), Total(plain, both));
            }
            else if (all.Any(t => t.Route == DeepRouteCandidate.GpuLaFp64)) gpuLa = false;   // ran and not eligible

            // Per scene: the GPU's best eligible route against the CPU's best.
            bool? gpuDeep = null;
            double gpuSum = 0, cpuSum = 0; int n = 0;
            foreach (var s in scenes)
            {
                double? g = gpuRoutes.Select(r => Ms(r, s.Scene)).Where(v => v != null).Min();
                double? c = new[] { DeepRouteCandidate.CpuBla, DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuLaSimd }.Select(r => Ms(r, s.Scene)).Where(v => v != null).Min();
                if (g == null || c == null) continue;
                gpuSum += g.Value; cpuSum += c.Value; n++;
            }
            if (n > 0) gpuDeep = Beats(gpuSum, cpuSum);
            else if (!gpuRoutes.Any(ok.Contains)) gpuDeep = false;   // GPU routes ran, none eligible

            return (cpu, new DeepRouteGpuResult { GpuDeep = gpuDeep, GpuLa = gpuLa, Precision = precision, Measured = measured });
        }
    }
}
