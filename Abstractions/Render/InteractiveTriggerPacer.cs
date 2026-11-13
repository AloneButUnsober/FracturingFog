// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/InteractiveTriggerPacer.cs
//
// #1757 (interactivity epic #1756, S1) — pace pointer-driven renders to the pipeline.
// A drag delivered 92-235 Triggers a second against 36-61 frames shown (the #1754 live trace):
// every pointer move ran Trigger's view and setup work and queued a job that was then
// superseded unseen. The pacer lets one paced render be in flight at a time. Moves that arrive
// meanwhile collapse into one pending request (the latest wins), fired when a frame reaches the
// screen (the host's FrameLanded). So renders follow what the pipeline delivers — about the
// display refresh when Present waits for vsync — and the view never changes under a frame
// being calculated (a 2D frame is then neither torn nor cancelled).
//
// A slow frame must not hold input back, nor be cancelled over and over:
//   - once the frame in flight is MaxWaitMs old, the pending request fires anyway (the host then
//     cancels a 2D frame, or lets a cheap 3D one finish, as before #1757);
//   - that forced start happens once per landed frame. The restarted frame is then allowed to
//     land, however slow: firing again every MaxWaitMs cancelled each new 2D frame in turn (a live
//     2D drag with 100-230 ms frames showed one image in 1.2 s, #1757's first build);
//   - HardStaleMs is the last resort for a frame whose landing is never reported.
// A cancelled frame does not count as landed: releasing on it cancelled the frame just started.
// Timers only serve these bounds; the cadence comes from frames landing, so Windows' ~15.6 ms
// timer granularity doesn't set the frame rate.

using System;
using System.Threading;

namespace FracturingFog.Render
{
    /// <summary>#1757 — one paced interactive render in flight; later requests collapse into one.</summary>
    public sealed class InteractiveTriggerPacer : IDisposable
    {
        public const int DefaultMaxWaitMs = 50;
        public const int DefaultHardStaleMs = 2000;

        private readonly Action<bool> _fire;
        private readonly Func<long> _nowMs;
        private readonly int _maxWaitMs, _hardStaleMs;
        private readonly object _gate = new();
        private readonly Timer _watchdog;
        private bool _inFlight, _pending, _pendingProgressive, _forced, _disposed;
        private long _inFlightSinceMs;
        private long _fired;

        /// <param name="fire">Starts a render (the argument is the progressive flag).</param>
        /// <param name="maxWaitMs">Age of the frame in flight after which a pending request is forced
        /// once (per landed frame).</param>
        /// <param name="hardStaleMs">Age after which it is forced even after a forced start (a frame
        /// whose landing was never reported).</param>
        /// <param name="nowMs">Clock in milliseconds (tests pass a fake).</param>
        public InteractiveTriggerPacer(Action<bool> fire, int maxWaitMs = DefaultMaxWaitMs,
                                       int hardStaleMs = DefaultHardStaleMs, Func<long>? nowMs = null)
        {
            _fire = fire ?? throw new ArgumentNullException(nameof(fire));
            _maxWaitMs = Math.Max(1, maxWaitMs);
            _hardStaleMs = Math.Max(_maxWaitMs, hardStaleMs);
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            _watchdog = new Timer(_ => OnWatchdog(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Renders started by the pacer (diagnostics and tests).</summary>
        public long Fired => Interlocked.Read(ref _fired);

        /// <summary>A render is wanted for the current view (a pointer move).</summary>
        public void Request(bool progressive)
        {
            bool fireNow = false;
            lock (_gate)
            {
                if (_disposed) return;
                long now = _nowMs();
                if (!_inFlight) { Start(now, forced: false); fireNow = true; }
                else if (now - _inFlightSinceMs >= ForceAfterMs) { _pending = false; Start(now, forced: true); fireNow = true; }
                else
                {
                    _pending = true;
                    _pendingProgressive = progressive;   // the latest request wins
                    Arm(now);
                }
            }
            if (fireNow) Fire(progressive);
        }

        /// <summary>A frame reached the screen: start the pending render, if any, else go idle.</summary>
        public void FrameDone()
        {
            bool p;
            lock (_gate)
            {
                if (_disposed) return;
                _forced = false;   // a frame landed: a slow next one may be forced again
                if (!_pending) { _inFlight = false; Disarm(); return; }
                p = _pendingProgressive;
                _pending = false;
                Start(_nowMs(), forced: false);
            }
            Fire(p);
        }

        /// <summary>Drop a pending request and the in-flight mark: a non-paced render (a full render
        /// after the drag) covers the view.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _pending = false;
                _inFlight = false;
                _forced = false;
                Disarm();
            }
        }

        // Once per landed frame the pending request may be forced after MaxWaitMs; after that, only
        // after HardStaleMs.
        private int ForceAfterMs => _forced ? _hardStaleMs : _maxWaitMs;

        private void Start(long now, bool forced)
        {
            _inFlight = true;
            _inFlightSinceMs = now;
            if (forced) _forced = true;
            Disarm();
        }

        private void Arm(long now)
            => _watchdog.Change(Math.Max(1, ForceAfterMs - (now - _inFlightSinceMs)), Timeout.Infinite);

        private void Disarm() => _watchdog.Change(Timeout.Infinite, Timeout.Infinite);

        private void OnWatchdog()
        {
            bool p;
            lock (_gate)
            {
                if (_disposed || !_pending || !_inFlight) return;
                long now = _nowMs();
                if (now - _inFlightSinceMs < ForceAfterMs) { Arm(now); return; }
                p = _pendingProgressive;
                _pending = false;
                Start(now, forced: true);
            }
            Fire(p);
        }

        private void Fire(bool progressive)
        {
            Interlocked.Increment(ref _fired);
            _fire(progressive);
        }

        public void Dispose()
        {
            lock (_gate) { _disposed = true; _pending = false; }
            _watchdog.Dispose();
        }
    }
}
