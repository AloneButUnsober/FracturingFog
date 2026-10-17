// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1334: --gpubandprobe. Deep-zoom perturbation frames in TDR row bands, on the
// default D3D11 hardware adapter and the Vulkan device (each skipped when absent or
// without fp64). For 960×540 frames at maxIter 1000 / 3000 / 10000 it prints the
// band height and count, the host wall time and the GPU time (#1329 timestamps),
// and checks that the banded frame is byte-identical to the same frame dispatched
// as one band (no band boundaries at all: the independent anchor for "every row
// is computed once, from the right params"). Banding never changes a pixel, so
// running this on two builds compares their speed on identical work.
// Exit 0 = pass or SKIP; 1 = a banded frame differs from the one-band frame.

using System;
using System.Diagnostics;
using System.Reflection;
using FracturingFog.Interefaces;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class GpuBandProbe
    {
        private const int W = 960, H = 540, N = W * H;

        public static int Run()
        {
            // Speed is measured, not guarded: no first-band abort to the CPU on a weak-fp64 card.
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");
            int failures = 0;

            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
                Console.WriteLine($"gpubandprobe D3D11 SKIP: no hardware device (0x{hr.Code:X8}).");
            else
            {
                try
                {
                    using var k = new MandelbrotGpuKernel(device, context, new object());
                    failures += Backend("D3D11", k, typeof(MandelbrotGpuKernel).Assembly,
                        () => k.GroupShape(GpuGroupShapes.SlotPerturb).Y);
                }
                finally { context.Dispose(); device.Dispose(); }
            }

            VulkanComputeKernel? vk = null;
            try { vk = VulkanComputeKernel.TryCreateWithOwnContext(); } catch { }
            if (vk == null) Console.WriteLine("gpubandprobe Vulkan SKIP: no device.");
            else using (vk) failures += Backend("Vulkan", vk, typeof(VulkanComputeKernel).Assembly,
                () => vk.GroupShape(GpuGroupShapes.SlotPerturb).Y);

            Console.WriteLine(failures == 0 ? "gpubandprobe OK" : $"gpubandprobe FAIL: {failures} frame(s) differ");
            return failures == 0 ? 0 : 1;
        }

        private static int Backend(string name, IGpuKernel k, Assembly asm, Func<int> groupRows)
        {
            if (!k.SupportsPerturbation) { Console.WriteLine($"gpubandprobe {name} SKIP: no fp64 perturbation."); return 0; }
            Console.WriteLine($"gpubandprobe {name}: {(k as VulkanComputeKernel)?.Description ?? name}");
            // MandelbrotKernelSource is linked into both backend assemblies; each bands with its own copy.
            var src = asm.GetType("FracturingFog.Rendering.MandelbrotKernelSource", throwOnError: true)!;
            var bandRows = src.GetMethod("PerturbBandRows")!;
            var overrideField = src.GetField("PerturbBandRowsOverride")!;

            var banded = new Frame(); var whole = new Frame();
            int failures = 0;
            foreach (int maxIter in new[] { 1000, 3000, 10000 })
            {
                var (zr, zi, len) = Orbit(-0.743643887037151, 0.13182590420533, maxIter);
                double scale = 3.5 / (W * 1e5);
                void Render(Frame f) => k.RunPerturb(W, H, scale, maxIter, 512.0 * 512.0, -W / 2, -H / 2, zr, zi, len,
                    f.It, f.Sm, f.Zr, f.Zi, f.Dr, f.Di);

                Render(banded);   // resolves the perturbation shape (#1339) before the band count is read
                int rows = (int)bandRows.Invoke(null, new object[] { W, H, maxIter, groupRows() })!;
                int bands = (H + rows - 1) / rows;
                double best = double.MaxValue; GpuStageTimes? gpu = null;
                for (int r = 0; r < 3; r++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    Render(banded);
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    if (ms < best) { best = ms; gpu = k.LastGpuStageTimes; }
                }

                int saved = (int)overrideField.GetValue(null)!;
                overrideField.SetValue(null, 1 << 20);   // rounds down to whole groups, then clamps to the height: one band
                try { Render(whole); }
                finally { overrideField.SetValue(null, saved); }

                bool same = banded.SameAs(whole, out string where);
                if (!same) failures++;
                Console.WriteLine($"  maxIter {maxIter,5}: {bands,3} band(s) of {rows,3} rows, wall {best,7:F1} ms, " +
                    $"GPU {(gpu is { } g ? $"{g.TotalMs,7:F1} ms in {g.Packets} packets" : "n/a")}, " +
                    $"frame hash {banded.Hash():X16}, {(same ? "== one band" : "DIFFERS from one band at " + where)}");
            }
            return failures;
        }

        private sealed class Frame
        {
            public readonly int[] It = new int[N];
            public readonly float[] Sm = new float[N], Zr = new float[N], Zi = new float[N], Dr = new float[N], Di = new float[N];

            public bool SameAs(Frame o, out string where)
            {
                for (int i = 0; i < N; i++)
                {
                    if (It[i] != o.It[i] || Bits(Sm[i]) != Bits(o.Sm[i]) || Bits(Zr[i]) != Bits(o.Zr[i])
                        || Bits(Zi[i]) != Bits(o.Zi[i]) || Bits(Dr[i]) != Bits(o.Dr[i]) || Bits(Di[i]) != Bits(o.Di[i]))
                    {
                        where = $"pixel ({i % W}, {i / W})";
                        return false;
                    }
                }
                where = "";
                return true;
            }

            public ulong Hash()
            {
                ulong h = 14695981039346656037UL;
                void Mix(int v) { h ^= (uint)v; h *= 1099511628211UL; }
                for (int i = 0; i < N; i++)
                {
                    Mix(It[i]); Mix(Bits(Sm[i])); Mix(Bits(Zr[i])); Mix(Bits(Zi[i])); Mix(Bits(Dr[i])); Mix(Bits(Di[i]));
                }
                return h;
            }

            private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
        }

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
