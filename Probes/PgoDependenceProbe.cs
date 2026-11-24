// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2038 — --pgodependenceprobe [W H]: whether the CPU deep-zoom path needs the .NET runtime's
// dynamic profile-guided optimisation (PGO) to be fast. A batch render or a benchmark is a
// fresh process: hot code starts unoptimised or as ReadyToRun code and reaches its PGO-built
// Tier1 version only part-way through the frame, so code that is slow without PGO costs every
// such run (0.7.2 at 1e33: rith's report, FF's LA evaluator kept its pixel state in memory
// unless PGO inlined LaRunStages).
//
// The probe starts fresh child processes of this executable under three runtime settings
// (the runtime switches of Microsoft's "Compilation config settings" page, set through the
// environment; Docs/Resources-Bibliography.md #dotnet-runtime-compilation-config):
//   default          — tiered compilation with dynamic PGO, as shipped;
//   PGO off          — DOTNET_TieredPGO=0;
//   fully optimised  — DOTNET_TieredCompilation=0 + DOTNET_ReadyToRun=0 (every method fully
//                      optimised at its first call, no PGO).
// Each child renders the same deep frame twice, timing the first frame (cold) and the second
// (warm, in the same process). The scenes are DeepCostProbe's "path 1e33" (the fractadyne
// bench-kit spiral path, credited there) on the default deep route (calibration off). Rounds
// alternate the settings (FF_PGODEP_REPS, default 3); medians are printed with the PGO-off /
// default ratio, flagged above 1.10 (before #2038 on an i5-13420H: x1.17 at 1080p, ~x1.2 at
// 4K; rith's Ryzen 9 5950X at 4K: x1.37; after #2038: x1.00 here).
// FF_PGODEP_SCENE picks another DeepCostProbe scene by name. Exit 0 always.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace FracturingFog
{
    internal static class PgoDependenceProbe
    {
        private const string Flag = "--pgodependenceprobe";
        private const string ChildArg = "child";

        public static int Run(string[] args)
        {
            if (args.Length > 1 && args[1] == ChildArg) return Child(args);
            int w = args.Length > 2 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1920;
            int h = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 1080;
            int reps = int.TryParse(Environment.GetEnvironmentVariable("FF_PGODEP_REPS"), out int r) && r > 0 ? r : 3;
            string scene = Environment.GetEnvironmentVariable("FF_PGODEP_SCENE") ?? "path 1e33";
            var settings = new (string Name, (string, string)[] Env)[]
            {
                ("default", Array.Empty<(string, string)>()),
                ("PGO off", new[] { ("DOTNET_TieredPGO", "0") }),
                ("fully optimised", new[] { ("DOTNET_TieredCompilation", "0"), ("DOTNET_ReadyToRun", "0") }),
            };
            Console.WriteLine($"pgodependenceprobe {w}x{h}, scene \"{scene}\", CPU, default deep route, {reps} round(s), a fresh process per run");
            var cold = settings.ToDictionary(s => s.Name, _ => new List<double>());
            var warm = settings.ToDictionary(s => s.Name, _ => new List<double>());
            for (int k = 0; k < reps; k++)
                foreach (var (name, env) in settings)
                {
                    var (c, wm, err) = Spawn(w, h, scene, env);
                    if (err != null) { Console.WriteLine($"  {name}: {err}"); return 0; }
                    cold[name].Add(c); warm[name].Add(wm);
                }
            Console.WriteLine("setting | cold frame ms (median) | warm frame ms (median) | cold / warm");
            foreach (var (name, _) in settings)
            {
                double c = Median(cold[name]), wm = Median(warm[name]);
                Console.WriteLine($"| {name} | {c:F0} | {wm:F0} | {c / wm:F2} |");
            }
            double ratio = Median(cold["PGO off"]) / Median(cold["default"]);
            Console.WriteLine($"PGO off / default, cold: x{ratio:F2}" + (ratio > 1.10 ? "  <-- the deep path depends on dynamic PGO (#2038)" : ""));
            Console.WriteLine("pgodependenceprobe done");
            return 0;
        }

        private static (double Cold, double Warm, string? Error) Spawn(int w, int h, string scene, (string Key, string Value)[] env)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, new[] { Flag, ChildArg,
                w.ToString(CultureInfo.InvariantCulture), h.ToString(CultureInfo.InvariantCulture), scene })
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.Environment["FF_DEEP_ROUTE_CALIBRATION"] = "0";   // the fixed default route on every machine
            foreach (var (key, value) in env) psi.Environment[key] = value;
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            foreach (var line in stdout.Split('\n'))
                if (line.StartsWith("pgodep-child ", StringComparison.Ordinal))
                {
                    var parts = line.Trim().Split(' ');
                    return (double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture), null);
                }
            return (0, 0, $"child gave no result (exit {p.ExitCode}): {(stdout + stderr).Trim()}");
        }

        private static int Child(string[] args)
        {
            int w = int.Parse(args[2], CultureInfo.InvariantCulture), h = int.Parse(args[3], CultureInfo.InvariantCulture);
            string name = args[4];
            var s = DeepCostProbe.Scenes.Concat(DeepCostProbe.FfViews).FirstOrDefault(x => x.Name == name);
            if (s == null) { Console.WriteLine($"no DeepCostProbe scene \"{name}\""); return 0; }
            double zoom = s.FfZoom ? s.KfZoom : s.KfZoom * 3.5 * h / (4.0 * Math.Max(w, h));   // as DeepCostProbe
            double Frame()
            {
                var c = DeepCostProbe.Make(s, zoom, w, h);
                var sw = Stopwatch.StartNew();
                c.Calculate();
                return sw.Elapsed.TotalMilliseconds;
            }
            double cold = Frame(), warm = Frame();
            Console.WriteLine(FormattableString.Invariant($"pgodep-child {cold:F1} {warm:F1}"));
            return 0;
        }

        private static double Median(List<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            return s.Count % 2 == 1 ? s[s.Count / 2] : 0.5 * (s[s.Count / 2 - 1] + s[s.Count / 2]);
        }
    }
}
