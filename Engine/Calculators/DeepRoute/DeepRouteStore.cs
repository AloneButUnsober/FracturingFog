// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/DeepRoute/DeepRouteStore.cs
// #1665 / #1785 (S1) — the per-device store of the measured deep-zoom route.
//
// FF picked its deep-zoom route (CPU BLA rows or LA, GPU perturbation on or off, GPU
// LA, LA SIMD, fp64 or float-exp) from fixed thresholds and opt-ins, all set on one
// machine (#1663). The calibration pass (#1786, S2) measures the routes once per
// device and stores the winners here; MandelbrotCalculator's route switches read
// them (MandelbrotCalculator.DeepRoute.cs). The model is the #1339 thread-group
// shape store: measure once, key by device, reuse.
//
// Two kinds of rows:
//   • CPU rows (the LA crossover zoom, LA SIMD), keyed by CpuKey: the CPU brand,
//     architecture, thread count and SIMD set;
//   • GPU rows (GPU deep on / off, GPU LA, the fp64 / float-exp choice), keyed by the
//     CPU key AND the kernel's device key (backend, adapter, ids, driver: the #1339
//     key), since "is the GPU faster" depends on both.
// A changed CPU, GPU or driver is a new key: the old row simply stops matching. A
// schema version bump (the calibration procedure changed) ignores the whole file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FracturingFog.Abstractions;

namespace FracturingFog
{
    /// <summary>#1665 — the measured CPU-side deep route for one CPU. Null fields were
    /// not measured (the default applies).</summary>
    public sealed record DeepRouteCpuResult
    {
        /// <summary>The zoom from which CPU LA beats the CPU BLA rows (Auto LA mode);
        /// +∞ = LA never won. Null = not measured (<see cref="MandelbrotCalculator.LaAutoMinZoom"/>).</summary>
        public double? LaMinZoom { get; init; }
        /// <summary>The 4-lane AVX2 LA tail (#1628) measured faster.</summary>
        public bool? LaSimd { get; init; }
        /// <summary>When it was measured (ISO 8601, UTC), for display.</summary>
        public string? Measured { get; init; }
    }

    /// <summary>#1665 — the measured GPU-side deep route for one CPU + GPU pair.</summary>
    public sealed record DeepRouteGpuResult
    {
        /// <summary>GPU deep perturbation measured faster than the CPU deep path.</summary>
        public bool? GpuDeep { get; init; }
        /// <summary>The GPU LA stage kernel (#1630) measured faster than GPU SA / BLA.</summary>
        public bool? GpuLa { get; init; }
        /// <summary>The faster plain deep kernel: <see cref="GpuDeepPrecision.Double"/> or
        /// <see cref="GpuDeepPrecision.Fp32"/> (float-exp). Used under Auto only.</summary>
        public GpuDeepPrecision? Precision { get; init; }
        public string? Measured { get; init; }
    }

    public static class DeepRouteStore
    {
        /// <summary>Bump when the calibration procedure changes what a row means: a file
        /// written under another version is ignored (and rewritten by the next store).</summary>
        public const int SchemaVersion = 1;

        public const string FileName = "deep-route-calibration.json";

        /// <summary>The store file under the app data root.</summary>
        public static string StorePath => AppDataPaths.Combine(FileName);

        private sealed class FileShape
        {
            public int Version { get; set; }
            public Dictionary<string, DeepRouteCpuResult> Cpu { get; set; } = new();
            public Dictionary<string, DeepRouteGpuResult> Gpu { get; set; } = new();
        }

        private static readonly JsonSerializerOptions s_json = new()
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,   // LaMinZoom = Infinity
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly object s_lock = new();
        private static FileShape? s_file;
        private static string? s_filePath;

        // ── keys ────────────────────────────────────────────────────────────────

        private static string? s_cpuKey;

        /// <summary>"brand|arch|Nt|simd" for this process's CPU.</summary>
        public static string CpuKey => s_cpuKey ??= BuildCpuKey(CpuBrand(), RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, SimdSet());

        /// <summary>The CPU key from its parts (tests build other machines' keys).</summary>
        public static string BuildCpuKey(string brand, string arch, int threads, string simd)
            => $"{brand.Trim()}|{arch}|{threads}t|{simd}";

        /// <summary>The GPU row key: this CPU and the kernel's device key.</summary>
        public static string GpuKey(string cpuKey, string deviceKey) => $"{cpuKey}||{deviceKey}";

        /// <summary>The vector extensions the CPU kernels can use.</summary>
        public static string SimdSet()
        {
            var parts = new List<string>();
            if (Avx2.IsSupported) parts.Add("avx2");
            if (Avx512F.IsSupported) parts.Add("avx512f");
            if (AdvSimd.IsSupported) parts.Add("neon");
            return parts.Count == 0 ? "scalar" : string.Join("+", parts);
        }

