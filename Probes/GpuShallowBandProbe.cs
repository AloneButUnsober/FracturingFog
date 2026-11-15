// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1481: --gpubandprobe shallow. The shallow escape-time kernels (base, colour, orbit
// colour) in TDR row bands, on the default D3D11 hardware adapter and the Vulkan device
// (each skipped when absent). Checked, per backend:
//   • 960x540, the period-3 minibrot at 4000 iterations: the frame in 64-row bands and
//     in the budget's own bands is byte-identical to the frame dispatched as one band,
//     for each kernel, one thread per pixel and wavefront. A frame of another view runs
//     before each banded one, so a band that wrote the wrong rows (or none) would keep
//     that view's pixels;
//   • 1920x1080, the same view: the frame that ran past the OS watchdog on a GT 710 when
//     it was one dispatch (device removed). It runs in the budget's bands only (one band
//     is the crash), and is checked against the same frame in 64-row bands; a removed
//     device is a FAIL;
//   • the wall time of the budget's bands against one band at 960x540 (the cost of the
//     extra dispatches), and the GPU packets (#1329 timestamps) of each frame.
// Exit 0 = pass or SKIP; 1 = a check failed.

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class GpuShallowBandProbe
    {
        private const double Cx = -1.7548776662466927, Cy = 0.0, Span = 0.04;   // period-3 minibrot
        private const double OtherCx = -0.7453, OtherCy = 0.1127, OtherSpan = 0.01;
        private const int MaxIter = 4000;

        private enum Variant { Base, Colour, Orbit }

        public static int Run()
        {
            int failures = 0;
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
                Console.WriteLine($"gpubandprobe shallow D3D11 SKIP: no hardware device (0x{hr.Code:X8}).");
            else
            {
                try
                {
                    using var k = new MandelbrotGpuKernel(device, context, new object());
                    failures += Backend("D3D11", k, typeof(MandelbrotGpuKernel).Assembly, () => k.LastShallowBandCount);
                }
                finally { context.Dispose(); device.Dispose(); }
            }

            VulkanComputeKernel? vk = null;
            try { vk = VulkanComputeKernel.TryCreateWithOwnContext(); } catch { }
            if (vk == null) Console.WriteLine("gpubandprobe shallow Vulkan SKIP: no device.");
            else using (vk) failures += Backend("Vulkan", vk, typeof(VulkanComputeKernel).Assembly, () => vk.LastShallowBandCount);

            Console.WriteLine(failures == 0 ? "gpubandprobe shallow OK" : $"gpubandprobe shallow FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        private static IGpuHlslPalette OrbitTheme()
        {
            bool prev = InterpretedOrbitColorMap.GpuEnabled;
            InterpretedOrbitColorMap.GpuEnabled = true;
            try
            {
                var t = InterpretedColorMap.TryCreate(
                    "return hsv(fract(trapMin + trapCross + trapRing + trapHyperbola + trapHexagon + stripeAvg * 3.0 + tiaAvg + curvature * 0.1), " +
                    "saturate(0.3 + gaussian), saturate(0.2 + expSmooth + lyapunov * 0.05));", null, out var err);
                if (t is not InterpretedOrbitColorMap) throw new InvalidOperationException("orbit theme: " + err);
                return (IGpuHlslPalette)t;
            }
            finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
        }

        private static int Backend(string name, IGpuKernel k, Assembly asm, Func<int> bandCount)
        {
            Console.WriteLine($"gpubandprobe shallow {name}: {(k as VulkanComputeKernel)?.Description ?? name}");
            // MandelbrotKernelSource is linked into both backend assemblies; each bands with its own copy.
            var src = asm.GetType("FracturingFog.Rendering.MandelbrotKernelSource", throwOnError: true)!;
            var overrideField = src.GetField("ShallowBandRowsOverride")!;
            var colour = ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First();
            var orbit = OrbitTheme();
            int failures = 0;

            Frame Render(int w, int h, Variant variant, int schedule, int rows, bool other = false)
            {
                var f = new Frame(w * h);
                k.SetPalette(variant switch { Variant.Colour => colour, Variant.Orbit => orbit, _ => null });
                if (variant != Variant.Base && !k.HasGpuPalette) throw new InvalidOperationException($"{variant} palette did not compile");
                int saved = (int)overrideField.GetValue(null)!;
                overrideField.SetValue(null, rows);
                GpuWavefront.Mode = schedule;
                try
                {
                    double cx = other ? OtherCx : Cx, cy = other ? OtherCy : Cy, span = other ? OtherSpan : Span;
                    long t0 = Stopwatch.GetTimestamp();
                    k.Run(w, h, cx, cy, span / w, MaxIter, 4.0, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di, null,
                          FractalKind.Mandelbrot, 0f, 0f,
                          colorDst: variant == Variant.Base ? null : f.Col, trapDst: variant == Variant.Orbit ? f.Trap : null);
                    f.Ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                }
                finally { overrideField.SetValue(null, saved); GpuWavefront.Reset(); }
                f.Bands = bandCount();
                f.Packets = k.LastGpuStageTimes?.Packets ?? -1;
                return f;
            }

            const int WholeFrame = 1 << 20;   // rounds to whole groups, then clamps to the height: one band
            foreach (Variant variant in Enum.GetValues<Variant>())
                foreach (int schedule in new[] { GpuWavefront.Off, GpuWavefront.On })
                {
                    string label = $"{variant}, {(schedule == GpuWavefront.On ? "wavefront" : "one thread per pixel")}";
                    try
                    {
                        // 960x540: banded == one band.
                        const int w = 960, h = 540;
                        var whole = Render(w, h, variant, schedule, WholeFrame);
                        foreach (int rows in new[] { 64, 0 })
                        {
                            Render(w, h, variant, schedule, WholeFrame, other: true);   // dirty every buffer
                            var banded = Render(w, h, variant, schedule, rows);
                            string? diff = banded.FirstDifference(whole, w);
                            if (diff != null || banded.Bands < (rows == 0 ? 1 : 2) || whole.Bands != 1)
                            {
                                Console.Error.WriteLine($"  FAIL {label} {w}x{h}, {(rows == 0 ? "budget" : rows + "-row")} bands " +
                                                        $"({banded.Bands}): {diff ?? "band count"}");
                                failures++;
                            }
                        }
                        double tWhole = Enumerable.Range(0, 3).Min(_ => Render(w, h, variant, schedule, WholeFrame).Ms);
                        var auto = Enumerable.Range(0, 3).Select(_ => Render(w, h, variant, schedule, 0)).MinBy(f => f.Ms)!;
                        Console.WriteLine($"  {label,-36} {w}x{h}: one band {tWhole,7:F1} ms, {auto.Bands} budget band(s) " +
                                          $"{auto.Ms,7:F1} ms ({auto.Ms / tWhole:F2}x), {auto.Packets} GPU packet(s)");

                        // 1920x1080: the frame that reset the GT 710 as one dispatch.
                        const int bw = 1920, bh = 1080;
                        Render(bw, bh, variant, schedule, 64, other: true);
                        var big = Render(bw, bh, variant, schedule, 0);
                        Render(bw, bh, variant, schedule, 0, other: true);
                        var big64 = Render(bw, bh, variant, schedule, 64);
                        string? bigDiff = big.FirstDifference(big64, bw);
                        if (bigDiff != null || big.Bands < 2)
                        {
                            Console.Error.WriteLine($"  FAIL {label} {bw}x{bh}: {bigDiff ?? $"{big.Bands} band(s)"}");
                            failures++;
                        }
                        Console.WriteLine($"  {label,-36} {bw}x{bh}: {big.Bands} budget bands {big.Ms,7:F1} ms, " +
                                          $"{big.Packets} GPU packet(s), == 64-row bands: {bigDiff == null}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  FAIL {label}: {ex.GetType().Name}: {ex.Message}");
                        failures++;
                        if (ex.Message.Contains("removed", StringComparison.OrdinalIgnoreCase)
                            || ex.Message.Contains("DEVICE_LOST", StringComparison.OrdinalIgnoreCase))
                            return failures;   // nothing more runs on a lost device
                    }
                }
            k.SetPalette(null);
            return failures;
        }

        private sealed class Frame
        {
            public readonly int[] It; public readonly float[] Sm, Zr, Zi, Dr, Di, Trap; public readonly uint[] Col;
            public double Ms; public int Bands, Packets;

            public Frame(int n)
            {
                It = new int[n]; Sm = new float[n]; Zr = new float[n]; Zi = new float[n]; Dr = new float[n]; Di = new float[n];
                Trap = new float[n]; Col = new uint[n];
            }

            public string? FirstDifference(Frame o, int w)
            {
                for (int i = 0; i < It.Length; i++)
                {
                    if (It[i] != o.It[i] || Col[i] != o.Col[i] || Bits(Sm[i]) != Bits(o.Sm[i]) || Bits(Zr[i]) != Bits(o.Zr[i])
                        || Bits(Zi[i]) != Bits(o.Zi[i]) || Bits(Dr[i]) != Bits(o.Dr[i]) || Bits(Di[i]) != Bits(o.Di[i])
                        || Bits(Trap[i]) != Bits(o.Trap[i]))
                        return $"pixel ({i % w}, {i / w})";
                }
                return null;
            }

            private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
        }
    }
}
