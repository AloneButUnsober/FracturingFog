// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Logging/ClusterLogger.cs
// Master-side NDJSON event log. One file per UTC day under
// %APPDATA%\FracturingFog\master-logs\cluster-yyyyMMdd.log so an operator
// debugging "why did worker X get marked stale yesterday" has a single
// chronological stream to grep without correlating per-session logs.
//
// Mirrors SessionLogger's bounded-channel pump-task pattern so a slow
// disk does not stall the cluster dispatch loop. Lines are JSON objects;
// every event carries an iso-8601 ts and a "kind" tag.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FracturingFog.Server.Logging;

public sealed class ClusterLogger : IDisposable
{
    private const int ChannelCapacity = 8192;
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _logDir;
    private readonly Channel<string> _channel;
    private readonly Task _pumpTask;
    private readonly CancellationTokenSource _pumpCts = new();
    private long _droppedCount;
    private bool _disposed;

    public ClusterLogger(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(logDir);
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        _pumpTask = Task.Run(PumpAsync);
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
        if (!_channel.Writer.TryWrite(json))
            Interlocked.Increment(ref _droppedCount);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (string line in _channel.Reader.ReadAllAsync(_pumpCts.Token).ConfigureAwait(false))
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
                    await sw.WriteLineAsync(line).ConfigureAwait(false);
                }
                catch { /* swallow per-line write errors */ }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _channel.Writer.TryComplete();
            try { _pumpTask.Wait(FlushTimeout); } catch { }
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
        }
        finally { try { _pumpCts.Dispose(); } catch { } }
    }
}
