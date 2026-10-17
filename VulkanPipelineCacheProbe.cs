// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1341: --vkpipelinecacheprobe. The persisted VkPipelineCache on the Vulkan
// device (SKIP without one). Each session is a child process (a fresh VkDevice)
// on one throwaway data root, so nothing touches the real ShaderCache:
//   1. cold:      no pipeline-cache files (the SPIR-V is compiled too);
//   2. warm:      the files the cold session saved;
//   3. no cache:  the pipeline cache switched off (FF_VK_PIPELINE_CACHE=0), the
//                 SPIR-V still cached: what every session paid before #1341;
//   4. corrupt:   the compute file with one payload byte flipped;
//   5. foreign:   the compute file re-wrapped with another pipelineCacheUUID.
// A session builds the compute kernel's pipelines (SP for every fractal kind, a
// GPU palette, fp64 / SA / orbit / float-exp / scaled-fp32 perturbation,
// Buddhabrot) and the relief and froxel kernels' (built at creation), reports the
// time spent in vkCreateComputePipelines per kernel and what the cache load did,
// and hashes every compute output. Checks: the warm session loads every file and
// its frames hash the same as the cold one; the corrupt and foreign files are
// rejected (for the right reason) and the session still renders the same frames.
// Exit 0 = pass or SKIP; 1 = a check failed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FracturingFog.Abstractions;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;

namespace FracturingFog
{
    internal static class VulkanPipelineCacheProbe
    {
        private const string ChildFlag = "--vkpipelinecacheprobe-session";

        public static int Run(string[] args)
        {
            if (args.Length >= 4 && args[0] == ChildFlag) return Session(args[1], args[2], args[3]);

            string? device;
            try { device = VulkanComputeKernel.ProbeDeviceName(); } catch { device = null; }
            if (device == null) { Console.WriteLine("vkpipelinecacheprobe SKIP: no Vulkan device."); return 0; }
            Console.WriteLine($"vkpipelinecacheprobe on {device}");

            string root = Path.Combine(Path.GetTempPath(), "FracturingFog.PipelineCacheProbe", Guid.NewGuid().ToString("N"));
            int failures = 0;
            void Check(bool ok, string what) { if (!ok) { Console.Error.WriteLine("  FAIL " + what); failures++; } }
            try
            {
                var cold = Child(root, "on", "cold");
                var warm = Child(root, "on", "warm");
                var none = Child(root, "off", "no cache");
                if (cold == null || warm == null || none == null) return 1;

                Check(cold.All(k => k.State is "NoFile"), "cold: a cache file already existed");
                Check(warm.All(k => k.State == "Loaded" && k.LoadedBytes > 0), "warm: not every kernel loaded its file");
                Check(none.All(k => k.State == "Disabled"), "no cache: the cache was not off");
                Check(Same(cold, warm) && Same(cold, none), "the frames differ between sessions");

                string file = Directory.GetFiles(Path.Combine(root, "ShaderCache"), "vk-pipelines-compute-*.bin").Single();
                byte[] good = File.ReadAllBytes(file);

                var bad = (byte[])good.Clone();
                bad[^17] ^= 0x5A;                                    // one payload byte
                File.WriteAllBytes(file, bad);
                var corrupt = Child(root, "on", "corrupt");
                Check(corrupt?.Single(k => k.Kernel == "compute") is { State: "Rejected", Reason: "checksum mismatch" },
                    "corrupt: the flipped byte was not rejected by the checksum");
                Check(corrupt != null && Same(cold, corrupt), "corrupt: the frames differ");

                var payload = good.AsSpan(44).ToArray();
                payload[16] ^= 0xFF;                                 // pipelineCacheUUID[0]
                File.WriteAllBytes(file, VulkanPipelineCache.Wrap(payload));
                var foreign = Child(root, "on", "foreign");
                Check(foreign?.Single(k => k.Kernel == "compute") is { State: "Rejected", Reason: "another driver (pipelineCacheUUID)" },
                    "foreign: another driver's cache was not rejected");
                Check(foreign != null && Same(cold, foreign), "foreign: the frames differ");
            }
            finally { try { Directory.Delete(root, recursive: true); } catch { } }

            Console.WriteLine(failures == 0 ? "vkpipelinecacheprobe OK" : $"vkpipelinecacheprobe FAIL: {failures} check(s)");
            return failures == 0 ? 0 : 1;
        }

