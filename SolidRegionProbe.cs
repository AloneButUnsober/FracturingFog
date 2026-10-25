// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1343: --solidregionprobe [W H]. Mariani–Silver solid-region skipping on the CPU
// Mandelbrot paths: for a few views (shallow SIMD and deep perturbation), the frame
// time with the skip off, on with Auto (#1394; with its measured costly share and
// whether it declined) and forced on (Auto off), the share of pixels filled without
// iterating, the masked passes, and the pixels that differ from the full render (a
// filled pixel that a full render finds escaping — the known sub-pixel-channel
// artifact).
// Exit 0 always (a measurement).

using System;
using System.Diagnostics;
using System.Threading;
using FracturingFog.Models;

namespace FracturingFog
{
    internal static class SolidRegionProbe
    {
        private sealed record View(string Name, double X, double Y, double Zoom, int Iter);

        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 960, h = args.Length > 2 ? int.Parse(args[2]) : 540;
            Console.WriteLine($"solidregionprobe {w}x{h}, CPU, tile {MandelbrotCalculator.SolidTileSize} -> {MandelbrotCalculator.SolidMinTile}");
            // Golden-mean Siegel point on the main cardioid: c = e^{iθ}/2 − e^{2iθ}/4.
            double th = 2 * Math.PI * 0.6180339887498949;
            double sx = Math.Cos(th) / 2 - Math.Cos(2 * th) / 4, sy = Math.Sin(th) / 2 - Math.Sin(2 * th) / 4;
            var views = new[]
            {
                new View("whole set", -0.75, 0, 1, 1000),
                new View("whole set, 5000 it", -0.75, 0, 1, 5000),
                new View("seahorse valley", -0.7436, 0.1318, 60, 2000),
                new View("period-3 minibrot", -1.7548776662466927, 0, 80, 2000),
                new View("cardioid edge (Siegel)", sx, sy, 2000, 4000),
                new View("deep Siegel 1e14", sx, sy, 1e14, 8000),
                new View("deep minibrot 1e14", -1.7548776662466927, 0, 1e14, 8000),
                new View("deep cusp -1.75 at 1e14", -1.75, 0, 1e14, 20000),
                new View("cusp -1.75 at 1e5 (SP)", -1.75, 0, 1e5, 20000),
                // #1394 — Auto calibration: a satellite bulb, a small minibrot in a wide
                // frame, elephant valley, a deep minibrot framed with its surroundings.
                new View("satellite bulb region", -0.1592, 1.0317, 40, 2000),
                new View("small minibrot, 5000 it", -1.7548776662466927, 0, 8, 5000),
                new View("elephant valley", 0.2925, 0.0149, 100, 2000),
                new View("minibrot interior at 3e3", -1.7548776662466927, 0, 3e3, 8000),
                new View("period-3 minibrot at 30", -1.7548776662466927, 0, 30, 2000),
                new View("period-3 minibrot at 45", -1.7548776662466927, 0, 45, 2000),
                new View("period-3 minibrot at 60", -1.7548776662466927, 0, 60, 2000),
            };
            foreach (var v in views)
            {
                // Off, Auto (#1394, the default with the skip on) and forced (Auto off).
                MandelbrotCalculator Make(bool skip, bool auto) => new(w, h)
                {
                    CenterX = v.X, CenterY = v.Y, Zoom = v.Zoom, MaxIterations = v.Iter,
                    ColorMap = ColorPalette.BuiltIns[0], UseGpuCompute = false, SolidRegionSkip = skip, SolidRegionAuto = auto,
                };
                var calcs = new[] { Make(false, true), Make(true, true), Make(true, false) };
                foreach (var c in calcs) c.Calculate(CancellationToken.None);   // warm-up (JIT, orbit)
                // #1394 — best of 7 alternating runs (a 3-run mean of 20-40 ms frames
                // swung ±15 % between probe runs).
                var t = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                for (int r = 0; r < 7; r++)
                    for (int k = 0; k < calcs.Length; k++)
                    {
                        var sw = Stopwatch.StartNew(); calcs[k].Calculate(CancellationToken.None); t[k] = Math.Min(t[k], sw.Elapsed.TotalMilliseconds);
                    }
                var off = calcs[0];
                int maxIt = v.Iter;
                long inSet = 0;
                for (int i = 0; i < w * h; i++) if (off.IterationBuffer[i] >= maxIt) inSet++;
                Console.WriteLine($"  {v.Name,-24} [{off.LastPrecisionLabel}] in-set {inSet / (double)(w * h),6:P1}, rows costly {calcs[1].LastFrameSolidCostlyShare,6:P1}: off {t[0],8:F1} ms");
                for (int k = 1; k < calcs.Length; k++)
                {
                    var on = calcs[k];
                    long diff = 0, wrongInSet = 0;
                    for (int i = 0; i < w * h; i++)
                    {
                        if (off.ColorBuffer[i] != on.ColorBuffer[i])
                        {
                            diff++;
                            if (on.IterationBuffer[i] >= maxIt && off.IterationBuffer[i] < maxIt) wrongInSet++;
                        }
                    }
                    Console.WriteLine($"      {(k == 1 ? "auto  " : "forced")} {t[k],8:F1} ms ({t[0] / t[k],5:F2}x){(on.LastFrameSolidDeclined ? " declined" : "")}; " +
                                      $"filled {on.LastFrameFilledPixels / (double)(w * h),6:P1}, {on.LastFrameSolidPasses} passes; differ {diff} px ({wrongInSet} filled but escaping)");
                }
            }
            Console.WriteLine("solidregionprobe done");
            return 0;
        }
    }
}
