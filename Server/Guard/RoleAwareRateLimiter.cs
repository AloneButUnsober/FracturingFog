// Server/Guard/RoleAwareRateLimiter.cs
// Per-method, per-role rate limiter for dispatched JSON-RPC calls. Layered
// on top of the existing per-IP TCP-accept limiter (EndpointRateLimiter):
// that one bounds connection-establish churn before the TLS handshake; this
// one bounds call churn INSIDE an authenticated session.
//
// Policy per role (D-6c dev plan §6.6):
//
//   * client → per-IP token bucket. Caps how often a single client (UI or
//     batch driver) can hit job.submit / job.status / job.fetch / job.cancel
//     during a long-lived session. The per-IP TCP-accept limiter does not
//     cover this surface because a client's session stays open and reuses
//     one socket for many calls.
//
//   * worker → per-thumbprint token bucket, applied ONLY to "tile.next".
//     Workers hold one long session and long-poll tile.next in a tight loop;
//     a runaway worker (or a buggy build) can spin the long-poll at
//     thousands per second and exhaust the master's dispatcher bookkeeping.
//     worker.register, worker.heartbeat, tile.deliver and tile.error are
//     intrinsically bounded by other mechanisms (heartbeat cadence, one
//     deliver per assigned tile) so they bypass this limiter.
//
//   * admin → always allowed. The dev plan §6.6 ask is "Admin-role:
//     unlimited but log every call." The audit-log side of that ask is
//     emitted by ClusterCoordinator when an admin invokes a cluster.* RPC.
//
// A per-minute setting of 0 disables the policy for that role — same
// convention as EndpointRateLimiter. The bucket maps clean themselves
// every minute to bound memory under churn from many transient keys.

using System;
using System.Collections.Concurrent;
using System.Threading;

using FracturingFog.Server.Tls;

namespace FracturingFog.Server.Guard;

public enum RoleLimiterDecision
{
    Allow,
    RefusedRate,
}

public sealed class RoleAwareRateLimiter
{
    private readonly Bucket _client;
    private readonly Bucket _workerTileNext;

    /// <summary><paramref name="clientPerMinute"/> + <paramref name="clientBurst"/>
    /// govern per-IP client-call rate. <paramref name="workerTileNextPerMinute"/>
    /// + <paramref name="workerTileNextBurst"/> govern per-thumbprint
    /// worker tile.next churn. A perMinute of 0 disables that policy
    /// (admin role always allowed regardless).</summary>
    public RoleAwareRateLimiter(
        int clientPerMinute, int clientBurst,
        int workerTileNextPerMinute, int workerTileNextBurst)
    {
        _client         = new Bucket(clientPerMinute, clientBurst);
        _workerTileNext = new Bucket(workerTileNextPerMinute, workerTileNextBurst);
    }

    public bool ClientEnabled         => _client.Enabled;
    public bool WorkerTileNextEnabled => _workerTileNext.Enabled;

    /// <summary>D-6c1 — swap the rate / burst on both buckets without
    /// rebuilding the limiter, so cluster.config.set takes effect on the
    /// next call without dropping in-flight per-key state. perMinute = 0
    /// disables that bucket (same convention as the constructor).</summary>
    public void Reconfigure(
        int clientPerMinute, int clientBurst,
        int workerTileNextPerMinute, int workerTileNextBurst)
    {
        _client.Reconfigure(clientPerMinute, clientBurst);
        _workerTileNext.Reconfigure(workerTileNextPerMinute, workerTileNextBurst);
    }

    /// <summary>Resolve whether <paramref name="role"/> may execute
    /// <paramref name="method"/> right now. <paramref name="key"/> is the
    /// per-role bucket key (remote IP for client, cert thumbprint for
    /// worker; ignored for admin).</summary>
    public RoleLimiterDecision TryAccept(CertRole role, string key, string method)
    {
        switch (role)
        {
            case CertRole.Admin:
                // Admin: never refused. Audit log handled cluster-side.
                return RoleLimiterDecision.Allow;

            case CertRole.Worker:
                // Only tile.next is rate-gated; the other worker methods
                // are bounded by protocol cadence (heartbeat) or by
                // per-tile budget (deliver/error/register).
                if (!_workerTileNext.Enabled) return RoleLimiterDecision.Allow;
                if (!string.Equals(method, "tile.next", StringComparison.Ordinal))
                    return RoleLimiterDecision.Allow;
                return _workerTileNext.TryTake(key)
                    ? RoleLimiterDecision.Allow
                    : RoleLimiterDecision.RefusedRate;

            default: // CertRole.Client
                if (!_client.Enabled) return RoleLimiterDecision.Allow;
                return _client.TryTake(key)
                    ? RoleLimiterDecision.Allow
                    : RoleLimiterDecision.RefusedRate;
        }
    }

    /// <summary>String-keyed token bucket map. Mirrors
    /// <see cref="EndpointRateLimiter"/>'s internal Bucket but keyed by
    /// caller-supplied string so we can mix IP-keyed (client) and
    /// thumbprint-keyed (worker) policies in one limiter.</summary>
    private sealed class Bucket
    {
        private double _ratePerSec;
        private double _capacity;
        private readonly ConcurrentDictionary<string, Slot> _slots = new();
        private long _lastSweepTicks = DateTime.UtcNow.Ticks;

        public Bucket(int perMinute, int burst)
        {
            _ratePerSec = perMinute > 0 ? perMinute / 60.0 : 0.0;
            _capacity   = Math.Max(1, burst);
        }

        public bool Enabled => Volatile.Read(ref _ratePerSec) > 0;

        public void Reconfigure(int perMinute, int burst)
        {
            // Volatile write so a concurrent TryTake reading these on the
            // next call sees the new value. Per-slot _tokens stay as-is —
            // callers in flight keep the burst they already accrued.
            Volatile.Write(ref _ratePerSec, perMinute > 0 ? perMinute / 60.0 : 0.0);
            Volatile.Write(ref _capacity,   Math.Max(1, burst));
        }

        public bool TryTake(string key)
        {
            double rate = Volatile.Read(ref _ratePerSec);
            double cap  = Volatile.Read(ref _capacity);
            var slot = _slots.GetOrAdd(key ?? "(null)", _ => new Slot(cap));
            bool ok = slot.TryTake(rate, cap);
            SweepIfDue();
            return ok;
        }

        private void SweepIfDue()
        {
            long now  = DateTime.UtcNow.Ticks;
            long last = System.Threading.Interlocked.Read(ref _lastSweepTicks);
            if (new TimeSpan(now - last) < TimeSpan.FromMinutes(1)) return;
            if (System.Threading.Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last) return;

            double cap = Volatile.Read(ref _capacity);
            long staleCutoffTicks = DateTime.UtcNow.AddMinutes(-10).Ticks;
            foreach (var kv in _slots)
                if (kv.Value.IsIdleSince(staleCutoffTicks, cap))
                    _slots.TryRemove(kv.Key, out _);
        }

        private sealed class Slot
        {
            private readonly object _lock = new();
            private double _tokens;
            private long _lastTicks;

            public Slot(double initial) { _tokens = initial; _lastTicks = DateTime.UtcNow.Ticks; }

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
}