        private sealed record KernelReport(string Kernel, int Pipelines, double Ms, string State, string? Reason, int LoadedBytes, string Hash);

        private static bool Same(List<KernelReport> a, List<KernelReport> b) =>
            a.Single(k => k.Kernel == "compute").Hash == b.Single(k => k.Kernel == "compute").Hash;

        private static List<KernelReport>? Child(string root, string cache, string label)
        {
            // The report comes back in its own file, apart from anything else the
            // session's start-up prints.
            string report = Path.Combine(Path.GetTempPath(), $"ff-vkpc-{Guid.NewGuid():N}.txt");
            var psi = new ProcessStartInfo(Environment.ProcessPath!, new[] { ChildFlag, root, cache, report })
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            long t0 = Stopwatch.GetTimestamp();
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            double wall = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"  FAIL {label}: the session exited {p.ExitCode}\n{stdout}{stderr}");
                return null;
            }
            string text = File.Exists(report) ? File.ReadAllText(report) : "";
            try { File.Delete(report); } catch { }
            if (!text.Contains("KERNEL\t", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"  FAIL {label}: no report ({psi.FileName} {string.Join(' ', psi.ArgumentList)})\n{stdout}{stderr}");
                return null;
            }
            var reports = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("KERNEL\t", StringComparison.Ordinal))
                .Select(l => l.Trim().Split('\t'))
                .Select(f => new KernelReport(f[1], int.Parse(f[2]), double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture),
                    f[4], f[5].Length == 0 ? null : f[5], int.Parse(f[6]), f[7]))
                .ToList();
            Console.WriteLine($"  {label,-9}: " + string.Join(", ", reports.Select(r =>
                $"{r.Kernel} {r.Pipelines} pipelines {r.Ms,7:F1} ms ({r.State}{(r.Reason != null ? ": " + r.Reason : "")}{(r.LoadedBytes > 0 ? $", {r.LoadedBytes / 1024} KiB" : "")})"))
                + $"; total {reports.Sum(r => r.Ms):F0} ms in vkCreateComputePipelines, process {wall:F0} ms");
            return reports;
        }

        // ── one session (child process) ──────────────────────────────────────

        private static int Session(string root, string cache, string reportPath)
        {
            using var report = new StreamWriter(reportPath);
            AppDataPaths.SetProcessRootOverride(root);
            VulkanPipelineCache.Enabled = cache == "on";
            GpuGroupShapes.ProcessSetting = "off";              // the same pipelines every session (no tuning)
            Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");   // before the kernel source type loads

            using (var k = VulkanComputeKernel.TryCreateWithOwnContext())
            {
                if (k == null) { Console.Error.WriteLine("no Vulkan device"); return 2; }
                string hash = ComputeFrames(k);
                Report(report, "compute", k.PipelinesCreated, k.PipelineCreateMs, k.PipelineCache, hash);
            }
            using (var r = ReliefRaymarchVulkanKernel.TryCreateWithOwnContext())
                if (r != null) Report(report, "relief", r.PipelinesCreated, r.PipelineCreateMs, r.PipelineCache, "-");
            using (var f = FroxelVolumeVulkanKernel.TryCreateWithOwnContext())
                if (f != null) Report(report, "froxel", f.PipelinesCreated, f.PipelineCreateMs, f.PipelineCache, "-");
            return 0;
        }

        private static void Report(StreamWriter w, string kernel, int pipelines, double ms, VulkanPipelineCache c, string hash) =>
            w.WriteLine(string.Join('\t', "KERNEL", kernel, pipelines.ToString(),
                ms.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                c.State.ToString(), c.RejectReason ?? "", c.LoadedBytes.ToString(), hash));

        private static string ComputeFrames(VulkanComputeKernel k)
        {
            const int W = 96, H = 64, N = W * H;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var it = new int[N]; var sm = new float[N]; var zr = new float[N]; var zi = new float[N]; var dr = new float[N]; var di = new float[N];
            void Add(params Array[] outs)
            {
                foreach (var a in outs)
                {
                    var bytes = new byte[Buffer.ByteLength(a)];
                    Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
                    sha.AppendData(bytes);
                }
            }

            for (int kind = 0; kind <= (int)FractalKind.Phoenix; kind++)
            {
                k.Run(W, H, -0.5, 0, 3.0 / W, 300, 4.0, it, sm, zr, zi, dr, di, kind: (FractalKind)kind, param0: kind == 4 ? 3 : 0.2f);
                Add(it, sm, zr, zi, dr, di);
            }

            var theme = InterpretedColorMap.TryCreate("return hsv(frac(t * 3.0), 0.8, 1.0);", null, out _);
            if (theme is FracturingFog.Interefaces.IGpuHlslPalette pal)
            {
                k.SetPalette(pal);
                var color = new uint[N];
                k.Run(W, H, -0.5, 0, 3.0 / W, 300, 4.0, it, sm, zr, zi, dr, di, colorDst: color);
                k.SetPalette(null);
                Add(it, color);
            }

            var (oZr, oZi, len) = Orbit(-0.743643887037151, 0.13182590420533, 1000);
            double scale = 3.5 / (W * 1e5);
            if (k.SupportsPerturbation)
            {
                k.RunPerturb(W, H, scale, 1000, 512.0 * 512.0, -W / 2, -H / 2, oZr, oZi, len, it, sm, zr, zi, dr, di);
                Add(it, sm, zr, zi, dr, di);
                var sa = new SeriesApproximation(oZr, oZi, len);
                k.RunPerturbSA(W, H, scale, 1000, 512.0 * 512.0, -W / 2, -H / 2, oZr, oZi, len, 1e-3, sa.SafeMax,
                    sa.AR, sa.AI, sa.BR, sa.BI, sa.CR, sa.CI, sa.DR, sa.DI, it, sm, zr, zi, dr, di);
                Add(it, sm, zr, zi, dr, di);
                // MandelbrotKernelSource is linked into both backend assemblies: the Vulkan one's.
                var src = typeof(VulkanComputeKernel).Assembly.GetType("FracturingFog.Rendering.MandelbrotKernelSource", throwOnError: true)!;
                int mask = (int)src.GetField("OrbAll")!.GetValue(null)!;
                var orbit = new float[N * (int)src.GetMethod("PerturbOrbitStride")!.Invoke(null, new object[] { mask })!];
                k.RunPerturbOrbit(W, H, scale, 1000, 512.0 * 512.0, -W / 2, -H / 2, oZr, oZi, len, mask,
                    -0.743643887037151, 0.13182590420533, it, sm, zr, zi, dr, di, orbit);
                Add(it, orbit);
            }
            foreach (bool fx in new[] { false, true })
            {
                VulkanComputeKernel.ForceFloatExpEntry = fx;
                try { k.RunPerturbFloatExp(W, H, scale, 1000, 512.0 * 512.0, -W / 2, -H / 2, oZr, oZi, len, it, sm, zr, zi, dr, di); }
                finally { VulkanComputeKernel.ForceFloatExpEntry = false; }
                Add(it, sm, zr, zi, dr, di);
            }

            var hr = new uint[N]; var hg = new uint[N]; var hb = new uint[N];
            k.RunBuddhaBatch(new GpuBuddhaBatch(W, H, 3.0 / W, -0.5, 0, 200, false, false, 20, 100, 7u, 1, 20_000), hr, hg, hb);
            Add(hr, hg, hb);
            return Convert.ToHexString(sha.GetHashAndReset())[..16];
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
