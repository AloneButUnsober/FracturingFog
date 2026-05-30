// Server/Logging/SessionLogger.cs
// Per-connection text log under %APPDATA%\FracturingFog\server-logs\.
// One file per accepted session: <utc>_<remote>_<conn-id>.log.
// First line records the client cert thumbprint so audit trails can map
// back to the cert that authenticated the session.

using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FracturingFog.Server.Logging;

public sealed class SessionLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private bool _disposed;

    public string Path { get; }
    public string SessionId { get; }

    private SessionLogger(string path, string sessionId, StreamWriter writer)
    {
        Path = path;
        SessionId = sessionId;
        _writer = writer;
    }

    public static SessionLogger Open(string logDir, string remoteEndpoint, string? clientCertThumbprint)
    {
        Directory.CreateDirectory(logDir);
        string sessionId = Guid.NewGuid().ToString("N")[..8];
        string safeRemote = SanitizeFileName(remoteEndpoint);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string path = System.IO.Path.Combine(logDir, $"{stamp}_{safeRemote}_{sessionId}.log");

        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var sw = new StreamWriter(fs) { AutoFlush = true };
        sw.WriteLine($"# FracturingFog server session {sessionId}");
        sw.WriteLine($"# opened     : {DateTime.UtcNow:O}");
        sw.WriteLine($"# remote     : {remoteEndpoint}");
        sw.WriteLine($"# clientCert : {clientCertThumbprint ?? "(none)"}");
        sw.WriteLine();
        return new SessionLogger(path, sessionId, sw);
    }

    public void Info(string line) => WriteLevel("INFO", line);
    public void Warn(string line) => WriteLevel("WARN", line);
    public void Err(string line)  => WriteLevel("ERR ", line);

    private void WriteLevel(string level, string line)
    {
        if (_disposed) return;
        lock (_lock)
        {
            _writer.WriteLine(
                $"{DateTime.UtcNow:HH:mm:ss.fff} [{level}] {line}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            lock (_lock)
            {
                _writer.WriteLine();
                _writer.WriteLine($"# closed     : {DateTime.UtcNow:O}");
                _writer.Flush();
                _writer.Dispose();
            }
        }
        catch { }
    }

    private static string SanitizeFileName(string s)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(':', '_');
    }
}
