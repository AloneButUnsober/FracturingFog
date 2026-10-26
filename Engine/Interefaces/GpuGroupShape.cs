// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Interefaces/GpuGroupShape.cs
//
// #1339 — the compute thread-group shape of the 2D escape-time / perturbation
// kernels (MandelbrotKernelSource, D3D11 + Vulkan), tuned per device and kernel.
//
// Every kernel reads its pixel from SV_DispatchThreadID alone (no group-shared
// memory, no SV_GroupThreadID), so the shape only changes which pixels run
// together in a warp / wavefront, never a pixel's value: every shape gives the
// same bytes. It does change the speed. On a GT 710 the shallow SP kernel runs
// 5–15% faster in 16×8 groups than in 8×8, while the fp64 perturbation kernel
// is fastest in 8×8 (16×8 costs 4%), and other vendors' warp / wave widths (AMD
// 64, Intel 8–32) favour other shapes again.
//
// So each backend tunes each kernel slot once per device + driver + kernel
// source: it times every candidate on a fixed representative frame
// (Representative, GPU timestamps when the device has them) and keeps the
// fastest — the default 8×8 unless another shape beats it by MinGain, so timer
// noise can't flip a tie. The winner is persisted next to the shader blob cache
// (ShaderCache/gpu-group-shapes.json), so the one-time pause (a compile + a few
// small frames per candidate) is paid once per machine and redone only when the
// device, the driver or the kernel source changes.
//
// #1377 — the relief raymarch (ReliefRaymarchKernelSource, slot "relief") and
// the two froxel passes ("froxel-integrate", "froxel-composite") tune the same
// way; their representative frames need uniforms, so they live with the relief
// code (Lighting/RaymarchGroupShapes.cs). The relief slot is opt-in
// (OptInSlots): "auto" keeps it 8×8, "relief=auto" tunes it.
//
// FF_GPU_GROUP_SHAPE overrides: "auto" (default: tune), "off" (8×8, no tuning),
// "16x8" (every slot), or "sp=16x8,perturb=8x8" (those slots; the rest tune).
// Both kernel assemblies link their own MandelbrotKernelSource, so this lives
// in Engine, which both reference (like GpuPeriodicity).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using FracturingFog.Abstractions;

