// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/Services/BatchProcessRunner.cs
// #999 (CB7 of #64) — run a batch command from inside the app: launch the
// executable as a child process with the argument LIST (no shell, so no
// quoting at all), stream its stdout / stderr into the Command panel, cancel
// by killing the process tree.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.UI.Avalonia.Services;

/// <summary>Splits console output into lines, honouring carriage-return
/// overwrite (a CR-redrawn progress bar): text ended by a lone <c>\r</c> is
/// emitted as a <em>live</em> line that the next emission replaces; <c>\n</c>
/// (or <c>\r\n</c>) commits a line.</summary>
public sealed class ConsoleLineSplitter
{
    private readonly Action<string, bool> _emit;   // (text, live)
    private readonly StringBuilder _line = new();
    private bool _pendingCr;

    public ConsoleLineSplitter(Action<string, bool> emit) => _emit = emit;

    public void Feed(ReadOnlySpan<char> chars)
    {
        foreach (char c in chars)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                if (c == '\n') { Commit(); continue; }   // \r\n
                Overwrite();                             // lone \r
            }
            if (c == '\r') _pendingCr = true;
            else if (c == '\n') Commit();
            else _line.Append(c);
        }
    }

    /// <summary>End of stream: flush whatever is pending.</summary>
    public void Flush()
    {
        if (_pendingCr) { _pendingCr = false; Overwrite(); }
        if (_line.Length > 0) Commit();
    }

    private void Commit() { _emit(_line.ToString(), false); _line.Clear(); }
    private void Overwrite() { _emit(_line.ToString(), true); _line.Clear(); }
}

/// <summary>A bounded console log fed by <see cref="ConsoleLineSplitter"/>.</summary>
public sealed class ConsoleLog
{
    private readonly List<string> _lines = new();
    private readonly int _max;
    private bool _lastLive;

    public ConsoleLog(int maxLines = 400) => _max = maxLines;

    public void Add(string text, bool live)
    {
        if (_lastLive && _lines.Count > 0) _lines[^1] = text;
        else _lines.Add(text);
        _lastLive = live;
        if (_lines.Count > _max) _lines.RemoveRange(0, _lines.Count - _max);
    }

    public void Clear() { _lines.Clear(); _lastLive = false; }

    public IReadOnlyList<string> Lines => _lines;

    public override string ToString() => string.Join(Environment.NewLine, _lines);
}

public static class BatchProcessRunner
{
    /// <summary>Run <paramref name="executable"/> with <paramref name="args"/>
    /// (passed as a list — no shell parsing), streaming output lines to
    /// <paramref name="onOutput"/> (text, live). Cancellation kills the process
    /// tree. Returns the exit code, or null when cancelled.</summary>
    public static async Task<int?> RunAsync(
        string executable, IReadOnlyList<string> args, Action<string, bool> onOutput, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {executable}.");
        // One splitter per stream (a CR redraw belongs to its own stream), one
        // lock so the two streams' lines interleave whole.
        var gate = new object();
        void Emit(string text, bool live) { lock (gate) onOutput(text, live); }
        var pumps = Task.WhenAll(Pump(process.StandardOutput, Emit), Pump(process.StandardError, Emit));

        bool cancelled = false;
        using (cancel.Register(() =>
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); } catch { }
        }))
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await pumps.ConfigureAwait(false);
        }
        return cancelled ? null : process.ExitCode;
    }

    private static async Task Pump(StreamReader reader, Action<string, bool> emit)
    {
        var splitter = new ConsoleLineSplitter(emit);
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            splitter.Feed(buffer.AsSpan(0, n));
        splitter.Flush();
    }
}
