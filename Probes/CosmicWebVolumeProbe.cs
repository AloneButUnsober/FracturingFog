// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2024: --cwvolprobe [W H] [grids...]. The Cosmic Web volume raymarch on every backend (D3D11 on the
// hardware device and on WARP, Vulkan) against the CPU reference, on the "Zeldovich Web Forming"
// particles (128^3 = 2.1M) at each density grid size (default 128 and 256): the CIC deposit + map
// time, the mass check (grid total = particles x MassOne exactly), and per backend the first frame
// (grid upload + march) and a second frame (march only), and the parity against the CPU: pixels
// with any channel more than 2 units off, and the largest channel difference. Exits 1 when the mass
// check fails or a backend has more than 0.5% of pixels more than 2 units off. The cost table in the
// #2024 PR comes from this probe.

using System;
using System.Diagnostics;
using System.Linq;
using FracturingFog.Calculators.Gpu;
using FracturingFog.CosmicWeb;
using FracturingFog.Models;
using FracturingFog.Rendering;

namespace FracturingFog
{
    internal static class CosmicWebVolumeProbe
    {
        public const double MaxOffFraction = 0.005;
        public const int ChannelTolerance = 2;

        public static int Run(string[] args)
        {
            var nums = args.Skip(1).Select(a => int.TryParse(a, out int v) ? v : -1).ToArray();
            int w = nums.Length >= 2 && nums[0] > 0 ? nums[0] : 960;
            int h = nums.Length >= 2 && nums[1] > 0 ? nums[1] : 600;
            var grids = nums.Length > 2 ? nums.Skip(2).Where(v => v > 0).ToArray() : new[] { 128, 256 };
            var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == "Cosmic Web - Zeldovich Web Forming");
            var p = new FractalParameters();
            region.Params!.ApplyTo(p);
            p.CosmicWebRender = CosmicWebRender.Volume;
            var sw = Stopwatch.StartNew();
            var pts = CosmicWebCalculator.PointsFor(p);
            Console.WriteLine($"cwvolprobe {w}x{h}, {pts.Count:N0} particles ({sw.Elapsed.TotalMilliseconds:0} ms to generate)");
            var lut = VolumeRaymarch.BakeLut(new HsvPalette());
            var cam = CosmicWebCalculator.CameraFor(p, region.Zoom, w, h);
            int failures = 0;
            foreach (int g in grids)
            {
                int gs = DensityGrid.SanitiseGrid(g);
                sw.Restart();
                var mass = DensityGrid.DepositCic(pts, gs);
                double depositMs = sw.Elapsed.TotalMilliseconds;
                long sum = 0;
                foreach (long m in mass) sum += m;
                bool massOk = sum == (long)pts.Count * DensityGrid.MassOne;
                if (!massOk) failures++;
                sw.Restart();
                var grid = DensityGrid.Build(pts, gs, p.CosmicWebVolumeSmoothing);
                double buildMs = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"  grid {gs}^3: CIC deposit {depositMs:0} ms, deposit+smooth+map {buildMs:0} ms, mass {(massOk ? "exact" : $"WRONG ({sum} vs {(long)pts.Count * DensityGrid.MassOne})")}");
                var vp = CosmicWebCalculator.VolumeParamsFor(p, cam, gs, 0xFF000000u);
                sw.Restart();
                var cpu = VolumeRaymarch.Render(vp, grid.Value, lut);
                Console.WriteLine($"    CPU reference      {sw.Elapsed.TotalMilliseconds,8:0} ms");
                void Try(string name, Func<ICosmicWebVolumeKernel?> make)
                {
                    ICosmicWebVolumeKernel? k = null;
                    try { k = make(); } catch (Exception ex) { Console.WriteLine($"    {name,-18} SKIP: {ex.Message}"); return; }
                    if (k == null) { Console.WriteLine($"    {name,-18} SKIP: unavailable"); return; }
                    using (k)
                    {
                        var t = Stopwatch.StartNew();
                        var f1 = k.Render(vp, grid, lut);
                        double first = t.Elapsed.TotalMilliseconds;
                        t.Restart();
                        var f2 = k.Render(vp, grid, lut);
                        double second = t.Elapsed.TotalMilliseconds;
                        if (f1 == null || f2 == null) { Console.WriteLine($"    {name,-18} FAIL: {k.LastError}"); failures++; return; }
                        int off = 0, maxd = 0;
                        for (int i = 0; i < cpu.Colour.Length; i++)
                        {
                            uint a = cpu.Colour[i], b = f2.Colour[i];
                            int d = 0;
                            for (int s = 0; s < 24; s += 8) d = Math.Max(d, Math.Abs((int)((a >> s) & 0xFF) - (int)((b >> s) & 0xFF)));
                            maxd = Math.Max(maxd, d);
                            if (d > ChannelTolerance) off++;
                        }
                        double frac = (double)off / cpu.Colour.Length;
                        bool ok = frac <= MaxOffFraction;
                        if (!ok) failures++;
                        Console.WriteLine($"    {name,-18} first {first,7:0} ms, then {second,7:0} ms; {off} px >{ChannelTolerance} off ({frac:P3}), max {maxd} {(ok ? "" : "FAIL")}  [{k.DeviceName}]");
                    }
                }
                Try("D3D11 hardware", () => CosmicWebVolumeD3D11Kernel.TryCreateWithOwnDevice());
                Try("D3D11 WARP", () => CosmicWebVolumeD3D11Kernel.TryCreateWithOwnDevice(warp: true));
                Try("Vulkan", () => FracturingFog.Rendering.Vulkan.CosmicWebVolumeVulkanKernel.TryCreateWithOwnContext());
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
