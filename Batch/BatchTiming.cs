// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Batch/BatchTiming.cs
// #1640 — where a --batch run's wall time goes outside the render itself: process
// start-up before Main, the stores and GPU setup BatchEntry runs, and the file
// encode after PosterRenderer's own clock stops. Printed by --verbose so benchmark
// kits can see the split (rith's 0.7.1 benchmark measured ~1.6 s per image outside
// FF's own time; see Docs/Resources-Bibliography.md#rith-benchmark-thread).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FracturingFog.Batch
{
    internal static class BatchTiming
    {
        private static readonly long s_start = Stopwatch.GetTimestamp();
        private static long s_last = s_start;
        private static readonly List<(string Phase, double Ms)> s_phases = new();

        /// <summary>Time from process creation to the first call here (runtime
        /// start-up, assembly load, JIT of Main). -1 when the OS start time is unknown.</summary>
        public static double PreMainMs { get; } = MeasurePreMain();

        private static double MeasurePreMain()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                return Math.Max(0, (DateTime.Now - p.StartTime).TotalMilliseconds);
            }
            catch { return -1; }
        }

        /// <summary>Starts the clock (call first thing in BatchEntry.Run).</summary>
        public static void Start() => _ = PreMainMs;

        /// <summary>Closes the phase that ran since the previous mark.</summary>
        public static void Mark(string phase)
        {
            long now = Stopwatch.GetTimestamp();
            lock (s_phases) s_phases.Add((phase, Stopwatch.GetElapsedTime(s_last, now).TotalMilliseconds));
            s_last = now;
        }

        /// <summary>One line: <c>start-up 412 ms (pre-Main 300, stores 90, gpu 22) · ...</c>.</summary>
        public static string Format(IEnumerable<(string Phase, double Ms)>? extra = null)
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            if (PreMainMs >= 0) sb.Append(inv, $"pre-Main {PreMainMs:0} ms");
            lock (s_phases)
                foreach (var (phase, ms) in s_phases)
                    sb.Append(sb.Length > 0 ? ", " : "").Append(inv, $"{phase} {ms:0} ms");
            if (extra != null)
                foreach (var (phase, ms) in extra)
                    sb.Append(sb.Length > 0 ? ", " : "").Append(inv, $"{phase} {ms:0} ms");
            double total = Math.Max(0, PreMainMs) + Stopwatch.GetElapsedTime(s_start).TotalMilliseconds;
            sb.Append(inv, $"; total so far {total:0} ms");
            return sb.ToString();
        }
    }
}
