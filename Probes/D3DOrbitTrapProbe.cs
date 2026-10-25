// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-J (GPU parity plan G4.3): --d3dorbittrapprobe. The D3D11 half of the
// orbit-trap parity check; the Vulkan half is Server.Tests' S1173JGpuOrbitTrapTests
// (the test project is net10.0 and cannot load the D3D backend).
//
// It creates a headless D3D11 hardware device (as --d3dpturbcalc does), attaches a
// real MandelbrotGpuKernel to a MandelbrotCalculator running a trapMin orbit theme,
// and compares TrapBuffer with the CPU orbit path's. Checked:
//   - the GPU orbit kernel really ran (LastFrameUsedGpuCompute);
//   - every pixel was written (TrapBuffer is NaN-filled before the GPU frame);
//   - in-set pixels carry 0, escaped pixels lie in [0, |c|] (z_1 = c);
//   - the median / p95 |GPU - CPU| trap where both took the same escape iteration;
//   - a theme that does not read trapMin gives an all-zero trap, as on the CPU.
//
// Exit 0 = pass or SKIP (no D3D11 hardware device); 1 = parity failure.

using System;
using System.Linq;
using FracturingFog.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FracturingFog
{
    internal static class D3DOrbitTrapProbe
    {
        private const int W = 320, H = 240, MaxIter = 256;

        public static int Run()
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"d3dorbittrapprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
                return 0;
            }

            string adapterName = "D3D11 adapter";
            try
            {
                using var dxgi = device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgi.GetAdapter();
                adapterName = adapter.Description.Description;
            }
            catch { /* cosmetic */ }

            bool savedGpuOrbit = InterpretedOrbitColorMap.GpuEnabled;
            try
            {
                InterpretedOrbitColorMap.GpuEnabled = true;
                using var kernel = new FracturingFog.Rendering.MandelbrotGpuKernel(device, context, new object());
                Console.WriteLine($"d3dorbittrapprobe {W}x{H} maxIter={MaxIter} on {adapterName}");
                bool ok = TrapCase(kernel) & ZeroCase(kernel);
                Console.WriteLine(ok ? $"d3dorbittrapprobe OK: {adapterName}" : "d3dorbittrapprobe FAIL");
                return ok ? 0 : 1;
            }
            finally
            {
                InterpretedOrbitColorMap.GpuEnabled = savedGpuOrbit;
                context.Dispose();
                device.Dispose();
            }
        }

        private static InterpretedOrbitColorMap? Theme(string src)
            => InterpretedColorMap.TryCreate(src, null, out _) as InterpretedOrbitColorMap;

        private static (float[] Trap, int[] Iter, bool Gpu) Render(InterpretedOrbitColorMap map,
            FracturingFog.Rendering.IGpuKernel? kernel)
        {
            var c = new MandelbrotCalculator(W, H)
            {
                CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = MaxIter, ColorMap = map,
                UseGpuCompute = kernel != null, GpuKernel = kernel,
            };
            Array.Fill(c.TrapBuffer, float.NaN);
            c.Calculate();
            return ((float[])c.TrapBuffer.Clone(), (int[])c.IterationBuffer.Clone(), c.LastFrameUsedGpuCompute);
        }

        private static bool TrapCase(FracturingFog.Rendering.IGpuKernel kernel)
        {
            var theme = Theme("return hsv(saturate(trapMin), 0.9, 1.0);");
            if (theme == null) { Console.Error.WriteLine("  trapMin: theme did not compile"); return false; }
            var g = Render(theme, kernel);
            var c = Render(theme, null);
            if (!g.Gpu) { Console.Error.WriteLine("  trapMin: the GPU orbit kernel did not run"); return false; }

            int nan = 0, badInSet = 0, outOfRange = 0, escaped = 0;
            double scale = 3.5 / Math.Max(W, H);
            for (int i = 0; i < g.Trap.Length; i++)
            {
                float t = g.Trap[i];
                if (float.IsNaN(t)) { nan++; continue; }
                if (g.Iter[i] >= MaxIter) { if (t != 0f) badInSet++; continue; }
                escaped++;
                double cr = -0.5 + (i % W - 0.5 * W) * scale, ci = (i / W - 0.5 * H) * scale;
                if (t < 0f || t > Math.Sqrt(cr * cr + ci * ci) * (1 + 1e-5) + 1e-6) outOfRange++;
            }
            var d = Enumerable.Range(0, g.Trap.Length)
                .Where(i => g.Iter[i] == c.Iter[i] && g.Iter[i] < MaxIter && !float.IsNaN(g.Trap[i]))
                .Select(i => (double)Math.Abs(g.Trap[i] - c.Trap[i])).OrderBy(v => v).ToArray();
            double median = d.Length > 0 ? d[d.Length / 2] : double.NaN;
            double p95 = d.Length > 0 ? d[(int)(d.Length * 0.95)] : double.NaN;
            Console.WriteLine($"  trapMin: escaped={escaped} shared={d.Length} median|d|={median:E2} p95|d|={p95:E2} " +
                              $"nan={nan} inSet!=0={badInSet} outOfRange={outOfRange}");
            return nan == 0 && badInSet == 0 && outOfRange == 0 && escaped > g.Trap.Length / 4
                && d.Length > escaped / 2 && median < 1e-4 && p95 < 1e-2;
        }

        private static bool ZeroCase(FracturingFog.Rendering.IGpuKernel kernel)
        {
            var theme = Theme("return hsv(saturate(stripeAvg), 0.9, 1.0);");
            if (theme == null) { Console.Error.WriteLine("  stripeAvg: theme did not compile"); return false; }
            var g = Render(theme, kernel);
            int nonZero = g.Trap.Count(v => v != 0f);
            Console.WriteLine($"  stripeAvg (no trapMin): gpu={g.Gpu} nonZeroTrap={nonZero}");
            return g.Gpu && nonZero == 0;
        }
    }
}
