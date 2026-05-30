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
    private string? _lastErrorCode;
    private string? _lastErrorMessage;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public int InFlight => Volatile.Read(ref _inFlight);
    public long Completed => Interlocked.Read(ref _completed);
    public long Failed => Interlocked.Read(ref _failed);

    public string? LastErrorCode    => Volatile.Read(ref _lastErrorCode);
    public string? LastErrorMessage => Volatile.Read(ref _lastErrorMessage);

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
        Volatile.Write(ref _lastErrorCode, code);
        Volatile.Write(ref _lastErrorMessage, message);
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
