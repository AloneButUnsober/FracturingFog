// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1343: --solidregionprobe [W H]. Mariani–Silver solid-region skipping on the CPU
// Mandelbrot paths: for a few views (shallow SIMD and deep perturbation), the frame
// time with the skip off, on with Auto (#1394; with its measured costly share and
// whether it declined) and forced on (Auto off), the share of pixels filled without
// iterating, the masked passes, and the pixels that differ from the full render (a
// filled pixel that a full render finds escaping — the known sub-pixel-channel
// artifact).
// #1394 part 2 — the same for Julia and Multibrot (EscapeTimeCalculator). FF_SOLID_CASES
// = mandelbrot / escape runs one half only.
// #1394 part 1 — FF_SOLID_GPU=1: on the D3D11 GPU (default hardware adapter; the
// shallow kernel, and the deep perturbation kernels with GPU perturbation on), best
// of 3, with the GPU views below instead (SKIP without a device).
// Exit 0 always (a measurement).

using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using FracturingFog.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class SolidRegionProbe
    {
        private sealed record View(string Name, double X, double Y, double Zoom, int Iter, double XLo = 0, double YLo = 0);

        // #1394 part 2 — a Julia (C = the constant) or Multibrot (D = the power) view.
        private sealed record EscapeView(string Name, FractalType Type, Complex C, int D, double X, double Y, double Zoom, int Iter);

        // What one configuration of one view produced.
        private sealed record Frame(uint[] Color, int[] Iter, long Filled, int Passes, double CostlyShare, bool Declined);

        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 960, h = args.Length > 2 ? int.Parse(args[2]) : 540;
            if (Environment.GetEnvironmentVariable("FF_SOLID_GPU") == "1") return RunGpu(w, h);
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
            string cases = Environment.GetEnvironmentVariable("FF_SOLID_CASES") ?? "";
            if (cases is "" or "mandelbrot")
                foreach (var v in views)
                {
                    // Off, Auto (#1394, the default with the skip on) and forced (Auto off).
                    MandelbrotCalculator Make(bool skip, bool auto) => new(w, h)
                    {
                        CenterX = v.X, CenterY = v.Y, Zoom = v.Zoom, MaxIterations = v.Iter,
                        ColorMap = ColorPalette.BuiltIns[0], UseGpuCompute = false, SolidRegionSkip = skip, SolidRegionAuto = auto,
                    };
                    var calcs = new[] { Make(false, true), Make(true, true), Make(true, false) };
                    Measure(v.Name, v.Iter, w, h, calcs.Length, k => calcs[k].Calculate(CancellationToken.None),
                        k => new Frame(calcs[k].ColorBuffer, calcs[k].IterationBuffer, calcs[k].LastFrameFilledPixels,
                            calcs[k].LastFrameSolidPasses, calcs[k].LastFrameSolidCostlyShare, calcs[k].LastFrameSolidDeclined),
                        calcs[0].LastPrecisionLabel);
                }

            // #1394 part 2 — Julia / Multibrot: connected Julia sets with large interiors
            // (rabbit, basilica, San Marco, a Siegel disk), ones with no interior (a
            // dendrite, a Cantor dust), and Multibrot powers 3 / 4 (SIMD) and 8 (scalar).
            double t2 = 2 * Math.PI * 0.6180339887498949;
            var siegelC = new Complex(Math.Cos(t2) / 2 - Math.Cos(2 * t2) / 4, Math.Sin(t2) / 2 - Math.Sin(2 * t2) / 4);
            var escapeViews = new[]
            {
                new EscapeView("Julia rabbit", FractalType.Julia, new(-0.123, 0.745), 2, 0, 0, 1, 1000),
                new EscapeView("Julia rabbit, 5000 it", FractalType.Julia, new(-0.123, 0.745), 2, 0, 0, 1, 5000),
                new EscapeView("Julia rabbit at 20", FractalType.Julia, new(-0.123, 0.745), 2, -0.0884, 0.4, 20, 2000),
                new EscapeView("Julia basilica", FractalType.Julia, new(-1, 0), 2, 0, 0, 1, 1000),
                new EscapeView("Julia San Marco", FractalType.Julia, new(-0.75, 0), 2, 0, 0, 1, 2000),
                new EscapeView("Julia Siegel disk", FractalType.Julia, siegelC, 2, 0, 0, 1, 4000),
                new EscapeView("Julia default C", FractalType.Julia, new(-0.7, 0.27015), 2, 0, 0, 1, 1000),
                new EscapeView("Julia dendrite (c = i)", FractalType.Julia, new(0, 1), 2, 0, 0, 1, 1000),
                new EscapeView("Julia dust (c = 0.5)", FractalType.Julia, new(0.5, 0), 2, 0, 0, 1, 1000),
                new EscapeView("Multibrot d=3", FractalType.Multibrot, default, 3, 0, 0, 1, 1000),
                new EscapeView("Multibrot d=4", FractalType.Multibrot, default, 4, 0, 0, 1, 2000),
                new EscapeView("Multibrot d=3 bulb at 30", FractalType.Multibrot, default, 3, 0, 0.9, 30, 2000),
                new EscapeView("Multibrot d=8 (scalar)", FractalType.Multibrot, default, 8, 0, 0, 1, 1000),
            };
            if (cases is "" or "escape")
                foreach (var v in escapeViews)
                {
                    EscapeTimeCalculator Make(bool skip, bool auto) => new(w, h)
                    {
                        CenterX = v.X, CenterY = v.Y, Zoom = v.Zoom, MaxIterations = v.Iter,
                        ColorMap = ColorPalette.BuiltIns[0], UseGpuCompute = false, FractalType = v.Type,
                        FractalParameters = new FractalParameters
                        {
                            JuliaC = v.C, MultibrotExponent = v.D, SolidRegionSkip = skip, SolidRegionAuto = auto,
                        },
                    };
                    var calcs = new[] { Make(false, true), Make(true, true), Make(true, false) };
                    Measure(v.Name, v.Iter, w, h, calcs.Length, k => calcs[k].Calculate(CancellationToken.None),
                        k => new Frame(calcs[k].ColorBuffer, calcs[k].IterationBuffer, calcs[k].LastFrameFilledPixels,
                            calcs[k].LastFrameSolidPasses, calcs[k].LastFrameSolidCostlyShare, calcs[k].LastFrameSolidDeclined),
                        "SP");
                }
            Console.WriteLine("solidregionprobe done");
            return 0;
        }

        // #1394 part 1 — the GPU views: shallow (SP kernel, periodicity on as shipped) and
        // deep (perturbation: double where the device has fp64, else fp32 / float-exp).
        private static int RunGpu(int w, int h)
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"solidregionprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
                return 0;
            }
            bool perturb = MandelbrotCalculator.UseGpuPerturbation;
            try
            {
                using var k = new FracturingFog.Rendering.MandelbrotGpuKernel(device, context, new object());
                MandelbrotCalculator.UseGpuPerturbation = true;
                Console.WriteLine($"solidregionprobe {w}x{h}, D3D11 ({k.DeviceName}), tile {MandelbrotCalculator.SolidTileSize} -> {MandelbrotCalculator.SolidMinTile}");
                double th = 2 * Math.PI * 0.6180339887498949;
                double sx = Math.Cos(th) / 2 - Math.Cos(2 * th) / 4, sy = Math.Sin(th) / 2 - Math.Sin(2 * th) / 4;
                var views = new[]
                {
                    new View("whole set", -0.75, 0, 1, 1000),
                    new View("period-3 minibrot", -1.7548776662466927, 0, 80, 2000),
                    new View("minibrot interior at 3e3", -1.7548776662466927, 0, 3e3, 8000),
                    new View("cusp -1.75 at 1e3", -1.75, 0, 1e3, 20000),
                    new View("deep minibrot 1e14", -1.7548776662466927, 0, 1e14, 4000),
                    new View("deep Siegel 1e14", sx, sy, 1e14, 4000),
                    new View("deep cusp -1.75 at 1e14", -1.75, 0, 1e14, 4000),
                    // Shipped deep presets (Resources/regions.json, as --gpuwavefrontprobe deep).
                    new View("Rosette 4.5e15", -1.7687788319518845, 0.001738993325347507, 4543920741896643, 20000),
                    new View("Spider Star 1.2e21", -1.9998000000003664, -2.56187704972203e-24, 1.2103202390345426e+21, 20000, -4.920546579744369e-17),
                    new View("Deep Mini 2e24", -1.9998000000003664, 4.404058120451339e-27, 1.9569549242016094e+24, 50000, -4.9205478607770514e-17),
                    new View("Deep Mini at 4e24", -1.9998000000003664, 4.404058120451339e-27, 3.9e24, 20000, -4.9205478607770514e-17),
                    new View("Deep Mini at 6e24", -1.9998000000003664, 4.404058120451339e-27, 5.9e24, 20000, -4.9205478607770514e-17),
                    new View("Deep Mini at 9e24", -1.9998000000003664, 4.404058120451339e-27, 8.8e24, 20000, -4.9205478607770514e-17),
                    new View("Deep Mini at 1e25", -1.9998000000003664, 4.404058120451339e-27, 1.9569549242016094e+25, 50000, -4.9205478607770514e-17),
                };
                string cases = Environment.GetEnvironmentVariable("FF_SOLID_CASES") ?? "";
                string only = Environment.GetEnvironmentVariable("FF_SOLID_VIEW") ?? "";   // a name substring
                if (cases is "" or "mandelbrot")
                    foreach (var v in views)
                    {
                        if (!v.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                        MandelbrotCalculator Make(bool skip, bool auto) => new(w, h)
                        {
                            CenterX = v.X, CenterXLo = v.XLo, CenterY = v.Y, CenterYLo = v.YLo, Zoom = v.Zoom, MaxIterations = v.Iter,
                            ColorMap = ColorPalette.BuiltIns[0], UseGpuCompute = true, GpuKernel = k,
                            SolidRegionSkip = skip, SolidRegionAuto = auto,
                        };
                        var calcs = new[] { Make(false, true), Make(true, true), Make(true, false) };
                        calcs[0].Calculate(CancellationToken.None);   // the route, for the label
                        Measure(v.Name, v.Iter, w, h, calcs.Length, kk => calcs[kk].Calculate(CancellationToken.None),
                            kk => new Frame(calcs[kk].ColorBuffer, calcs[kk].IterationBuffer, calcs[kk].LastFrameFilledPixels,
                                calcs[kk].LastFrameSolidPasses, calcs[kk].LastFrameSolidCostlyShare, calcs[kk].LastFrameSolidDeclined),
                            calcs[0].LastPrecisionLabel + (calcs[0].LastFrameUsedGpuCompute || calcs[0].LastFrameUsedGpuPerturbation ? " GPU" : " CPU"),
                            runs: 3);
                    }
                var escapeViews = new[]
                {
                    new EscapeView("Julia rabbit", FractalType.Julia, new(-0.123, 0.745), 2, 0, 0, 1, 1000),
                    new EscapeView("Julia rabbit at 20", FractalType.Julia, new(-0.123, 0.745), 2, -0.0884, 0.4, 20, 2000),
                    new EscapeView("Julia San Marco", FractalType.Julia, new(-0.75, 0), 2, 0, 0, 1, 2000),
                    new EscapeView("Multibrot d=4", FractalType.Multibrot, default, 4, 0, 0, 1, 2000),
                    new EscapeView("Multibrot d=3 bulb at 30", FractalType.Multibrot, default, 3, 0, 0.9, 30, 2000),
                };
                if (cases is "" or "escape")
                    foreach (var v in escapeViews)
                    {
                        if (!v.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                        EscapeTimeCalculator Make(bool skip, bool auto) => new(w, h)
                        {
                            CenterX = v.X, CenterY = v.Y, Zoom = v.Zoom, MaxIterations = v.Iter,
                            ColorMap = ColorPalette.BuiltIns[0], UseGpuCompute = true, GpuKernel = k, FractalType = v.Type,
                            FractalParameters = new FractalParameters
                            {
                                JuliaC = v.C, MultibrotExponent = v.D, SolidRegionSkip = skip, SolidRegionAuto = auto,
                            },
                        };
                        var calcs = new[] { Make(false, true), Make(true, true), Make(true, false) };
                        calcs[0].Calculate(CancellationToken.None);   // the route, for the label
                        Measure(v.Name, v.Iter, w, h, calcs.Length, kk => calcs[kk].Calculate(CancellationToken.None),
                            kk => new Frame(calcs[kk].ColorBuffer, calcs[kk].IterationBuffer, calcs[kk].LastFrameFilledPixels,
                                calcs[kk].LastFrameSolidPasses, calcs[kk].LastFrameSolidCostlyShare, calcs[kk].LastFrameSolidDeclined),
                            calcs[0].LastGpuRoute.State == FracturingFog.Render.GpuRouteState.Gpu ? "SP GPU" : "SP CPU", runs: 3);
                    }
                Console.WriteLine("solidregionprobe done");
                return 0;
            }
            finally
            {
                MandelbrotCalculator.UseGpuPerturbation = perturb;
                context.Dispose(); device.Dispose();
            }
        }

        // Off (k = 0), Auto (1) and forced (2): best of 7 alternating runs (#1394 — a
        // 3-run mean of 20-40 ms frames swung ±15 % between probe runs), then the fills
        // and the pixels that differ from the full render.
        private static void Measure(string name, int maxIt, int w, int h, int count, Action<int> calc, Func<int, Frame> frame, string label,
            int runs = 7)
        {
            for (int k = 0; k < count; k++) calc(k);   // warm-up (JIT, orbit)
            var t = new double[count];
            Array.Fill(t, double.MaxValue);
            for (int r = 0; r < runs; r++)
                for (int k = 0; k < count; k++)
                {
                    var sw = Stopwatch.StartNew(); calc(k); t[k] = Math.Min(t[k], sw.Elapsed.TotalMilliseconds);
                }
            var off = frame(0);
            long inSet = 0;
            for (int i = 0; i < w * h; i++) if (off.Iter[i] >= maxIt) inSet++;
            Console.WriteLine($"  {name,-24} [{label}] in-set {inSet / (double)(w * h),6:P1}, rows costly {frame(1).CostlyShare,6:P1}: off {t[0],8:F1} ms");
            for (int k = 1; k < count; k++)
            {
                var on = frame(k);
                long diff = 0, wrongInSet = 0;
                for (int i = 0; i < w * h; i++)
                {
                    if (off.Color[i] != on.Color[i])
                    {
                        diff++;
                        if (on.Iter[i] >= maxIt && off.Iter[i] < maxIt) wrongInSet++;
                    }
                }
                Console.WriteLine($"      {(k == 1 ? "auto  " : "forced")} {t[k],8:F1} ms ({t[0] / t[k],5:F2}x){(on.Declined ? " declined" : "")}; " +
                                  $"filled {on.Filled / (double)(w * h),6:P1}, {on.Passes} passes; differ {diff} px ({wrongInSet} filled but escaping)");
            }
        }
    }
}
