// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/InteractionTrace.cs
//
// #1762 (interactivity epic #1756, S6) — opt-in input-to-screen trace.
// The #86 trace (FF_GPU_PERTURB_DEBUG) starts at Trigger and appends one formatted line per event
// to a file, so it can't show when a pointer move reached the UI thread, how long the UI thread
// spent before Trigger, or how the frame's time splits between calculation, the upload post
// pipeline and Present. This trace follows one interaction end to end:
//
//   pointer move (OS time stamp, coalesced points) → input handler → pacer decision → Trigger
//   (ApplyView / alt-calculator sync cost) → calc thread start (queue wait, upload-barrier wait)
//   → calculation end → upload queued → upload start (thread-pool delay) → post pipeline →
//   Present → frame landed, plus a UI-thread lag probe (post time vs run time).
//
// Switch: FF_INTERACTION_TRACE=1. Output: %TEMP%/ff_interaction_trace.csv, rewritten at each app
// start. Analyse with Tools/InteractionTrace/analyze_interaction_trace.py (per-drag stats and
// which epic slice each number bears on); see Docs/Technical/Interaction-Trace.md.
//
// Hot path: Mark is a static call that reads one field and returns when the trace is off — no
// allocation, no string. When on, a record is a struct written into a fixed ring (one
// Interlocked increment); a background thread writes the file, so tracing does not stall the
// threads it measures.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace FracturingFog.Render
{
    /// <summary>#1762 — what a trace record marks. The numbers are written to the file; append
    /// new kinds at the end so old logs still parse.</summary>
    public enum TraceKind : byte
    {
        /// <summary>A pointer move reached the render surface's handler. A = OS-to-handler delay in
        /// ms (-1 unknown), B = points coalesced into this event.</summary>
        PointerMove = 1,
        /// <summary>The input handler returned. A = handler time in µs, B = the input kind
        /// (<see cref="InputKind"/>).</summary>
        InputHandled = 2,
        /// <summary>A pointer press, release, wheel step or key reached its handler. B = the input
        /// kind; A = OS-to-handler delay in ms (-1 unknown).</summary>
        InputOther = 3,
        /// <summary>The view model was asked to render. A = 0 full, 1 fast (progressive),
        /// 2 interactive (a Math Plane editor drag).</summary>
        RenderHint = 4,
        /// <summary>The pacer took a request. A = 0 started (idle), 1 started (forced: the frame in
        /// flight was old), 2 pending (collapsed into the next start).</summary>
        PacerRequest = 5,
        /// <summary>The pacer started a pending render. A = 0 on a landed frame, 1 by its watchdog.</summary>
        PacerFire = 6,
        /// <summary>Trigger entered (its seq is not known yet; pair with the next
        /// <see cref="TriggerExit"/> on the same thread). B = the fractal type
        /// (<see cref="FracturingFog.FractalType"/>, named in the file header).</summary>
        TriggerEnter = 7,
        /// <summary>Trigger returned. Seq = the job. A = ApplyView µs, B = flags
        /// (<see cref="TriggerFlags"/>).</summary>
        TriggerExit = 8,
        /// <summary>The calc thread took a job. A = progressive stage (0 final).</summary>
        CalcStart = 9,
        /// <summary>The calc thread waited for the previous upload to finish. A = wait µs.</summary>
        UploadBarrier = 10,
        /// <summary>The calc thread finished a job. A = stage, B = 1 if its token was cancelled.</summary>
        CalcEnd = 11,
        /// <summary>A frame's upload was handed to the thread pool.</summary>
        UploadQueued = 12,
        /// <summary>The thread pool started that upload.</summary>
        UploadStart = 13,
        /// <summary>The upload's post pipeline (post-FX, overlays, HUD) ran. A = µs.</summary>
        PostPipeline = 14,
        /// <summary>A frame was handed to the renderer and presented. A = µs in UpdateTexture +
        /// Render (a vsync'd Present blocks here), B = stage (0 final; -1 a re-present of the
        /// current image, -2 after a resize, -3 an external buffer; those have seq -1).</summary>
        Present = 15,
        /// <summary>A frame reached the screen (the host's FrameLanded). A = stage (0 final).</summary>
        Landed = 16,
        /// <summary>A frame was calculated but not shown. A = <see cref="DropReason"/>.</summary>
        Dropped = 17,
        /// <summary>The upload finished (the barrier opens).</summary>
        UploadEnd = 18,
        /// <summary>The UI-thread lag probe ran. A = post-to-run delay in µs.</summary>
        UiLag = 19,
        /// <summary>The render surface was resized. A = width, B = height.</summary>
        Resize = 20,
        /// <summary>#1773 — a startup step finished. A = the step (<see cref="StartupPhase"/>),
        /// B = µs since the process started (Process.StartTime), so the time before the trace
        /// itself started is counted too.</summary>
        StartupPhase = 21,
    }

    /// <summary>#1773 — <see cref="TraceKind.StartupPhase"/> A: the startup steps, in the order
    /// they normally finish. Append new ones at the end so old logs still parse.</summary>
    public enum StartupPhase
    {
        MainEntered = 1,
        ShellStarting = 2,
        AvaloniaReady = 3,
        MainWindowCreated = 4,
        SplashShown = 5,
        MainWindowOpened = 6,
        SurfaceCreated = 7,
        RendererCreated = 8,
        PersistedCalculatorsLoaded = 9,
        RenderHostCreated = 10,
        GpuSetupDone = 11,
        ServicesDone = 12,
        LibrariesLoaded = 13,
        ViewModelsBuilt = 14,
        SurfaceReadyDone = 15,
        DataContextAssigned = 16,
        FirstTrigger = 17,
        FirstFrameCompleted = 18,
        SplashDismissed = 19,
    }

    /// <summary>#1762 — <see cref="TraceKind.InputHandled"/> / <see cref="TraceKind.InputOther"/> B.</summary>
    public enum InputKind { Move = 0, Press = 1, Release = 2, Wheel = 3, Key = 4 }

    /// <summary>#1762 — <see cref="TraceKind.TriggerExit"/> B.</summary>
    [Flags]
    public enum TriggerFlags
    {
        None = 0,
        /// <summary>The frame in flight was kept running (3D, cheap).</summary>
        KeptInFlight = 1,
        /// <summary>A frame was in flight and was cancelled.</summary>
        CancelledInFlight = 2,
        /// <summary>A progressive (preview-first) render.</summary>
        Progressive = 4,
        /// <summary>The view is 3D.</summary>
        Is3D = 8,
        /// <summary>Ran on the UI thread (<see cref="InteractionTrace.UiThreadId"/>).</summary>
        OnUiThread = 16,
    }

    /// <summary>#1762 — <see cref="TraceKind.Dropped"/> A.</summary>
    public enum DropReason { Cancelled = 1, FinalSuperseded = 2, PreviewSuperseded = 3 }

    /// <summary>#1762 — the opt-in interaction trace (FF_INTERACTION_TRACE=1).</summary>
    public static class InteractionTrace
    {
        public const string EnvVar = "FF_INTERACTION_TRACE";
        public const string FileName = "ff_interaction_trace.csv";

        private static volatile InteractionTraceRecorder? s_rec;

        static InteractionTrace()
        {
            if (Environment.GetEnvironmentVariable(EnvVar) is "1" or "true" or "yes" or "on")
            {
                try
                {
                    s_rec = new InteractionTraceRecorder(DefaultPath);
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { s_rec?.Dispose(); } catch { } };
                }
                catch { s_rec = null; /* a diagnostic must never stop the app */ }
            }
        }

        public static string DefaultPath => Path.Combine(Path.GetTempPath(), FileName);

        /// <summary>True while records are kept. Guard any work done only for the trace with it.</summary>
        public static bool Enabled => s_rec != null;

        /// <summary>The UI thread's managed id (set by the shell; 0 unknown).</summary>
        public static int UiThreadId { get; set; }

        /// <summary>Stopwatch ticks now (for durations passed to <see cref="Mark"/>).</summary>
        public static long Now() => Stopwatch.GetTimestamp();

        /// <summary>Microseconds since <paramref name="startTicks"/>.</summary>
        public static long MicrosSince(long startTicks)
            => (Stopwatch.GetTimestamp() - startTicks) * 1_000_000 / Stopwatch.Frequency;

        /// <summary>Record an event. Free when the trace is off.</summary>
        /// <summary>#1773 — a startup step finished (free when the trace is off).</summary>
        public static void Startup(StartupPhase phase)
        {
            var r = s_rec;
            if (r is null) return;
            r.Add(TraceKind.StartupPhase, 0, (long)phase, MicrosSinceProcessStart());
        }

        private static long s_processStartTicks;   // DateTime.UtcNow ticks at process start (0 unread)

        private static long MicrosSinceProcessStart()
        {
            long start = Interlocked.Read(ref s_processStartTicks);
            if (start == 0)
            {
                try { start = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
                catch { start = -1; }
                Interlocked.Exchange(ref s_processStartTicks, start);
            }
            return start < 0 ? -1 : (DateTime.UtcNow.Ticks - start) / 10;
        }

        public static void Mark(TraceKind kind, long seq = 0, long a = 0, long b = 0)
        {
            var r = s_rec;
            if (r is null) return;
            r.Add(kind, seq, a, b);
        }

        /// <summary>Start a trace into <paramref name="path"/> (tests; replaces any running trace).
        /// Disposing stops it and flushes the file.</summary>
        public static IDisposable Start(string path)
        {
            var rec = new InteractionTraceRecorder(path);
            var old = Interlocked.Exchange(ref s_rec, rec);
            old?.Dispose();
            return new Stopper(rec);
        }

        /// <summary>Write what has been recorded so far (tests; the writer also flushes on its own).</summary>
        public static void Flush() => s_rec?.Flush();

        private sealed class Stopper : IDisposable
        {
            private InteractionTraceRecorder? _rec;
            public Stopper(InteractionTraceRecorder rec) => _rec = rec;
            public void Dispose()
            {
                var rec = Interlocked.Exchange(ref _rec, null);
                if (rec == null) return;
                Interlocked.CompareExchange(ref s_rec, null, rec);
                rec.Dispose();
            }
        }
    }

    /// <summary>#1762 — the ring of trace records and the thread that writes them out.</summary>
    internal sealed class InteractionTraceRecorder : IDisposable
    {
        private struct Rec
        {
            public long Stamp;     // index + 1 once written (0 = empty)
            public long Ticks;
            public long Seq, A, B;
            public int Thread;
            public TraceKind Kind;
        }

        private const int Capacity = 1 << 17;   // ~6 MB; minutes of a busy drag between flushes
        private const int Mask = Capacity - 1;
        private const int FlushIntervalMs = 100;

        private readonly Rec[] _ring = new Rec[Capacity];
        private readonly long _t0 = Stopwatch.GetTimestamp();
        private readonly StreamWriter _out;
        private readonly Thread _writer;
        private readonly object _writeGate = new();
        private readonly ManualResetEventSlim _stop = new(false);
        private long _next;    // records claimed
        private long _read;    // records written to the file
        private long _dropped;
        private int _disposed;

        public InteractionTraceRecorder(string path)
        {
            _out = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            _out.WriteLine($"# FF interaction trace #1762 v1, started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            _out.WriteLine($"# stopwatch_hz={Stopwatch.Frequency} cores={Environment.ProcessorCount} os={Environment.OSVersion}");
            _out.WriteLine("# kinds: " + string.Join(" ", Enum.GetNames(typeof(TraceKind))));
            _out.WriteLine("# fractal_types: " + string.Join(" ",
                Enum.GetValues<FracturingFog.FractalType>().Select(t => $"{(int)t}={t}").Distinct()));
            _out.WriteLine("t_us,thread,kind,seq,a,b");
            _out.Flush();
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "FF interaction trace", Priority = ThreadPriority.BelowNormal };
            _writer.Start();
        }

        public void Add(TraceKind kind, long seq, long a, long b)
        {
            long i = Interlocked.Increment(ref _next) - 1;
            ref Rec r = ref _ring[i & Mask];
            r.Ticks = Stopwatch.GetTimestamp();
            r.Seq = seq; r.A = a; r.B = b;
            r.Thread = Environment.CurrentManagedThreadId;
            r.Kind = kind;
            Volatile.Write(ref r.Stamp, i + 1);   // publish last
        }

        private void WriterLoop()
        {
            try
            {
                while (!_stop.Wait(FlushIntervalMs)) Flush();
            }
            catch { /* diagnostic */ }
        }

        public void Flush()
        {
            lock (_writeGate)
            {
                if (_disposed == 2) return;
                long end = Volatile.Read(ref _next);
                double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
                while (_read < end)
                {
                    ref Rec r = ref _ring[_read & Mask];
                    long stamp = Volatile.Read(ref r.Stamp);
                    if (stamp == _read + 1)
                    {
                        long t = (long)((r.Ticks - _t0) * usPerTick);
                        _out.Write(t); _out.Write(',');
                        _out.Write(r.Thread); _out.Write(',');
                        _out.Write(r.Kind.ToString()); _out.Write(',');
                        _out.Write(r.Seq); _out.Write(',');
                        _out.Write(r.A); _out.Write(',');
                        _out.WriteLine(r.B);
                        _read++;
                    }
                    else if (stamp > _read + 1)
                    {
                        // The writers lapped the ring: those records are gone.
                        long skipTo = Math.Max(_read + 1, stamp - Capacity);
                        _dropped += skipTo - _read;
                        _out.WriteLine($"# dropped {skipTo - _read} records (ring lapped)");
                        _read = skipTo;
                    }
                    else break;   // claimed but not yet written: next flush
                }
                _out.Flush();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
            _stop.Set();
            _writer.Join(2000);
            try { Flush(); } catch { }
            lock (_writeGate)
            {
                _disposed = 2;
                try { _out.WriteLine($"# end dropped={_dropped}"); _out.Dispose(); } catch { }
            }
        }
    }
}
