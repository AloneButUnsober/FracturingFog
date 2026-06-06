// Server/Metrics.cs
// Lock-free counters surfaced through server.status. Polled by ServerAdmin.

using System;
using System.Threading;

namespace FracturingFog.Server;

public sealed class Metrics
{
    private int _inFlight;
    private long _completed;
    private long _failed;
    // Code + message published as a single immutable record so that
    // status readers never see a code from request A paired with a
    // message from request B when two failures interleave (queueDepth>1).
    private LastError? _lastError;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public int InFlight => Volatile.Read(ref _inFlight);
    public long Completed => Interlocked.Read(ref _completed);
    public long Failed => Interlocked.Read(ref _failed);

    public string? LastErrorCode    => Volatile.Read(ref _lastError)?.Code;
    public string? LastErrorMessage => Volatile.Read(ref _lastError)?.Message;

    public long UptimeSeconds => (long)(DateTime.UtcNow - _startedUtc).TotalSeconds;

    public IDisposable BeginRender()
    {
        Interlocked.Increment(ref _inFlight);
        return new InFlightScope(this);
    }

    public void RecordSuccess() => Interlocked.Increment(ref _completed);

    public void RecordFailure(string code, string message)
    {
        Interlocked.Increment(ref _failed);
        Volatile.Write(ref _lastError, new LastError(code, SanitizeForStatus(message)));
    }

    private sealed record LastError(string Code, string Message);

    /// <summary>Strips host-side paths + truncates so server.status does
    /// not echo filesystem layout or full stack traces to any authenticated
    /// client. Keeps the human-readable head of the message (typically the
    /// exception type + a short reason) and drops the rest.</summary>
    private static string SanitizeForStatus(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // Replace anything that looks like a Windows drive-letter path
        // (C:\..., D:\...) or a POSIX absolute path (/var/lib/..., /Users/..)
        // with a placeholder. Conservative regex — over-redacts rather than
        // leaks. Errors strings sanitized here become read by the
        // ServerAdmin polling status, so trim aggressively.
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"[A-Za-z]:\\[^\s""'<>|*?]+", "<path>");
        s = System.Text.RegularExpressions.Regex.Replace(s,
            @"(?<=\s|^)/(?:[A-Za-z0-9._-]+/)+[A-Za-z0-9._-]+", "<path>");
        const int MaxStatusMessage = 240;
        if (s.Length > MaxStatusMessage)
            s = s[..MaxStatusMessage] + "…";
        return s;
    }

    private sealed class InFlightScope : IDisposable
    {
        private readonly Metrics _m;
        private int _done;
        public InFlightScope(Metrics m) => _m = m;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref _m._inFlight);
        }
    }
}
