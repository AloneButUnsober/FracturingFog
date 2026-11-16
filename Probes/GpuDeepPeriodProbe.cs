// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1805: --gpudeepperiodprobe. The periodicity early-out of the GPU deep kernels (plain,
// SA + BLA, float-exp, scaled fp32) on the default D3D11 hardware adapter and the Vulkan
// device (each skipped when absent; the fp64 kernels also need fp64). Per kernel:
//   • the period-3 minibrot, its nucleus as a periodic reference (stops at Z_{p+1}), on a
//     480x320 frame 0.03 wide (interior, edge, exterior): off, on and diagnostic give the
//     same iterations, and the same smooth and final z / dz on escaping pixels; the
//     diagnostic's catches; the time off vs on (best of 3);
//   • the time off vs on on the seahorse at zoom 1e5 with its full-length reference (no
//     periodic wrap: the per-block check's cost on an exterior frame).
// Exit 0 = pass or SKIP; 1 = a frame differs.

using System;
using System.Diagnostics;
using System.Linq;
using FracturingFog.FFMath;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class GpuDeepPeriodProbe
    {
        private const int W = 480, H = 320, N = W * H, MaxIter = 5000;
        private const double EscapeR2 = 512.0 * 512.0;

        private enum Kernel { Plain, SaBla, FloatExp, ScaledF32 }

        public static int Run()
        {
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");
            int failures = 0;
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
                Console.WriteLine($"gpudeepperiodprobe D3D11 SKIP: no hardware device (0x{hr.Code:X8}).");
            else
            {
                try
                {
                    using var k = new MandelbrotGpuKernel(device, context, new object());
                    failures += Backend("D3D11", k, f => MandelbrotGpuKernel.ForceFloatExpEntry = f);
                }
                finally { context.Dispose(); device.Dispose(); }
            }
            VulkanComputeKernel? vk = null;
            try { vk = VulkanComputeKernel.TryCreateWithOwnContext(); } catch { }
            if (vk == null) Console.WriteLine("gpudeepperiodprobe Vulkan SKIP: no device.");
            else using (vk) failures += Backend("Vulkan", vk, f => VulkanComputeKernel.ForceFloatExpEntry = f);
            Console.WriteLine(failures == 0 ? "gpudeepperiodprobe OK" : $"gpudeepperiodprobe FAIL: {failures} frame(s) differ");
            return failures == 0 ? 0 : 1;
        }

        private sealed class Frame
        {
            public readonly int[] It = new int[N];
            public readonly float[] Sm = new float[N], Zr = new float[N], Zi = new float[N], Dr = new float[N], Di = new float[N];
            public double Ms;
        }

        private static (double[] Zr, double[] Zi, int Len) Orbit(double cx, double cy, int maxIter, int stopAt)
        {
            var zr = new double[maxIter + 1]; var zi = new double[maxIter + 1];
            double x = 0, y = 0; int len = Math.Min(maxIter, stopAt);
            for (int n = 0; n < maxIter; n++)
            {
                zr[n] = x; zi[n] = y;
                if (n >= stopAt) break;
                if (x * x + y * y >= EscapeR2) { len = n; break; }
                double nx = x * x - y * y + cx; y = 2 * x * y + cy; x = nx;
            }
            return (zr, zi, len);
        }

        private static Frame Render(IGpuKernel k, Kernel kind, int mode, (double[] Zr, double[] Zi, int Len) r,
            double scale, double offX0, double offY0, Action<bool> forceFx)
        {
            var f = new Frame();
            GpuPeriodicity.Mode = mode;
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                switch (kind)
                {
                    case Kernel.Plain:
                        k.RunPerturb(W, H, scale, MaxIter, EscapeR2, offX0, offY0, r.Zr, r.Zi, r.Len, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);
                        break;
                    case Kernel.SaBla:
                        var sa = new SeriesApproximation(r.Zr, r.Zi, r.Len);
                        var bla = new BlaTable(r.Zr, r.Zi, r.Len, Math.Sqrt(2.0) * 0.5 * W * scale);
                        k.RunPerturbSA(W, H, scale, MaxIter, EscapeR2, offX0, offY0, r.Zr, r.Zi, r.Len, 1e-3, sa.SafeMax,
                            sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI,
                            f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di, bla.GpuCoefficients, bla.Levels);
                        break;
                    default:
                        forceFx(kind == Kernel.FloatExp);
                        try { k.RunPerturbFloatExp(W, H, scale, MaxIter, EscapeR2, offX0, offY0, r.Zr, r.Zi, r.Len, f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di); }
                        finally { forceFx(false); }
                        break;
                }
                f.Ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            finally { GpuPeriodicity.Reset(); }
            return f;
        }

        private static int Bits(float x) => BitConverter.SingleToInt32Bits(x);

        private static int Backend(string name, IGpuKernel k, Action<bool> forceFx)
        {
            Console.WriteLine($"gpudeepperiodprobe {name}: {(k as VulkanComputeKernel)?.Description ?? name}");
            int failures = 0;
            var nucleus = Orbit(-1.7548776662466927, 0, MaxIter, stopAt: 3);   // Z_0..Z_3, refLen 4
            var seahorse = Orbit(-0.743643887037151, 0.13182590420533, MaxIter, stopAt: int.MaxValue);
            foreach (Kernel kind in Enum.GetValues<Kernel>())
            {
                if (kind is Kernel.Plain or Kernel.SaBla && !k.SupportsPerturbation) { Console.WriteLine($"  {kind}: SKIP (no fp64)"); continue; }
                try
                {
                    double sc = 0.03 / W;
                    var off = Render(k, kind, GpuPeriodicity.Off, nucleus, sc, -W / 2 + 60, -H / 2, forceFx);
                    var on = Render(k, kind, GpuPeriodicity.On, nucleus, sc, -W / 2 + 60, -H / 2, forceFx);
                    var diag = Render(k, kind, GpuPeriodicity.Diagnostic, nucleus, sc, -W / 2 + 60, -H / 2, forceFx);
                    string? diff = null;
                    for (int i = 0; i < N && diff == null; i++)
                    {
                        if (off.It[i] != on.It[i] || off.It[i] != diag.It[i]) diff = $"iteration at {i}";
                        else if (off.It[i] < MaxIter && (Bits(off.Sm[i]) != Bits(on.Sm[i]) || Bits(off.Sm[i]) != Bits(diag.Sm[i])
                            || Bits(off.Zr[i]) != Bits(on.Zr[i]) || Bits(off.Zi[i]) != Bits(on.Zi[i])
                            || Bits(off.Dr[i]) != Bits(on.Dr[i]) || Bits(off.Di[i]) != Bits(on.Di[i]))) diff = $"pixel {i}";
                    }
                    if (diff != null) failures++;
                    int inSet = off.It.Count(i => i >= MaxIter), caught = diag.Sm.Count(s => s < 0);
                    double tOff = Enumerable.Range(0, 3).Min(_ => Render(k, kind, GpuPeriodicity.Off, nucleus, sc, -W / 2 + 60, -H / 2, forceFx).Ms);
                    double tOn = Enumerable.Range(0, 3).Min(_ => Render(k, kind, GpuPeriodicity.On, nucleus, sc, -W / 2 + 60, -H / 2, forceFx).Ms);
                    double sc2 = 3.5 / (W * 1e5);
                    double eOff = Enumerable.Range(0, 3).Min(_ => Render(k, kind, GpuPeriodicity.Off, seahorse, sc2, -W / 2, -H / 2, forceFx).Ms);
                    double eOn = Enumerable.Range(0, 3).Min(_ => Render(k, kind, GpuPeriodicity.On, seahorse, sc2, -W / 2, -H / 2, forceFx).Ms);
                    Console.WriteLine($"  {kind,-10}: minibrot {inSet} in set, caught {caught}, off {tOff,7:F1} ms, on {tOn,7:F1} ms ({tOff / tOn,5:F2}x); " +
                                      $"seahorse 1e5 off {eOff,7:F1} ms, on {eOn,7:F1} ms ({eOn / eOff - 1,6:P1}); {(diff == null ? "off == on == diagnostic" : "DIFFERS: " + diff)}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  FAIL {kind}: {ex.GetType().Name}: {ex.Message}");
                    failures++;
                }
            }
            return failures;
        }
    }
}
