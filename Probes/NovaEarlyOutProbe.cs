// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1979 / #1980 / #1964: --novaearlyoutprobe [W H]. Nova (d = 3, R = 1, z0 = 1; Julia c = -0.45) frame
// times on views with large non-converging areas: attracting-cycle interiors (minibrots) and chaotic
// regions, shallow (the double loop) and deep (the perturbation engine). Warm runs, the median of
// FF_NOVA_REPS (default 5), at each cap in FF_NOVA_CAPS (default 1500,6000), in four configurations:
//   off   - no early-out (as before #1979);
//   cycle - Brent's periodicity check (#1979; on by default);
//   chaos - the opt-in chaos early-out alone (#1980, multiplier past 10^200);
//   both  - both.
// Per row: the time, the non-converged pixels, the iterations the frame ran (the sum of the smooth buffer,
// as the frame left it, for "off"), the pixels each early-out stopped, the iterations saved, and the pixels
// whose colour / basin / smooth count differ from "off". FF_NOVA_OUT=<dir> writes off / both / a difference
// mask (white = differs) as PNG. FF_NOVA_VIEWS=<text> runs the views whose name contains it. FF_NOVA_SCAN=1 writes an overview of the plane instead (choosing views).
// The early-out knobs are set by name, so the same file builds on a tree without them (only "off" runs):
// that is how the "before" column was measured. Exit 0 always (a measurement).
// The early-out ideas are pauldelbrot's (fractalforums.org topic 5693; Docs/Resources-Bibliography.md).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog.Imaging;
using FracturingFog.Models;

namespace FracturingFog
{
    internal static class NovaEarlyOutProbe
    {
        // A view; with Bisect (a non-converged point A and a converged point B at cap 1500) its centre is the
        // basin boundary on the segment A..B, by bisection with the double loop's rule.
        private sealed record View(string Name, double X, double Y, double Zoom, bool Julia = false,
                                   (double X, double Y, double X2, double Y2)? Bisect = null, double JuliaC = -0.45);

        private static readonly View[] Views =
        {
            new("parameter plane", 0, 0, 0.6),                  // mostly converged: the checks' overhead
            new("minibrot", -0.45, 0, 12),                       // a period >= 2 minibrot: attracting-cycle interior
            new("minibrot filaments", -0.60, 0, 30),             // left of it: slow / chaotic pixels
            new("Julia cycle basin", JuliaX, 0, JuliaZoom, Julia: true),  // c = -0.45 lies in the minibrot: an attracting cycle
            new("deep minibrot edge", 0, 0, 1e12, Bisect: (-0.45, 0, -0.45, 0.1)),
            new("deep Julia basin edge", 0, 0, 1e12, Julia: true, Bisect: (JuliaX, 0, JuliaX, 0.7)),
        };

        private const double JuliaX = 0.9, JuliaZoom = 1.5;

        private static readonly string[] Configs = { "off", "cycle", "chaos", "both" };

        // The double loop's verdict for one point (z0 = 1 and c the point, or z0 the point and c = jc).
        private static bool NonConverged(double x, double y, bool julia, double jc, int cap)
        {
            double zr = julia ? x : 1, zi = julia ? y : 0, cr = julia ? jc : x, ci = julia ? 0 : y;
            for (int n = 0; n < cap; n++)
            {
                double z2 = zr * zr - zi * zi, z2i = 2 * zr * zi;          // z^2
                double z3r = z2 * zr - z2i * zi, z3i = z2 * zi + z2i * zr; // z^3
                double dr = 3 * z2, di = 3 * z2i, den = dr * dr + di * di;
                double nr = z3r - 1, ni = z3i;
                double qr = (nr * dr + ni * di) / den, qi = (ni * dr - nr * di) / den;
                double ar = zr - qr + cr, ai = zi - qi + ci;
                double sr = ar - zr, si = ai - zi;
                zr = ar; zi = ai;
                if (sr * sr + si * si < 1e-12) return false;
            }
            return true;
        }

