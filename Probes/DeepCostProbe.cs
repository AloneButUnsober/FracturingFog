// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1613: --deepcostprobe [W H]. Per-pixel cost of the CPU deep-zoom perturbation
// path on benchmark scenes from the fractadyne bench-kit (WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit; centres
// conjugated from the kit's .kfr files; the calculator takes the KF zoom as is).
// The scenes come from rith's FF 0.7.1 benchmark report (a fractalforums.org direct message), which
// found the period-448 one 3x slower than a deeper scene; both credited in
// Docs/Resources-Bibliography.md (#fractadyne-bench-kit, #rith-ff-071-benchmark).
// For each scene: frame time (best of FF_DEEPCOST_REPS, default 2), the pixels
// the rebased loop resolved and total iterations, for the default path (SA + BLA,
// rebased pixels keep BLA) and with acceleration off (plain rebasing, the slow
// reference), plus the pixels whose iteration count differs between the two.
// FF_DEEPCOST_PERIODIC_AB=1 adds the frame with the periodic reference orbit off
// (#1614); FF_DEEPCOST_NOREF=1 skips the acceleration-off reference.
// FF_DEEPCOST_DUMP=dir writes those pixels as CSV (x,y,default,reference) for an
// exact check outside FF; FF_DEEPCOST_SCENE filters by name; FF_DEEPCOST_EPS sets
// the BLA tolerance. Exit 0 always.

using System;
using System.Diagnostics;
using System.Linq;
using FracturingFog.Models;

namespace FracturingFog
{
    internal static class DeepCostProbe
    {
        private sealed record Scene(string Name, string X, string Y, double KfZoom, int Iter);

