// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1613: --deepcostprobe [W H]. Per-pixel cost of the CPU deep-zoom perturbation
// path on benchmark scenes from the fractadyne bench-kit (WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit; centres
// conjugated from the kit's .kfr files, a mirror view of the same cost; the KF zoom
// converts as #1615's KfrLocation.FfZoom, KF spanning the height with 4/zoom).
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
// the BLA tolerance. FF_DEEPCOST_LA_AB=1 (#1626) adds the frame through the LA stage
// evaluator (UseLaEvaluator): its time, LA / plain / BLA work per pixel, and the pixels
// whose iteration count differs from the default frame. FF_DEEPCOST_LA_SIMD_AB=1 (#1628)
// times the LA evaluator scalar and 4-lane, alternating frames in one process (best of
// FF_DEEPCOST_REPS each), and checks the two frames are the same bytes.
// FF_DEEPCOST_REGIONS=<regions.json> (#1629) instead times FF's default deep path against
// the LA evaluator on every Mandelbrot region of that file at zoom >= 1e13, alternating
// the two in one process, and prints a sorted table (pixel stage, best of reps).
// FF_DEEPCOST_KFR=<dir> does the same over every .kfr file in a directory (read with
// FF's own KfrLocation, #1615): DeepLa Off ("before", the pre-#1629 path) against Auto,
// whole-frame and pixel-stage times.
// Exit 0 always.

using System;
using System.Diagnostics;
using System.Linq;
using FracturingFog.Models;

namespace FracturingFog
{
    internal static class DeepCostProbe
    {
        internal sealed record Scene(string Name, string X, string Y, double KfZoom, int Iter, bool FfZoom = false);

        // #1629 — FF's own deep views (built-in regions in Installer/Stage/Resources/regions.json,
        // as Server.Tests/S1274SimdRebasingTests.cs uses them; E45Test04's cap is FF's choice),
        // added with FF_DEEPCOST_FFVIEWS=1. Their zoom is FF's, not KF's.
        internal static readonly Scene[] FfViews =
        {
            new("lightning 7.6e26", "-1.4181949444785762|-7.4415882477902279E-17|0|0|0|0|0|0",
                "-0.12700786443815276|-2.3429499355532375E-18|0|0|0|0|0|0", 7.58348E+26, 14786, true),
            new("deeper 4.5e46", "-1.9918151296901943|-7.8219818188678307E-17|3.2454272033149852E-33|-2.6986232918289806E-49|0|0|0|0",
                "-5.5240415753972429E-06|-2.8404793590633191E-22|1.5048294824547351E-38|-6.0649764033320806E-55|0|0|0|0", 4.49845E+46, 49820, true),
            new("e45test04 1.1e47", "0.40679612541749072|1.0460588279145483E-17|-4.3674629952735269E-35|5.0770999219861446E-50|0|0|0|0",
                "-0.56778808906247447|-4.0266051197805093E-17|1.5194922328871422E-33|5.0770999219861446E-50|0|0|0|0", 1.07808E+47, 20000, true),
            new("3e47test 3e47", "-1.9918151296901943|-7.8219844803880472E-17|1.660139930392911E-34|8.217274172159319E-51|0|0|0|0",
                "-5.5240415753972429E-06|-2.8659813126937928E-22|6.6910924119662832E-39|6.2394735914401016E-55|0|0|0|0", 3E+47, 100000, true),
            // The period-3 nucleus (Newton in 60 digits, to double-double): periodic references
            // below Auto's 1e30 threshold.
            new("nucleus3 1e14", "-1.7548776662466927|-3.552187509647825e-17|0|0|0|0|0|0", "0|0|0|0|0|0|0|0", 1e14, 5000, true),
            new("nucleus3 1e22", "-1.7548776662466927|-3.552187509647825e-17|0|0|0|0|0|0", "0|0|0|0|0|0|0|0", 1e22, 20000, true),
            new("nucleus3 1e28", "-1.7548776662466927|-3.552187509647825e-17|0|0|0|0|0|0", "0|0|0|0|0|0|0|0", 1e28, 50000, true),
        };

