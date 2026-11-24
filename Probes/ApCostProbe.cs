// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2015 — --apcostprobe [W H]: where a frame's time goes past 1e100 (the arbitrary-
// precision reference orbit, epic #1428), on a directory of .kfr scenes (FF_APCOST_KFR,
// e.g. the fractadyne bench-kit's ladder by WindySnowOwl: bibliography
// #fractadyne-bench-kit). One sample per pixel, CPU, the default deep route. Per scene:
// the reference orbit (ms, its length, µs per iteration, the period found), the
// BLA / SA tables and the pixels (incl. the LA stages past ~1e290, where the float-exp
// rows build them), the frame, and how many threads the frame kept busy (CPU time /
// wall). FF_APCOST_REPS (default 1) repeats each scene; the best frame counts.
// FF_APCOST_EXACT=1 (#2032) also renders each frame with the full-length orbit
// (periodic reference off) and counts the pixels whose iteration count differs.
// Exit 0 always.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FracturingFog
{
    internal static class ApCostProbe
    {
        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 320, h = args.Length > 2 ? int.Parse(args[2]) : 180;
            int reps = int.TryParse(Environment.GetEnvironmentVariable("FF_APCOST_REPS"), out int r) && r > 0 ? r : 1;
            string? dir = Environment.GetEnvironmentVariable("FF_APCOST_KFR");
            if (dir == null || !Directory.Exists(dir)) { Console.WriteLine("apcostprobe: set FF_APCOST_KFR to a directory of .kfr files"); return 0; }
            Console.WriteLine($"apcostprobe {w}x{h}, CPU, 1 sample per pixel, best of {reps}");
            Console.WriteLine("scene | zoom | iter cap | frame ms | orbit ms (length, us/iter, period) | BLA+SA ms | pixels ms | threads busy");
            foreach (var f in Directory.GetFiles(dir, "*.kfr").OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!FracturingFog.ViewState.KfrLocation.TryParse(File.ReadAllText(f), out var loc, out var err) || loc == null)
                { Console.WriteLine($"  {Path.GetFileName(f)}: {err}"); continue; }
                var (X, Y) = loc.Centre();
                double zl = loc.FfZoomLog10(w, h);
                int iter = loc.Iterations ?? 10000;
                double best = double.MaxValue, cpu = 0;
                MandelbrotCalculator? kept = null;
                for (int k = 0; k < reps; k++)
                {
                    var c = new MandelbrotCalculator(w, h)
                    {
                        Quality = FracturingFog.Models.QualityPreset.Unbounded,
                        CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3],
                        CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
                        CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3],
                        CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
                        Zoom = Math.Pow(10, Math.Min(zl, 300)),
                        MaxIterations = iter,
                    };
                    c.SetDepth(loc.CentreExact(), zl);
                    var proc = Process.GetCurrentProcess();
                    var cpu0 = proc.TotalProcessorTime;
                    var sw = Stopwatch.StartNew();
                    c.Calculate();
                    double ms = sw.Elapsed.TotalMilliseconds;
                    proc.Refresh();
                    if (ms < best) { best = ms; cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds; kept = c; }
                }
                var s = kept!.LastHpStageMs;
                int len = kept.ReferenceOrbitLength;
                string P(double v) => v.ToString("N0", CultureInfo.InvariantCulture);
                string exact = "";
                if (Environment.GetEnvironmentVariable("FF_APCOST_EXACT") == "1")
                {
                    // #2032 — the same frame with the full-length orbit (periodic reference off):
                    // pixels whose iteration count differs.
                    bool saved = MandelbrotCalculator.PeriodicReferenceOrbit;
                    MandelbrotCalculator.PeriodicReferenceOrbit = false;
                    try
                    {
                        var full = new MandelbrotCalculator(w, h)
                        {
                            Quality = FracturingFog.Models.QualityPreset.Unbounded,
                            CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3],
                            CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
                            CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3],
                            CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
                            Zoom = Math.Pow(10, Math.Min(zl, 300)), MaxIterations = iter,
                        };
                        full.SetDepth(loc.CentreExact(), zl);
                        full.Calculate();
                        int d = 0, io = 0;
                        for (int i = 0; i < full.IterationBuffer.Length; i++)
                        {
                            if (full.IterationBuffer[i] != kept.IterationBuffer[i]) d++;
                            if ((full.IterationBuffer[i] >= iter) != (kept.IterationBuffer[i] >= iter)) io++;
                        }
                        exact = $" vs full orbit: iter diff {d}, in/out {io} |";
                    }
                    finally { MandelbrotCalculator.PeriodicReferenceOrbit = saved; }
                }
                Console.WriteLine($"| {Path.GetFileNameWithoutExtension(f)} | 1e{zl:F0} | {P(iter)} | {P(best)} | {P(s.RefOrbit)} ({P(len)}, {1000.0 * s.RefOrbit / Math.Max(1, len):F1}, {kept.ReferencePeriod}) | " +
                                  $"{P(s.Bla + s.Sa)} | {P(s.Pixels)} | {cpu / Math.Max(1e-9, best):F1} |" + exact);
            }
            Console.WriteLine("apcostprobe done");
            return 0;
        }
    }
}
