// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1342: --gpuperiodicityprobe. The D3D11 half of the GPU periodicity check
// (Server.Tests has no D3D reference; S1342GpuPeriodicityTests covers Vulkan). On
// the default hardware adapter, a real MandelbrotGpuKernel renders each view with
// the periodicity early-out off, on and in its diagnostic mode:
//   - on vs off, iter, smooth and colour are bit-identical on every pixel, and so
//     is the final z / dz/dc of every escaping pixel (base, colour and orbit
//     kernels; Mandelbrot past the bulbs, Julia, Burning Ship, Tricorn,
//     Multibrot, Phoenix);
//   - precondition: the check fired on at least a third of each view's in-set
//     pixels (past the cardioid / period-2 skip), and only on in-set pixels;
//   - the frame time, off vs on, at caps 10k / 100k (reported).
// Exit 0 = pass or SKIP (no D3D11 hardware device); 1 = failure.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FracturingFog
{
    internal static class GpuPeriodicityProbe
    {
        private const int W = 256, H = 192, N = W * H;

        private sealed record View(string Name, FractalKind Kind, double Cx, double Cy, double Width,
                                   int MaxIter, float P0 = 0f, float P1 = 0f)
        {
            public double Scale => Width / W;
        }

        private static readonly View[] Views =
        {
            new("Whole set", FractalKind.Mandelbrot, -0.6, 0.0, 3.2, 2000),
            new("Period-3 bulb", FractalKind.Mandelbrot, -0.1225611668766536, 0.7448617666197442, 0.2, 2000),
            new("Period-3 minibrot", FractalKind.Mandelbrot, -1.7548776662466927, 0.0, 0.04, 4000),
            new("Seahorse valley", FractalKind.Mandelbrot, -0.7453, 0.1127, 0.01, 3000),
            new("Rabbit Julia", FractalKind.Julia, 0.0, 0.0, 3.2, 2000, -0.12256117f, 0.74486177f),
            new("Burning Ship", FractalKind.BurningShip, -0.45, -0.5, 3.0, 2000),
            new("Tricorn", FractalKind.Tricorn, -0.3, 0.0, 4.0, 2000),
            new("Multibrot d3", FractalKind.Multibrot, 0.0, 0.0, 3.0, 2000, 3f),
            new("Phoenix", FractalKind.Phoenix, 0.0, 0.0, 3.0, 2000, -0.5f),
        };

        private sealed class Out
        {
            public int[] It = new int[N];
            public float[] Sm = new float[N], Zr = new float[N], Zi = new float[N], Dr = new float[N], Di = new float[N], Trap = new float[N];
            public uint[] Col = new uint[N];
            public double Ms;
        }

        private enum Variant { Base, Colour, Orbit }

        public static int Run()
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"gpuperiodicityprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
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

            bool savedGpuOrbit = InterpretedOrbitColorMap.GpuEnabled;
            try
            {
                InterpretedOrbitColorMap.GpuEnabled = true;
                using var k = new MandelbrotGpuKernel(device, context, new object());
                Console.WriteLine($"gpuperiodicityprobe {W}x{H} on {adapter}");
                var colour = ColorPalette.BuiltIns.OfType<IGpuHlslPalette>().First();
                var orbit = InterpretedColorMap.TryCreate("return hsv(saturate(trapMin + stripeAvg * 0.3), 0.9, 1.0);", null, out _) as IGpuHlslPalette;
                Check(orbit != null, "orbit theme did not compile");

                Out Frame(View v, int mode, Variant variant = Variant.Base, int? cap = null)
                {
                    var o = new Out();
                    k.SetPalette(variant switch { Variant.Colour => colour, Variant.Orbit => orbit, _ => null });
                    GpuPeriodicity.Mode = mode;
                    try
                    {
                        k.Run(W, H, v.Cx, v.Cy, v.Scale, cap ?? v.MaxIter, 4.0, o.It, o.Sm, o.Zr, o.Zi, o.Dr, o.Di,
                              null, v.Kind, v.P0, v.P1,
                              colorDst: variant == Variant.Base ? null : o.Col,
                              trapDst: variant == Variant.Orbit ? o.Trap : null);
                    }
                    finally { GpuPeriodicity.Reset(); }
                    o.Ms = k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs;
                    return o;
                }

                static bool SameBits(float[] a, float[] b, Func<int, bool> where)
                {
                    for (int i = 0; i < a.Length; i++)
                        if (where(i) && BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i])) return false;
                    return true;
                }

                foreach (var v in Views)
                {
                    foreach (var variant in new[] { Variant.Base, Variant.Colour, Variant.Orbit })
                    {
                        if (variant == Variant.Orbit && v.Kind != FractalKind.Mandelbrot) continue;
                        var off = Frame(v, GpuPeriodicity.Off, variant);
                        var on = Frame(v, GpuPeriodicity.On, variant);
                        var diag = Frame(v, GpuPeriodicity.Diagnostic, variant);
                        string l = $"{v.Name} ({variant})";
                        Check(off.It.AsSpan().SequenceEqual(on.It), $"{l}: iteration differs");
                        Check(SameBits(off.Sm, on.Sm, _ => true), $"{l}: smooth differs");
                        Check(off.Col.AsSpan().SequenceEqual(on.Col), $"{l}: colour differs");
                        Check(SameBits(off.Trap, on.Trap, _ => true), $"{l}: trap differs");
                        bool Esc(int i) => off.It[i] < v.MaxIter;
                        Check(SameBits(off.Zr, on.Zr, Esc) && SameBits(off.Zi, on.Zi, Esc)
                              && SameBits(off.Dr, on.Dr, Esc) && SameBits(off.Di, on.Di, Esc), $"{l}: escaping final z / dz differs");
                        Check(off.It.AsSpan().SequenceEqual(diag.It), $"{l}: diagnostic mode changed an iteration");

                        var det = Enumerable.Range(0, N).Where(i => diag.Sm[i] < 0f).ToList();
                        int hard = Enumerable.Range(0, N).Count(i => off.It[i] >= v.MaxIter && !BulbSkipped(v, i));
                        Check(det.All(i => off.It[i] >= v.MaxIter && !BulbSkipped(v, i)), $"{l}: the check fired on a pixel not in set");
                        Check(hard > 500 && det.Count > hard / 3, $"{l}: the check fired on {det.Count} of {hard} in-set pixels");
                        if (variant == Variant.Base)
                            Console.WriteLine($"  {v.Name}: {hard} in set past the bulb skip, {det.Count} by the check");
                    }
                }

                foreach (var name in new[] { "Period-3 bulb", "Rabbit Julia", "Period-3 minibrot", "Seahorse valley", "Whole set" })
                {
                    var v = Views.First(x => x.Name == name);
                    foreach (int cap in new[] { 10_000, 100_000 })
                    {
                        Frame(v, GpuPeriodicity.On, cap: cap);
                        double tOff = Enumerable.Range(0, 2).Min(_ => Frame(v, GpuPeriodicity.Off, cap: cap).Ms);
                        double tOn = Enumerable.Range(0, 2).Min(_ => Frame(v, GpuPeriodicity.On, cap: cap).Ms);
                        Console.WriteLine($"  {name} cap {cap}: off {tOff:F1} ms, on {tOn:F1} ms ({tOff / tOn:F1}x)");
                    }
                }
                var cusp = new View("Cusp Julia", FractalKind.Julia, 0.0, 0.0, 1.0, 10_000, 0.2501f, 0f);
                Frame(cusp, GpuPeriodicity.On);
                double cOff = Enumerable.Range(0, 3).Min(_ => Frame(cusp, GpuPeriodicity.Off).Ms);
                double cOn = Enumerable.Range(0, 3).Min(_ => Frame(cusp, GpuPeriodicity.On).Ms);
                Console.WriteLine($"  Cusp Julia (all escape): off {cOff:F2} ms, on {cOn:F2} ms ({(cOn / cOff - 1) * 100:+0;-0}%)");

                // Exterior-heavy 1080p frames, as the app renders them.
                const int BW = 1920, BH = 1080, BN = BW * BH;
                var bit = new int[BN]; var bsm = new float[BN];
                var bzr = new float[BN]; var bzi = new float[BN]; var bdr = new float[BN]; var bdi = new float[BN];
                k.SetPalette(null);
                foreach (var (name, cx, cy, width, cap) in new[]
                {
                    ("Seahorse 1e4, 2048 iter, 1080p", -0.743643887037151, 0.131825904205330, 3.5 / 1e4, 2048),
                    ("Seahorse 1e3, 5000 iter, 1080p", -0.7453, 0.1127, 3.5e-3, 5000),
                    ("Whole set, 512 iter, 1080p", -0.5, 0.0, 3.5, 512),
                })
                {
                    double T(int mode)
                    {
                        GpuPeriodicity.Mode = mode;
                        try { k.Run(BW, BH, cx, cy, width / BW, cap, 4.0, bit, bsm, bzr, bzi, bdr, bdi); }
                        finally { GpuPeriodicity.Reset(); }
                        return k.LastGpuStageTimes?.DispatchMs ?? k.LastDispatchMs;
                    }
                    T(GpuPeriodicity.Off); T(GpuPeriodicity.On);
                    double bOff = Enumerable.Range(0, 3).Min(_ => T(GpuPeriodicity.Off));
                    double bOn = Enumerable.Range(0, 3).Min(_ => T(GpuPeriodicity.On));
                    Console.WriteLine($"  {name}: off {bOff:F1} ms, on {bOn:F1} ms ({(bOn / bOff - 1) * 100:+0;-0}%)");
                }
            }
            finally
            {
                InterpretedOrbitColorMap.GpuEnabled = savedGpuOrbit;
                context.Dispose();
                device.Dispose();
            }
            Console.WriteLine(failures == 0 ? $"gpuperiodicityprobe OK: {adapter}" : $"gpuperiodicityprobe FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        // The kernel's closed-form cardioid / period-2 bulb skip (CPU double form).
        private static bool BulbSkipped(View v, int i)
        {
            if (v.Kind != FractalKind.Mandelbrot) return false;
            double cx = v.Cx + (i % W - 0.5 * W) * v.Scale, cy = v.Cy + (i / W - 0.5 * H) * v.Scale;
            double bx = cx + 1.0;
            if (bx * bx + cy * cy <= 0.0625) return true;
            double xm = cx - 0.25, q = xm * xm + cy * cy;
            return q * (q + xm) <= 0.25 * cy * cy;
        }
    }
}
