// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1340: --gpuwavefrontprobe. The D3D11 half of the wavefront (persistent-thread)
// schedule check (Server.Tests has no D3D reference; S1340GpuWavefront*Tests cover
// Vulkan). On the default hardware adapter, a real MandelbrotGpuKernel:
//   --gpuwavefrontprobe [W H]       shallow kernel, 1920x1080 by default: each view
//       (Mandelbrot, Julia, Burning Ship, Phoenix; periodicity off and on) in both
//       schedules — iter, smooth, colour and final z / dz/dc bit-identical (the base
//       kernel with the periodicity check off, the colour kernel with it on) — and
//       the dispatch time of each;
//   --gpuwavefrontprobe deep [W H]  deep perturbation, 960x540 by default: shipped
//       deep presets through the calculator (SA + BLA), one thread per pixel vs the
//       wavefront schedule forced on — iter and smooth bit-identical — with the
//       frame's warp waste and what Auto would pick for the next frame; #1386 — then
//       a first frame with the opt-in seed dispatch (GpuWavefront.Seed): its ¼
//       estimate of the warp waste, the schedule it picked, and its time against the
//       unseeded first frame (one thread per pixel), output bit-identical;
//   --gpuwavefrontprobe orbit [W H]  #1384 — the orbit colour kernel, 960x540 by
//       default (at 1080p its one-thread-per-pixel frame of the period-3 minibrot
//       runs past the GT 710's 2 s watchdog: the shallow kernels dispatch one
//       frame, with no bands): the shallow views with a theme reading every orbit input, iter,
//       smooth, colour, trap and final z / dz/dc bit-identical, and both times;
//   --gpuwavefrontprobe deepkernels [W H]  #1384 — the deep orbit kernel (an orbit
//       theme) and the fp32 kernel (FF_GPU_PERTURB_PRECISION fp32, SA / BLA off)
//       through the calculator on the shipped deep presets, 480x270 by default: iter,
//       smooth and colour bit-identical, both times, and the frame's warp waste.
// Exit 0 = pass or SKIP (no D3D11 hardware device); 1 = a frame differs.

