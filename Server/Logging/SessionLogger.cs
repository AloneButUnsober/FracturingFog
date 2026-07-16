// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Logging/SessionLogger.cs
// Per-connection text log under %APPDATA%\FracturingFog\server-logs\.
// One file per accepted session: <utc>_<remote>_<conn-id>.log.
// First line records the client cert thumbprint so audit trails can map
// back to the cert that authenticated the session.
//
// Sync file IO is OFF the call path: Info/Warn/Err enqueue a formatted
// line on a bounded channel; a single background task drains it. A slow
// disk no longer blocks the render loop or the TLS accept loop, and a
// burst of frame-progress lines never makes the worker wait on flush.
// Dispose() signals the pump to drain remaining lines and waits up to
// the flush timeout so crashes still produce visible tail content.

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FracturingFog.Server.Logging;

public sealed class SessionLogger : IDisposable
{
    /// <summary>Upper bound on queued lines. Bounded to keep memory finite
    /// when the disk is slow; FullMode.DropWrite drops *new* lines once
    /// the queue is saturated, preserving the earlier tail of the session
    /// for diagnosis instead of evicting it.</summary>
    private const int ChannelCapacity = 4096;

    /// <summary>Maximum time Dispose() waits for the pump to drain before
    /// abandoning the tail. A misbehaving disk should not stall server
    /// shutdown forever.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(3);

    private readonly StreamWriter _writer;
    private readonly Channel<string> _channel;
    private readonly Task _pumpTask;
    private readonly CancellationTokenSource _pumpCts = new();
    private long _droppedCount;
    private bool _disposed;

    public string Path { get; }
    public string SessionId { get; }

    private SessionLogger(string path, string sessionId, StreamWriter writer)
    {
        Path = path;
        SessionId = sessionId;
        _writer = writer;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        _pumpTask = Task.Run(PumpAsync);
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
        if (!_channel.Writer.TryWrite(formatted))
            Interlocked.Increment(ref _droppedCount);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (string line in _channel.Reader.ReadAllAsync(_pumpCts.Token).ConfigureAwait(false))
            {
                try { await _writer.WriteLineAsync(line).ConfigureAwait(false); }
                catch (Exception) { /* swallow per-line write errors — log file may have been rotated */ }
            }
            // Defer the flush so a burst of lines coalesces into one disk
            // write under load. The final flush below catches the tail
            // when the channel completes.
            try { await _writer.FlushAsync().ConfigureAwait(false); } catch { }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Signal end-of-stream to the pump so it drains remaining
            // queued lines, then write the close marker AFTER the pump
            // finishes so the close line is always last in the file.
            _channel.Writer.TryComplete();
            try { _pumpTask.Wait(FlushTimeout); } catch { }

            long dropped = Interlocked.Read(ref _droppedCount);
            if (dropped > 0)
                _writer.WriteLine($"# WARN: {dropped} log line(s) dropped due to slow disk / full queue");
            _writer.WriteLine();
            _writer.WriteLine($"# closed     : {DateTime.UtcNow:O}");
            _writer.Flush();
            _writer.Dispose();
        }
        catch { }
        finally { try { _pumpCts.Dispose(); } catch { } }
    }

    private static string SanitizeFileName(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(':', '_');
    }
}
