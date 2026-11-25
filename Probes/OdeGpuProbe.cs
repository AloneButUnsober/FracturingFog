// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2049: --odegpuprobe [W H] [family] [preset...]. The GPU ODE template on each ported family
// (henonheiles, threebody, spinningbinary; default: all) against the CPU trace, on each named preset (default:
// every built-in preset of the family that integrates an ODE): D3D11 on the hardware device and
// on WARP, and Vulkan, in double and single precision. Prints the time per frame and the pixels
// whose outcome differs from the CPU, split into interior pixels (all 8 CPU neighbours share the
// outcome) and edge pixels, and the floor any trace that is not bit-identical meets: the CPU
// against itself with its tolerance moved by one part in 10^12. For Hénon-Heiles also the solid
// blocks (flat exit colours): filled pixels, pixels off the full flat render, both times.
// Exits 1 when a double kernel differs on more interior pixels than max(1 in 10,000 (the #1972
// standard), twice that floor) — the rule accepted in #2054. The tables in the #2049 PRs come
// from this probe.

using System;
using System.Collections.Generic;
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

        /// <summary>One family's view of a preset: the GPU item rule, the CPU outcome, the job and the GPU outcome.</summary>
        private sealed class Adapter
        {
            public required Func<double, double, double[], bool> Launch;   // (a, b, state) -> integrate on the GPU?
            public required Func<double, double, int> CpuGate;             // CPU outcome at (a, b)
            public required Func<double, double, int> CpuGatePerturbed;    // same, tolerance x (1 + 1e-12)
            public required Func<bool, OdeJobTemplate> Job;
            public required Func<OdeResult, int, int> GpuGate;
            public required Func<int, int, double, double, double, (double, double)> PlaneAt;
        }

        private static int s_w, s_h;

        private static Adapter? AdapterFor(FractalRegion region)
        {
            var p = new FractalParameters();
            region.Params!.ApplyTo(p);
            var p2 = new FractalParameters();
            region.Params!.ApplyTo(p2);
            switch (region.FractalType)
            {
                case FractalType.HenonHeilesBasins:
                {
                    var scene = HenonHeilesScene.Build(p);
                    p2.HenonHeilesTolerance *= 1.0 + 1e-12;
                    var scene2 = HenonHeilesScene.Build(p2);
                    return new Adapter
                    {
                        Launch = (a, b, s) => scene.LaunchState(a, b, s) && s[0] * s[0] + s[1] * s[1] <= 4.0,
                        CpuGate = (a, b) => { Span<double> s = stackalloc double[4]; return scene.Trace(a, b, s).Exit; },
                        CpuGatePerturbed = (a, b) => { Span<double> s = stackalloc double[4]; return scene2.Trace(a, b, s).Exit; },
                        Job = single => new OdeJobTemplate(HenonHeilesGpu.Family, new[] { scene.TimeCap }, scene.Settings.RelTol,
                            scene.Settings.AbsTol, scene.Settings.InitialStep, scene.Settings.MaxSteps, single),
                        GpuGate = (r, i) => HenonHeilesGpu.OutcomeOf(r, i).Exit,
                        PlaneAt = (x, y, cx, cy, z) => HenonHeilesCalculator.PlaneAt(x + 0.5, y + 0.5, s_w, s_h, cx, cy, z),
                    };
                }
                case FractalType.ThreeBodyBasins:
                {
                    if (p.ThreeBodyView == ThreeBodyView.LagrangeBasins) return null;   // a Newton map, CPU only
                    var scene = ThreeBodyScene.Build(p);
                    p2.ThreeBodyTolerance *= 1.0 + 1e-12;
                    var scene2 = ThreeBodyScene.Build(p2);
                    double e2 = scene.EscapeRadius * scene.EscapeRadius;
                    return new Adapter
                    {
                        Launch = (a, b, s) => scene.LaunchState(a, b, s) && a * a + b * b <= e2,
                        CpuGate = (a, b) => { Span<double> s = stackalloc double[4]; return scene.Trace(a, b, s).Gate; },
                        CpuGatePerturbed = (a, b) => { Span<double> s = stackalloc double[4]; return scene2.Trace(a, b, s).Gate; },
                        Job = single => ThreeBodyGpu.Job(scene, single),
                        GpuGate = (r, i) => ThreeBodyGpu.OutcomeOf(r, i).Gate,
                        PlaneAt = (x, y, cx, cy, z) => ThreeBodyCalculator.PlaneAt(x + 0.5, y + 0.5, s_w, s_h, cx, cy, z),
                    };
                }
                case FractalType.SpinningBinaryBasins:
                {
                    var scene = SpinningBinaryScene.Build(p);
                    p2.SpinBinTolerance *= 1.0 + 1e-12;
                    var scene2 = SpinningBinaryScene.Build(p2);
                    return new Adapter
                    {
                        Launch = (a, b, s) => { scene.LaunchState(a, b, s); return true; },
                        CpuGate = (a, b) => { Span<double> s = stackalloc double[SpinningBinarySystem.StateSize]; return scene.Trace(a, b, s).Gate; },
                        CpuGatePerturbed = (a, b) => { Span<double> s = stackalloc double[SpinningBinarySystem.StateSize]; return scene2.Trace(a, b, s).Gate; },
                        Job = single => SpinningBinaryGpu.Job(scene, single),
                        GpuGate = (r, i) => SpinningBinaryGpu.OutcomeOf(scene, r, i).Gate,
                        PlaneAt = (x, y, cx, cy, z) => SpinningBinaryCalculator.PlaneAt(x + 0.5, y + 0.5, s_w, s_h, cx, cy, z),
                    };
                }
                default:
                    return null;
            }
        }

        public static int Run(string[] args)
        {
            var nums = args.Skip(1).Select(a => int.TryParse(a, out int v) ? v : -1).ToArray();
            int w = s_w = nums.Length >= 2 && nums[0] > 0 ? nums[0] : 480;
            int h = s_h = nums.Length >= 2 && nums[1] > 0 ? nums[1] : 300;
            var words = args.Skip(1).Where(a => !int.TryParse(a, out _)).ToArray();
            var families = new Dictionary<string, FractalType>
            {
                ["henonheiles"] = FractalType.HenonHeilesBasins, ["threebody"] = FractalType.ThreeBodyBasins,
                ["spinningbinary"] = FractalType.SpinningBinaryBasins,
            };
            var wantTypes = words.Where(families.ContainsKey).Select(x => families[x]).ToHashSet();
            var names = words.Where(x => !families.ContainsKey(x)).ToArray();
            if (wantTypes.Count == 0) wantTypes = families.Values.ToHashSet();
            var regions = FractalRegionLibrary.Instance.BuiltIns.Where(r => wantTypes.Contains(r.FractalType)
                && (names.Length == 0 || names.Contains(r.Name))).ToArray();
            int failures = 0;
            Console.WriteLine($"odegpuprobe {w}x{h}");
            var state = new double[16];   // launch-state scratch, shared by every preset
            foreach (var region in regions)
            {
                var ad = AdapterFor(region);
                if (ad == null) { Console.WriteLine($"  {region.Name}: not an ODE view (skipped)"); continue; }
                (double, double) View(int idx) => ad.PlaneAt(idx % w, idx / w, region.CenterX, region.CenterY, region.Zoom);

                var cpu = new int[w * h];
                var items = new List<int>();
                for (int i = 0; i < w * h; i++)
                {
                    var (a, b) = View(i);
                    if (ad.Launch(a, b, state)) items.Add(i); else cpu[i] = -2;
                }
                var sw = Stopwatch.StartNew();
                Parallel.For(0, items.Count, k => { var (a, b) = View(items[k]); cpu[items[k]] = ad.CpuGate(a, b); });
                double cpuMs = sw.Elapsed.TotalMilliseconds;
                bool Interior(int i)
                {
                    int x = i % w, y = i / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h || cpu[yy * w + xx] != cpu[i]) return false;
                        }
                    return true;
                }
                int interiorCount = items.Count(Interior);
                Console.WriteLine($"  {region.Name}: {items.Count:N0} traced ({interiorCount:N0} interior)");
                Console.WriteLine($"    CPU fp64 ({Environment.ProcessorCount} threads)   {cpuMs,9:0} ms");
                int selfIn = 0, selfEdge = 0;
                object gate = new();
                Parallel.For(0, items.Count, k =>
                {
                    var (a, b) = View(items[k]);
                    if (ad.CpuGatePerturbed(a, b) == cpu[items[k]]) return;
                    lock (gate) { if (Interior(items[k])) selfIn++; else selfEdge++; }
                });
                Console.WriteLine($"    CPU, tolerance x(1+1e-12)              differs: {selfIn} interior ({(double)selfIn / Math.Max(1, interiorCount):P3}), {selfEdge} edge   [sensitivity floor]");
                int allowed = Math.Max((int)Math.Floor(MaxInteriorShare * interiorCount), 2 * selfIn);

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
                            var t = ad.Job(single);
                            OdeJob JobFor(IReadOnlyList<int> list) => new()
                            {
                                Family = t.Family, FamilyParams = t.FamilyParams, RelTol = t.RelTol, AbsTol = t.AbsTol,
                                InitialStep = t.InitialStep, MaxSteps = t.MaxSteps, Count = list.Count, Single = single,
                                Launches = (first, n, dst) =>
                                {
                                    int dim = t.Family.Dimension;
                                    var s = new double[16];
                                    for (int i = 0; i < n; i++)
                                    {
                                        var (a, b) = View(list[first + i]);
                                        ad.Launch(a, b, s);
                                        Array.Copy(s, 0, dst, i * dim, dim);
                                    }
                                },
                            };
                            k.Trace(JobFor(items.Take(Math.Min(items.Count, 64)).ToList()));   // warm (compile)
                            var tw = Stopwatch.StartNew();
                            var r = k.Trace(JobFor(items));
                            double ms = tw.Elapsed.TotalMilliseconds;
                            if (r == null) { Console.WriteLine($"    {label,-26} FAIL: {k.LastError}"); if (!single) failures++; continue; }
                            int inDiff = 0, edgeDiff = 0;
                            for (int q = 0; q < items.Count; q++)
                            {
                                if (ad.GpuGate(r, q) == cpu[items[q]]) continue;
                                if (Interior(items[q])) inDiff++; else edgeDiff++;
                            }
                            double share = interiorCount > 0 ? (double)inDiff / interiorCount : 0;
                            bool fail = !single && inDiff > allowed;
                            if (fail) failures++;
                            Console.WriteLine($"    {label,-26} {ms,9:0} ms ({cpuMs / Math.Max(1e-9, ms):0.0}x); differs: {inDiff} interior ({share:P3}), {edgeDiff} edge {(fail ? "FAIL" : "")}  [{k.DeviceName}]");
                        }
                    }
                }
                Try("D3D11 hardware", () => OdeD3D11Kernel.TryCreateWithOwnDevice());
                Try("D3D11 WARP", () => OdeD3D11Kernel.TryCreateWithOwnDevice(warp: true));
                Try("Vulkan", () => FracturingFog.Rendering.Vulkan.OdeVulkanKernel.TryCreateWithOwnContext());

                if (region.FractalType == FractalType.HenonHeilesBasins)
                {
                    var p = new FractalParameters();
                    region.Params!.ApplyTo(p);
                    p.HenonHeilesShadeExits = false;
                    var full = RenderHh(p, region, w, h, false, out double fullMs, out _);
                    var blocks = RenderHh(p, region, w, h, true, out double blockMs, out long filled);
                    int diff = 0;
                    for (int i = 0; i < full.Length; i++) if (full[i] != blocks[i]) diff++;
                    Console.WriteLine($"    solid blocks (flat)        {blockMs,9:0} ms vs full {fullMs:0} ms ({fullMs / Math.Max(1e-9, blockMs):0.0}x); filled {filled:N0} px ({(double)filled / (w * h):P1}); differ from full: {diff} px");
                }
            }
            return failures == 0 ? 0 : 1;
        }

        private static uint[] RenderHh(FractalParameters p, FractalRegion region, int w, int h, bool blocks, out double ms, out long filled)
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
