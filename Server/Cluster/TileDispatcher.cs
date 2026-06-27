// Server/Cluster/TileDispatcher.cs
// Per-job tile work queue + in-flight tracking + retry budget. Backs
// the coordinator's tile.next long-poll: workers ask for a tile, the
// dispatcher hands them one if any are pending, otherwise the call
// blocks on a TaskCompletionSource that is signalled when:
//   * a new tile is enqueued (worker.tile.error retried, or a job
//     submission that just landed)
//   * the master is shutting down
//   * the per-call CancellationToken fires (worker disconnect)
//
// Threading: one shared dispatcher serves all jobs. State maps are
// ConcurrentDictionary-keyed. A single per-dispatcher monitor guards
// the pending-queue + awaiter-list interplay (claim ↔ release is the
// hot path and a coarse lock keeps the code obvious).
//
// Retry: each tile carries an attempt counter. Master ships attempts
// 1..MaxAttempts; after the last attempt fails the whole job goes
// "failed". The dev plan (§9 D-2 acceptance) allows non-determinism
// only on tie-broken outputs, so a tile that fails late is fatal —
// no silent fallback to a partial image.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Server.Cluster.Protocol;

namespace FracturingFog.Server.Cluster;

public sealed class TileDispatcher
{
    public int MaxAttempts { get; init; } = 3;

    /// <summary>How long a tile.next caller waits when the queue is
    /// empty before the coordinator returns WaitAgain=true. The
    /// coordinator's TileNextHold drives the actual call-side timeout —
    /// this is the dispatcher-internal default used when callers don't
    /// pass their own deadline.</summary>
    public TimeSpan DefaultWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();

    // jobId → job state. Coordinator adds a job on submit, removes on
    // terminal state.
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);

    // Awaiters queued waiting for tiles. New tiles signal one of them
    // (FIFO); a job-submit also signals every awaiter (any may match).
    private readonly LinkedList<TaskCompletionSource<bool>> _awaiters = new();

    /// <summary>Enqueue a fresh job's tiles. Workers idle on tile.next
    /// get a wake-up so they immediately claim one.</summary>
    public void EnqueueJob(string jobId, IReadOnlyList<TileJobDto> tiles)
    {
        if (tiles.Count == 0) return;
        var st = _jobs.GetOrAdd(jobId, _ => new JobState());
        lock (_lock)
        {
            foreach (var t in tiles)
            {
                t.Attempt = 1;
                st.Pending.Enqueue(t);
            }
        }
        SignalAll();
    }

    /// <summary>Mark a job done — no more tile.next calls will receive
    /// its tiles. Any tiles still in the queue are discarded (race
    /// with a cancellation that beat a worker's claim).</summary>
    public void RetireJob(string jobId)
    {
        if (!_jobs.TryRemove(jobId, out _)) return;
        // Awaiters that picked up nothing this round will see "no jobs"
        // on their next iteration and return WaitAgain themselves.
    }

    /// <summary>Long-poll for the next tile. Returns null on timeout or
    /// cancellation — caller signals the worker WaitAgain=true.</summary>
    public async Task<TileJobDto?> ClaimNextAsync(string workerId, TimeSpan? wait, CancellationToken ct)
    {
        TimeSpan waitFor = wait ?? DefaultWaitTimeout;
        var deadline = DateTime.UtcNow + waitFor;

        while (!ct.IsCancellationRequested)
        {
            TileJobDto? tile;
            TaskCompletionSource<bool>? awaiter;
            lock (_lock)
            {
                tile = TryClaimAnyLocked(workerId);
                if (tile != null) return tile;

                // Nothing available right now — register and sleep.
                awaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _awaiters.AddLast(awaiter);
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                RemoveAwaiter(awaiter);
                return null;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(remaining);
            try
            {
                using (linkedCts.Token.Register(() => awaiter.TrySetResult(false)))
                {
                    await awaiter.Task.ConfigureAwait(false);
                }
            }
            finally { RemoveAwaiter(awaiter); }
        }
        return null;
    }

    private TileJobDto? TryClaimAnyLocked(string workerId)
    {
        // Round-robin between jobs: each pending tile is claimed FIFO
        // across jobs in id order. Tiny job count makes the cost
        // negligible; rebalancing strategy lands in D-3.
        foreach (var kv in _jobs)
        {
            var st = kv.Value;
            if (st.Pending.TryDequeue(out var t))
            {
                t.JobId = kv.Key;
                st.InFlight[t.TileId] = new InFlightTile
                {
                    WorkerId   = workerId,
                    AssignedAt = DateTime.UtcNow,
                    Tile       = t,
                };
                return t;
            }
        }
        return null;
    }

    /// <summary>Worker successfully delivered a tile. Returns true if
    /// the master should accept the delivery; false if it was already
    /// completed (idempotent — duplicate delivery from a slow retry).</summary>
    public bool AcceptDelivery(string jobId, int tileId)
    {
        if (!_jobs.TryGetValue(jobId, out var st)) return false;
        lock (_lock)
        {
            if (!st.InFlight.TryRemove(tileId, out _)) return false;
            st.Completed.TryAdd(tileId, true);
            return true;
        }
    }

    /// <summary>Worker reported failure on a tile. If retry budget
    /// remains, re-enqueue with incremented Attempt. Returns true when
    /// the tile was re-queued, false when the budget is exhausted (the
    /// coordinator should then fail the whole job).</summary>
    public bool RecordFailure(string jobId, int tileId)
    {
        if (!_jobs.TryGetValue(jobId, out var st)) return false;
        TileJobDto? toRequeue;
        lock (_lock)
        {
            if (!st.InFlight.TryRemove(tileId, out var inFlight)) return false;
            if (inFlight.Tile.Attempt >= MaxAttempts) return false;

            var retry = inFlight.Tile;
            retry.Attempt += 1;
            st.Pending.Enqueue(retry);
            toRequeue = retry;
        }
        SignalAll();
        return toRequeue != null;
    }

    public int PendingCount(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var st)) return 0;
        return st.Pending.Count;
    }

    public int InFlightCount(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var st)) return 0;
        return st.InFlight.Count;
    }

    public int CompletedCount(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var st)) return 0;
        return st.Completed.Count;
    }

    public bool KnowsJob(string jobId) => _jobs.ContainsKey(jobId);

    private void SignalAll()
    {
        LinkedListNode<TaskCompletionSource<bool>>? node;
        lock (_lock)
        {
            node = _awaiters.First;
        }
        // Walk outside the lock — every awaiter's continuation may grab
        // back into the lock to claim; holding it here would deadlock.
        while (node != null)
        {
            node.Value.TrySetResult(true);
            node = node.Next;
        }
    }

    private void RemoveAwaiter(TaskCompletionSource<bool> tcs)
    {
        lock (_lock)
        {
            for (var n = _awaiters.First; n != null; n = n.Next)
            {
                if (n.Value == tcs) { _awaiters.Remove(n); break; }
            }
        }
    }

    private sealed class JobState
    {
        public ConcurrentQueue<TileJobDto> Pending { get; } = new();
        public ConcurrentDictionary<int, InFlightTile> InFlight { get; } = new();
        public ConcurrentDictionary<int, bool> Completed { get; } = new();
    }

    private sealed class InFlightTile
    {
        public required string WorkerId { get; init; }
        public required DateTime AssignedAt { get; init; }
        public required TileJobDto Tile { get; init; }
    }
}
