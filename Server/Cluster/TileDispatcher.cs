// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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

    // ── D-3b work-stealing knobs ───────────────────────────────────────
    /// <summary>When a job's pending queue is empty and idle workers
    /// arrive on tile.next, the dispatcher may hand a duplicate of an
    /// already-in-flight tile to the idle worker. First delivery wins;
    /// the merger is idempotent. Only fires when the remaining in-flight
    /// count drops below this fraction of the total tile count.</summary>
    public double StealRemainingFraction { get; init; } = 0.10;

    /// <summary>Minimum age of an in-flight tile before stealing is
    /// allowed — defends against stealing a tile from a worker that has
    /// only just received it.</summary>
    public TimeSpan StealMinAge { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Tiny jobs (fewer than this many tiles) skip stealing —
    /// per-tile setup cost dominates and the speedup is below noise.</summary>
    public int StealMinTotalTiles { get; init; } = 4;

    /// <summary>Wall-clock provider — swappable so tests can age in-flight
    /// tiles deterministically without sleeping.</summary>
    public Func<DateTime> NowUtc { get; init; } = () => DateTime.UtcNow;

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
                // D-3b — when no fresh tile is pending, look for a
                // straggler we can shadow. The duplicate carries the
                // same TileId; the first worker to deliver wins (merger
                // and AcceptDelivery are idempotent).
                tile ??= TryStealLocked(workerId);
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
        // negligible.
        foreach (var kv in _jobs)
        {
            var st = kv.Value;
            if (st.Pending.TryDequeue(out var t))
            {
                t.JobId = kv.Key;
                st.InFlight[t.TileId] = new InFlightTile
                {
                    WorkerId   = workerId,
                    AssignedAt = NowUtc(),
                    Tile       = t,
                };
                return t;
            }
        }
        return null;
    }

    /// <summary>D-3b work-stealing path. When a job's pending queue is
    /// empty AND we are in the last <see cref="StealRemainingFraction"/>
    /// of tiles, hand an idle worker a duplicate of the oldest in-flight
    /// tile. The original assignment stays in InFlight — both workers
    /// race; the first delivery wins (ArtifactMerger's per-tile seen
    /// gate and AcceptDelivery's TryRemove both no-op the duplicate).
    /// Defends against:
    ///   - re-stealing the same tile to the same worker (Stealers set)
    ///   - stealing from the same worker that owns the tile (skip self)
    ///   - stealing tiles that just got assigned (StealMinAge)
    ///   - over-fragmenting tiny jobs (StealMinTotalTiles).</summary>
    private TileJobDto? TryStealLocked(string workerId)
    {
        DateTime now = NowUtc();
        foreach (var kv in _jobs)
        {
            var st = kv.Value;
            if (!st.Pending.IsEmpty) continue;
            if (st.InFlight.IsEmpty) continue;

            int inFlight = st.InFlight.Count;
            int total    = inFlight + st.Completed.Count;
            if (total < StealMinTotalTiles) continue;
            if (inFlight > Math.Max(1, total * StealRemainingFraction)) continue;

            InFlightTile? victim = null;
            foreach (var f in st.InFlight.Values)
            {
                if (f.WorkerId == workerId) continue;
                if (f.Stealers != null && f.Stealers.Contains(workerId)) continue;
                if (now - f.AssignedAt < StealMinAge) continue;
                if (victim is null || f.AssignedAt < victim.AssignedAt) victim = f;
            }
            if (victim is null) continue;
            victim.Stealers ??= new HashSet<string>(StringComparer.Ordinal);
            victim.Stealers.Add(workerId);
            // Return the SAME TileJobDto reference. Worker code paths
            // only read its fields; no mutation that would race the
            // original worker's local copy on the other side of the
            // wire (the dto was serialised + deserialised per worker
            // anyway in the real wire path).
            return victim.Tile;
        }
        return null;
    }

    /// <summary>Worker successfully delivered a tile. Returns true if
    /// the master should accept the delivery; false if it was already
    /// completed (idempotent — duplicate delivery from a slow retry).
    /// The accepting worker's id is recorded against the tile so D-5c's
    /// cluster.jobTileMap can colour the per-tile grid by who rendered
    /// it. Duplicate delivery (work-steal race) preserves the first
    /// winner — second delivery sees TryRemove fail and returns false.</summary>
    public bool AcceptDelivery(string jobId, int tileId, string workerId)
    {
        if (!_jobs.TryGetValue(jobId, out var st)) return false;
        lock (_lock)
        {
            if (!st.InFlight.TryRemove(tileId, out _)) return false;
            st.Completed[tileId] = workerId;
            return true;
        }
    }

    /// <summary>D-4b — return a freshly claimed tile to the pending queue
    /// without counting it as a failure. The coordinator uses this when
    /// the video framepipeline is behind its <c>MaxFrameQueueDepth</c>
    /// gate: the worker that was about to receive the tile gets WaitAgain
    /// instead and the tile stays available for whoever asks next (which
    /// may be the same worker once the encoder catches up). Attempt count
    /// is preserved — backpressure is not the worker's fault.</summary>
    public bool ReturnPending(string jobId, TileJobDto tile)
    {
        if (!_jobs.TryGetValue(jobId, out var st)) return false;
        lock (_lock)
        {
            // Clear in-flight bookkeeping if present. The tile may be
            // a stealer-clone that was never recorded; either way is OK.
            st.InFlight.TryRemove(tile.TileId, out _);
            st.Pending.Enqueue(tile);
        }
        SignalAll();
        return true;
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

    /// <summary>D-5c — snapshot of every tile's current live state for the
    /// admin tile-map view. Returned dictionary is keyed by tileId; the
    /// value records whether the tile is pending / in-flight / completed,
    /// plus the worker id (in-flight → assignee, completed → deliverer).
    /// Pending tiles have no worker id yet. Callers receive a fresh copy
    /// taken under the dispatcher monitor so iteration is safe without
    /// holding the lock; tiles missing from the result are pending and
    /// have never been claimed.</summary>
    public IReadOnlyDictionary<int, TileLiveState> SnapshotTileStates(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var st))
            return new Dictionary<int, TileLiveState>();
        var result = new Dictionary<int, TileLiveState>(st.InFlight.Count + st.Completed.Count);
        lock (_lock)
        {
            foreach (var kv in st.Completed)
                result[kv.Key] = new TileLiveState("completed", kv.Value);
            // InFlight wins ties: a tile that was completed by one worker
            // and re-stolen for a duplicate run still shows as completed
            // (first delivery is canonical). The dispatcher never moves
            // a tile from Completed back to InFlight, so this branch only
            // fills in tiles that haven't been delivered.
            foreach (var kv in st.InFlight)
                if (!result.ContainsKey(kv.Key))
                    result[kv.Key] = new TileLiveState("inflight", kv.Value.WorkerId);
        }
        return result;
    }

    /// <summary>D-5c — one tile's live state in the dispatcher. <c>State</c>
    /// is <c>pending</c>, <c>inflight</c>, or <c>completed</c>. <c>WorkerId</c>
    /// is null for pending tiles, the assignee for in-flight, and the first
    /// successful deliverer for completed.</summary>
    public readonly record struct TileLiveState(string State, string? WorkerId);

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
        /// <summary>D-5c — value is the workerId that delivered this tile so
        /// the cluster.jobTileMap RPC can colour the per-tile grid by who
        /// rendered it. Pre-D-5c the value was a plain <c>bool</c>; the
        /// only consumer was <see cref="CompletedCount"/> which still works
        /// because <c>Count</c> is value-type-agnostic.</summary>
        public ConcurrentDictionary<int, string> Completed { get; } = new();
    }

    private sealed class InFlightTile
    {
        public required string WorkerId { get; init; }
        public required DateTime AssignedAt { get; init; }
        public required TileJobDto Tile { get; init; }
        /// <summary>D-3b — set of worker ids that have been handed a
        /// duplicate of this tile via work-stealing. Null until the
        /// first steal. Guarded by the dispatcher monitor.</summary>
        public HashSet<string>? Stealers { get; set; }
    }
}