        private static View Resolve(View v)
        {
            if (v.Bisect is not { } s) return v;
            double lo = 0, hi = 1;   // lo non-converged, hi converged
            for (int i = 0; i < 80; i++)
            {
                double t = 0.5 * (lo + hi);
                if (t == lo || t == hi) break;
                if (NonConverged(s.X + t * (s.X2 - s.X), s.Y + t * (s.Y2 - s.Y), v.Julia, v.JuliaC, 1500)) lo = t; else hi = t;
            }
            return v with { X = s.X + lo * (s.X2 - s.X), Y = s.Y + lo * (s.Y2 - s.Y) };
        }

        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 640, h = args.Length > 2 ? int.Parse(args[2]) : 360;
            int reps = int.TryParse(Environment.GetEnvironmentVariable("FF_NOVA_REPS"), out var r) ? r : 5;
            var caps = (Environment.GetEnvironmentVariable("FF_NOVA_CAPS") ?? "1500,6000").Split(',').Select(int.Parse).ToArray();
            string? outDir = Environment.GetEnvironmentVariable("FF_NOVA_OUT");
            if (outDir != null) Directory.CreateDirectory(outDir);
            if (Environment.GetEnvironmentVariable("FF_NOVA_SCAN") == "1") return Scan(w, h, outDir);
            bool hasKnobs = typeof(NewtonCalculator).GetProperty("Periodicity") != null;

