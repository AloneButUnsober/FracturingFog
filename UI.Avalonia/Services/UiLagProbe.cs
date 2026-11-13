// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/Services/UiLagProbe.cs
//
// #1762 (interactivity epic #1756, S6) — how long work posted to the UI thread waits before it
// runs. A timer posts a stamped callback every ProbeIntervalMs at the priority the app's own
// posts use; the callback records the post-to-run delay (TraceKind.UiLag). A busy UI thread
// (layout, bindings, a slow handler) shows as lag here and as late pointer moves in the trace.
// It also tells the trace which thread is the UI thread (InteractionTrace.UiThreadId).
// Runs only while FF_INTERACTION_TRACE is on.

using System;
using System.Threading;
using Avalonia.Threading;
using FracturingFog.Render;

namespace FracturingFog.UI.Avalonia.Services;

public sealed class UiLagProbe : IDisposable
{
    public const int ProbeIntervalMs = 10;

    private readonly Timer _timer;
    private int _posted;   // one probe in flight at a time: a stalled thread is one long lag, not a queue of probes

    private UiLagProbe()
    {
        Dispatcher.UIThread.Post(() => InteractionTrace.UiThreadId = Environment.CurrentManagedThreadId);
        _timer = new Timer(_ => Probe(), null, ProbeIntervalMs, ProbeIntervalMs);
    }

    /// <summary>Starts the probe when the interaction trace is on; null otherwise.</summary>
    public static IDisposable? StartIfTracing()
    {
        if (!InteractionTrace.Enabled) return null;
        try { return new UiLagProbe(); }
        catch { return null; /* diagnostic */ }
    }

    private void Probe()
    {
        if (Interlocked.Exchange(ref _posted, 1) != 0) return;
        long t0 = InteractionTrace.Now();
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                InteractionTrace.Mark(TraceKind.UiLag, 0, InteractionTrace.MicrosSince(t0));
                Volatile.Write(ref _posted, 0);
            });
        }
        catch { Volatile.Write(ref _posted, 0); }
    }

    public void Dispose() => _timer.Dispose();
}
