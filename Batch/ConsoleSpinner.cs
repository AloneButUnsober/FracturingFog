// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Batch/ConsoleSpinner.cs
// Indeterminate-progress spinner for tasks that don't expose a percent
// callback (a single offscreen image render finishes when Calculate()
// returns). Ticks on a background thread; Stop() prints a final line.

using System;
using System.Diagnostics;
using System.Threading;

namespace FracturingFog.Batch
{
    public sealed class ConsoleSpinner : IDisposable
    {
        private static readonly char[] Frames = { '|', '/', '-', '\\' };

        private readonly string _label;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private int _lastLineLen;

        public ConsoleSpinner(string label)
        {
            _label = label;
            _thread = new Thread(Loop) { IsBackground = true, Name = "BatchSpinner" };
            _thread.Start();
        }

        private void Loop()
        {
            int i = 0;
            while (!_cts.IsCancellationRequested)
            {
                char f = Frames[i % Frames.Length];
                string elapsed = FormatDuration(_sw.Elapsed);
                string line = $"{_label} {f}  elapsed {elapsed}";
                if (line.Length < _lastLineLen)
                    line += new string(' ', _lastLineLen - line.Length);
                _lastLineLen = line.Length;
                try { Console.Write('\r'); Console.Write(line); } catch { }
                i++;
                try { Thread.Sleep(120); } catch { }
            }
        }

        public void Stop(string? finalMessage = null)
        {
            _cts.Cancel();
            try { _thread.Join(500); } catch { }
            string elapsed = FormatDuration(_sw.Elapsed);
            string line = $"{_label} done  elapsed {elapsed}";
            if (!string.IsNullOrEmpty(finalMessage)) line += "  " + finalMessage;
            if (line.Length < _lastLineLen)
                line += new string(' ', _lastLineLen - line.Length);
            try { Console.Write('\r'); Console.WriteLine(line); } catch { }
        }

        public void Dispose() => _cts.Dispose();

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1.0)
                return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
            return $"{t.Minutes:D2}:{t.Seconds:D2}";
        }
    }
}
