// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Logging/ClusterLogger.cs
// Master-side NDJSON event log. One file per UTC day under
// %APPDATA%\FracturingFog\master-logs\cluster-yyyyMMdd.log so an operator
// debugging "why did worker X get marked stale yesterday" has a single
// chronological stream to grep without correlating per-session logs.
//
// Mirrors SessionLogger's bounded-queue pump: a dedicated background THREAD
// drains it so a slow disk does not stall the cluster dispatch loop. Lines
// are JSON objects; every event carries an iso-8601 ts and a "kind" tag.
// #1159 — a thread, not a Task.Run pump: under thread-pool starvation the old
// async pump might never run before Dispose's 3 s wait gave up, losing the
// queued events (same flaw fixed in SessionLogger, #1157).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;

namespace FracturingFog.Server.Logging;

public sealed class ClusterLogger : IDisposable
{
    private const int QueueCapacity = 8192;
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _logDir;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), QueueCapacity);
    private readonly Thread _pump;
    private long _droppedCount;
    private volatile bool _disposed;

    public ClusterLogger(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(logDir);
        _pump = new Thread(Pump) { IsBackground = true, Name = "cluster-log" };
        _pump.Start();
    }

    /// <summary>Append an event. <paramref name="fields"/> is appended as
    /// flat key/value pairs alongside "ts" and "kind".</summary>
    public void Event(string kind, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (_disposed) return;
        var line = new Dictionary<string, object?>(capacity: 4 + (fields?.Count ?? 0))
        {
            ["ts"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["kind"] = kind,
        };
        if (fields != null)
            foreach (var kv in fields) line[kv.Key] = kv.Value;

        string json;
        try { json = JsonSerializer.Serialize(line, JsonOpts); }
        catch { return; }  // never throw from a logger
        bool added;
        // Full queue drops the NEW event (keeps the earlier stream); a write
        // racing Dispose's CompleteAdding is a write after dispose — ignored.
        try { added = _queue.TryAdd(json); }
        catch (InvalidOperationException) { return; }
        if (!added) Interlocked.Increment(ref _droppedCount);
    }

    private void Pump()
    {
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                string path = Path.Combine(_logDir,
                    $"cluster-{DateTime.UtcNow:yyyyMMdd}.log");
                try
                {
                    // FileStream-per-line is intentional: NDJSON files are
                    // append-friendly, days roll at midnight without us
                    // needing to detect the rollover ourselves, and a
                    // crashed master leaves no half-open handle. Tiny perf
                    // cost is fine — cluster events are O(workers * 1/s),
                    // not O(pixels).
                    using var fs = new FileStream(path,
                        FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var sw = new StreamWriter(fs);
                    sw.WriteLine(line);
                }
                catch { /* swallow per-line write errors */ }
            }
        }
        catch { /* queue disposed under us — shutdown */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Drain then join: the pump writes every queued event and exits
            // before the drop note below, so the note is always last.
            _queue.CompleteAdding();
            if (!_pump.Join(FlushTimeout)) return;   // pathological disk: don't race the pump
            long dropped = Interlocked.Read(ref _droppedCount);
            if (dropped > 0)
            {
                // Best-effort note when we lost events due to disk pressure.
                try
                {
                    string path = Path.Combine(_logDir,
                        $"cluster-{DateTime.UtcNow:yyyyMMdd}.log");
                    File.AppendAllText(path,
                        $"{{\"ts\":\"{DateTime.UtcNow:O}\",\"kind\":\"logger-drop\"," +
                        $"\"droppedLines\":{dropped}}}\n");
                }
                catch { }
            }
            _queue.Dispose();
        }
        catch { /* never throw from a logger */ }
    }
}