            Console.WriteLine($"novaearlyoutprobe {w}x{h}, reps {reps}, {Environment.ProcessorCount} threads, early-out knobs: {(hasKnobs ? "yes" : "no (before)")}");
            Console.WriteLine("view | zoom | cap | config | path | ms (median) | speed-up | non-converged px | cycle stops | chaos stops | iterations saved | differ from off");
            string? only = Environment.GetEnvironmentVariable("FF_NOVA_VIEWS");   // a name filter (substring)
            foreach (var v0 in Views.Where(v => only == null || v.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
            {
                var v = Resolve(v0);
                if (v0.Bisect != null) Console.WriteLine($"# {v.Name}: centre {v.X:R} {(v.Y < 0 ? "-" : "+")} {Math.Abs(v.Y):R}i");
                foreach (int cap in caps)
                {
                    NewtonCalculator? off = null;
                    double offMs = 0, offIters = 0;
                    foreach (var cfg in hasKnobs ? Configs : Configs.Take(1))
                    {
                        var calc = Make(w, h, v, cap, cfg);
                        calc.Calculate(CancellationToken.None);   // warm
                        var times = new List<double>();
                        for (int i = 0; i < reps; i++)
                        {
                            var sw = Stopwatch.StartNew();
                            calc.Calculate(CancellationToken.None);
                            times.Add(sw.Elapsed.TotalMilliseconds);
                        }
                        times.Sort();
                        double ms = times[times.Count / 2];
                        int nc = calc.BasinBuffer.Count(b => b < 0);
                        string stops = "- | - | -", diff = "-", speed = "-";
                        if (off == null) { off = calc; offMs = ms; offIters = calc.SmoothBuffer.Sum(s => (double)s); }
                        else
                        {
                            speed = $"{offMs / ms:F2}x";
                            int dc = 0, db = 0, dz = 0;
                            for (int i = 0; i < w * h; i++)
                            {
                                if (calc.ColorBuffer[i] != off.ColorBuffer[i]) dc++;
                                if (calc.BasinBuffer[i] != off.BasinBuffer[i] || calc.SmoothBuffer[i] != off.SmoothBuffer[i]) db++;
                                if (calc.FinalZrBuffer[i] != off.FinalZrBuffer[i] || calc.FinalZiBuffer[i] != off.FinalZiBuffer[i]) dz++;
                            }
                            diff = $"{dc} colour, {db} basin/smooth, {dz} final z";
                        }
                        if (hasKnobs)
                        {
                            dynamic st = typeof(NewtonCalculator).GetProperty("LastEarlyOuts")!.GetValue(calc)!;
                            long cyc = st.Cycles, chs = st.ChaosStops, sav = st.IterationsSaved;
                            stops = $"{cyc} | {chs} | {sav:G4} ({100 * sav / Math.Max(1, offIters):F1}%)";
                        }
                        Console.WriteLine($"{v.Name} | {v.Zoom:G3} | {cap} | {cfg} | {calc.LastPrecisionLabel} | {ms:F1} | {speed} | {nc} ({100.0 * nc / (w * h):F1}%) | {stops} | {diff}");
                        if (outDir != null && (cfg == "off" || cfg == "both"))
                        {
                            string stem = Path.Combine(outDir, $"{Slug(v.Name)}_{cap}");
                            ParallelPngWriter.Write($"{stem}_{cfg}.png", calc.ColorBuffer, w, h);
                            if (cfg == "both")
                                ParallelPngWriter.Write($"{stem}_diff.png",
                                    calc.ColorBuffer.Select((c, i) => c == off.ColorBuffer[i] ? 0xFF000000u : 0xFFFFFFFFu).ToArray(), w, h);
                        }
                    }
                }
            }
            return 0;
        }

        private static NewtonCalculator Make(int w, int h, View v, int cap, string cfg = "off")
        {
            var p = new FractalParameters { NewtonExponent = 3, NewtonRelaxation = 1.0, NovaJuliaMode = v.Julia, NovaJuliaC = new(v.JuliaC, 0) };
            var calc = new NewtonCalculator(w, h)
            {
                FractalType = FractalType.Nova, MaxIterations = cap, ColorMap = new NewtonBasinShadedMap(), Quality = QualityPreset.Ultra,
                FractalParameters = p, CenterX = v.X, CenterY = v.Y, Zoom = v.Zoom,
            };
            // By name: absent on a tree without the early-outs.
            typeof(NewtonCalculator).GetProperty("Periodicity")?.SetValue(calc, cfg is "cycle" or "both");
            typeof(FractalParameters).GetProperty("ChaosEarlyOut")?.SetValue(p, cfg is "chaos" or "both");
            return calc;
        }

        private static string Slug(string s) => new(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

        // An overview: the frame, and a coarse map of the non-converged share (for choosing views).
        private static int Scan(int w, int h, string? outDir)
        {
            double x = double.Parse(Environment.GetEnvironmentVariable("FF_NOVA_X") ?? "-0.35");
            double y = double.Parse(Environment.GetEnvironmentVariable("FF_NOVA_Y") ?? "0");
            double z = double.Parse(Environment.GetEnvironmentVariable("FF_NOVA_ZOOM") ?? "1");
            bool julia = Environment.GetEnvironmentVariable("FF_NOVA_JULIA") == "1";
            int cap = int.Parse(Environment.GetEnvironmentVariable("FF_NOVA_CAP") ?? "1500");
            double jc = double.Parse(Environment.GetEnvironmentVariable("FF_NOVA_JC") ?? "-0.45");
            var calc = Make(w, h, new View("scan", x, y, z, julia, JuliaC: jc), cap);
            calc.Calculate(CancellationToken.None);
            double scale = (3.5 / Math.Max(w, h)) / z;
            Console.WriteLine($"scan ({x}, {y}) zoom {z}: x {x - w / 2 * scale:G6}..{x + w / 2 * scale:G6}, y {y - h / 2 * scale:G6}..{y + h / 2 * scale:G6}");
            const int cw = 64, ch = 24;
            for (int j = 0; j < ch; j++)
            {
                var line = new char[cw];
                for (int i = 0; i < cw; i++)
                {
                    int n = 0, nc = 0;
                    for (int py = j * h / ch; py < (j + 1) * h / ch; py++)
                        for (int px = i * w / cw; px < (i + 1) * w / cw; px++) { n++; if (calc.BasinBuffer[py * w + px] < 0) nc++; }
                    line[i] = nc == 0 ? '.' : nc * 4 < n ? ':' : nc * 4 < 3 * n ? '+' : '#';
                }
                Console.WriteLine(new string(line));
            }
            if (outDir != null) ParallelPngWriter.Write(Path.Combine(outDir, "scan.png"), calc.ColorBuffer, w, h);
            return 0;
        }
    }
}
