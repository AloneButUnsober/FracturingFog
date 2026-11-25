// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2049: --odegpuprobe [W H] [preset...]. The GPU ODE template on Hénon-Heiles against the CPU
// trace, on each named preset (default: every built-in Hénon-Heiles preset): D3D11 on the
// hardware device and on WARP, and Vulkan, in double and single precision. Prints the time per
// frame and the pixels whose exit differs from the CPU, split into interior pixels (all 8 CPU
// neighbours share the pixel's exit) and edge pixels. Then the solid blocks (flat exit colours):
// filled pixels, the pixels that differ from the full flat render, and both times.
// It also measures the floor any trace that is not bit-identical meets: the CPU against itself
// with its tolerance moved by one part in 10^12. Exits 1 when a double kernel differs on more
// interior pixels than max(1 in 10,000 (the #1972 standard), twice that floor): near the escape
// energy the CPU flips as many interior pixels against itself as the GPU does against it, so
// the fixed share alone is not reachable there. The tables in the #2049 PR come from this probe.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Scattering;

namespace FracturingFog
{
    internal static class OdeGpuProbe
    {
        public const double MaxInteriorShare = 1e-4;

        public static int Run(string[] args)
        {
            var nums = args.Skip(1).Select(a => int.TryParse(a, out int v) ? v : -1).ToArray();
            int w = nums.Length >= 2 && nums[0] > 0 ? nums[0] : 480;
            int h = nums.Length >= 2 && nums[1] > 0 ? nums[1] : 300;
            var names = args.Skip(1).Where(a => !int.TryParse(a, out _)).ToArray();
            var regions = FractalRegionLibrary.Instance.BuiltIns.Where(r => r.FractalType == FractalType.HenonHeilesBasins
                && (names.Length == 0 || names.Contains(r.Name))).ToArray();
            int failures = 0;
            Console.WriteLine($"odegpuprobe {w}x{h}");
            foreach (var region in regions)
            {
                var p = new FractalParameters();
                region.Params!.ApplyTo(p);
                var scene = HenonHeilesScene.Build(p);
                (double, double) View(int idx) => HenonHeilesCalculator.PlaneAt(idx % w + 0.5, idx / w + 0.5, w, h, region.CenterX, region.CenterY, region.Zoom);

                // CPU reference: exits per pixel (-2 = not traced: forbidden / launched outside).
                var cpu = new int[w * h];
                var items = new System.Collections.Generic.List<int>();
                Span<double> ls = stackalloc double[4];
                for (int i = 0; i < w * h; i++)
                {
                    var (a, b) = View(i);
                    if (scene.LaunchState(a, b, ls) && ls[0] * ls[0] + ls[1] * ls[1] <= 4.0) items.Add(i);
                    else cpu[i] = -2;
                }
                var sw = Stopwatch.StartNew();
                Parallel.For(0, items.Count, k =>
                {
                    Span<double> s = stackalloc double[4];
                    var (a, b) = View(items[k]);
                    cpu[items[k]] = scene.Trace(a, b, s).Exit;
                });
                double cpuMs = sw.Elapsed.TotalMilliseconds;
                bool Interior(int i)
                {
                    int x = i % w, y = i / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) return false;
                            if (cpu[yy * w + xx] != cpu[i]) return false;
                        }
                    return true;
                }
                int interiorCount = items.Count(Interior);
                int allowed = 0;
                Console.WriteLine($"  {region.Name}: {items.Count:N0} traced ({interiorCount:N0} interior)");
                Console.WriteLine($"    CPU fp64 ({Environment.ProcessorCount} threads)   {cpuMs,9:0} ms");
                // The floor any non-bit-identical trace meets: the CPU itself, with its tolerance moved by one
                // part in 10^12 (an ulp-sized change to every step size), against the CPU.
                {
                    var p2 = new FractalParameters();
                    region.Params!.ApplyTo(p2);
                    p2.HenonHeilesTolerance *= 1.0 + 1e-12;
                    var scene2 = HenonHeilesScene.Build(p2);
                    int selfIn = 0, selfEdge = 0;
                    object gate = new();
                    Parallel.For(0, items.Count, k =>
                    {
                        Span<double> s = stackalloc double[4];
                        var (a, b) = View(items[k]);
                        if (scene2.Trace(a, b, s).Exit == cpu[items[k]]) return;
                        lock (gate) { if (Interior(items[k])) selfIn++; else selfEdge++; }
                    });
                    Console.WriteLine($"    CPU, tolerance x(1+1e-12)              exit differs: {selfIn} interior ({(double)selfIn / Math.Max(1, interiorCount):P3}), {selfEdge} edge   [sensitivity floor]");
                    allowed = Math.Max((int)Math.Floor(MaxInteriorShare * interiorCount), 2 * selfIn);
                }

