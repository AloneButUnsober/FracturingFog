// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1329: --gputimestampprobe. The D3D11 check of the GPU timestamp queries
// (Server.Tests has no D3D reference; S1329GpuTimestampTests covers Vulkan). On
// the default hardware adapter, a real MandelbrotGpuKernel:
//   - an all-in-set frame (inside the period-3 bulb, which the shader's cardioid /
//     period-2 skip doesn't cover, so every pixel runs maxIter) reports
//     dispatch > 0, a copy-out stage (D3D11 always copies to staging), one
//     packet, no upload (the inputs are CPU-written dynamic buffers), and stages
//     summing to no more than the host's wall time around the frame;
//   - the dispatch time grows roughly linearly with maxIter (1000 / 2000 / 4000);
//   - a banded deep-zoom perturbation frame reports one packet per band plus the
//     copy-out packet (skipped without fp64 shader ops).
// Exit 0 = pass or SKIP (no D3D11 hardware device); 1 = failure.

using System;
using FracturingFog.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FracturingFog
{
    internal static class GpuTimestampProbe
    {
        public static int Run()
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"gputimestampprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
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

            // Correctness, not speed: no first-band abort on a weak-fp64 card (as --d3dpturbcalc).
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");
            try
            {
                using var k = new MandelbrotGpuKernel(device, context, new object());
                Console.WriteLine($"gputimestampprobe on {adapter}");
                const int W = 512, H = 512, N = W * H;
                var it = new int[N]; var sm = new float[N];
                var zr = new float[N]; var zi = new float[N]; var dr = new float[N]; var di = new float[N];
                GpuStageTimes? Frame(int maxIter, out double wall, out bool allInSet)
                {
                    k.Run(W, H, -0.1225611668766536, 0.7448617666197442, 0.02 / W, maxIter, 4.0, it, sm, zr, zi, dr, di);
                    wall = k.LastDispatchMs + k.LastReadbackMs;
                    allInSet = Array.TrueForAll(it, v => v == maxIter);
                    return k.LastGpuStageTimes;
                }

                Frame(500, out _, out _);   // warm
                var t = Frame(1000, out double wall1, out bool inSet);
                Check(inSet, "precondition: the view is not all in set");
                if (t is null)
                {
                    Console.WriteLine("gputimestampprobe SKIP: the device returned no timestamps.");
                    return 0;
                }
                Console.WriteLine($"  SP frame: {t}, host wall {wall1:F2} ms");
                Check(t.Value.DispatchMs > 0, "dispatch time is not positive");
                Check(t.Value.CopyOutMs > 0, "no copy-out stage (D3D11 copies to staging every frame)");
                Check(t.Value.UploadMs == 0, "an upload stage on D3D11 (inputs are CPU-written)");
                Check(t.Value.Packets == 1, $"{t.Value.Packets} packets for a single dispatch");
                Check(t.Value.TotalMs <= wall1, $"GPU stages {t.Value.TotalMs:F3} ms > host wall {wall1:F3} ms");

                double Best(int maxIter)
                {
                    double best = double.MaxValue;
                    for (int r = 0; r < 3; r++)
                    {
                        var s = Frame(maxIter, out double w, out _);
                        if (s is { } v) { best = Math.Min(best, v.DispatchMs); Check(v.TotalMs <= w, $"stages > wall at {maxIter}"); }
                    }
                    return best;
                }
                double d1 = Best(1000), d2 = Best(2000), d4 = Best(4000);
                Console.WriteLine($"  dispatch: 1000 -> {d1:F2} ms, 2000 -> {d2:F2} ms, 4000 -> {d4:F2} ms");
                Check(d2 / d1 is >= 1.6 and <= 2.4, $"2000/1000 dispatch ratio {d2 / d1:F2} not ~2");
                Check(d4 / d1 is >= 3.2 and <= 4.8, $"4000/1000 dispatch ratio {d4 / d1:F2} not ~4");

                if (k.SupportsPerturbation)
                {
                    const int PW = 128, PH = 384, MaxIter = 4000;
                    var (orZr, orZi, len) = Orbit(-0.1225611668766536, 0.7448617666197442, MaxIter);
                    int pn = PW * PH;
                    var pit = new int[pn]; var psm = new float[pn];
                    var pzr = new float[pn]; var pzi = new float[pn]; var pdr = new float[pn]; var pdi = new float[pn];
                    k.RunPerturb(PW, PH, 3.5 / (PW * 1e4), MaxIter, 512.0 * 512.0, -PW / 2, -PH / 2, orZr, orZi, len,
                        pit, psm, pzr, pzi, pdr, pdi);
                    int bandRows = BandRows(PW, PH, MaxIter);
                    int bands = (PH + bandRows - 1) / bandRows;
                    var pt = k.LastGpuStageTimes;
                    double pwall = k.LastDispatchMs + k.LastReadbackMs;
                    Console.WriteLine($"  perturbation: {pt?.ToString() ?? "null"}, {bands} band(s), host wall {pwall:F2} ms");
                    Check(pt is { } p1 && p1.Packets == bands + 1, $"packets {pt?.Packets} != {bands} bands + copy-out");
                    Check(pt is { } p2 && p2.TotalMs <= pwall, "perturbation stages > host wall");
                }
                else Console.WriteLine("  perturbation: skipped (no fp64 shader ops)");
            }
            finally
            {
                context.Dispose();
                device.Dispose();
            }
            Console.WriteLine(failures == 0 ? $"gputimestampprobe OK: {adapter}" : $"gputimestampprobe FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        // MandelbrotKernelSource is linked into both the D3D and the Vulkan assembly;
        // the D3D kernel bands with the D3D assembly's copy.
        private static int BandRows(int w, int h, int maxIter) =>
            (int)typeof(MandelbrotGpuKernel).Assembly.GetType("FracturingFog.Rendering.MandelbrotKernelSource", throwOnError: true)!
                .GetMethod("PerturbBandRows")!.Invoke(null, new object[] { w, h, maxIter })!;

        private static (double[] Zr, double[] Zi, int Len) Orbit(double cx, double cy, int maxIter)
        {
            var zr = new double[maxIter + 1]; var zi = new double[maxIter + 1];
            double x = 0, y = 0; int len = maxIter;
            for (int n = 0; n < maxIter; n++)
            {
                zr[n] = x; zi[n] = y;
                if (x * x + y * y >= 512.0 * 512.0) { len = n; break; }
                double nx = x * x - y * y + cx; y = 2 * x * y + cy; x = nx;
            }
            return (zr, zi, len);
        }
    }
}
