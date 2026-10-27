// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1394 part 1: --gpupixelmaskprobe [W H]. The D3D11 half of the GPU pixel-mask check
// (Server.Tests has no D3D reference; S1394GpuPixelMaskTests cover Vulkan). On the
// default hardware adapter, a real MandelbrotGpuKernel, for the shallow kernel (base
// and colour; Mandelbrot, Julia, Multibrot) and the deep ones (double, SA, fp32 /
// float-exp), both schedules: a masked dispatch into arrays holding a sentinel must
// give every marked pixel exactly the full dispatch's iter / smooth / final z / dz
// (and colour) and leave every unmarked one at the sentinel. Then the time of a
// sparse mask (the solid-region grid lines) against the full dispatch: the unmarked
// threads must cost next to nothing (checked on the deep kernels, < 0.6x: all in set,
// every pixel costs the cap).
// Exit 0 = pass or SKIP (no D3D11 hardware device); 1 = a pixel differs.

using System;
using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class GpuPixelMaskProbe
    {
        private const uint Sentinel = 0xDEADBEEF;

        private sealed class Out
        {
            public readonly int[] It; public readonly float[] Sm, Zr, Zi, Dr, Di; public readonly uint[] Col;
            public Out(int n, bool sentinel)
            {
                It = new int[n]; Sm = new float[n]; Zr = new float[n]; Zi = new float[n]; Dr = new float[n]; Di = new float[n]; Col = new uint[n];
                if (!sentinel) return;
                Array.Fill(It, unchecked((int)Sentinel)); Array.Fill(Col, Sentinel);
                float s = BitConverter.Int32BitsToSingle(unchecked((int)Sentinel));
                foreach (var a in new[] { Sm, Zr, Zi, Dr, Di }) Array.Fill(a, s);
            }
        }

        public static int Run(string[] args)
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"gpupixelmaskprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
                return 0;
            }
            try
            {
                int w = args.Length > 2 ? int.Parse(args[1]) : 640, h = args.Length > 2 ? int.Parse(args[2]) : 360;
                using var k = new MandelbrotGpuKernel(device, context, new object());
                Console.WriteLine($"gpupixelmaskprobe {w}x{h}, D3D11 ({k.DeviceName})");
                return Check(k, w, h);
            }
            finally { context.Dispose(); device.Dispose(); }
        }

        // A scattered mask (every pixel whose hash is below the share) and the grid
        // lines of a solid-region frame (sparse, whole rows and 1-px columns).
        private static byte[] Scatter(int n, double share)
        {
            var m = new byte[n];
            var rng = new Random(1394);
            for (int i = 0; i < n; i++) m[i] = (byte)(rng.NextDouble() < share ? 1 : 0);
            return m;
        }

        private static byte[] Grid(int w, int h)
        {
            var m = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (y % MandelbrotCalculator.SolidTileSize == 0 || x % MandelbrotCalculator.SolidTileSize == 0
                        || y == h - 1 || x == w - 1) m[y * w + x] = 1;
            return m;
        }

        private static int Compare(string label, Out full, Out masked, byte[] mark, bool colour)
        {
            int bad = 0;
            static int B(float f) => BitConverter.SingleToInt32Bits(f);
            for (int i = 0; i < mark.Length && bad < 5; i++)
            {
                bool ok = mark[i] != 0
                    ? full.It[i] == masked.It[i] && B(full.Sm[i]) == B(masked.Sm[i]) && B(full.Zr[i]) == B(masked.Zr[i])
                      && B(full.Zi[i]) == B(masked.Zi[i]) && B(full.Dr[i]) == B(masked.Dr[i]) && B(full.Di[i]) == B(masked.Di[i])
                      && (!colour || full.Col[i] == masked.Col[i])
                    : masked.It[i] == unchecked((int)Sentinel) && (uint)B(masked.Sm[i]) == Sentinel && (uint)B(masked.Zr[i]) == Sentinel
                      && (uint)B(masked.Zi[i]) == Sentinel && (uint)B(masked.Dr[i]) == Sentinel && (uint)B(masked.Di[i]) == Sentinel
                      && masked.Col[i] == Sentinel;
                if (ok) continue;
                Console.Error.WriteLine($"  FAIL {label}: pixel {i} ({(mark[i] != 0 ? "marked" : "unmarked")}) " +
                                        $"iter {masked.It[i]} vs {full.It[i]}, colour {masked.Col[i]:X8} vs {full.Col[i]:X8}");
                bad++;
            }
            return bad == 0 ? 0 : 1;
        }

        private static int Check(MandelbrotGpuKernel k, int w, int h)
        {
            int n = w * h, failures = 0;
            var colourMap = ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First();
            var masks = new (string Name, byte[] Mark)[] { ("scatter 30%", Scatter(n, 0.3)), ("grid lines", Grid(w, h)) };

            var shallow = new (string Name, FractalKind Kind, double Cx, double Cy, double Span, int It, float P0, float P1)[]
            {
                ("Mandelbrot minibrot", FractalKind.Mandelbrot, -1.7548776662466927, 0, 0.05, 4000, 0, 0),
                ("Julia rabbit", FractalKind.Julia, 0, 0, 3.2, 2000, -0.123f, 0.745f),
                ("Multibrot d=4", FractalKind.Multibrot, 0, 0, 3.2, 2000, 4, 0),
            };
            foreach (var v in shallow)
                foreach (bool colour in new[] { false, true })
                    foreach (int wave in new[] { GpuWavefront.Off, GpuWavefront.On })
                    {
                        Out Frame(byte[]? mark, out double ms)
                        {
                            var o = new Out(n, mark != null);
                            k.SetPalette(colour ? colourMap : null);
                            k.PixelMask = mark == null ? null : GpuPixelMask.FromBytes(mark, w, h);
                            GpuWavefront.Mode = wave;
                            try
                            {
                                k.Run(w, h, v.Cx, v.Cy, v.Span / w, v.It, 4.0, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di,
                                      null, v.Kind, v.P0, v.P1, colorDst: colour ? o.Col : null);
                            }
                            finally { k.PixelMask = null; GpuWavefront.Reset(); }
                            if (!colour) Array.Fill(o.Col, mark == null ? 0u : Sentinel);
                            ms = k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs;
                            return o;
                        }
                        var full = Frame(null, out double fullMs);
                        foreach (var (mName, mark) in masks)
                        {
                            var masked = Frame(mark, out double ms);
                            string label = $"{v.Name}, {(colour ? "colour" : "base")}, {(wave == GpuWavefront.On ? "wavefront" : "per pixel")}, {mName}";
                            int f = Compare(label, full, masked, mark, colour);
                            failures += f;
                            Console.WriteLine($"  {(f == 0 ? "ok  " : "FAIL")} {label,-62} full {fullMs,7:F2} ms, masked {ms,7:F2} ms");
                        }
                    }

            // Deep: a reference orbit at the period-3 nucleus (never escapes), pixels at 1e6.
            const double cx = -1.7548776662466927;
            const int maxIt = 1000;   // the GT 710 runs fp64 at 1/24 rate: 4000 trips the SA too-slow abort
            var refZr = new double[maxIt]; var refZi = new double[maxIt];
            double zr = 0, zi = 0;
            for (int i = 0; i < maxIt; i++) { refZr[i] = zr; refZi[i] = zi; (zr, zi) = (zr * zr - zi * zi + cx, 2 * zr * zi); }
            double scale = 3.5 / w / 3e4;
            var zero = new double[maxIt + 1];
            var deep = new (string Name, Action<Out> Run)[]
            {
                ("deep double", o => k.RunPerturb(w, h, scale, maxIt, 512.0 * 512.0, -w / 2.0 + 0.25, -h / 2.0 + 0.25,
                    refZr, refZi, maxIt, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di)),
                ("deep SA kernel", o => k.RunPerturbSA(w, h, scale, maxIt, 512.0 * 512.0, -w / 2.0 + 0.25, -h / 2.0 + 0.25,
                    refZr, refZi, maxIt, 1e-6, 0, zero, zero, zero, zero, zero, zero, zero, zero,
                    o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di)),
                ("deep fp32", o => k.RunPerturbFloatExp(w, h, scale, maxIt, 512.0 * 512.0, -w / 2.0 + 0.25, -h / 2.0 + 0.25,
                    refZr, refZi, maxIt, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di)),
            };
            foreach (var (name, run) in deep)
            {
                if (name != "deep fp32" && !k.SupportsPerturbation) { Console.WriteLine($"  skip {name}: no fp64"); continue; }
                foreach (int wave in new[] { GpuWavefront.Off, GpuWavefront.On })
                {
                    Out Frame(byte[]? mark, out double ms)
                    {
                        var o = new Out(n, mark != null);
                        Array.Fill(o.Col, mark == null ? 0u : Sentinel);
                        k.PixelMask = mark == null ? null : GpuPixelMask.FromBytes(mark, w, h);
                        GpuWavefront.Mode = GpuWavefront.On;
                        GpuWavefront.PerturbMode = wave;
                        try { run(o); }
                        finally { k.PixelMask = null; GpuWavefront.Reset(); }
                        ms = k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs;
                        return o;
                    }
                    var full = Frame(null, out double fullMs);
                    foreach (var (mName, mark) in masks)
                    {
                        var masked = Frame(mark, out double ms);
                        string label = $"{name}, {(wave == GpuWavefront.On ? "wavefront" : "per pixel")}, {mName}";
                        int f = Compare(label, full, masked, mark, colour: false);
                        // The outputs can't show a kernel that iterates the unmarked pixels anyway
                        // (only marked ones are read back); the time does. The frame is all in set,
                        // so every pixel costs the cap and the grid lines (~3 %) must cost far less.
                        if (f == 0 && mName == "grid lines" && ms >= 0.6 * fullMs)
                        {
                            Console.Error.WriteLine($"  FAIL {label}: masked {ms:F2} ms is not well under full {fullMs:F2} ms");
                            f = 1;
                        }
                        failures += f;
                        Console.WriteLine($"  {(f == 0 ? "ok  " : "FAIL")} {label,-62} full {fullMs,7:F2} ms, masked {ms,7:F2} ms");
                    }
                }
            }
            Console.WriteLine(failures == 0 ? "gpupixelmaskprobe PASS" : $"gpupixelmaskprobe FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }
    }
}
