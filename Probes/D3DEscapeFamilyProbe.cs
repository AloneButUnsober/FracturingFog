// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-I (GPU parity plan G4.4): --d3dfamilyprobe. The D3D11 half of the
// Multibrot / Phoenix / domain-warp parity check; the Vulkan half is Server.Tests'
// S1173IGpuEscapeFamiliesTests (the test project is net10.0 and cannot load the
// D3D backend). Same cases, same checks: a real MandelbrotGpuKernel drives an
// EscapeTimeCalculator, and each GPU frame is compared with the CPU frame —
//   - the frame ran on the GPU with GPU colouring (LastGpuRoute);
//   - the escape iteration agrees on > 97% of pixels and the median |smooth| < 0.01;
//   - the mean colour drift is below max(4, 8x drift) of a contrast frame that
//     differs only in the feature under test (another power, p, or the warp off).
//
// Exit 0 = pass or SKIP (no D3D11 hardware device); 1 = parity failure.

using System;
using System.Linq;
using System.Numerics;
using FracturingFog.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FracturingFog
{
    internal static class D3DEscapeFamilyProbe
    {
        private const int W = 320, H = 240, MaxIter = 256;

        private static FractalParameters P(int d = 3, double pr = -0.5, double pi = 0.0,
                                           double warp = 0.0, double freq = 1.0, Complex? julia = null)
        {
            var p = new FractalParameters
            {
                MultibrotExponent = d, PhoenixP = new Complex(pr, pi),
                DomainWarpEnabled = warp != 0.0, DomainWarpStrength = warp, DomainWarpFrequency = freq,
            };
            if (julia is Complex jc) p.JuliaC = jc;
            return p;
        }

        private static readonly (string Label, FractalType Type, FractalParameters P, FractalParameters Contrast)[] Cases =
        {
            ("Multibrot d2",        FractalType.Multibrot, P(d: 2), P(d: 3)),
            ("Multibrot d3",        FractalType.Multibrot, P(d: 3), P(d: 4)),
            ("Multibrot d4",        FractalType.Multibrot, P(d: 4), P(d: 5)),
            ("Multibrot d5",        FractalType.Multibrot, P(d: 5), P(d: 4)),
            ("Multibrot d8",        FractalType.Multibrot, P(d: 8), P(d: 7)),
            ("Phoenix",             FractalType.Phoenix,   P(pr: -0.5), P(pr: -0.3)),
            ("Phoenix complex p",   FractalType.Phoenix,   P(pr: -0.4, pi: 0.1), P(pr: -0.4)),
            ("Julia + warp",        FractalType.Julia,     P(warp: 0.15, julia: new Complex(-0.8, 0.156)), P(julia: new Complex(-0.8, 0.156))),
            ("Multibrot d3 + warp", FractalType.Multibrot, P(d: 3, warp: 0.2, freq: 2.0), P(d: 3)),
            ("Phoenix + warp",      FractalType.Phoenix,   P(warp: 0.1, freq: 0.0), P()),
        };

        public static int Run()
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"d3dfamilyprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
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

            try
            {
                using var kernel = new FracturingFog.Rendering.MandelbrotGpuKernel(device, context, new object());
                Console.WriteLine($"d3dfamilyprobe {W}x{H} maxIter={MaxIter} on {adapterName}");
                int failures = 0;
                foreach (var c in Cases) failures += RunCase(kernel, c) ? 0 : 1;
                Console.WriteLine(failures == 0
                    ? $"d3dfamilyprobe OK: {adapterName}"
                    : $"d3dfamilyprobe FAIL: {failures}/{Cases.Length} case(s).");
                return failures == 0 ? 0 : 1;
            }
            finally
            {
                context.Dispose();
                device.Dispose();
            }
        }

        private static EscapeTimeCalculator Render(FractalType type, FractalParameters p,
            FracturingFog.Rendering.IGpuKernel? kernel)
        {
            var c = new EscapeTimeCalculator(W, H)
            {
                FractalType = type, FractalParameters = p,
                ColorMap = ColorPalette.BuiltIns.First(m => m is FracturingFog.Interefaces.IGpuHlslPalette),
                MaxIterations = MaxIter, CenterX = 0, CenterY = 0, Zoom = 1.0,
                UseGpuCompute = kernel != null, GpuKernel = kernel,
            };
            c.Calculate();
            return c;
        }

        private static double MeanDrift(uint[] a, uint[] b, int n)
        {
            long sum = 0;
            for (int i = 0; i < n; i++)
                for (int s = 0; s < 24; s += 8)
                    sum += Math.Abs((int)((a[i] >> s) & 0xFF) - (int)((b[i] >> s) & 0xFF));
            return sum / (n * 3.0);
        }

        private static bool RunCase(FracturingFog.Rendering.IGpuKernel kernel,
            (string Label, FractalType Type, FractalParameters P, FractalParameters Contrast) t)
        {
            var g = Render(t.Type, t.P, kernel);
            var c = Render(t.Type, t.P, null);
            var o = Render(t.Type, t.Contrast, null);
            int n = W * H;
            bool onGpu = g.LastGpuRoute.State == FracturingFog.Render.GpuRouteState.Gpu
                         && string.IsNullOrEmpty(g.LastGpuRoute.Detail);
            int same = 0;
            var d = new System.Collections.Generic.List<double>();
            for (int i = 0; i < n; i++)
            {
                if (g.IterationBuffer[i] != c.IterationBuffer[i]) continue;
                same++;
                if (c.IterationBuffer[i] < MaxIter) d.Add(Math.Abs(g.SmoothBuffer[i] - c.SmoothBuffer[i]));
            }
            d.Sort();
            double median = d.Count > 0 ? d[d.Count / 2] : double.NaN;
            double drift = MeanDrift(g.ColorBuffer, c.ColorBuffer, n);
            double contrast = MeanDrift(o.ColorBuffer, c.ColorBuffer, n);
            bool ok = onGpu && same > 0.97 * n && d.Count > n / 10 && median < 0.01
                      && contrast > Math.Max(4.0, 8 * drift);
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {t.Label,-20} route={g.LastGpuRoute.Reason} " +
                              $"iterAgree={same * 100.0 / n:F2}% median|smooth|={median:E1} drift={drift:F2} contrast={contrast:F2}");
            return ok;
        }
    }
}
