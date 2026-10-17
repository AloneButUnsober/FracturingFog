// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1332: --gpudeferprobe. The D3D11 check of the deferred readback (Server.Tests has
// no D3D reference; S1332DeferredReadbackTests covers Vulkan). On the default
// hardware adapter, real calculators on one shared MandelbrotGpuKernel:
//   - a GPU-coloured frame defers (pending = iterations | smooth | final z |
//     distance) and its colour is already the eager frame's;
//   - reading each buffer brings back exactly the eager frame's bytes (iterations,
//     smooth, final z / dz, distance), and clears just that output;
//   - a pending frame survives another calculator's dispatch on the kernel (it is
//     flushed into its own arrays first), and its own next frame supersedes it;
//   - Mandelbrot (several views, sizes, palettes) and the escape-time families;
//   - independently (eager and deferred share the copy code), the outputs agree with
//     the CPU calculator where the iteration does.
// Then frame times, eager vs deferred, at 1080p and 4K (reported, not asserted).
// Exit 0 = pass or SKIP (no D3D11 hardware device); 1 = failure.

using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FracturingFog
{
    internal static class GpuDeferredReadbackProbe
    {
        private const int AllPending = 15;   // GpuAuxOutputs.All | distance (8)

        public static int Run()
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"gpudeferprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
                return 0;
            }
            string adapter = "D3D11 adapter";
            try
            {
                using var dxgi = device.QueryInterface<IDXGIDevice>();
                using var a = dxgi.GetAdapter();
                adapter = a.Description.Description;
            }
            catch { /* cosmetic */ }

            int failures = 0;
            void Check(bool ok, string what)
            {
                if (ok) return;
                Console.Error.WriteLine("  FAIL " + what);
                failures++;
            }

            try
            {
                using var k = new MandelbrotGpuKernel(device, context, new object());
                Console.WriteLine($"gpudeferprobe on {adapter}");
                Check(k.SupportsDeferredReadback, "the D3D11 kernel does not support deferred readback");
                var palettes = ColorPalette.BuiltIns.Where(m => m is FracturingFog.Interefaces.IGpuHlslPalette).Take(2).ToArray();
                Check(palettes.Length == 2, "precondition: two GPU palettes");

                // Mandelbrot: views x sizes x palettes.
                var views = new (double Cx, double Cy, double Zoom, int Iter)[]
                {
                    (-0.6, 0.0, 1.0, 256),
                    (-0.7453, 0.1127, 300.0, 1000),
                    (0.2501, 0.0, 40.0, 2000),
                    (-0.1225611668766536, 0.7448617666197442, 175.0, 500),   // inside the period-3 bulb: all in set
                };
                int allInSetCases = 0;
                var sizes = new (int W, int H)[] { (320, 240), (257, 131), (640, 360) };
                int cases = 0;
                foreach (var v in views)
                foreach (var s in sizes)
                foreach (var pal in palettes)
                {
                    MandelbrotCalculator Make() => new(s.W, s.H)
                    {
                        CenterX = v.Cx, CenterY = v.Cy, Zoom = v.Zoom, MaxIterations = v.Iter,
                        ColorMap = pal, UseGpuCompute = true, GpuKernel = k,
                    };
                    string label = $"Mandelbrot ({v.Cx}, {v.Cy}) zoom {v.Zoom} {s.W}x{s.H} {pal.GetType().Name}";
                    var eager = Make();
                    GpuDeferredReadback.Enabled = false;
                    eager.Calculate();
                    GpuDeferredReadback.Enabled = true;
                    var lazy = Make();
                    lazy.Calculate();
                    Check(eager.LastFrameUsedGpuCompute && lazy.LastFrameUsedGpuCompute, label + ": precondition: frames ran on the GPU");
                    Check(eager.GpuAuxPending == 0, label + ": the eager frame left outputs pending");
                    Check(lazy.GpuAuxPending == AllPending, $"{label}: deferred frame pending {lazy.GpuAuxPending}, expected {AllPending}");
                    Check(Same(eager.ColorBuffer, lazy.ColorBuffer), label + ": colour differs before any read");
                    // The GPU's any-escaped count answers the solid-frame check as the
                    // eager frame's iteration scan does, without reading them back.
                    bool scanAny = eager.IterationBuffer.Take(s.W * s.H).Any(it => it < v.Iter);
                    Check(eager.GpuFrameAnyEscaped == null, label + ": an eager frame claims a GPU escape count");
                    Check(lazy.GpuFrameAnyEscaped == scanAny, $"{label}: GPU any-escaped {lazy.GpuFrameAnyEscaped}, scan {scanAny}");
                    Check(IterationRescue.IsAllInSet(lazy) == IterationRescue.IsAllInSet(eager), label + ": solid-frame check differs");
                    if (!scanAny) allInSetCases++;
                    Check(lazy.GpuAuxPending == AllPending, label + ": reading the colour or the escape count read back aux outputs");
                    // One output at a time: each read clears only its own bit.
                    Check(Same(eager.SmoothBuffer, lazy.SmoothBuffer), label + ": smooth differs");
                    Check(lazy.GpuAuxPending == (AllPending & ~2), $"{label}: after smooth, pending {lazy.GpuAuxPending}");
                    Check(Same(eager.IterationBuffer, lazy.IterationBuffer), label + ": iterations differ");
                    Check(Same(eager.DistanceBuffer, lazy.DistanceBuffer), label + ": distance differs");
                    Check(lazy.GpuAuxPending == 0, $"{label}: after distance, pending {lazy.GpuAuxPending} (needs iterations + final z)");
                    Check(Same(eager.FinalZrBuffer, lazy.FinalZrBuffer) && Same(eager.FinalZiBuffer, lazy.FinalZiBuffer)
                          && Same(eager.FinalDrBuffer, lazy.FinalDrBuffer) && Same(eager.FinalDiBuffer, lazy.FinalDiBuffer),
                        label + ": final z / dz differ");
                    // Independent anchor (eager and deferred share the copy code): the
                    // outputs agree with the CPU calculator where the iteration does.
                    if (ReferenceEquals(pal, palettes[0])) MatchesCpu(label, lazy, v.Iter, Check);
                    cases++;
                }

                Check(allInSetCases > 0, "precondition: no all-in-set case");

                // Another calculator's dispatch flushes a pending frame into its own arrays.
                {
                    var eager = new MandelbrotCalculator(320, 240) { CenterX = -0.7453, CenterY = 0.1127, Zoom = 300, MaxIterations = 1000, ColorMap = palettes[0], UseGpuCompute = true, GpuKernel = k };
                    GpuDeferredReadback.Enabled = false;
                    eager.Calculate();
                    GpuDeferredReadback.Enabled = true;
                    var lazy = new MandelbrotCalculator(320, 240) { CenterX = -0.7453, CenterY = 0.1127, Zoom = 300, MaxIterations = 1000, ColorMap = palettes[0], UseGpuCompute = true, GpuKernel = k };
                    lazy.Calculate();
                    var other = new MandelbrotCalculator(200, 100) { CenterX = 0.3, CenterY = 0.5, Zoom = 3, MaxIterations = 500, ColorMap = palettes[1], UseGpuCompute = true, GpuKernel = k };
                    other.Calculate();   // deferred too: the kernel flushes lazy's frame first
                    Check(lazy.GpuAuxPending == AllPending, "flush: precondition: lazy still pending on the calculator side");
                    Check(Same(eager.IterationBuffer, lazy.IterationBuffer) && Same(eager.SmoothBuffer, lazy.SmoothBuffer)
                          && Same(eager.FinalZrBuffer, lazy.FinalZrBuffer) && Same(eager.DistanceBuffer, lazy.DistanceBuffer),
                        "flush: a frame pending across another calculator's dispatch differs from eager");
                    Check(other.GpuAuxPending == AllPending, "flush: the other calculator's own frame is not pending");

                    // Supersede: the same calculator's next frame replaces the pending one.
                    var eager2 = new MandelbrotCalculator(200, 100) { CenterX = -1.25, CenterY = 0.02, Zoom = 20, MaxIterations = 700, ColorMap = palettes[1], UseGpuCompute = true, GpuKernel = k };
                    GpuDeferredReadback.Enabled = false;
                    eager2.Calculate();
                    GpuDeferredReadback.Enabled = true;
                    other.CenterX = -1.25; other.CenterY = 0.02; other.Zoom = 20; other.MaxIterations = 700;
                    other.Calculate();
                    Check(Same(eager2.IterationBuffer, other.IterationBuffer) && Same(eager2.SmoothBuffer, other.SmoothBuffer)
                          && Same(eager2.FinalDiBuffer, other.FinalDiBuffer) && Same(eager2.DistanceBuffer, other.DistanceBuffer),
                        "supersede: a calculator's next frame does not read back as its eager twin");
                }

                // Escape-time families (Julia, Burning Ship, Multibrot, Phoenix).
                foreach (var (type, p) in new (FractalType, FractalParameters)[]
                {
                    (FractalType.Julia, new FractalParameters { JuliaC = new Complex(-0.8, 0.156) }),
                    (FractalType.BurningShip, new FractalParameters()),
                    (FractalType.Multibrot, new FractalParameters { MultibrotExponent = 3 }),
                    (FractalType.Phoenix, new FractalParameters { PhoenixP = new Complex(-0.5, 0) }),
                })
                {
                    EscapeTimeCalculator Make() => new(320, 240)
                    {
                        FractalType = type, FractalParameters = p, ColorMap = palettes[0],
                        MaxIterations = 400, CenterX = 0, CenterY = 0, Zoom = 1.0, UseGpuCompute = true, GpuKernel = k,
                    };
                    var eager = Make();
                    GpuDeferredReadback.Enabled = false;
                    eager.Calculate();
                    GpuDeferredReadback.Enabled = true;
                    var lazy = Make();
                    lazy.Calculate();
                    Check(lazy.GpuAuxPending == AllPending, $"{type}: deferred frame pending {lazy.GpuAuxPending}");
                    Check(Same(eager.ColorBuffer, lazy.ColorBuffer), $"{type}: colour differs");
                    Check(Same(eager.IterationBuffer, lazy.IterationBuffer) && Same(eager.SmoothBuffer, lazy.SmoothBuffer)
                          && Same(eager.FinalZrBuffer, lazy.FinalZrBuffer) && Same(eager.FinalZiBuffer, lazy.FinalZiBuffer)
                          && Same(eager.FinalDrBuffer, lazy.FinalDrBuffer) && Same(eager.FinalDiBuffer, lazy.FinalDiBuffer)
                          && Same(eager.DistanceBuffer, lazy.DistanceBuffer),
                        $"{type}: deferred outputs differ from eager");
                    cases++;
                }
                Console.WriteLine($"  {cases} case(s) compared bit for bit, plus flush + supersede");

                // Timing (reported): one calculator per size, eager vs deferred, then
                // deferred plus the host's per-frame solid-frame check (reads iterations).
                foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) })
                {
                    var calc = new MandelbrotCalculator(w, h) { ColorMap = palettes[0], UseGpuCompute = true, GpuKernel = k };
                    foreach (var (name, cx, cy, zoom, iter) in new[]
                    {
                        ("full set, 256 it", -0.6, 0.0, 1.0, 256),
                        ("seahorse, 1000 it", -0.7453, 0.1127, 300.0, 1000),
                    })
                    {
                        calc.CenterX = cx; calc.CenterY = cy; calc.Zoom = zoom; calc.MaxIterations = iter;
                        double Time(bool defer, bool readIter)
                        {
                            var t = new double[7];
                            GpuDeferredReadback.Enabled = defer;
                            for (int r = 0; r < t.Length; r++)
                            {
                                long t0 = Stopwatch.GetTimestamp();
                                calc.Calculate();
                                if (readIter) IterationRescue.IsAllInSet(calc);
                                t[r] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                            }
                            var sorted = t.Skip(2).OrderBy(x => x).ToArray();
                            return sorted[sorted.Length / 2];
                        }
                        double e = Time(false, true), d = Time(true, false), di = Time(true, true);
                        Console.WriteLine($"  {w}x{h} {name}: eager {e:F1} ms, deferred {d:F1} ms ({d / e * 100:F0}%), "
                            + $"deferred + solid-frame check {di:F1} ms ({di / e * 100:F0}%)");
                    }
                }
            }
            finally
            {
                GpuDeferredReadback.Reset();
                context.Dispose();
                device.Dispose();
            }
            Console.WriteLine(failures == 0 ? $"gpudeferprobe OK: {adapter}" : $"gpudeferprobe FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        private static void MatchesCpu(string label, MandelbrotCalculator gpu, int maxIter, Action<bool, string> check)
        {
            var cpu = new MandelbrotCalculator(gpu.Width, gpu.Height)
            {
                CenterX = gpu.CenterX, CenterY = gpu.CenterY, Zoom = gpu.Zoom, MaxIterations = maxIter, ColorMap = gpu.ColorMap,
            };
            cpu.Calculate();
            int n = gpu.Width * gpu.Height;
            var shared = Enumerable.Range(0, n).Where(i => gpu.IterationBuffer[i] == cpu.IterationBuffer[i]).ToArray();
            check(shared.Length > n / 2, $"{label}: only {shared.Length}/{n} pixels share their iteration with the CPU");
            var esc = shared.Where(i => cpu.IterationBuffer[i] < maxIter).ToArray();
            if (esc.Length < 32) return;   // all in set: nothing more to compare
            static double Median(System.Collections.Generic.IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
            double sm = Median(esc.Select(i => (double)Math.Abs(gpu.SmoothBuffer[i] - cpu.SmoothBuffer[i])));
            double de = Median(esc.Select(i => Math.Abs(gpu.DistanceBuffer[i] - cpu.DistanceBuffer[i]) / Math.Max(1e-12, Math.Abs(cpu.DistanceBuffer[i]))));
            double zr = Median(esc.Select(i => Math.Abs(gpu.FinalZrBuffer[i] - cpu.FinalZrBuffer[i]) / Math.Max(1.0, Math.Abs(cpu.FinalZrBuffer[i]))));
            check(sm < 0.02, $"{label}: median |smooth GPU - CPU| {sm:E2}");
            check(de < 1e-2, $"{label}: median relative distance GPU vs CPU {de:E2}");
            check(zr < 1e-2, $"{label}: median relative final zr GPU vs CPU {zr:E2}");
        }

        private static bool Same<T>(T[] a, T[] b) where T : unmanaged
            => a.Length == b.Length && System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan()));
    }
}