using System;
using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class GpuWavefrontProbe
    {
        private sealed record View(string Name, FractalKind Kind, double Cx, double Cy, double Width,
                                   int MaxIter, float P0 = 0f, float P1 = 0f);

        private static readonly View[] Views =
        {
            new("Whole set 512", FractalKind.Mandelbrot, -0.5, 0.0, 3.5, 512),
            new("Seahorse 1e4 2048", FractalKind.Mandelbrot, -0.743643887037151, 0.131825904205330, 3.5e-4, 2048),
            new("Seahorse 1e3 5000", FractalKind.Mandelbrot, -0.7453, 0.1127, 3.5e-3, 5000),
            new("Elephant 1e2 20000", FractalKind.Mandelbrot, 0.2925, 0.0149, 0.035, 20000),
            new("Minibrot p3 4000", FractalKind.Mandelbrot, -1.7548776662466927, 0.0, 0.04, 4000),
            new("Spiral 1e5 50000", FractalKind.Mandelbrot, -0.761574, -0.0847596, 3.5e-5, 50000),
            new("Rabbit Julia 2000", FractalKind.Julia, 0.0, 0.0, 3.2, 2000, -0.12256117f, 0.74486177f),
            new("Burning Ship 2000", FractalKind.BurningShip, -1.762, -0.028, 0.05, 2000),
            new("Phoenix 2000", FractalKind.Phoenix, 0.0, 0.0, 3.0, 2000, -0.5f),
        };

        // Shipped deep presets (Resources/regions.json), Mandelbrot, at 20k iterations.
        private static readonly (string Name, double Cx, double CxLo, double Cy, double CyLo, double Zoom, int MaxIter)[] DeepViews =
        {
            ("SA probe 1e15", -1.1726999042772253, 8.9529605787776783E-17, -0.2968356710071185, -2.3536240906562374E-18, 1e15, 4096),
            ("Rosette 4.5e15", -1.7687788319518845, 0, 0.001738993325347507, 0, 4543920741896643, 20000),
            ("Marina 3.7e16", -0.8166959533897611, 0, 0.17829091833221097, 0, 37039746442841656, 20000),
            ("Spider Star 1.2e21", -1.9998000000003664, -4.920546579744369e-17, -2.56187704972203e-24, 0, 1.2103202390345426e+21, 20000),
            ("Racing Julias 1.3e21", -1.7687788319518845, 5.6796099650454546e-18, 0.0017389933253475003, 9.353974864002243e-20, 1.2715782623199528e+21, 20000),
            ("Kidney Star 5.4e22", -1.4181949445046638, -3.99373066043531e-17, -0.12700786442374343, -1.3063484171237339e-18, 5.436540412585831e+22, 20000),
            ("Deep Mini 2e24", -1.9998000000003664, -4.9205478607770514e-17, 4.404058120451339e-27, 0, 1.9569549242016094e+24, 50000),
            ("Deep Lightning 7.6e26", -1.4181949444785762, -7.4415882477902279E-17, -0.12700786443815276, -2.3429499355532375E-18, 7.58348E+26, 20000),
        };

        private sealed class Out
        {
            public readonly int[] It; public readonly float[] Sm, Zr, Zi, Dr, Di; public readonly uint[] Col;
            public double Ms;
            public Out(int n) { It = new int[n]; Sm = new float[n]; Zr = new float[n]; Zi = new float[n]; Dr = new float[n]; Di = new float[n]; Col = new uint[n]; }
        }

        private static bool SameBits(float[] a, float[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
            return true;
        }

        public static int Run(string[] args)
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"gpuwavefrontprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
                return 0;
            }
            try
            {
                string mode = args.Length > 1 && args[1] is "deep" or "orbit" or "deepkernels" ? args[1] : "";
                int a = mode.Length > 0 ? 2 : 1;
                int w = args.Length > a + 1 ? int.Parse(args[a]) : mode switch { "deep" or "orbit" => 960, "deepkernels" => 480, _ => 1920 };
                int h = args.Length > a + 1 ? int.Parse(args[a + 1]) : mode switch { "deep" or "orbit" => 540, "deepkernels" => 270, _ => 1080 };
                using var k = new MandelbrotGpuKernel(device, context, new object());
                return mode switch
                {
                    "deep" => Deep(k, w, h),
                    "orbit" => ShallowOrbit(k, w, h),
                    "deepkernels" => DeepKernels(k, w, h),
                    _ => Shallow(k, w, h),
                };
            }
            finally { context.Dispose(); device.Dispose(); }
        }

        private static int Shallow(MandelbrotGpuKernel k, int w, int h)
        {
            int n = w * h, failures = 0;
            var colour = ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First();
            Console.WriteLine($"gpuwavefrontprobe {w}x{h}, {GpuWavefront.Threads} threads");
            Out Frame(View v, int schedule, int periodicity, bool col)
            {
                var o = new Out(n);
                k.SetPalette(col ? colour : null);
                GpuWavefront.Mode = schedule; GpuPeriodicity.Mode = periodicity;
                try
                {
                    k.Run(w, h, v.Cx, v.Cy, v.Width / w, v.MaxIter, 4.0, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di,
                          null, v.Kind, v.P0, v.P1, colorDst: col ? o.Col : null);
                }
                finally { GpuWavefront.Reset(); GpuPeriodicity.Reset(); }
                if (k.LastFrameWavefront != (schedule == GpuWavefront.On)) { Console.Error.WriteLine($"  FAIL {v.Name}: wrong schedule ran"); failures++; }
                o.Ms = k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs;
                return o;
            }
            foreach (var v in Views)
                foreach (int per in new[] { GpuPeriodicity.Off, GpuPeriodicity.On })
                {
                    bool col = per == GpuPeriodicity.On;
                    var off = Frame(v, GpuWavefront.Off, per, col);
                    var on = Frame(v, GpuWavefront.On, per, col);
                    string l = $"{v.Name}, periodicity {(per == GpuPeriodicity.On ? "on" : "off")}";
                    if (!off.It.AsSpan().SequenceEqual(on.It) || !SameBits(off.Sm, on.Sm) || !off.Col.AsSpan().SequenceEqual(on.Col)
                        || !SameBits(off.Zr, on.Zr) || !SameBits(off.Zi, on.Zi) || !SameBits(off.Dr, on.Dr) || !SameBits(off.Di, on.Di))
                    {
                        Console.Error.WriteLine($"  FAIL {l}: the wavefront frame differs");
                        failures++;
                    }
                    double tOff = Enumerable.Range(0, 3).Min(_ => Frame(v, GpuWavefront.Off, per, false).Ms);
                    double tOn = Enumerable.Range(0, 3).Min(_ => Frame(v, GpuWavefront.On, per, false).Ms);
                    Console.WriteLine($"  {l}: one thread per pixel {tOff:F1} ms, wavefront {tOn:F1} ms ({tOff / tOn:F2}x), " +
                                      $"warp waste {GpuWavefront.WarpWaste(off.It, w, h):F2}");
                }
            Console.WriteLine(failures == 0 ? "gpuwavefrontprobe OK" : $"gpuwavefrontprobe FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        // #1384 — a theme over every orbit input, so every accumulator is in the kernel.
        private static IColorMap OrbitTheme()
        {
            bool prev = InterpretedOrbitColorMap.GpuEnabled;
            InterpretedOrbitColorMap.GpuEnabled = true;
            try
            {
                var t = InterpretedColorMap.TryCreate(
                    "return hsv(fract(trapMin + trapCross + trapRing + trapHyperbola + trapHexagon + stripeAvg * 3.0 + tiaAvg + curvature * 0.1), " +
                    "saturate(0.3 + gaussian), saturate(0.2 + expSmooth + lyapunov * 0.05));", null, out var err);
                if (t is not InterpretedOrbitColorMap) throw new InvalidOperationException("orbit theme: " + err);
                return t;
            }
            finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
        }

        private static int ShallowOrbit(MandelbrotGpuKernel k, int w, int h)
        {
            int n = w * h, failures = 0;
            var theme = (IGpuHlslPalette)OrbitTheme();
            Console.WriteLine($"gpuwavefrontprobe orbit {w}x{h}, {GpuWavefront.Threads} threads");
            (Out O, float[] Trap) Frame(View v, int schedule)
            {
                var o = new Out(n); var trap = new float[n];
                k.SetPalette(theme);
                if (!k.HasGpuPalette) throw new InvalidOperationException("the orbit theme did not compile");
                GpuWavefront.Mode = schedule;
                try
                {
                    k.Run(w, h, v.Cx, v.Cy, v.Width / w, v.MaxIter, 4.0, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di,
                          null, v.Kind, v.P0, v.P1, colorDst: o.Col, trapDst: trap);
                }
                finally { GpuWavefront.Reset(); }
                if (k.LastFrameWavefront != (schedule == GpuWavefront.On)) { Console.Error.WriteLine($"  FAIL {v.Name}: wrong schedule ran"); failures++; }
                o.Ms = k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs;
                return (o, trap);
            }
            foreach (var v in Views)
            {
                var (off, offTrap) = Frame(v, GpuWavefront.Off);
                var (on, onTrap) = Frame(v, GpuWavefront.On);
                if (!off.It.AsSpan().SequenceEqual(on.It) || !SameBits(off.Sm, on.Sm) || !off.Col.AsSpan().SequenceEqual(on.Col)
                    || !SameBits(offTrap, onTrap) || !SameBits(off.Zr, on.Zr) || !SameBits(off.Zi, on.Zi) || !SameBits(off.Dr, on.Dr) || !SameBits(off.Di, on.Di))
                {
                    Console.Error.WriteLine($"  FAIL {v.Name}: the wavefront orbit frame differs");
                    failures++;
                }
                double tOff = Enumerable.Range(0, 3).Min(_ => Frame(v, GpuWavefront.Off).O.Ms);
                double tOn = Enumerable.Range(0, 3).Min(_ => Frame(v, GpuWavefront.On).O.Ms);
                Console.WriteLine($"  {v.Name} (orbit): one thread per pixel {tOff:F1} ms, wavefront {tOn:F1} ms ({tOff / tOn:F2}x), " +
                                  $"warp waste {GpuWavefront.WarpWaste(off.It, w, h):F2}");
            }
            k.SetPalette(null);
            Console.WriteLine(failures == 0 ? "gpuwavefrontprobe orbit OK" : $"gpuwavefrontprobe orbit FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        private static int DeepKernels(MandelbrotGpuKernel k, int w, int h)
        {
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");
            bool savedPerturb = MandelbrotCalculator.UseGpuPerturbation, savedOrbit = MandelbrotCalculator.UseGpuDeepOrbit,
                 savedSa = MandelbrotCalculator.UseGpuSeriesApproximation, savedBla = MandelbrotCalculator.UseGpuBla;
            var savedPrecision = MandelbrotCalculator.GpuDeepPrecisionMode;
            MandelbrotCalculator.UseGpuPerturbation = true;
            var theme = OrbitTheme();
            int failures = 0;
            Console.WriteLine($"gpuwavefrontprobe deepkernels {w}x{h}");
            try
            {
                foreach (bool orbit in new[] { true, false })
                {
                    MandelbrotCalculator.UseGpuDeepOrbit = orbit;
                    MandelbrotCalculator.GpuDeepPrecisionMode = orbit ? GpuDeepPrecision.Double : GpuDeepPrecision.Fp32;
                    MandelbrotCalculator.UseGpuSeriesApproximation = orbit;   // fp32: the SA / BLA kernels would take the frame
                    MandelbrotCalculator.UseGpuBla = orbit;
                    string kernel = orbit ? "orbit" : "fp32";
                    foreach (var v in DeepViews)
                    {
                        (int[] It, float[] Sm, uint[] Col, double Ms, bool Wave, bool Path) Frame(int perturbMode)
                        {
                            var c = new MandelbrotCalculator(w, h)
                            {
                                CenterX = v.Cx, CenterXLo = v.CxLo, CenterY = v.Cy, CenterYLo = v.CyLo,
                                Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = orbit ? theme : new HsvPalette(), GpuKernel = k,
                            };
                            GpuWavefront.PerturbMode = perturbMode;
                            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                            try { c.Calculate(System.Threading.CancellationToken.None); }
                            finally { GpuWavefront.Reset(); }
                            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                            bool path = orbit ? c.LastFrameUsedGpuOrbitPerturbation : c.LastFrameUsedGpuFloatExp;
                            return ((int[])c.IterationBuffer.Clone(), (float[])c.SmoothBuffer.Clone(), (uint[])c.ColorBuffer.Clone(),
                                    ms, c.LastFrameUsedGpuWavefront, path);
                        }
                        var off = Frame(GpuWavefront.Off);
                        if (!off.Path) { Console.WriteLine($"  {v.Name} ({kernel}): not on the {kernel} kernel"); continue; }
                        var on = Frame(GpuWavefront.On);
                        if (!on.Wave) { Console.Error.WriteLine($"  FAIL {v.Name} ({kernel}): the forced frame ran one thread per pixel"); failures++; }
                        if (!off.It.AsSpan().SequenceEqual(on.It) || !SameBits(off.Sm, on.Sm) || !off.Col.AsSpan().SequenceEqual(on.Col))
                        {
                            Console.Error.WriteLine($"  FAIL {v.Name} ({kernel}): the wavefront frame differs");
                            failures++;
                        }
                        double tOff = Enumerable.Range(0, 2).Min(_ => Frame(GpuWavefront.Off).Ms);
                        double tOn = Enumerable.Range(0, 2).Min(_ => Frame(GpuWavefront.On).Ms);
                        double waste = GpuWavefront.WarpWaste(off.It, w, h);
                        Console.WriteLine($"  {v.Name} ({kernel}): one thread per pixel {tOff:F1} ms, wavefront {tOn:F1} ms ({tOff / tOn:F2}x); " +
                                          $"warp waste {waste:F2} -> Auto {(waste >= GpuWavefront.PerturbWasteThreshold ? "wavefront" : "one thread per pixel")}");
                    }
                }
            }
            finally
            {
                MandelbrotCalculator.UseGpuPerturbation = savedPerturb; MandelbrotCalculator.UseGpuDeepOrbit = savedOrbit;
                MandelbrotCalculator.UseGpuSeriesApproximation = savedSa; MandelbrotCalculator.UseGpuBla = savedBla;
                MandelbrotCalculator.GpuDeepPrecisionMode = savedPrecision;
            }
            Console.WriteLine(failures == 0 ? "gpuwavefrontprobe deepkernels OK" : $"gpuwavefrontprobe deepkernels FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        private static int Deep(MandelbrotGpuKernel k, int w, int h)
        {
            // Speed is measured, not guarded: no first-band abort to the CPU on a weak-fp64 card.
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");
            bool savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
            MandelbrotCalculator.UseGpuPerturbation = true;
            int failures = 0;
            Console.WriteLine($"gpuwavefrontprobe deep {w}x{h}, Auto threshold {GpuWavefront.PerturbWasteThreshold}");
            try
            {
                foreach (var v in DeepViews)
                {
                    double seedEst = 0;
                    (int[] It, float[] Sm, double Ms, bool Wave, string Route) Frame(int perturbMode, bool seed = false)
                    {
                        var c = new MandelbrotCalculator(w, h)
                        {
                            CenterX = v.Cx, CenterXLo = v.CxLo, CenterY = v.Cy, CenterYLo = v.CyLo,
                            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = new HsvPalette(), GpuKernel = k,
                        };
                        GpuWavefront.PerturbMode = perturbMode;
                        GpuWavefront.Seed = seed;
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        try { c.Calculate(System.Threading.CancellationToken.None); }
                        finally { GpuWavefront.Reset(); }
                        if (seed) seedEst = c.LastFrameRanWavefrontSeed ? c.LastGpuWavefrontSeed : double.NaN;
                        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                        if (!c.LastFrameUsedGpuPerturbation) ms = double.NaN;
                        return ((int[])c.IterationBuffer.Clone(), (float[])c.SmoothBuffer.Clone(), ms,
                                c.LastFrameUsedGpuWavefront, c.LastGpuRoute.Detail ?? "");
                    }
                    var off = Frame(GpuWavefront.Off);
                    var on = Frame(GpuWavefront.On);
                    if (double.IsNaN(off.Ms)) { Console.WriteLine($"  {v.Name}: CPU route ({off.Route})"); continue; }
                    if (!on.Wave) { Console.Error.WriteLine($"  FAIL {v.Name}: the forced frame ran one thread per pixel"); failures++; }
                    if (!off.It.AsSpan().SequenceEqual(on.It) || !SameBits(off.Sm, on.Sm))
                    {
                        Console.Error.WriteLine($"  FAIL {v.Name}: the wavefront frame differs");
                        failures++;
                    }
                    double tOff = Enumerable.Range(0, 2).Min(_ => Frame(GpuWavefront.Off).Ms);
                    double tOn = Enumerable.Range(0, 2).Min(_ => Frame(GpuWavefront.On).Ms);
                    double waste = GpuWavefront.WarpWaste(off.It, w, h);
                    Console.WriteLine($"  {v.Name}: one thread per pixel {tOff:F1} ms, wavefront {tOn:F1} ms ({tOff / tOn:F2}x); " +
                                      $"warp waste {waste:F2} -> Auto {(waste >= GpuWavefront.PerturbWasteThreshold ? "wavefront" : "one thread per pixel")}");
                    var seeded = Frame(GpuWavefront.Auto, seed: true);
                    if (!off.It.AsSpan().SequenceEqual(seeded.It) || !SameBits(off.Sm, seeded.Sm))
                    {
                        Console.Error.WriteLine($"  FAIL {v.Name}: the seeded frame differs");
                        failures++;
                    }
                    double tSeed = Enumerable.Range(0, 2).Min(_ => Frame(GpuWavefront.Auto, seed: true).Ms);
                    Console.WriteLine($"      seeded first frame: 1/{GpuWavefront.SeedDivisor} estimate {seedEst:F2} -> " +
                                      $"{(seeded.Wave ? "wavefront" : "one thread per pixel")}, {tSeed:F1} ms vs {tOff:F1} ms unseeded ({tOff / tSeed:F2}x)");
                }
            }
            finally { MandelbrotCalculator.UseGpuPerturbation = savedPerturb; }
            Console.WriteLine(failures == 0 ? "gpuwavefrontprobe deep OK" : $"gpuwavefrontprobe deep FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }
    }
}
