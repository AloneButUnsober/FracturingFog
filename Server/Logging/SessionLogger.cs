// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Logging/SessionLogger.cs
// Per-connection text log under %APPDATA%\FracturingFog\server-logs\.
// One file per accepted session: <utc>_<remote>_<conn-id>.log.
// First line records the client cert thumbprint so audit trails can map
// back to the cert that authenticated the session.
//
// Sync file IO is OFF the call path: Info/Warn/Err enqueue a formatted
// line on a bounded queue; a dedicated background THREAD drains it. A slow
// disk no longer blocks the render loop or the TLS accept loop, and a burst
// of frame-progress lines never makes the caller wait on IO.
//
// Why a thread, not a thread-pool task: the drain used to be an async
// Task.Run pump. Under thread-pool starvation (a loaded server, or a
// saturated test run) it might not have run at all by the time Dispose()
// gave up waiting (3 s) — Dispose then wrote the close marker and closed the
// writer, losing every queued line, and a late pump could race Dispose on the
// same StreamWriter. A dedicated thread is scheduled independently of the
// pool, is the ONLY writer until it exits, and Dispose joins it before
// touching the file. The file is flushed whenever the queue runs empty, so a
// crash still leaves the tail on disk.

using System;
using System.Globalization;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;

namespace FracturingFog.Server.Logging;

public sealed class SessionLogger : IDisposable
{
    /// <summary>Upper bound on queued lines. Bounded to keep memory finite
    /// when the disk is slow; a full queue drops *new* lines (TryAdd fails),
    /// preserving the earlier tail of the session for diagnosis instead of
    /// evicting it.</summary>
    private const int QueueCapacity = 4096;

    /// <summary>Maximum time Dispose() waits for the pump thread to drain. Only
    /// a disk whose writes block longer than this hits it (the pump no longer
    /// waits for a thread-pool thread); shutdown must not stall forever.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(3);

    private readonly StreamWriter _writer;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), QueueCapacity);
    private readonly Thread _pump;
    private long _droppedCount;
    private volatile bool _disposed;

    public string Path { get; }
    public string SessionId { get; }

    private SessionLogger(string path, string sessionId, StreamWriter writer)
    {
        Path = path;
        SessionId = sessionId;
        _writer = writer;
        _pump = new Thread(Pump) { IsBackground = true, Name = $"session-log-{sessionId}" };
        _pump.Start();
    }

    public static SessionLogger Open(string logDir, string remoteEndpoint, string? clientCertThumbprint)
    {
        Directory.CreateDirectory(logDir);
        string sessionId = Guid.NewGuid().ToString("N")[..8];
        string safeRemote = SanitizeFileName(remoteEndpoint);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        // Retry on collision rather than throw. GUID prefix makes same-
        // second + same-remote collisions vanishingly rare, but if it
        // happens (or the FS already holds the same name from a prior
        // crash mid-write) we don't want the whole session to abort.
        string path;
        FileStream fs;
        int attempt = 0;
        while (true)
        {
            string suffix = attempt == 0 ? sessionId : $"{sessionId}_{attempt}";
            path = System.IO.Path.Combine(logDir, $"{stamp}_{safeRemote}_{suffix}.log");
            try
            {
                fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                break;
            }
            catch (IOException) when (attempt < 8) { attempt++; }
        }
        var sw = new StreamWriter(fs);
        // Header lines go through the writer synchronously BEFORE the
        // pump starts handling user lines — guarantees the session-open
        // metadata is the first thing in the file even if the very first
        // user line is dropped due to a slow disk + DropWrite policy.
        sw.WriteLine($"# FracturingFog server session {sessionId}");
        sw.WriteLine($"# opened     : {DateTime.UtcNow:O}");
        sw.WriteLine($"# remote     : {remoteEndpoint}");
        sw.WriteLine($"# clientCert : {clientCertThumbprint ?? "(none)"}");
        sw.WriteLine();
        sw.Flush();
        return new SessionLogger(path, sessionId, sw);
    }

    public void Info(string line) => Enqueue("INFO", line);
    public void Warn(string line) => Enqueue("WARN", line);
    public void Err(string line)  => Enqueue("ERR ", line);

    private void Enqueue(string level, string line)
    {
        if (_disposed) return;
        string formatted = $"{DateTime.UtcNow:HH:mm:ss.fff} [{level}] {line}";
        bool added;
        // Bounded + non-blocking: a full queue drops the NEW line (keeps the
        // earlier tail for diagnosis). A write racing Dispose's CompleteAdding
        // is a write after dispose — ignored.
        try { added = _queue.TryAdd(formatted); }
        catch (InvalidOperationException) { return; }
        if (!added) Interlocked.Increment(ref _droppedCount);
    }

    private void Pump()
    {
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    _writer.WriteLine(line);
                    // Coalesce: flush only when nothing else is waiting, so a burst
                    // is one disk write but an idle session's tail is on disk.
                    if (_queue.Count == 0) _writer.Flush();
                }
                catch (Exception) { /* swallow per-line write errors — log file may have been rotated */ }
            }
        }
        catch (Exception) { /* queue disposed under us — Dispose owns the file from here */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // End-of-stream: the pump drains every queued line, then exits. Join
            // it before touching the writer so it is never used by two threads;
            // the close marker is therefore always last in the file.
            _queue.CompleteAdding();
            bool drained = _pump.Join(FlushTimeout);

            long dropped = Interlocked.Read(ref _droppedCount);
            if (!drained)
            {
                // Pathological disk (writes blocked > timeout): leave the pump to
                // finish on its own rather than race it on the writer.
                return;
            }
            if (dropped > 0)
                _writer.WriteLine($"# WARN: {dropped} log line(s) dropped due to slow disk / full queue");
            _writer.WriteLine();
            _writer.WriteLine($"# closed     : {DateTime.UtcNow:O}");
            _writer.Flush();
            _writer.Dispose();
            _queue.Dispose();
        }
        catch { }
    }

    private static string SanitizeFileName(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(':', '_');
    }
}
