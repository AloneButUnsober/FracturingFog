// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1339: --gpugroupshapeprobe. The tuned compute thread-group shapes, on the
// default D3D11 hardware adapter and the Vulkan device (each skipped when absent).
// The store is redirected to a throwaway directory, so every slot is tuned afresh
// (and the real one is never touched). For each slot (SP escape time, fp64
// perturbation, fp32 perturbation; the perturbation slots only where supported):
//   - the tuning: every candidate's best time on the representative frame, the
//     winner and how long the one-time pause took;
//   - a full-size frame (SP 1920×1080 seahorse at 1000 iterations; perturbation
//     640×360 at 1e5, 2000 iterations) on a tuned kernel against an 8×8 one: the
//     best of 5 GPU dispatch times, and the outputs must be byte-identical (the
//     shape only changes which pixels share a warp, never a pixel).
// #1377 — then the relief raymarch and the two froxel passes, on each backend:
// the tuning (each candidate's best time on RaymarchGroupShapes' 256×192
// representative frame, the winner, the one-time pause), and a 1280×720 frame
// of the same scene (shadows, AO, specular, froxel fog; beauty and depth from
// the 8×8 relief kernel) on a tuned kernel against an 8×8 one: the best of 5 GPU
// times per kernel / pass, and byte-identical colour, AOV and froxel output.
// The relief slot is opt-in (GpuGroupShapes.OptInSlots): its tuning compiles the
// relief shader per candidate (FXC ~3 min each on the GT 710), so it runs only
// with "--gpugroupshapeprobe relief"; "relief=32x8" runs the tuned kernel in that
// shape instead (checks the bytes of a non-8×8 relief); without either the relief
// kernel stays 8×8.
// Exit 0 = pass or SKIP; 1 = a tuned frame differs from the 8×8 frame.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FracturingFog.Abstractions;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Rendering.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class GpuGroupShapeProbe
    {
        public static int Run(string[]? args = null)
        {
            string? reliefArg = args == null ? null : Array.Find(args, a => a.StartsWith("relief", StringComparison.OrdinalIgnoreCase));
            string reliefSetting = reliefArg == null ? "auto" : reliefArg.Contains('=') ? reliefArg : "relief=auto";
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");
            string temp = Path.Combine(Path.GetTempPath(), "FracturingFog.GroupShapeProbe", Guid.NewGuid().ToString("N"));
            AppDataPaths.SetProcessRootOverride(temp);
            GpuGroupShapes.ForgetLoadedStore();
            int failures = 0;
            try
            {
                var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                    out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
                if (hr.Failure || device == null || context == null)
                    Console.WriteLine($"gpugroupshapeprobe D3D11 SKIP: no hardware device (0x{hr.Code:X8}).");
                else
                {
                    try
                    {
                        failures += Backend("D3D11", () => new MandelbrotGpuKernel(device, context, new object()),
                            k => ((MandelbrotGpuKernel)k).GroupShape, k => ((MandelbrotGpuKernel)k).GroupShapeDeviceKey);
                        var gate = new object();
                        failures += Raymarch("D3D11", reliefSetting, () => new ReliefRaymarchGpuKernel(device, context, gate),
                            () => new FroxelGpuKernel(device, context, gate),
                            k => ((ReliefRaymarchGpuKernel)k).GroupShape, (k, s) => ((FroxelGpuKernel)k).GroupShape(s),
                            k => ((ReliefRaymarchGpuKernel)k).GroupShapeDeviceKey);
                    }
                    finally { context.Dispose(); device.Dispose(); }
                }

                bool vulkan;
                try { using var probe = VulkanComputeKernel.TryCreateWithOwnContext(); vulkan = probe != null; }
                catch { vulkan = false; }
                if (!vulkan) Console.WriteLine("gpugroupshapeprobe Vulkan SKIP: no device.");
                else
                {
                    failures += Backend("Vulkan", () => VulkanComputeKernel.TryCreateWithOwnContext()!,
                        k => ((VulkanComputeKernel)k).GroupShape, k => ((VulkanComputeKernel)k).GroupShapeDeviceKey);
                    failures += Raymarch("Vulkan", reliefSetting, () => ReliefRaymarchVulkanKernel.TryCreateWithOwnContext()!,
                        () => FroxelVolumeVulkanKernel.TryCreateWithOwnContext()!,
                        k => ((ReliefRaymarchVulkanKernel)k).GroupShape, (k, s) => ((FroxelVolumeVulkanKernel)k).GroupShape(s),
                        k => ((ReliefRaymarchVulkanKernel)k).GroupShapeDeviceKey);
                }
            }
            finally
            {
                GpuGroupShapes.ResetSettingForThread();
                AppDataPaths.ClearOverride();
                GpuGroupShapes.ForgetLoadedStore();
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
            Console.WriteLine(failures == 0 ? "gpugroupshapeprobe OK" : $"gpugroupshapeprobe FAIL: {failures} frame(s) differ");
            return failures == 0 ? 0 : 1;
        }

        private static int Backend(string name, Func<IGpuKernel> create,
            Func<IGpuKernel, Func<string, GpuGroupShape>> shapeOf, Func<IGpuKernel, string> deviceKey)
        {
            // A kernel resolves a slot's shape on the slot's first frame, under the
            // setting in force then: "off" for the 8x8 kernel, "auto" for the tuned one.
            using var plain = create();
            using var tuned = create();
            Console.WriteLine($"gpugroupshapeprobe {name}: {deviceKey(tuned)}");

            int failures = 0;
            foreach (string slot in new[] { GpuGroupShapes.SlotEscape, GpuGroupShapes.SlotPerturb, GpuGroupShapes.SlotPerturbFp32 })
            {
                if (slot == GpuGroupShapes.SlotPerturb && !tuned.SupportsPerturbation
                    || slot == GpuGroupShapes.SlotPerturbFp32 && !tuned.SupportsPerturbationFloatExp)
                {
                    Console.WriteLine($"  {slot,-12}: SKIP (not supported)");
                    continue;
                }
                var a = new Frame(slot); var b = new Frame(slot);
                GpuGroupShapes.SetSettingForThread("off");
                b.Render(plain);   // the 8x8 kernel's first frame of the slot: 8x8
                if (shapeOf(plain)(slot) != GpuGroupShape.Default) throw new InvalidOperationException("the 8x8 kernel is not 8x8");
                GpuGroupShapes.SetSettingForThread("auto");
                long t0 = Stopwatch.GetTimestamp();
                a.Render(tuned);   // the slot's first frame: tunes
                double tuneMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                var tuning = GpuGroupShapes.LastTuning;
                var winner = shapeOf(tuned)(slot);
                string timings = tuning is { } t && t.Key.Contains("|" + slot + "|", StringComparison.Ordinal)
                    ? string.Join(", ", t.Timings.Select(x => $"{x.Shape} {x.BestMs:F2}"))
                    : "(not tuned)";
                Console.WriteLine($"  {slot,-12}: tuned to {winner} in {tuneMs:F0} ms (incl. the frame); representative ms: {timings}");

                double tunedMs = Best(tuned, a), plainMs = Best(plain, b);
                bool same = a.SameAs(b, out string where);
                if (!same) failures++;
                Console.WriteLine($"  {"",-12}  {a.Label}: 8x8 {plainMs,7:F2} ms, {winner} {tunedMs,7:F2} ms ({Math.Round((tunedMs / plainMs - 1) * 100, 1):+0.0;-0.0;0.0}%), " +
                    (same ? "byte-identical" : "DIFFERS at " + where));
            }
            GpuGroupShapes.ResetSettingForThread();
            return failures;
        }

        // #1377 — the relief raymarch and the froxel passes (see the file header).
        private static int Raymarch(string name, string reliefSetting, Func<IReliefRaymarchKernel> createRelief, Func<IFroxelVolumeKernel> createFroxel,
            Func<IReliefRaymarchKernel, GpuGroupShape> reliefShape, Func<IFroxelVolumeKernel, string, GpuGroupShape> froxelShape,
            Func<IReliefRaymarchKernel, string> deviceKey)
        {
            const int W = 1280, H = 720;
            int failures = 0, n = W * H;
            using var plainR = createRelief();
            using var tunedR = createRelief();
            using var plainF = createFroxel();
            using var tunedF = createFroxel();
            Console.WriteLine($"gpugroupshapeprobe {name} relief / froxel: {deviceKey(tunedR)}");

            // The 8x8 kernels resolve on their first frame under "off"; the scene's
            // beauty and depth come from the 8x8 relief kernel.
            GpuGroupShapes.SetSettingForThread("off");
            var scene = RaymarchGroupShapes.Capture(W, H, fill: plainR);
            var plainOut = new ReliefOut(n);
            plainOut.Run(plainR, scene);
            var plainFrox = new uint[n];
            plainF.Composite(scene.Froxel, scene.Beauty, scene.Depth, W, H, plainFrox);

            GpuGroupShapes.SetSettingForThread(reliefSetting);
            long t0 = Stopwatch.GetTimestamp();
            RaymarchGroupShapes.TimeRelief(tunedR);   // the first frame: tunes (relief only when asked)
            double tuneMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Console.WriteLine($"  {"relief",-16}: tuned to {reliefShape(tunedR)} in {tuneMs:F0} ms; representative ms: {Timings("relief")}");
            t0 = Stopwatch.GetTimestamp();
            var integrateTimings = "";
            RaymarchGroupShapes.TimeFroxel(tunedF, GpuGroupShapes.SlotFroxelIntegrate);   // tunes both passes
            tuneMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            integrateTimings = Timings("froxel-composite");
            Console.WriteLine($"  {"froxel",-16}: integrate tuned to {froxelShape(tunedF, GpuGroupShapes.SlotFroxelIntegrate)}, " +
                $"composite to {froxelShape(tunedF, GpuGroupShapes.SlotFroxelComposite)}, both in {tuneMs:F0} ms; composite representative ms: {integrateTimings}");
            GpuGroupShapes.ResetSettingForThread();

            var tunedOut = new ReliefOut(n);
            tunedOut.Run(tunedR, scene);
            string? diff = tunedOut.FirstDifference(plainOut, W);
            if (diff != null) failures++;
            double pr = BestRelief(plainR, scene), tr = BestRelief(tunedR, scene);
            Console.WriteLine($"  {"",-16}  relief {W}x{H}: 8x8 {pr,8:F2} ms, {reliefShape(tunedR)} {tr,8:F2} ms ({Pct(tr, pr)}), " +
                (diff == null ? "byte-identical (colour + AOV)" : "DIFFERS at " + diff));

            var tunedFrox = new uint[n];
            tunedF.Composite(scene.Froxel, scene.Beauty, scene.Depth, W, H, tunedFrox);
            int fd = -1;
            for (int i = 0; i < n && fd < 0; i++) if (tunedFrox[i] != plainFrox[i]) fd = i;
            if (fd >= 0) failures++;
            var (pi, pc) = BestFroxel(plainF, scene);
            var (ti, tc) = BestFroxel(tunedF, scene);
            Console.WriteLine($"  {"",-16}  froxel {W}x{H} ({scene.Froxel.Grid.DimX}x{scene.Froxel.Grid.DimY}x{scene.Froxel.Grid.DimZ} grid): " +
                $"integrate 8x8 {pi:F3} ms, {froxelShape(tunedF, GpuGroupShapes.SlotFroxelIntegrate)} {ti:F3} ms ({Pct(ti, pi)}); " +
                $"composite 8x8 {pc:F3} ms, {froxelShape(tunedF, GpuGroupShapes.SlotFroxelComposite)} {tc:F3} ms ({Pct(tc, pc)}), " +
                (fd < 0 ? "byte-identical" : $"DIFFERS at pixel ({fd % W}, {fd / W})"));
            return failures;
        }

        private static string Timings(string slot) =>
            GpuGroupShapes.LastTuning is { } t && t.Key.Contains("|" + slot + "|", StringComparison.Ordinal)
                ? string.Join(", ", t.Timings.Select(x => $"{x.Shape} {x.BestMs:F2}"))
                : "(not tuned)";

        private static string Pct(double tuned, double plain) => $"{Math.Round((tuned / plain - 1) * 100, 1):+0.0;-0.0;0.0}%";

        private static double BestRelief(IReliefRaymarchKernel k, RaymarchGroupShapes.Scene s)
        {
            var dst = new uint[s.W * s.H];
            RaymarchGroupShapes.TimeRelief(k, s, dst);
            double best = double.MaxValue;
            for (int r = 0; r < 5; r++) best = Math.Min(best, RaymarchGroupShapes.TimeRelief(k, s, dst));
            return best;
        }

        private static (double Integrate, double Composite) BestFroxel(IFroxelVolumeKernel k, RaymarchGroupShapes.Scene s)
        {
            var dst = new uint[s.W * s.H];
            k.Composite(s.Froxel, s.Beauty, s.Depth, s.W, s.H, dst);
            double bi = double.MaxValue, bc = double.MaxValue;
            for (int r = 0; r < 5; r++)
            {
                k.Composite(s.Froxel, s.Beauty, s.Depth, s.W, s.H, dst);
                if (k.LastPassTimes is { } p) { bi = Math.Min(bi, p.IntegrateMs); bc = Math.Min(bc, p.CompositeMs); }
                else { bi = Math.Min(bi, k.LastDispatchMs); bc = double.NaN; }
            }
            return (bi, bc);
        }

        private sealed class ReliefOut(int n)
        {
            public readonly uint[] Color = new uint[n];
            public readonly float[] Normal = new float[n * 3], Depth = new float[n];

            public void Run(IReliefRaymarchKernel k, RaymarchGroupShapes.Scene s) =>
                k.Run(s.Relief, s.Field, s.Keep, s.Albedo, Color, Normal, Depth);

            public string? FirstDifference(ReliefOut o, int w)
            {
                for (int i = 0; i < Color.Length; i++) if (Color[i] != o.Color[i]) return $"colour ({i % w}, {i / w})";
                for (int i = 0; i < Normal.Length; i++) if (BitConverter.SingleToInt32Bits(Normal[i]) != BitConverter.SingleToInt32Bits(o.Normal[i])) return $"normal ({i / 3 % w}, {i / 3 / w})";
                for (int i = 0; i < Depth.Length; i++) if (BitConverter.SingleToInt32Bits(Depth[i]) != BitConverter.SingleToInt32Bits(o.Depth[i])) return $"depth ({i % w}, {i / w})";
                return null;
            }
        }

        private static double Best(IGpuKernel k, Frame f)
        {
            double best = double.MaxValue;
            f.Render(k);
            for (int r = 0; r < 5; r++)
            {
                f.Render(k);
                double ms = k.LastGpuStageTimes is { DispatchMs: > 0 } t ? t.DispatchMs : k.LastDispatchMs;
                best = Math.Min(best, ms);
            }
            return best;
        }

        private sealed class Frame
        {
            private readonly string _slot;
            private readonly int _w, _h, _maxIter;
            public readonly int[] It; public readonly float[] Sm, Zr, Zi, Dr, Di;
            private static (double[] Zr, double[] Zi, int Len)? s_orbit;

            public Frame(string slot)
            {
                _slot = slot;
                (_w, _h, _maxIter) = slot == GpuGroupShapes.SlotEscape ? (1920, 1080, 1000) : (640, 360, 2000);
                int n = _w * _h;
                It = new int[n]; Sm = new float[n]; Zr = new float[n]; Zi = new float[n]; Dr = new float[n]; Di = new float[n];
            }

            public string Label => $"{_w}x{_h} {_maxIter} it";

            public void Render(IGpuKernel k)
            {
                if (_slot == GpuGroupShapes.SlotEscape)
                {
                    k.Run(_w, _h, -0.7436, 0.1318, 0.004 / _w, _maxIter, 4.0, It, Sm, Zr, Zi, Dr, Di);
                    return;
                }
                var (oZr, oZi, len) = s_orbit ??= Orbit(-0.743643887037151, 0.13182590420533, _maxIter);
                double scale = 3.5 / (_w * 1e5);
                if (_slot == GpuGroupShapes.SlotPerturb)
                    k.RunPerturb(_w, _h, scale, _maxIter, 512.0 * 512.0, -_w / 2, -_h / 2, oZr, oZi, len, It, Sm, Zr, Zi, Dr, Di);
                else
                    k.RunPerturbFloatExp(_w, _h, scale, _maxIter, 512.0 * 512.0, -_w / 2, -_h / 2, oZr, oZi, len, It, Sm, Zr, Zi, Dr, Di);
            }

            public bool SameAs(Frame o, out string where)
            {
                for (int i = 0; i < It.Length; i++)
                {
                    if (It[i] != o.It[i] || Bits(Sm[i]) != Bits(o.Sm[i]) || Bits(Zr[i]) != Bits(o.Zr[i])
                        || Bits(Zi[i]) != Bits(o.Zi[i]) || Bits(Dr[i]) != Bits(o.Dr[i]) || Bits(Di[i]) != Bits(o.Di[i]))
                    {
                        where = $"pixel ({i % _w}, {i / _w})";
                        return false;
                    }
                }
                where = "";
                return true;
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
