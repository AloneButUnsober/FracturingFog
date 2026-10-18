// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1343: --solidregionprobe [W H]. Mariani–Silver solid-region skipping on the CPU
// Mandelbrot paths: for a few views (shallow SIMD and deep perturbation), the frame
// time with the skip off and on, the share of pixels filled without iterating, the
// masked passes, and the pixels that differ from the full render (a filled pixel
// that a full render finds escaping — the known sub-pixel-channel artifact).
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
            };
            foreach (var v in views)
            {
                MandelbrotCalculator Make(bool skip) => new(w, h)
                {
                    CenterX = v.X, CenterY = v.Y, Zoom = v.Zoom, MaxIterations = v.Iter,
                    ColorMap = ColorPalette.BuiltIns[0], UseGpuCompute = false, SolidRegionSkip = skip,
                };
                var off = Make(false);
                var on = Make(true);
                off.Calculate(CancellationToken.None); on.Calculate(CancellationToken.None);   // warm-up (JIT, orbit)
                double tOff = 0, tOn = 0;
                for (int r = 0; r < 3; r++)
                {
                    var sw = Stopwatch.StartNew(); off.Calculate(CancellationToken.None); tOff += sw.Elapsed.TotalMilliseconds;
                    sw.Restart(); on.Calculate(CancellationToken.None); tOn += sw.Elapsed.TotalMilliseconds;
                }
                long diff = 0, wrongInSet = 0, inSet = 0;
                int maxIt = v.Iter;
                for (int i = 0; i < w * h; i++)
                {
                    if (off.IterationBuffer[i] >= maxIt) inSet++;
                    if (off.ColorBuffer[i] != on.ColorBuffer[i])
                    {
                        diff++;
                        if (on.IterationBuffer[i] >= maxIt && off.IterationBuffer[i] < maxIt) wrongInSet++;
                    }
                }
                Console.WriteLine($"  {v.Name,-24} [{off.LastPrecisionLabel}] in-set {inSet / (double)(w * h),6:P1}: off {tOff / 3,8:F1} ms, on {tOn / 3,8:F1} ms ({tOff / tOn,5:F2}x); " +
                                  $"filled {on.LastFrameFilledPixels / (double)(w * h),6:P1}, {on.LastFrameSolidPasses} passes; differ {diff} px ({wrongInSet} filled but escaping)");
            }
            Console.WriteLine("solidregionprobe done");
            return 0;
        }
    }
}
