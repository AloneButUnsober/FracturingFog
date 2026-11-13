// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1762 (interactivity epic #1756, S6) — the opt-in interaction trace.
// Recorder: no allocation on the hot path (off or on), every record written once in each thread's
// order, and a lapped ring reports what it dropped. Host: a paced drag through the real
// FractalRenderHost with a renderer stub that blocks in Render() as a vsync'd Present does,
// checked against counts taken outside the trace — the stub's Render calls, the host's
// FrameLanded events, the pacer's fires and requests — and the causal order of each frame's
// records (Trigger → calc → upload → Present → landed).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// The trace is process-wide (one static recorder): kept out of the parallel phase.
[Collection("MandelbrotGpuStatics")]
public sealed class InteractionTraceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ff_trace_test_" + Guid.NewGuid().ToString("N"));
    public InteractionTraceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed record Row(long T, int Thread, TraceKind Kind, long Seq, long A, long B);

    private static List<Row> Parse(string path, out long droppedNoted) => Parse(path, out droppedNoted, out _);

    private static List<Row> Parse(string path, out long droppedNoted, out long droppedAtEnd)
    {
        var rows = new List<Row>();
        droppedNoted = 0;
        droppedAtEnd = -1;
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith("# end dropped="))
            {
                droppedAtEnd = long.Parse(line["# end dropped=".Length..]);
                continue;
            }
            if (line.StartsWith("# dropped "))
            {
                droppedNoted += long.Parse(line.Split(' ')[2]);
                continue;
            }
            if (line.StartsWith('#') || line.StartsWith("t_us")) continue;
            var f = line.Split(',');
            rows.Add(new Row(long.Parse(f[0]), int.Parse(f[1]), Enum.Parse<TraceKind>(f[2]),
                             long.Parse(f[3]), long.Parse(f[4]), long.Parse(f[5])));
        }
        return rows;
    }

    // ── Recorder ──

    [Fact]
    public void Mark_AllocatesNothing_OffOrOn()
    {
        Assert.False(InteractionTrace.Enabled);   // off unless FF_INTERACTION_TRACE is set
        static long Burst()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20_000; i++) InteractionTrace.Mark(TraceKind.PointerMove, i, i, i);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Burst();   // JIT
        Assert.Equal(0, Burst());
        using (InteractionTrace.Start(Path.Combine(_dir, "a.csv")))
        {
            Assert.True(InteractionTrace.Enabled);
            Burst();
            Assert.Equal(0, Burst());
        }
        Assert.False(InteractionTrace.Enabled);
    }

    [Fact]
    public void EveryRecord_IsWrittenOnce_InEachThreadsOrder()
    {
        string path = Path.Combine(_dir, "b.csv");
        const int Threads = 4, PerThread = 5000;
        var ids = new int[Threads];
        using (InteractionTrace.Start(path))
        {
            var ts = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
            {
                ids[t] = Environment.CurrentManagedThreadId;
                for (int i = 0; i < PerThread; i++)
                    InteractionTrace.Mark((TraceKind)(1 + i % 20), t, i, -i);
            })).ToList();
            ts.ForEach(x => x.Start());
            ts.ForEach(x => x.Join());
        }
        var rows = Parse(path, out long dropped);
        Assert.Equal(0, dropped);
        Assert.Equal(Threads * PerThread, rows.Count);
        for (int t = 0; t < Threads; t++)
        {
            var mine = rows.Where(r => r.Seq == t).ToList();
            Assert.All(mine, r => Assert.Equal(ids[t], r.Thread));
            Assert.Equal(Enumerable.Range(0, PerThread).Select(i => (long)i), mine.Select(r => r.A));
            Assert.All(mine, r => Assert.Equal(-r.A, r.B));
            Assert.All(mine, r => Assert.Equal((TraceKind)(1 + r.A % 20), r.Kind));
            for (int i = 1; i < mine.Count; i++) Assert.True(mine[i].T >= mine[i - 1].T);
        }
    }

    [Fact]
    public void ALappedRing_ReportsWhatItDropped()
    {
        string path = Path.Combine(_dir, "c.csv");
        const int N = 600_000;   // > 4 rings, faster than the 100 ms flush
        using (InteractionTrace.Start(path))
            for (int i = 0; i < N; i++) InteractionTrace.Mark(TraceKind.UiLag, 0, i);
        var rows = Parse(path, out long dropped, out long droppedAtEnd);
        Assert.Equal(N, rows.Count + dropped);
        Assert.Equal(dropped, droppedAtEnd);   // the closing total agrees with the gaps noted
        Assert.True(dropped > 0, "the ring never lapped: the test did not exercise the drop path");
        // What was kept is in order and not duplicated.
        for (int i = 1; i < rows.Count; i++) Assert.True(rows[i].A > rows[i - 1].A);
    }

    // ── Host ──

    private const int W = 320, H = 200;

    private sealed class VsyncRenderer : IFractalRenderer
    {
        public int Renders;
        public void UpdateTexture(uint[] c, int w, int h) { }
        public void Render() { Thread.Sleep(14); Interlocked.Increment(ref Renders); }   // a vsync'd Present
        public void Resize(int w, int h) { }
        public string RendererDescription => "vsync stub";
        public bool VSync { get; set; } = true;
        public void Dispose() { }
    }

    public enum Case { Mandelbrot2DPan, Mandelbulb3DProgressive }

    [Theory]
    [InlineData(Case.Mandelbrot2DPan)]
    [InlineData(Case.Mandelbulb3DProgressive)]
    public void APacedDrag_TracesEachFrameFromTriggerToScreen(Case c)
    {
        bool progressive = c == Case.Mandelbulb3DProgressive;
        var (w, h) = progressive ? (480, 320) : (W, H);
        var vs = new FractalViewState { Quality = QualityPreset.All.First(q => q.Name == "Draft") };
        if (progressive) { vs.FractalType = FractalType.Mandelbulb; vs.Zoom = 1; }
        else { vs.FractalType = FractalType.Mandelbrot; vs.CenterX = -0.75; vs.CenterY = 0.1; vs.Zoom = 4; }
        var r = new VsyncRenderer();
        string path = Path.Combine(_dir, $"host_{c}.csv");
        int renders, landed = 0, fired = 0;
        const int Moves = 200;
        using (var host = new FractalRenderHost(r, vs, w, h, ColorPalette.BuiltIns[0]))
        {
            var first = new AutoResetEvent(false);
            host.FrameCompleted += (_, _) => first.Set();
            host.Trigger();
            Assert.True(first.WaitOne(20000));
            Thread.Sleep(300);   // nothing in flight

            using var pacer = new InteractiveTriggerPacer(p => { Interlocked.Increment(ref fired); host.Trigger(p); });
            host.FrameLanded += (_, _) => Interlocked.Increment(ref landed);
            host.FrameLanded += (_, _) => pacer.FrameDone();
            int renders0;
            using (InteractionTrace.Start(path))
            {
                renders0 = Volatile.Read(ref r.Renders);
                long start = Stopwatch.GetTimestamp(), tick = Stopwatch.Frequency * 3 / 1000;
                for (int i = 1; i <= Moves; i++)
                {
                    if (progressive) vs.FractalParameters.BulbCameraTheta = 0.9 + 0.01 * i;
                    else vs.CenterX = -0.75 + 0.002 * i;
                    pacer.Request(progressive);
                    long next = start + i * tick;
                    while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
                }
                // The last move lands and its chain settles.
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    int l = Volatile.Read(ref landed); Thread.Sleep(400); return l == Volatile.Read(ref landed);
                }, 20000));
                renders = Volatile.Read(ref r.Renders) - renders0;
                InteractionTrace.Flush();
                // Counts read while the trace still runs, so a late record can't slip in between.
                landed = Volatile.Read(ref landed);
                fired = Volatile.Read(ref fired);
            }
        }

        var rows = Parse(path, out long dropped);
        Assert.Equal(0, dropped);

        // Counts against sources outside the trace.
        Assert.Equal(Moves, rows.Count(x => x.Kind == TraceKind.PacerRequest));
        Assert.Equal(renders, rows.Count(x => x.Kind == TraceKind.Present));
        int landedRecs = rows.Count(x => x.Kind == TraceKind.Landed);
        Assert.Equal(landed, landedRecs);
        int pacerStarts = rows.Count(x => x.Kind == TraceKind.PacerRequest && x.A != 2) + rows.Count(x => x.Kind == TraceKind.PacerFire);
        Assert.Equal(fired, pacerStarts);
        // Every paced Trigger is traced; a progressive chain's later stages are follow-on jobs, not Triggers.
        Assert.Equal(fired, rows.Count(x => x.Kind == TraceKind.TriggerExit));
        Assert.Equal(rows.Count(x => x.Kind == TraceKind.TriggerEnter), rows.Count(x => x.Kind == TraceKind.TriggerExit));
        Assert.True(landed >= 10, $"only {landed} frames landed: the drag did not exercise the pipeline");

        // Each landed frame's records, in causal order.
        var firstOf = new Dictionary<(TraceKind, long), Row>();
        foreach (var x in rows) firstOf.TryAdd((x.Kind, x.Seq), x);
        var triggerSeqs = rows.Where(x => x.Kind == TraceKind.TriggerExit).Select(x => x.Seq).ToList();
        foreach (var l in rows.Where(x => x.Kind == TraceKind.Landed))
        {
            // The job came from the latest Trigger at or before it (a follow-on stage keeps its own seq,
            // higher than the Trigger's).
            Assert.Contains(triggerSeqs, s => s <= l.Seq);
            Assert.True(firstOf.TryGetValue((TraceKind.CalcStart, l.Seq), out var cs), $"seq {l.Seq}: no CalcStart");
            Assert.True(firstOf.TryGetValue((TraceKind.CalcEnd, l.Seq), out var ce), $"seq {l.Seq}: no CalcEnd");
            Assert.True(firstOf.TryGetValue((TraceKind.UploadStart, l.Seq), out var us), $"seq {l.Seq}: no UploadStart");
            var pres = rows.Where(x => x.Kind == TraceKind.Present && x.Seq == l.Seq && x.T <= l.T).ToList();
            Assert.NotEmpty(pres);
            Assert.True(cs.T <= ce.T && ce.T <= us.T && us.T <= pres[^1].T && pres[^1].T <= l.T,
                        $"seq {l.Seq}: out of order {cs.T} {ce.T} {us.T} {pres[^1].T} {l.T}");
            Assert.Equal(0, ce.B);   // a landed frame was not cancelled
        }
        // A Trigger's records pair on its thread and its seq rises.
        var seqs = rows.Where(x => x.Kind == TraceKind.TriggerExit).Select(x => x.Seq).ToList();
        for (int i = 1; i < seqs.Count; i++) Assert.True(seqs[i] > seqs[i - 1]);
        // The flags say what kind of render each Trigger was.
        Assert.All(rows.Where(x => x.Kind == TraceKind.TriggerExit), x =>
        {
            Assert.Equal(progressive, (x.B & (long)TriggerFlags.Progressive) != 0);
            Assert.Equal(progressive, (x.B & (long)TriggerFlags.Is3D) != 0);
        });
    }
}