        internal static readonly Scene[] Scenes =
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
            string? regionsPath = Environment.GetEnvironmentVariable("FF_DEEPCOST_REGIONS");
            if (regionsPath != null) return RunRegions(regionsPath, w, h, reps);
            string? kfrDir = Environment.GetEnvironmentVariable("FF_DEEPCOST_KFR");
            if (kfrDir != null) return RunKfr(kfrDir, w, h, reps);
            var all = Environment.GetEnvironmentVariable("FF_DEEPCOST_FFVIEWS") == "1" ? Scenes.Concat(FfViews) : Scenes;
            foreach (var s in all)
            {
                if (only != null && !s.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                // #1615 — KF spans the height with 4/zoom, MandelbrotCalculator max(W, H)
                // with 3.5/zoom (before #1615 the KF zoom was used as is: a view about
                // 2x wider than the kit's).
                double zoom = s.FfZoom ? s.KfZoom : s.KfZoom * 3.5 * h / (4.0 * Math.Max(w, h));
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
                // #1626 — FF_DEEPCOST_LA_AB=1: the same frame through the LA evaluator.
                if (Environment.GetEnvironmentVariable("FF_DEEPCOST_LA_AB") == "1")
                {
                    var (l, lSm, lMs, _, lIt) = Render(s, zoom, w, h, reps, noAccel: false, la: true);
                    var ls = s_lastStages;
                    var lw = s_lastLaWork;
                    double px = Math.Max(1, lw.Pixels);
                    int ld = 0, lio = 0, lsm = 0;
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (a[i] != l[i]) ld++;
                        if ((a[i] >= s.Iter) != (l[i] >= s.Iter)) lio++;
                        if (a[i] == l[i] && Math.Abs(aSm[i] - lSm[i]) > 0.01f) lsm++;
                    }
                    Console.WriteLine($"  {s.Name,-14} LA     : {lMs,7:F0} ms (BLA+LA tables {ls.Bla:F0} ms, pixels {ls.Pixels:F0} ms; default {aMs:F0} ms) | per px " +
                                      $"LA {lw.LaSteps / px:F0} + plain {lw.PlainSteps / px:F0} + BLA {lw.BlaSkips / px:F0} steps (LA on {s_lastUsedLa}, tail climbs/px {s_lastClimbs / px:F1}) | " +
                                      $"iter diff vs default {ld:N0} ({100.0 * ld / a.Length:F2} %), in/out {lio:N0}, smooth >0.01 where equal {lsm:N0}");
                }
                // #1628 — scalar against 4-lane LA, alternating so clock drift hits both.
                if (Environment.GetEnvironmentVariable("FF_DEEPCOST_LA_SIMD_AB") == "1")
                {
                    double bestS = double.MaxValue, bestV = double.MaxValue, pxS = 0, pxV = 0;
                    MandelbrotCalculator? cs = null, cv = null;
                    for (int k = 0; k < reps; k++)
                    {
                        foreach (bool simd in new[] { false, true })
                        {
                            var c = Make(s, zoom, w, h);
                            c.UseLaEvaluator = true; c.LaSimd = simd;
                            var sw2 = Stopwatch.StartNew();
                            c.Calculate();
                            double ms = sw2.Elapsed.TotalMilliseconds;
                            if (simd) { if (ms < bestV) { bestV = ms; pxV = c.LastHpStageMs.Pixels; } cv = c; }
                            else { if (ms < bestS) { bestS = ms; pxS = c.LastHpStageMs.Pixels; } cs = c; }
                        }
                    }
                    bool same = cs!.IterationBuffer.AsSpan().SequenceEqual(cv!.IterationBuffer)
                                && cs.SmoothBuffer.AsSpan().SequenceEqual(cv.SmoothBuffer)
                                && cs.ColorBuffer.AsSpan().SequenceEqual(cv.ColorBuffer);
                    Console.WriteLine($"  {s.Name,-14} LA scalar {bestS,7:F0} ms (pixels {pxS:F0}) | 4-lane {bestV,7:F0} ms (pixels {pxV:F0}, on {cv.LastFrameUsedLaSimd}) | " +
                                      $"pixels x{pxS / Math.Max(1e-9, pxV):F2} | bytes {(same ? "identical" : "DIFFER")}");
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
        private static (long Pixels, long LaSteps, long PlainSteps, long BlaSkips) s_lastLaWork;
        private static bool s_lastUsedLa;
        private static long s_lastClimbs;

        private static (int[] Iter, float[] Smooth, double Ms, long Rebased, long Iters) Render(Scene s, double zoom, int w, int h, int reps, bool noAccel, bool la = false)
        {
            double best = double.MaxValue;
            MandelbrotCalculator? last = null;
            for (int k = 0; k < reps; k++)
            {
                var c = Make(s, zoom, w, h);
                c.DisableAcceleration = noAccel;
                if (la) c.UseLaEvaluator = true;   // #1626
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
            s_lastLaWork = last.LastLaWork; s_lastUsedLa = last.LastFrameUsedLa; s_lastClimbs = last.LastLaTailClimbs;
            if (!noAccel) { s_lastStages = last.LastHpStageMs; s_lastPeriod = last.ReferencePeriod; s_lastWork = last.LastPtWork; s_lastRebased = last.PtRebasedPixels; }
            return ((int[])last.IterationBuffer.Clone(), (float[])last.SmoothBuffer.Clone(), best, last.PtRebasedPixels, iters);
        }

        // #1629 — DeepLa Off against Auto over a directory of .kfr locations.
        private static int RunKfr(string dir, int w, int h, int reps)
        {
            Console.WriteLine("scene | zoom | iter | before: frame / pixels ms | after: frame / pixels ms | frame x | LA ran | iter diff");
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.kfr").OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!FracturingFog.ViewState.KfrLocation.TryParse(System.IO.File.ReadAllText(f), out var loc, out var err) || loc == null)
                { Console.WriteLine($"  {System.IO.Path.GetFileName(f)}: {err}"); continue; }
                var (X, Y) = loc.Centre();
                double zoom = loc.FfZoom(w, h);
                int iter = loc.Iterations ?? 10000;
                var sc = new Scene(System.IO.Path.GetFileNameWithoutExtension(f),
                    string.Join("|", X.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))),
                    string.Join("|", Y.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))), zoom, iter, true);
                double bF = double.MaxValue, bP = 0, aF = double.MaxValue, aP = 0;
                MandelbrotCalculator? cb = null, ca = null;
                for (int k = 0; k < reps; k++)
                    foreach (var mode in k % 2 == 0 ? new[] { DeepLaMode.Off, DeepLaMode.Auto } : new[] { DeepLaMode.Auto, DeepLaMode.Off })   // swap order: no first-run bias
                    {
                        var c = Make(sc, zoom, w, h);
                        c.LaMode = mode;
                        var sw = Stopwatch.StartNew();
                        c.Calculate();
                        double ms = sw.Elapsed.TotalMilliseconds;
                        if (mode == DeepLaMode.Off) { if (ms < bF) { bF = ms; bP = c.LastHpStageMs.Pixels; } cb = c; }
                        else { if (ms < aF) { aF = ms; aP = c.LastHpStageMs.Pixels; } ca = c; }
                    }
                int diff = 0;
                for (int i = 0; i < cb!.IterationBuffer.Length; i++) if (cb.IterationBuffer[i] != ca!.IterationBuffer[i]) diff++;
                Console.WriteLine($"| {sc.Name} | {zoom:0.0e0} | {iter:N0} | {bF:N0} / {bP:N0} | {aF:N0} / {aP:N0} | x{bF / Math.Max(1e-9, aF):F2} | {(ca!.LastFrameUsedLa ? "yes" : "no")} | {100.0 * diff / cb.IterationBuffer.Length:F2} % |");
            }
            Console.WriteLine("deepcostprobe done");
            return 0;
        }

        // #1629 — default against LA over a regions file.
        private static int RunRegions(string path, int w, int h, int reps)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            string L(System.Text.Json.JsonElement r, string axis)
            {
                string[] names = [$"Center{axis}", $"Center{axis}Lo", $"Center{axis}2", $"Center{axis}3", $"Center{axis}4", $"Center{axis}5", $"Center{axis}6", $"Center{axis}7"];
                return string.Join("|", names.Select(n => r.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? v.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "0"));
            }
            var rows = new System.Collections.Generic.List<(string Name, double Zoom, int Iter, double Def, double La, int Period, double Diff, long LaSteps, long Plain)>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("FractalType", out var ft) && ft.ValueKind != System.Text.Json.JsonValueKind.Null && ft.ToString() != "Mandelbrot" && ft.ToString() != "0") continue;
                double zoom = r.TryGetProperty("Zoom", out var z) ? z.GetDouble() : 0;
                if (zoom < 1e13) continue;
                int iter = r.TryGetProperty("Iterations", out var it) ? it.GetInt32() : 1000;
                string name = r.TryGetProperty("Name", out var nm) ? nm.GetString() ?? "?" : "?";
                var sc = new Scene(name, L(r, "X"), L(r, "Y"), zoom, iter, true);
                double def = double.MaxValue, la = double.MaxValue;
                MandelbrotCalculator? cd = null, cl = null;
                for (int k = 0; k < reps; k++)
                    foreach (bool useLa in new[] { false, true })
                    {
                        var c = Make(sc, zoom, w, h);
                        c.UseLaEvaluator = useLa;
                        c.Calculate();
                        double ms = c.LastHpStageMs.Pixels;
                        if (useLa) { la = Math.Min(la, ms); cl = c; } else { def = Math.Min(def, ms); cd = c; }
                    }
                int diff = 0;
                for (int i = 0; i < cd!.IterationBuffer.Length; i++) if (cd.IterationBuffer[i] != cl!.IterationBuffer[i]) diff++;
                var lw = cl!.LastLaWork;
                rows.Add((name, zoom, iter, def, la, cd.ReferencePeriod, 100.0 * diff / cd.IterationBuffer.Length,
                          lw.LaSteps / Math.Max(1, lw.Pixels), lw.PlainSteps / Math.Max(1, lw.Pixels)));
                Console.WriteLine($"  {name,-24} {zoom,9:0.00e0} iter {iter,7} period {cd.ReferencePeriod,7} | default {def,7:F0} ms | LA {la,7:F0} ms (on {cl.LastFrameUsedLa}) | " +
                                  $"x{def / Math.Max(1e-9, la):F2} | LA {lw.LaSteps / (double)Math.Max(1, lw.Pixels):F0} + plain {lw.PlainSteps / (double)Math.Max(1, lw.Pixels):F0} | iter diff {100.0 * diff / cd.IterationBuffer.Length:F2} %");
            }
            int wins = rows.Count(x => x.La < x.Def * 0.95), losses = rows.Count(x => x.La > x.Def * 1.05);
            double geo = Math.Exp(rows.Average(x => Math.Log(x.Def / Math.Max(1e-9, x.La))));
            Console.WriteLine($"regions: {rows.Count}; LA faster (>5 %) on {wins}, slower on {losses}; geometric mean speed-up x{geo:F2}");
            foreach (var x in rows.Where(x => x.La > x.Def * 1.05).OrderBy(x => x.Def / x.La))
                Console.WriteLine($"  slower: {x.Name} {x.Zoom:0.00e0} period {x.Period} x{x.Def / x.La:F2} (LA {x.LaSteps} + plain {x.Plain})");
            Console.WriteLine("deepcostprobe done");
            return 0;
        }

        internal static MandelbrotCalculator Make(Scene s, double zoom, int w, int h)
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