        // The x86 brand string (CPUID 0x80000002..4), else the OS's description.
        private static string CpuBrand()
        {
            try
            {
                if (X86Base.IsSupported && (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax >= 0x80000004)
                {
                    var sb = new StringBuilder(48);
                    for (int leaf = 0; leaf < 3; leaf++)
                    {
                        var (a, b, c, d) = X86Base.CpuId(unchecked((int)(0x80000002 + leaf)), 0);
                        foreach (int r in new[] { a, b, c, d })
                            for (int i = 0; i < 4; i++)
                            {
                                char ch = (char)((r >> (8 * i)) & 0xFF);
                                if (ch != '\0') sb.Append(ch);
                            }
                    }
                    string s = sb.ToString().Trim();
                    if (s.Length > 0) return s;
                }
            }
            catch { /* fall through */ }
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.OSDescription;
        }

        // ── load / save ─────────────────────────────────────────────────────────

        private static FileShape Loaded()
        {
            string path = StorePath;
            if (s_file != null && s_filePath == path) return s_file;
            FileShape f = new() { Version = SchemaVersion };
            try
            {
                if (File.Exists(path))
                {
                    var read = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), s_json);
                    if (read is { Version: SchemaVersion })
                    {
                        read.Cpu ??= new();
                        read.Gpu ??= new();
                        f = read;
                    }
                }
            }
            catch { /* unreadable: start empty; the next store rewrites it */ }
            s_file = f;
            s_filePath = path;
            return f;
        }

        private static void Save(FileShape f)
        {
            try
            {
                string path = StorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(f, s_json));
                File.Move(tmp, path, overwrite: true);
            }
            catch { /* best effort: the in-memory copy still serves this session */ }
        }

        /// <summary>The stored CPU row for <paramref name="cpuKey"/> (default: this CPU).</summary>
        public static DeepRouteCpuResult? LookupCpu(string? cpuKey = null)
        {
            lock (s_lock) return Loaded().Cpu.TryGetValue(cpuKey ?? CpuKey, out var r) ? r : null;
        }

        /// <summary>The stored GPU row for this CPU and <paramref name="deviceKey"/>.</summary>
        public static DeepRouteGpuResult? LookupGpu(string deviceKey, string? cpuKey = null)
        {
            lock (s_lock) return Loaded().Gpu.TryGetValue(GpuKey(cpuKey ?? CpuKey, deviceKey), out var r) ? r : null;
        }

        /// <summary>True when some GPU row for this CPU has GPU deep on: lets a headless
        /// render open a device before its key is known (#1640's CpuOnlyView).</summary>
        public static bool AnyGpuDeepFor(string? cpuKey = null)
        {
            string prefix = GpuKey(cpuKey ?? CpuKey, "");
            lock (s_lock)
                foreach (var (k, v) in Loaded().Gpu)
                    if (v.GpuDeep == true && k.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        public static void StoreCpu(DeepRouteCpuResult result, string? cpuKey = null)
        {
            ArgumentNullException.ThrowIfNull(result);
            lock (s_lock) { var f = Loaded(); f.Cpu[cpuKey ?? CpuKey] = result; Save(f); }
        }

        public static void StoreGpu(string deviceKey, DeepRouteGpuResult result, string? cpuKey = null)
        {
            ArgumentNullException.ThrowIfNull(result);
            lock (s_lock) { var f = Loaded(); f.Gpu[GpuKey(cpuKey ?? CpuKey, deviceKey)] = result; Save(f); }
        }

        /// <summary>All stored rows (for the UI's view, #1787).</summary>
        public static (IReadOnlyDictionary<string, DeepRouteCpuResult> Cpu, IReadOnlyDictionary<string, DeepRouteGpuResult> Gpu) Snapshot()
        {
            lock (s_lock)
            {
                var f = Loaded();
                return (new Dictionary<string, DeepRouteCpuResult>(f.Cpu), new Dictionary<string, DeepRouteGpuResult>(f.Gpu));
            }
        }

        /// <summary>Forget every stored result (the file too): today's defaults apply again.</summary>
        public static void Reset()
        {
            lock (s_lock)
            {
                s_file = new FileShape { Version = SchemaVersion };
                s_filePath = StorePath;
                try { if (File.Exists(StorePath)) File.Delete(StorePath); } catch { }
            }
        }

        /// <summary>Test hook: drop the in-memory copy (the file is read again).</summary>
        public static void ForgetLoaded() { lock (s_lock) { s_file = null; s_filePath = null; } }
    }
}
