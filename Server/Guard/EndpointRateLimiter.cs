// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Guard/EndpointRateLimiter.cs
// Token-bucket rate limiter keyed on remote IP. Cheap, in-process, no
// allocation on the hot path once a bucket exists. Bounds the cost of a
// burst of TCP SYNs from a single attacker by rejecting (closing) excess
// connections BEFORE the TLS handshake runs — TLS handshake is the part
// that pins a thread + holds an SslStream resident.

using System;
using System.Collections.Concurrent;
using System.Net;

namespace FracturingFog.Server.Guard;

public sealed class EndpointRateLimiter
{
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private long _lastSweepTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// <paramref name="perMinute"/> = sustained accepts allowed per IP per
    /// minute. <paramref name="burst"/> = bucket capacity (initial allowance
    /// + maximum standing). Disabled when perMinute &lt;= 0.
    /// </summary>
    public EndpointRateLimiter(int perMinute, int burst)
    {
        _ratePerSecond = perMinute > 0 ? perMinute / 60.0 : 0.0;
        _capacity = Math.Max(1, burst);
    }

    public bool Enabled => _ratePerSecond > 0;

    /// <summary>Returns true when an accept from <paramref name="remote"/>
    /// is allowed and consumes one token. Returns false when the bucket is
    /// empty (caller should close the TCP socket immediately).</summary>
    public bool TryAccept(IPEndPoint? remote)
    {
        if (!Enabled) return true;
        string key = remote?.Address?.ToString() ?? "(unknown)";
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(_capacity));
        bool ok = bucket.TryTake(_ratePerSecond, _capacity);
        SweepIfDue();
        return ok;
    }

    /// <summary>Drop buckets that have been full + idle for &gt; 10 min so
    /// long-running servers do not accumulate one entry per scanner IP that
    /// ever hit the listener. Cheap O(n) walk gated to once per minute.</summary>
    private void SweepIfDue()
    {
        long now = DateTime.UtcNow.Ticks;
        long last = System.Threading.Interlocked.Read(ref _lastSweepTicks);
        if (new TimeSpan(now - last) < TimeSpan.FromMinutes(1)) return;
        if (System.Threading.Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last) return;

        var staleCutoffTicks = DateTime.UtcNow.AddMinutes(-10).Ticks;
        foreach (var kv in _buckets)
        {
            if (kv.Value.IsIdleSince(staleCutoffTicks, _capacity))
                _buckets.TryRemove(kv.Key, out _);
        }
    }

    private sealed class Bucket
    {
        private readonly object _lock = new();
        private double _tokens;
        private long _lastTicks;

        public Bucket(double initial) { _tokens = initial; _lastTicks = DateTime.UtcNow.Ticks; }

        public bool TryTake(double ratePerSec, double capacity)
        {
            lock (_lock)
            {
                long now = DateTime.UtcNow.Ticks;
                double elapsed = new TimeSpan(now - _lastTicks).TotalSeconds;
                _lastTicks = now;
                _tokens = Math.Min(capacity, _tokens + elapsed * ratePerSec);
                if (_tokens >= 1.0) { _tokens -= 1.0; return true; }
                return false;
            }
        }

        public bool IsIdleSince(long staleCutoffTicks, double capacity)
        {
            lock (_lock)
                return _lastTicks < staleCutoffTicks && _tokens >= capacity - 0.01;
        }
    }
}