                void Try(string name, Func<IOdeTraceKernel?> make)
                {
                    IOdeTraceKernel? k;
                    try { k = make(); } catch (Exception ex) { Console.WriteLine($"    {name,-26} SKIP: {ex.Message}"); return; }
                    if (k == null) { Console.WriteLine($"    {name,-26} SKIP: unavailable"); return; }
                    using (k)
                    {
                        foreach (bool single in new[] { false, true })
                        {
                            string label = $"{name} {(single ? "fp32" : "fp64")}";
                            if (!single && !k.SupportsDouble) { Console.WriteLine($"    {label,-26} SKIP: no fp64"); continue; }
                            HenonHeilesGpu.Trace(k, scene, items.Take(Math.Min(items.Count, 64)).ToList(), View, single, default);   // warm (compile)
                            var t = Stopwatch.StartNew();
                            var o = HenonHeilesGpu.Trace(k, scene, items, View, single, default);
                            double ms = t.Elapsed.TotalMilliseconds;
                            if (o == null) { Console.WriteLine($"    {label,-26} FAIL: {k.LastError}"); if (!single) failures++; continue; }
                            int inDiff = 0, edgeDiff = 0;
                            for (int q = 0; q < items.Count; q++)
                            {
                                if (o[q].Exit == cpu[items[q]]) continue;
                                if (Interior(items[q])) inDiff++; else edgeDiff++;
                            }
                            double share = interiorCount > 0 ? (double)inDiff / interiorCount : 0;
                            bool fail = !single && inDiff > allowed;
                            if (fail) failures++;
                            Console.WriteLine($"    {label,-26} {ms,9:0} ms ({cpuMs / Math.Max(1e-9, ms):0.0}x); exit differs: {inDiff} interior ({share:P3}), {edgeDiff} edge {(fail ? "FAIL" : "")}  [{k.DeviceName}]");
                        }
                    }
                }
                Try("D3D11 hardware", () => OdeD3D11Kernel.TryCreateWithOwnDevice());
                Try("D3D11 WARP", () => OdeD3D11Kernel.TryCreateWithOwnDevice(warp: true));
                Try("Vulkan", () => FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext());

                // Solid blocks with flat colours (CPU) against the full flat render.
                p.HenonHeilesShadeExits = false;
                var full = Render(p, region, w, h, false, out double fullMs, out _);
                var blocks = Render(p, region, w, h, true, out double blockMs, out long filled);
                int diff = 0;
                for (int i = 0; i < full.Length; i++) if (full[i] != blocks[i]) diff++;
                Console.WriteLine($"    solid blocks (flat)        {blockMs,9:0} ms vs full {fullMs:0} ms ({fullMs / Math.Max(1e-9, blockMs):0.0}x); filled {filled:N0} px ({(double)filled / (w * h):P1}); differ from full: {diff} px");
            }
            return failures == 0 ? 0 : 1;
        }

        private static uint[] Render(FractalParameters p, FractalRegion region, int w, int h, bool blocks, out double ms, out long filled)
        {
            p.HenonHeilesSolidBlocks = blocks;
            var c = new HenonHeilesCalculator(w, h) { CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, ColorMap = new HsvPalette(), FractalParameters = p };
            var sw = Stopwatch.StartNew();
            c.Calculate();
            ms = sw.Elapsed.TotalMilliseconds;
            filled = c.LastSolidFilled;
            return c.ColorBuffer;
        }
    }
}
