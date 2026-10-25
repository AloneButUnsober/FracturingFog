// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/OidnAsync.cs
//
// #1392 — the live view no longer waits for Intel Open Image Denoise. A settled
// frame takes OIDN ~1 s at 1080p on the CPU (0.85–3 s measured, #1338); the
// synchronous pass held that frame back. Now the live host shows the frame raw at
// once and denoises it on a background thread, then swaps the denoised frame in.
//
// One session per live host. While it is the thread's Current (Enter), the OIDN
// pass (ScreenSpacePost.ApplyOidn) asks it instead of running the filter:
//   • the frame equals the input of the last finished job → the job's output is
//     copied in: exactly the synchronous result (the CPU device is deterministic);
//   • it equals the running job's input → left raw (the job will deliver);
//   • otherwise a job starts on a copy (superseding, and cancelling, any other)
//     and the frame stays raw.
// Matching by content, not by a frame id, keeps it exact whatever re-presents the
// frame: the relief raymarch re-runs on every upload, and a 3D frame's buffer is
// re-uploaded by every repaint. When a job finishes and is still the latest,
// Completed fires (on the job's thread) and the host re-presents the current frame,
// which now finds the result. A re-present that still misses (a pass that is not
// repeatable, so the input never recurs) denoises synchronously rather than loop
// (Replay).
//
// Without a Current (batch, export, video runs, recording — everything that must
// hold a finished frame) nothing changes: the pass runs synchronously.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Imaging;

/// <summary>#1392 — background OIDN for the live view (see the file header).</summary>
public sealed class OidnAsync : IDisposable
{
    [ThreadStatic] private static OidnAsync? t_current;
    [ThreadStatic] private static long t_tag;
    [ThreadStatic] private static bool t_replay;

    /// <summary>The session the calling thread's OIDN pass defers to (null = run it now).</summary>
    public static OidnAsync? Current => t_current;

    /// <summary>The calling thread is re-presenting a frame after <see cref="Completed"/>.</summary>
    public static bool Replaying => t_current != null && t_replay;

    private readonly object _lock = new();
    private uint[]? _doneIn, _doneOut;   // the last finished job: input and denoised output
    private int _doneW, _doneH;
    private uint[]? _pendIn;             // the running job's input
    private int _pendW, _pendH;
    private long _serial;                // bumped by every job and every Cancel
    private bool _disposed;

    /// <summary>Raised on the job's thread when a job finished, succeeded and is still
    /// the latest request. The argument is the tag the frame was requested under.</summary>
    public event Action<long>? Completed;

    /// <summary>Jobs started (diagnostics / tests).</summary>
    public int JobsStarted { get; private set; }

    /// <summary>Re-presents that did not find their frame's result (and so denoised
    /// synchronously): 0 when every pass the session saw is repeatable.</summary>
    public int ReplayMisses { get; private set; }

    /// <summary>The last job's error (null when it succeeded or none ran).</summary>
    public string? LastError { get; private set; }

    /// <summary>True while a job is running or its <see cref="Completed"/> is being raised.</summary>
    public bool Pending { get { lock (_lock) return _pendIn != null || _delivering > 0; } }
    private int _delivering;

    /// <summary>Make this session the calling thread's Current until the scope is
    /// disposed. <paramref name="tag"/> rides along to <see cref="Completed"/> (the
    /// host passes the present sequence of the frame). <paramref name="replay"/>: a
    /// re-present after Completed — a miss then denoises synchronously.</summary>
    public Scope Enter(long tag, bool replay = false)
    {
        var s = new Scope(t_current, t_tag, t_replay);
        t_current = this; t_tag = tag; t_replay = replay;
        return s;
    }

    /// <summary>Restores the thread's previous Current.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly OidnAsync? _prev; private readonly long _prevTag; private readonly bool _prevReplay;
        internal Scope(OidnAsync? prev, long prevTag, bool prevReplay) { _prev = prev; _prevTag = prevTag; _prevReplay = prevReplay; }
        public void Dispose() { t_current = _prev; t_tag = _prevTag; t_replay = _prevReplay; }
    }

    /// <summary>What <see cref="Resolve"/> did with the frame.</summary>
    public enum Outcome
    {
        /// <summary>The frame now holds the denoised result.</summary>
        Denoised,
        /// <summary>The frame is left raw; a job will deliver.</summary>
        Deferred,
        /// <summary>The caller should denoise now (a replay that missed, or disposed).</summary>
        RunNow,
    }

    /// <summary>The OIDN pass's question for the w×h beauty <paramref name="frame"/>
    /// (see the file header).</summary>
    public Outcome Resolve(uint[] frame, int w, int h)
    {
        int n = w * h;
        if (n <= 0 || frame.Length < n) return Outcome.RunNow;
        lock (_lock)
        {
            if (_disposed) return Outcome.RunNow;
            if (_doneOut != null && _doneW == w && _doneH == h && Same(_doneIn!, frame, n))
            {
                Array.Copy(_doneOut, frame, n);
                return Outcome.Denoised;
            }
            if (_pendIn != null && _pendW == w && _pendH == h && Same(_pendIn, frame, n)) return Outcome.Deferred;
            if (t_replay) { ReplayMisses++; return Outcome.RunNow; }
            Start(frame.AsSpan(0, n).ToArray(), w, h, t_tag);
            return Outcome.Deferred;
        }
    }

    /// <summary>The denoised result when <paramref name="frame"/> equals the last
    /// finished job's input (null otherwise). The host's upload shows it in place of a
    /// 3D frame whose buffer stayed raw.</summary>
    public uint[]? TryGetResult(uint[] frame, int w, int h)
    {
        int n = w * h;
        if (n <= 0 || frame.Length < n) return null;
        lock (_lock)
            return _doneOut != null && _doneW == w && _doneH == h && Same(_doneIn!, frame, n) ? _doneOut : null;
    }

    /// <summary>Drop the running job (its result is discarded; OIDN stops at its next
    /// progress check). The finished result stays: it still answers its own frame.</summary>
    public void Cancel()
    {
        lock (_lock) { _serial++; _pendIn = null; }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; _serial++; _pendIn = null; _doneIn = _doneOut = null; }
    }

    private void Start(uint[] input, int w, int h, long tag)
    {
        long serial = ++_serial;
        _pendIn = input; _pendW = w; _pendH = h;
        JobsStarted++;
        bool Superseded() => Volatile.Read(ref _serial) != serial;
        Task.Run(() =>
        {
            var output = (uint[])input.Clone();
            string? err = "cancelled";
            bool ok = !Superseded() && OidnDenoiser.TryDenoiseBgra(output, null, null, w, h, out err, Superseded);
            lock (_lock)
            {
                if (serial != _serial) return;   // superseded or cancelled: discard
                _pendIn = null;
                LastError = ok ? null : err;
                if (!ok) return;
                _doneIn = input; _doneOut = output; _doneW = w; _doneH = h;
                _delivering++;
            }
            try { Completed?.Invoke(tag); }
            catch { /* a host failure must not fault the job */ }
            finally { lock (_lock) _delivering--; }
        });
    }

    private static bool Same(uint[] a, uint[] b, int n) => a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n));
}
