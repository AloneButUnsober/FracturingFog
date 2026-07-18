// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Batch/ConsoleProgress.cs
// Single-line CR-overwrite progress bar with percent + ETA. Safe to call
// many times per second; throttles redraws to ~20 Hz so we don't drown the
// console host.

using System;
using System.Diagnostics;

namespace FracturingFog.Batch
{
    public sealed class ConsoleProgress
    {
        private readonly string _label;
        private readonly int _barWidth;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _lastDrawMs = -1;
        private int _lastLineLen;

        public ConsoleProgress(string label, int barWidth = 32)
        {
            _label = label;
            _barWidth = Math.Max(8, barWidth);
        }

        /// <summary>
        /// Update progress. fraction is 0.0..1.0. extra is appended after ETA.
        /// </summary>
        public void Report(double fraction, string? extra = null)
        {
            if (fraction < 0.0) fraction = 0.0;
            else if (fraction > 1.0) fraction = 1.0;

            long nowMs = _sw.ElapsedMilliseconds;
            // Throttle redraws unless near completion.
            if (_lastDrawMs >= 0 && nowMs - _lastDrawMs < 50 && fraction < 1.0) return;
            _lastDrawMs = nowMs;

            int filled = (int)Math.Round(fraction * _barWidth);
            if (filled > _barWidth) filled = _barWidth;
            string bar = new string('#', filled) + new string('-', _barWidth - filled);

            double pct = fraction * 100.0;
            string eta;
            if (fraction > 0.001)
            {
                double totalEst = nowMs / fraction;
                double remainMs = totalEst - nowMs;
                eta = FormatDuration(TimeSpan.FromMilliseconds(remainMs));
            }
            else
            {
                eta = "--:--";
            }
            string elapsed = FormatDuration(_sw.Elapsed);

            string line = $"{_label} [{bar}] {pct,5:F1}%  elapsed {elapsed}  eta {eta}";
            if (!string.IsNullOrEmpty(extra)) line += "  " + extra;

            // Pad to clear previous line remnants when shrinking.
            if (line.Length < _lastLineLen)
                line = line + new string(' ', _lastLineLen - line.Length);
            _lastLineLen = line.Length;

            try
            {
                Console.Write('\r');
                Console.Write(line);
            }
            catch { /* console may be redirected/closed — ignore */ }
        }

        public void Finish(string? finalMessage = null)
        {
            Report(1.0, finalMessage);
            try { Console.WriteLine(); } catch { }
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1.0)
                return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
            return $"{t.Minutes:D2}:{t.Seconds:D2}";
        }
    }
}
