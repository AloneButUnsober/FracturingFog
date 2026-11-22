// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1972: --bhgpuprobe [W H] [eyebrow]. Every black hole trace backend against the CPU trace
// (double) on the default binary view (or the x60 eyebrow view): ILGPU (CUDA / OpenCL), D3D11
// on the hardware device and on WARP, and Vulkan, each in double and single precision. Prints the
// time per frame, pixels whose fate differs from the CPU split into interior / edge pixels, and
// exits 1 when a double kernel differs on more than 1 in 10,000 interior pixels (the parity the S1972 tests check for
// Vulkan; the test project cannot load the Windows-only D3D11 backend). The benchmark table in the
// #1972 PR comes from this probe.

using System;
using System.Diagnostics;
using System.Threading;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Scattering;

namespace FracturingFog
{
    internal static class BlackHoleGpuProbe
    {
        public static int Run(string[] args)
        {
            int w = args.Length > 2 && int.TryParse(args[1], out int aw) ? aw : 480;
            int h = args.Length > 2 && int.TryParse(args[2], out int ah) ? ah : 270;
            bool eyebrow = Array.Exists(args, a => a == "eyebrow");
            double zoom = BlackHoleShadowCalculator.DefaultZoom, cx = 0, cy = 0;
            var p = new FractalParameters();
            if (eyebrow)
            {
                // The built-in "Eyebrow Hierarchy" preset: its settings and view.
                var r = System.Linq.Enumerable.Single(FractalRegionLibrary.Instance.BuiltIns, x => x.Name == "Black Hole Shadow - Eyebrow Hierarchy");
                r.Params!.ApplyTo(p);
                cx = r.CenterX; cy = r.CenterY; zoom = r.Zoom;
            }
            var scene = BlackHoleShadowCalculator.Scene.Build(p, zoom, BlackHoleShadowCalculator.PixelPitch(w, h, zoom));
            var gp = BlackHoleShadowGpuCalculator.ParamsFor(scene, w, h, cx, cy, zoom);
            Console.WriteLine($"bhgpuprobe {w}x{h} {(eyebrow ? "Eyebrow Hierarchy preset" : "default view")}");

            // CPU reference (12 threads, double).
            var fates = new sbyte[w * h];
            var sw = Stopwatch.StartNew();
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    var (u, v) = BlackHoleShadowCalculator.PlaneAt(x + 0.5, y + 0.5, w, h, cx, cy, zoom);
                    var (o, _) = scene.Trace(u, v, default, channels: false);
                    fates[y * w + x] = (sbyte)(o.Fate == ScatterFate.Captured ? o.Body : o.Fate == ScatterFate.Escaped ? scene.EscapedGate : -1);
                }
            });
            Console.WriteLine($"  CPU fp64           {sw.Elapsed.TotalMilliseconds,9:0} ms");

            int failures = 0;
            void Compare(string name, BlackHoleShadowGpuCalculator.Outcomes? o, double ms, string err, bool dbl)
            {
                if (o == null) { Console.WriteLine($"  {name,-18} SKIP: {err}"); return; }
                int interior = 0, edgeFlips = 0, edges = 0;
                for (int i = 0; i < w * h; i++)
                {
                    bool edge = false;
                    int x = i % w, y = i / w;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < w && ny < h && fates[ny * w + nx] != fates[i]) edge = true;
                    }
                    if (edge) edges++;
                    if (o.Gate(i) == fates[i]) continue;
                    if (edge) edgeFlips++; else interior++;
                }
                Console.WriteLine($"  {name,-18} {ms,9:0} ms   interior diffs {interior,6}   edge flips {edgeFlips,6} of {edges}");
                // Double kernels: at most 1 in 10,000 interior pixels. The default view and 1080p match
                // the CPU exactly; on the Eyebrow Hierarchy preset (fates chaotic below the pixel) ulp-level
                // differences (Newton divide / sqrt, compiler FMA contraction) flip ~0.004% of pixels whose
                // neighbours happen to share a fate, on WARP too, so it is the arithmetic, not the GPU.
                if (dbl && interior > w * h / 10000) { failures++; Console.Error.WriteLine($"  FAIL {name}: {interior} interior pixels differ"); }
            }

            foreach (bool single in new[] { false, true })
            {
                string tag = single ? "f32" : "f64";
                var il = new BlackHoleShadowGpuCalculator();
                sw.Restart();
                var oi = il.Render(gp, CancellationToken.None, single);
                Compare($"ILGPU {tag}", oi, sw.Elapsed.TotalMilliseconds, il.LastError, !single);

                using (var d3d = BlackHoleD3D11Kernel.TryCreateWithOwnDevice())
                {
                    if (d3d == null) Console.WriteLine($"  D3D11 {tag,-12} SKIP: no hardware device");
                    else
                    {
                        sw.Restart();
                        var od = d3d.Trace(gp, single);
                        Compare($"D3D11 {tag}", od, sw.Elapsed.TotalMilliseconds, d3d.LastError, !single);
                        if (!single) Console.WriteLine($"      device: {d3d.DeviceName}");
                    }
                }
                if (w * h <= 200_000)
                    using (var warp = BlackHoleD3D11Kernel.TryCreateWithOwnDevice(warp: true))
                    {
                        if (warp != null)
                        {
                            sw.Restart();
                            var ow = warp.Trace(gp, single);
                            Compare($"D3D11 WARP {tag}", ow, sw.Elapsed.TotalMilliseconds, warp.LastError, !single);
                        }
                    }
                using (var vk = FracturingFog.Rendering.Vulkan.BlackHoleVulkanKernel.TryCreateWithOwnContext())
                {
                    if (vk == null) Console.WriteLine($"  Vulkan {tag,-11} SKIP: no Vulkan device");
                    else
                    {
                        sw.Restart();
                        var ov = vk.Trace(gp, single);
                        Compare($"Vulkan {tag}", ov, sw.Elapsed.TotalMilliseconds, vk.LastError, !single);
                        if (!single) Console.WriteLine($"      device: {vk.DeviceName}");
                    }
                }
            }
            Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
            return failures == 0 ? 0 : 1;
        }
    }
}