        private static readonly Scene[] Scenes =
        {
            new("p448 5.2e33",
                "-0.7145519151951202|-2.0790192891565988e-17|1.1753166641509792e-33|-8.102457136962753e-50|-1.0894092006534645e-66|-5.481175082318642e-83|-1.5340609125118276e-99|1.4285946495395532e-116",
                "-0.35402073332318235|2.5477179573089096e-17|-6.072997541261718e-34|-1.8911088656798612e-50|-4.820603700098655e-67|-3.1189999875481587e-83|1.2339668272603997e-99|-9.480108552256602e-117",
                5.176068e33, 13440),
            new("path 1e33",
                "-0.028041054305504548|1.3292199503012176e-18|-5.27591145479967e-35|2.798737894555097e-51|-1.4491927624390092e-67|-6.233008271562717e-84|4.7734689309385144e-101|2.9022031733306904e-117",
                "-0.6948927538996524|5.189489838305484e-17|5.567870094609155e-34|1.7818449158374648e-50|1.8079413574553354e-68|2.5201987988615068e-85|-7.246511953022119e-102|-6.412330137037785e-118",
                1e33, 2859),
            new("p953 1.3e53",
                "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.203746728154813e-51|-4.269596053996866e-68|1.9389949392385534e-84|6.25819023760954e-101|-2.6646795788290983e-117",
                "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3771597857226995e-50|-5.482812918750436e-67|4.692321383846679e-84|-2.313052339214723e-100|2.1139231852108862e-116",
                1.342765e53, 28590),
            // #1614 — the high-period minibrots of rith's report (10x Imagina there).
            new("p15248 2.1e57",
                "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.198613498328184e-51|-2.6338819725348693e-68|-1.2531186861084395e-84|8.265175494285413e-101|-2.8428099960154152e-117",
                "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3770883977626e-50|-1.6692666809801752e-67|-4.583534842942509e-84|4.2910480785722876e-100|-1.016687227891797e-116",
                2.142891e57, 457440),
            new("p121984 1.7e66",
                "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.2016807295614754e-51|1.7176952655143277e-67|1.0180672183123456e-83|-1.5782574900160777e-100|8.451632690787226e-117",
                "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3766027088999295e-50|9.932823572218873e-67|2.5207480153941874e-83|-8.913322115858611e-100|2.182658649920256e-116",
                1.682674e66, 3659520),
            new("p951094 2.1e68",
                "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.2033498374722585e-51|2.1867860643118847e-67|-1.2673249633835861e-83|-2.7319253065659894e-100|-1.9376664330122738e-116",
                "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3765984796783024e-50|7.914053078872335e-67|-5.2156113374567495e-83|-1.2800174978852658e-99|-7.991933193373865e-116",
                2.142891e68, 28532820),
        };

        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 960, h = args.Length > 2 ? int.Parse(args[2]) : 540;
            int reps = int.TryParse(Environment.GetEnvironmentVariable("FF_DEEPCOST_REPS"), out int r) && r > 0 ? r : 2;
            string? only = Environment.GetEnvironmentVariable("FF_DEEPCOST_SCENE");
            Console.WriteLine($"deepcostprobe {w}x{h}, CPU, best of {reps}");
            foreach (var s in Scenes)
            {
                if (only != null && !s.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                // MandelbrotCalculator spans 3.5/zoom across max(W, H), as KF spans
                // the width, so the .kfr zoom carries over for a landscape frame.
                double zoom = s.KfZoom;
                var (a, aSm, aMs, aReb, aIt) = Render(s, zoom, w, h, reps, noAccel: false);
                var st = s_lastStages;
                Console.WriteLine($"  {s.Name,-14} stages: orbit {st.RefOrbit,8:F0} ms ({1e3 * st.RefOrbit / s.Iter:F2} us/iter, period {s_lastPeriod})  " +
                                  $"BLA {st.Bla,6:F0} ms  SA {st.Sa,6:F0} ms  pixels {st.Pixels,7:F0} ms");
                var wk = s_lastWork;
                double batches = w * h / 4.0;
                Console.WriteLine($"  {s.Name,-14} work  : per 4-px batch {wk.PlainSteps / batches,10:F0} plain steps, {wk.BlaSkips / batches,8:F0} BLA skips " +
                                  $"(avg {(wk.BlaSkips == 0 ? 0 : wk.BlaIterSaved / (double)wk.BlaSkips + 1):F1} iter each); " +
                                  $"iterations per px {aIt / (double)(w * h),10:F0}; rebased px {s_lastRebased:N0}; " +
                                  $"CPU {s_lastCpuMs:F0} ms = {s_lastCpuMs / aMs:F1} threads busy of {Environment.ProcessorCount}");
                // #1614 — FF_DEEPCOST_PERIODIC_AB=1: the same frame with the periodic
                // reference off (full-length orbit), and the pixels that differ.
                if (Environment.GetEnvironmentVariable("FF_DEEPCOST_PERIODIC_AB") == "1")
                {
                    MandelbrotCalculator.PeriodicReferenceOrbit = false;
                    try
                    {
                        var (f, _, fMs, _, _) = Render(s, zoom, w, h, reps, noAccel: false);
                        var fs = s_lastStages;
                        int pd = 0;
                        for (int i = 0; i < a.Length; i++) if (a[i] != f[i]) pd++;
                        string? abDump = Environment.GetEnvironmentVariable("FF_DEEPCOST_DUMP");
                        if (abDump != null && pd > 0)
                        {
                            var sb = new System.Text.StringBuilder().AppendLine($"# {w} {h} {zoom:R} {s.Iter}");
                            for (int i = 0; i < a.Length; i++)
                                if (a[i] != f[i]) sb.AppendLine($"{i % w},{i / w},{a[i]},{f[i]}");
                            System.IO.File.WriteAllText(System.IO.Path.Combine(abDump, s.Name.Split(' ')[0] + "_periodic.csv"), sb.ToString());
                        }
                        Console.WriteLine($"  {s.Name,-14} periodic on {aMs,8:F0} ms | off {fMs,8:F0} ms (orbit {fs.RefOrbit:F0}, BLA {fs.Bla:F0}, pixels {fs.Pixels:F0}) | " +
                                          $"iter diff {pd:N0} ({100.0 * pd / a.Length:F2} %)");
                    }
                    finally { MandelbrotCalculator.PeriodicReferenceOrbit = true; }
                }
                if (Environment.GetEnvironmentVariable("FF_DEEPCOST_NOREF") == "1") continue;
                var (b, bSm, bMs, bReb, bIt) = Render(s, zoom, w, h, 1, noAccel: true);
                int diff = 0, escDiff = 0, sm01 = 0, sm1 = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i]) diff++;
                    float ds = Math.Abs(aSm[i] - bSm[i]);
                    if (ds > 0.01f) sm01++;
                    if (ds > 0.1f) sm1++;
                    if ((a[i] >= s.Iter) != (b[i] >= s.Iter)) escDiff++;
                }
                string? dump = Environment.GetEnvironmentVariable("FF_DEEPCOST_DUMP");
                if (dump != null && diff > 0)
                {
                    var sb = new System.Text.StringBuilder().AppendLine($"# {w} {h} {zoom:R} {s.Iter}");
                    for (int i = 0; i < a.Length; i++)
                        if (a[i] != b[i]) sb.AppendLine($"{i % w},{i / w},{a[i]},{b[i]}");
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dump, s.Name.Split(' ')[0] + ".csv"), sb.ToString());
                }
                Console.WriteLine($"  {s.Name,-14} default: {aMs,7:F0} ms rebased {aReb,8:N0} iters {aIt,15:N0} | " +
                                  $"no accel: {bMs,7:F0} ms rebased {bReb,8:N0} | " +
                                  $"iter diff {diff:N0} ({100.0 * diff / a.Length:F2} %), in/out diff {escDiff:N0}, " +
                                  $"smooth diff >0.01 {100.0 * sm01 / a.Length:F2} % >0.1 {100.0 * sm1 / a.Length:F2} %");
            }
            Console.WriteLine("deepcostprobe done");
            return 0;
        }

        private static MandelbrotCalculator.HpStageMs s_lastStages;
        private static int s_lastPeriod;
        private static (long BlaSkips, long BlaIterSaved, long PlainSteps) s_lastWork;
        private static long s_lastRebased;
        private static double s_lastCpuMs;

        private static (int[] Iter, float[] Smooth, double Ms, long Rebased, long Iters) Render(Scene s, double zoom, int w, int h, int reps, bool noAccel)
        {
            double best = double.MaxValue;
            MandelbrotCalculator? last = null;
            for (int k = 0; k < reps; k++)
            {
                var c = Make(s, zoom, w, h);
                c.DisableAcceleration = noAccel;
                var cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                if (double.TryParse(Environment.GetEnvironmentVariable("FF_DEEPCOST_EPS"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double eps)) c.BlaTolerance = eps;
                var sw = Stopwatch.StartNew();
                c.Calculate();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                if (!noAccel) s_lastCpuMs = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
                last = c;
            }
            long iters = last!.IterationBuffer.Sum(v => (long)v);
            if (!noAccel) { s_lastStages = last.LastHpStageMs; s_lastPeriod = last.ReferencePeriod; s_lastWork = last.LastPtWork; s_lastRebased = last.PtRebasedPixels; }
            return ((int[])last.IterationBuffer.Clone(), (float[])last.SmoothBuffer.Clone(), best, last.PtRebasedPixels, iters);
        }

        private static MandelbrotCalculator Make(Scene s, double zoom, int w, int h)
        {
            double[] X = s.X.Split('|').Select(double.Parse).ToArray();
            double[] Y = s.Y.Split('|').Select(double.Parse).ToArray();
            return new MandelbrotCalculator(w, h)
            {
                Quality = QualityPreset.Extreme,
                CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3],
                CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
                CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3],
                CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
                Zoom = zoom,
                MaxIterations = s.Iter,
            };
        }
    }
}