namespace FracturingFog.Rendering
{
    /// <summary>#1339 — a compute thread-group shape, X × Y threads (Z = 1).</summary>
    public readonly record struct GpuGroupShape(int X, int Y)
    {
        /// <summary>The shape every kernel source is written with.</summary>
        public static readonly GpuGroupShape Default = new(8, 8);

        /// <summary>What the kernel sources declare; <see cref="Apply"/> rewrites it.</summary>
        public const string SourceAttribute = "[numthreads(8, 8, 1)]";

        public int Threads => X * Y;

        /// <summary>Groups along x for <paramref name="width"/> pixels.</summary>
        public uint GroupsX(int width) => (uint)((width + X - 1) / X);

        /// <summary>Groups along y for <paramref name="height"/> pixels.</summary>
        public uint GroupsY(int height) => (uint)((height + Y - 1) / Y);

        /// <summary>The kernel source with every <see cref="SourceAttribute"/> in this
        /// shape. The default shape returns the source unchanged (same text, so the
        /// same shader-cache keys as before #1339).</summary>
        public string Apply(string hlsl)
        {
            if (!hlsl.Contains(SourceAttribute, StringComparison.Ordinal))
                throw new ArgumentException($"kernel source declares no {SourceAttribute}", nameof(hlsl));
            return this == Default ? hlsl : hlsl.Replace(SourceAttribute, $"[numthreads({X}, {Y}, 1)]", StringComparison.Ordinal);
        }

        public override string ToString() => $"{X}x{Y}";

        /// <summary>Parse "WxH" (1..1024 each, at most 1024 threads).</summary>
        public static bool TryParse(string? text, out GpuGroupShape shape)
        {
            shape = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] p = text.Trim().ToLowerInvariant().Split('x');
            if (p.Length != 2
                || !int.TryParse(p[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(p[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int y)
                || x < 1 || y < 1 || x > 1024 || y > 1024 || x * y > 1024) return false;
            shape = new GpuGroupShape(x, y);
            return true;
        }
    }

    /// <summary>#1339 — group-shape slots, the override, the tuner and its store.
    /// See the file header.</summary>
    public static class GpuGroupShapes
    {
        /// <summary>The shallow SP escape-time kernels (every fractal kind, with or
        /// without a GPU palette).</summary>
        public const string SlotEscape = "sp";
        /// <summary>The fp64 perturbation kernels (plain, orbit means, SA + BLA).</summary>
        public const string SlotPerturb = "perturb";
        /// <summary>The #1331 fp32 perturbation kernels (float-exp and scaled fp32).</summary>
        public const string SlotPerturbFp32 = "perturb-fp32";
        /// <summary>#1377 — the Relief 3D raymarch (CSRelief, pinhole and DOF).</summary>
        public const string SlotRelief = "relief";
        /// <summary>#1377 — the froxel populate + integrate pass (one thread per column).</summary>
        public const string SlotFroxelIntegrate = "froxel-integrate";
        /// <summary>#1377 — the froxel composite pass (one thread per pixel).</summary>
        public const string SlotFroxelComposite = "froxel-composite";

        /// <summary>Shapes the tuner times, the default first: 64–256 threads, whole
        /// multiples of a 32-wide warp, from square to wide-and-thin rows (a row of
        /// pixels tends to diverge less than a column).</summary>
        public static readonly IReadOnlyList<GpuGroupShape> Candidates =
        [
            GpuGroupShape.Default, new(16, 8), new(16, 16), new(32, 4), new(32, 8), new(16, 4),
        ];

        /// <summary>A non-default shape must be at least this much faster than the
        /// default to win, so timer noise can't flip a tie.</summary>
        public const double MinGain = 0.03;

        /// <summary>Timed runs per candidate, after one untimed warm-up (compile,
        /// first-dispatch costs); the best counts.</summary>
        public const int Reps = 3;

        /// <summary>FF_GPU_GROUP_SHAPE, read once.</summary>
        public static readonly string? EnvironmentSetting = Environment.GetEnvironmentVariable("FF_GPU_GROUP_SHAPE");

        /// <summary>The process's setting: FF_GPU_GROUP_SHAPE unless a host sets it
        /// (the test assembly pins "off", so only the tuning tests tune).</summary>
        public static string? ProcessSetting { get; set; } = EnvironmentSetting;

        [ThreadStatic] private static string? t_setting;
        [ThreadStatic] private static bool t_settingSet;

        /// <summary>The setting in force on this thread: a test's
        /// (<see cref="SetSettingForThread"/>), else <see cref="ProcessSetting"/>.</summary>
        public static string? Setting => t_settingSet ? t_setting : ProcessSetting;

        /// <summary>Test hook: the override for this thread (null = "auto").</summary>
        public static void SetSettingForThread(string? setting) { t_setting = setting; t_settingSet = true; }

        /// <summary>Drop this thread's override (back to <see cref="ProcessSetting"/>).</summary>
        public static void ResetSettingForThread() { t_setting = null; t_settingSet = false; }

        /// <summary>#1377 — slots that "auto" leaves at 8×8: tuned only when the
        /// setting names them ("relief=auto"). The relief raymarch: on a GT 710 every
        /// other shape is 1.2–6× slower, and tuning compiles the big relief shader once
        /// per candidate (FXC ~3 min each, a 15-minute first frame; DXC ~38 s in all).</summary>
        public static readonly IReadOnlyList<string> OptInSlots = [SlotRelief];

        /// <summary>The fixed shape <paramref name="setting"/> gives
        /// <paramref name="slot"/>, or null to tune it. Unparseable parts are ignored.
        /// An <see cref="OptInSlots"/> slot the setting doesn't name stays 8×8.</summary>
        public static GpuGroupShape? Fixed(string? setting, string slot)
        {
            GpuGroupShape? unnamed = Contains(OptInSlots, slot) ? GpuGroupShape.Default : null;
            if (string.IsNullOrWhiteSpace(setting)) return unnamed;
            string s = setting.Trim();
            if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return unnamed;
            if (s.Equals("off", StringComparison.OrdinalIgnoreCase) || s == "0") return GpuGroupShape.Default;
            if (GpuGroupShape.TryParse(s, out var all)) return all;
            foreach (string part in s.Split(',', ';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (!part[..eq].Trim().Equals(slot, StringComparison.OrdinalIgnoreCase)) continue;
                string v = part[(eq + 1)..].Trim();
                if (v.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
                if (GpuGroupShape.TryParse(v, out var one)) return one;
            }
            return unnamed;
        }

        private static bool Contains(IReadOnlyList<string> list, string s)
        {
            foreach (var x in list) if (string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>One candidate's timing, for probes and logs.</summary>
        public readonly record struct Timing(GpuGroupShape Shape, double BestMs);

        /// <summary>Time every candidate (warm-up + <see cref="Reps"/> runs of
        /// <paramref name="measureMs"/>, best kept) and return the fastest: the first
        /// (the default) unless another beats it by <see cref="MinGain"/>. A candidate
        /// whose measurement throws or isn't a positive number is left out; if the
        /// first is, the fastest of the rest wins outright.</summary>
        public static GpuGroupShape PickFastest(IReadOnlyList<GpuGroupShape> candidates,
            Func<GpuGroupShape, double> measureMs, List<Timing>? timings = null)
        {
            if (candidates.Count == 0) throw new ArgumentException("no candidates", nameof(candidates));
            double first = double.NaN, bestMs = double.PositiveInfinity;
            GpuGroupShape best = candidates[0];
            for (int c = 0; c < candidates.Count; c++)
            {
                double ms = double.PositiveInfinity;
                try
                {
                    measureMs(candidates[c]);   // warm-up
                    for (int r = 0; r < Reps; r++)
                    {
                        double t = measureMs(candidates[c]);
                        if (t > 0 && double.IsFinite(t)) ms = Math.Min(ms, t);
                    }
                }
                catch { ms = double.PositiveInfinity; }
                if (!double.IsFinite(ms)) continue;
                timings?.Add(new Timing(candidates[c], ms));
                if (c == 0) first = ms;
                if (ms < bestMs) { bestMs = ms; best = candidates[c]; }
            }
            if (double.IsFinite(first) && best != candidates[0] && bestMs > first * (1 - MinGain))
                return candidates[0];
            return best;
        }

        // ── the store ────────────────────────────────────────────────────────

        private static readonly object s_lock = new();
        private static Dictionary<string, string>? s_table;
        private static string? s_tablePath;

        /// <summary>ShaderCache/gpu-group-shapes.json under the app data root.</summary>
        public static string StorePath => Path.Combine(ShaderBlobCache.CacheDir, "gpu-group-shapes.json");

        /// <summary>The store key: device (backend, adapter, driver), slot and a hash
        /// of the kernel source the shape was tuned on.</summary>
        public static string Key(string deviceKey, string slot, string sourceHash) => $"{deviceKey}|{slot}|{sourceHash}";

        /// <summary>A short stable hash of <paramref name="source"/> for <see cref="Key"/>.</summary>
        public static string SourceHash(string source) => ShaderBlobCache.ComputeKey("group-shape", source, "", "")[..16];

        private static Dictionary<string, string> Table()
        {
            string path = StorePath;
            if (s_table != null && s_tablePath == path) return s_table;
            var t = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(path))
                {
                    var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    if (read != null) foreach (var (k, v) in read) t[k] = v;
                }
            }
            catch { /* unreadable: start empty; the next store rewrites it */ }
            s_table = t;
            s_tablePath = path;
            return t;
        }

        /// <summary>The stored shape for <paramref name="key"/>, if any.</summary>
        public static GpuGroupShape? Stored(string key)
        {
            lock (s_lock)
                return Table().TryGetValue(key, out string? v) && GpuGroupShape.TryParse(v, out var s) ? s : null;
        }

        /// <summary>Remember <paramref name="shape"/> for <paramref name="key"/> (best
        /// effort: an I/O failure only means the next process tunes again).</summary>
        public static void Store(string key, GpuGroupShape shape)
        {
            lock (s_lock)
            {
                var t = Table();
                t[key] = shape.ToString();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    string tmp = StorePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(tmp, StorePath, overwrite: true);
                }
                catch { }
            }
        }

        /// <summary>Test hook: forget the in-memory copy of the store (the file is
        /// re-read on next use).</summary>
        public static void ForgetLoadedStore() { lock (s_lock) { s_table = null; s_tablePath = null; } }

        private static int s_tuningRuns;

        /// <summary>How many tunings this process has run (tests: a stored winner is reused).</summary>
        public static int TuningRuns => System.Threading.Volatile.Read(ref s_tuningRuns);

        /// <summary>The last tuning this process ran (probes / logs).</summary>
        public static (string Key, GpuGroupShape Winner, IReadOnlyList<Timing> Timings)? LastTuning { get; private set; }

        /// <summary>The shape for <paramref name="slot"/> on this device: the override,
        /// else the stored winner, else a fresh tuning (<see cref="PickFastest"/> over
        /// the <paramref name="candidates"/> the device supports), stored.</summary>
        public static GpuGroupShape Resolve(string deviceKey, string slot, string sourceHash,
            IReadOnlyList<GpuGroupShape> candidates, Func<GpuGroupShape, double> measureMs)
        {
            if (Fixed(Setting, slot) is { } f) return f;
            string key = Key(deviceKey, slot, sourceHash);
            if (Stored(key) is { } s && Contains(candidates, s)) return s;
            var timings = new List<Timing>();
            long t0 = Stopwatch.GetTimestamp();
            var winner = PickFastest(candidates, measureMs, timings);
            System.Threading.Interlocked.Increment(ref s_tuningRuns);
            LastTuning = (key, winner, timings);
            Debug.WriteLine($"[GpuGroupShapes] {key}: {winner} in {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms ({string.Join(", ", timings)})");
            Store(key, winner);
            return winner;
        }

        private static bool Contains(IReadOnlyList<GpuGroupShape> list, GpuGroupShape s)
        {
            foreach (var c in list) if (c == s) return true;
            return false;
        }

        // ── the representative frames ────────────────────────────────────────

        private const int EscapeW = 512, EscapeH = 384, PerturbW = 256, PerturbH = 192, MaxIter = 1000;
        private const double SeahorseRe = -0.743643887037151, SeahorseIm = 0.13182590420533;
        private static (double[] Zr, double[] Zi, int Len)? s_orbit;

        /// <summary>Run <paramref name="slot"/>'s representative frame on
        /// <paramref name="kernel"/> (which must be dispatching its current trial
        /// shape) and return its GPU dispatch time, or the host's when the device
        /// has no timestamps. The view is the seahorse valley, boundary-heavy so the
        /// divergence a shape changes is in it: SP 512×384 at 1000 iterations;
        /// perturbation 256×192 at 1e5 zoom, 1000 iterations.</summary>
        public static double Representative(IGpuKernel kernel, string slot)
        {
            int w = slot == SlotEscape ? EscapeW : PerturbW, h = slot == SlotEscape ? EscapeH : PerturbH, n = w * h;
            var it = new int[n]; var sm = new float[n];
            var zr = new float[n]; var zi = new float[n]; var dr = new float[n]; var di = new float[n];
            if (slot == SlotEscape)
                kernel.Run(w, h, SeahorseRe, SeahorseIm, 0.004 / w, MaxIter, 4.0, it, sm, zr, zi, dr, di);
            else
            {
                var (oZr, oZi, len) = s_orbit ??= Orbit();
                double scale = 3.5 / (w * 1e5);
                if (slot == SlotPerturb)
                    kernel.RunPerturb(w, h, scale, MaxIter, 512.0 * 512.0, -w / 2, -h / 2, oZr, oZi, len, it, sm, zr, zi, dr, di);
                else
                    kernel.RunPerturbFloatExp(w, h, scale, MaxIter, 512.0 * 512.0, -w / 2, -h / 2, oZr, oZi, len, it, sm, zr, zi, dr, di);
            }
            return kernel.LastGpuStageTimes is { DispatchMs: > 0 } t ? t.DispatchMs : kernel.LastDispatchMs;
        }

        private static (double[] Zr, double[] Zi, int Len) Orbit()
        {
            var zr = new double[MaxIter + 1]; var zi = new double[MaxIter + 1];
            double x = 0, y = 0; int len = MaxIter;
            for (int i = 0; i < MaxIter; i++)
            {
                zr[i] = x; zi[i] = y;
                if (x * x + y * y >= 512.0 * 512.0) { len = i; break; }
                double nx = x * x - y * y + SeahorseRe; y = 2 * x * y + SeahorseIm; x = nx;
            }
            return (zr, zi, len);
        }
    }
}
