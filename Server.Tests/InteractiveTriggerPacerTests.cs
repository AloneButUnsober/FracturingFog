// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1757 (interactivity epic #1756, S1) — pointer-driven renders paced to the pipeline.
// Unit: the pacer's rules on a fake clock. Host: a 3 ms-per-move drag (a fast mouse) through the
// real FractalRenderHost with a renderer that blocks in Render() as a vsync'd Present does, paced
// vs unpaced in the same run. Independent checks: Triggers reaching the host per frame shown,
// the caller's (UI thread's) time per move, frames shown and the longest gap no worse, the last
// move rendered, and no torn image (every image shown is the settled frame of a dragged view).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// Timing comparisons (paced vs unpaced, in the same run): kept out of the parallel phase, where
// other tests' load skews them (a full-suite run failed two rate checks that pass alone).
[Collection("MandelbrotGpuStatics")]
public sealed class InteractiveTriggerPacerTests
{
    private readonly ITestOutputHelper _out;
    public InteractiveTriggerPacerTests(ITestOutputHelper output) => _out = output;

    // ── The pacer's rules ──

    private sealed class Rig
    {
        public long Now;
        public readonly List<bool> Fired = new();
        public readonly InteractiveTriggerPacer P;
        public Rig(int maxWait = 50, int hardStale = 2000)
            => P = new InteractiveTriggerPacer(p => { lock (Fired) Fired.Add(p); }, maxWait, hardStale, () => Interlocked.Read(ref Now));
        public int Count { get { lock (Fired) return Fired.Count; } }
    }

    [Fact]
    public void AnIdleRequest_FiresAtOnce_AndMovesInFlight_CollapseToTheLatest()
    {
        var r = new Rig();
        r.P.Request(true);
        Assert.Equal(new[] { true }, r.Fired);
        r.P.Request(true); r.P.Request(true); r.P.Request(false);   // a frame is in flight
        Assert.Single(r.Fired);
        r.P.FrameDone();                                             // it lands: one render, the latest
        Assert.Equal(new[] { true, false }, r.Fired);
        r.P.FrameDone();                                             // nothing pending: idle
        r.P.Request(true);                                           // so the next move fires at once
        Assert.Equal(new[] { true, false, true }, r.Fired);
    }

    [Fact]
    public void TheLastMove_IsNeverLost()
    {
        var r = new Rig();
        var rng = new Random(7);
        bool last = false;
        for (int i = 0; i < 500; i++)
        {
            last = rng.Next(2) == 0;
            r.P.Request(last);
            if (rng.Next(5) == 0) r.P.FrameDone();
        }
        while (true) { int n = r.Fired.Count; r.P.FrameDone(); if (r.Fired.Count == n) break; }
        Assert.Equal(last, r.Fired[^1]);
        Assert.True(r.Fired.Count < 200, $"{r.Fired.Count} renders for 500 moves");
    }

    [Fact]
    public void APendingMove_FiresAfterMaxWait_WhenTheFrameNeverLands()
    {
        var r = new Rig(maxWait: 30);
        r.P.Request(false);
        r.P.Request(true);
        Assert.Single(r.Fired);
        Interlocked.Add(ref r.Now, 31);                               // the frame is slow
        Assert.True(SpinWait.SpinUntil(() => { lock (r.Fired) return r.Fired.Count == 2; }, 2000), "the watchdog never fired");
        Assert.True(r.Fired[1]);
    }

    [Fact]
    public void AForcedStart_HappensOncePerLandedFrame()
    {
        // A 2D frame slower than MaxWait is cancelled by the forced start; forcing again every
        // MaxWait cancelled each new frame in turn (a live 1.2 s freeze). The restarted frame must
        // be allowed to land.
        var r = new Rig(maxWait: 30, hardStale: 5000);
        r.P.Request(false);
        r.P.Request(true);
        Interlocked.Add(ref r.Now, 31);
        Assert.True(SpinWait.SpinUntil(() => r.Count == 2, 2000), "the first forced start never came");

        r.P.Request(false);                                           // more moves while the restart runs
        Interlocked.Add(ref r.Now, 200);                              // far past MaxWait
        Thread.Sleep(150);                                            // the watchdog has had its chance
        r.P.Request(true);
        Assert.Equal(2, r.Count);                                     // not forced again

        r.P.FrameDone();                                              // the restarted frame lands
        Assert.Equal(3, r.Count);
        Assert.True(r.Fired[2]);                                      // the latest move
        r.P.Request(false);
        Interlocked.Add(ref r.Now, 31);                               // a landed frame re-arms the force
        Assert.True(SpinWait.SpinUntil(() => r.Count == 4, 2000), "no forced start after a frame landed");
    }

    [Fact]
    public void ALostCompletion_GoesStale_AndAResetDropsThePending()
    {
        var r = new Rig(maxWait: 50, hardStale: 100);
        r.P.Request(false);
        Interlocked.Add(ref r.Now, 101);                              // no FrameDone ever came
        r.P.Request(true);
        Assert.Equal(2, r.Fired.Count);                               // fired, not queued forever

        r.P.Request(false);                                           // pending behind the new one
        r.P.Reset();                                                  // a full render covers it
        r.P.FrameDone();
        Assert.Equal(2, r.Fired.Count);
        r.P.Request(true);                                            // idle again after the reset
        Assert.Equal(3, r.Fired.Count);
    }

    // ── A fast-mouse drag through the real host ──

    private const int W = 320, H = 200;

    private sealed class VsyncRenderer : IFractalRenderer
    {
        public readonly ConcurrentQueue<(long Ticks, uint[] Frame)> Shown = new();
        private readonly int _w, _h;
        private uint[]? _texture;
        public VsyncRenderer(int w = W, int h = H) { _w = w; _h = h; }
        public void UpdateTexture(uint[] c, int w, int h) => _texture = w == _w && h == _h ? c[..(w * h)] : null;
        public void Render()
        {
            Thread.Sleep(14);   // a vsync'd Present on a ~70 Hz display
            if (_texture != null) Shown.Enqueue((Stopwatch.GetTimestamp(), _texture));
        }
        public void Resize(int w, int h) { }
        public string RendererDescription => "vsync stub";
        public bool VSync { get; set; } = true;
        public void Dispose() { }
    }

    private static string Key(uint[] f)
    {
        var b = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
    }

    public enum Case { Mandelbrot2DPan, MathPlane3DOrbit, Mandelbrot2DSlow, Mandelbulb3DProgressive }

    // Mandelbulb3DProgressive: a right-drag (RenderHint.Fast → progressive) on a raymarcher, big
    // enough for the low-res preview chain (≥ 256² px); its previews must release the pacer.
    private static (int W, int H) Size(Case c) => c == Case.Mandelbulb3DProgressive ? (480, 320) : (W, H);
    private static bool Progressive(Case c) => c == Case.Mandelbulb3DProgressive;

    [ThreadStatic] private static bool t_inFrameDone;

    private static (FractalRenderHost, VsyncRenderer, FractalViewState) NewHost(Case c)
    {
        var vs = new FractalViewState();
        // Mandelbrot2DSlow: 16x quality AA makes each 2D frame slower than the pacer's 50 ms force
        // limit, like the live 2D drag whose frames took 100-230 ms. Temporal AA stays off (Draft's),
        // so a settled frame is one image, not a sequence still refining.
        var draft = QualityPreset.All.First(q => q.Name == "Draft");
        vs.Quality = c != Case.Mandelbrot2DSlow ? draft : new QualityPreset
        {
            Name = "Draft (16x AA, test)", Tier = draft.Tier, ZoomMin = draft.ZoomMin, ZoomMax = draft.ZoomMax,
            WheelZoomFactor = draft.WheelZoomFactor, IterBase = draft.IterBase, IterMax = draft.IterMax,
            IterPerDecade = draft.IterPerDecade, AllowHighPrecision = draft.AllowHighPrecision,
            HPZoomThreshold = draft.HPZoomThreshold, AaSamples = 16, TaaMaxSamples = draft.TaaMaxSamples,
        };
        if (c is Case.Mandelbrot2DPan or Case.Mandelbrot2DSlow) { vs.FractalType = FractalType.Mandelbrot; vs.CenterX = -0.75; vs.CenterY = 0.1; vs.Zoom = 4; }
        else if (c == Case.Mandelbulb3DProgressive) { vs.FractalType = FractalType.Mandelbulb; vs.Zoom = 1; }
        else { vs.FractalType = FractalType.MathPlane3D; MathPlane3DPresets.Find("Parametric torus")!.Apply(vs.FractalParameters); }
        var (w, h) = Size(c);
        var r = new VsyncRenderer(w, h);
        return (new FractalRenderHost(r, vs, w, h, ColorPalette.BuiltIns[0]), r, vs);
    }

    private static void Move(Case c, FractalViewState vs, int i)
    {
        if (c is Case.Mandelbrot2DPan or Case.Mandelbrot2DSlow) vs.CenterX = -0.75 + 0.002 * i;
        else if (c == Case.Mandelbulb3DProgressive) vs.FractalParameters.BulbCameraTheta = 0.9 + 0.01 * i;
        else vs.FractalParameters.MathPlane3DCameraTheta = 0.9 + 0.01 * i;
    }

    private const int Moves = 300, MoveMs = 3;

    private sealed record DragStats(int Triggers, double CallerMsPerMove, int Images, double Fps, double MaxGapMs,
                                    string LastShown, List<uint[]> AllShown);

    private static DragStats Drag(Case c, bool paced)
    {
        var (host, r, vs) = NewHost(c);
        using (host)
        {
            var first = new AutoResetEvent(false);
            host.FrameCompleted += (_, _) => first.Set();
            Move(c, vs, 0);
            host.Trigger();
            Assert.True(first.WaitOne(20000));
            Thread.Sleep(100);
            while (r.Shown.TryDequeue(out _)) { }

            int triggers = 0;
            using var pacer = new InteractiveTriggerPacer(p => { Interlocked.Increment(ref triggers); host.Trigger(p); });
            host.FrameLanded += (_, _) => pacer.FrameDone();

            long caller = 0;
            long start = Stopwatch.GetTimestamp(), tick = Stopwatch.Frequency * MoveMs / 1000;
            for (int i = 1; i <= Moves; i++)
            {
                Move(c, vs, i);
                long t0 = Stopwatch.GetTimestamp();
                if (paced) pacer.Request(Progressive(c));
                else { Interlocked.Increment(ref triggers); host.Trigger(Progressive(c)); }
                caller += Stopwatch.GetTimestamp() - t0;
                long next = start + i * tick;
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
            }
            long end = Stopwatch.GetTimestamp();
            Thread.Sleep(600);   // the last move lands

            var fresh = new List<long>();
            string? prev = null;
            foreach (var s in r.Shown.Where(s => s.Ticks <= end))
            {
                string k = Key(s.Frame);
                if (k != prev) fresh.Add(s.Ticks);
                prev = k;
            }
            double dragMs = (end - start) * 1000.0 / Stopwatch.Frequency;
            var times = new[] { start }.Concat(fresh).Append(end).ToList();
            double maxGap = times.Zip(times.Skip(1), (a, b) => (b - a) * 1000.0 / Stopwatch.Frequency).Max();
            var all = r.Shown.Select(s => s.Frame).ToList();
            return new DragStats(triggers, caller * 1000.0 / Stopwatch.Frequency / Moves, fresh.Count,
                                 fresh.Count * 1000.0 / dragMs, maxGap, Key(all[^1]), all);
        }
    }

    private static HashSet<string> Settled(Case c, out string last)
    {
        var set = new HashSet<string>();
        var (host, r, vs) = NewHost(c);
        using (host)
        {
            var done = new AutoResetEvent(false);
            host.FrameCompleted += (_, _) => done.Set();
            for (int i = 0; i <= Moves; i++)
            {
                Move(c, vs, i);
                host.Trigger();
                Assert.True(done.WaitOne(20000));
                Thread.Sleep(20);
            }
            Thread.Sleep(50);
            var shown = r.Shown.Select(s => s.Frame).ToList();
            foreach (var f in shown) set.Add(Key(f));
            last = Key(shown[^1]);
        }
        return set;
    }

    [Fact]
    public void ThePanDrag_GoesThroughThePacer_InTheMainViewModel()
    {
        // The real wiring: pointer moves → FractalInputController (left-drag pan, RenderHint.Fast)
        // → MainViewModel → the pacer → FractalRenderHost.Trigger. Each Trigger raises StatusRequested
        // once, so it counts the renders started.
        var (host, r, vs) = NewHost(Case.Mandelbrot2DPan);
        using (host)
        {
            var input = new FracturingFog.Input.FractalInputController(vs);
            // Frame completion must drive the pacer: a render started from inside the host's
            // FrameLanded (the MainViewModel's handler, bracketed by these two) is
            // frame-driven; one started from the watchdog timer is not. Load-independent, unlike a
            // render count.
            host.FrameLanded += (_, _) => t_inFrameDone = true;
            using var vm = new FracturingFog.UI.Avalonia.ViewModels.MainViewModel(host, input);
            host.FrameLanded += (_, _) => t_inFrameDone = false;
            var first = new AutoResetEvent(false);
            host.FrameCompleted += (_, _) => first.Set();
            host.Trigger();
            Assert.True(first.WaitOne(20000));
            Thread.Sleep(100);
            int triggers = 0, frameDriven = 0;
            host.StatusRequested += (_, _) =>
            {
                Interlocked.Increment(ref triggers);
                if (t_inFrameDone) Interlocked.Increment(ref frameDriven);
            };

            FracturingFog.Input.PointerInput P(int x, FracturingFog.Input.PointerButton b)
                => new(x, 100, W, H, b, FracturingFog.Input.InputModifiers.None);
            input.OnPointerDown(P(10, FracturingFog.Input.PointerButton.Left));
            long start = Stopwatch.GetTimestamp(), tick = Stopwatch.Frequency * MoveMs / 1000;
            for (int i = 1; i <= Moves; i++)
            {
                input.OnPointerMove(P(10 + i % 200, FracturingFog.Input.PointerButton.Left));
                long next = start + i * tick;
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
            }
            int duringDrag = Volatile.Read(ref triggers), driven = Volatile.Read(ref frameDriven);
            _out.WriteLine($"MainViewModel pan: {Moves} moves -> {duringDrag} renders started, {driven} by a frame landing");
            Assert.True(duringDrag * 3 < Moves, $"{duringDrag} renders for {Moves} pointer moves (not paced)");
            Assert.True(duringDrag >= 5, $"only {duringDrag} renders during the drag");
            Assert.True(driven * 2 >= duringDrag, $"only {driven} of {duringDrag} renders were started by a frame landing");
            input.OnPointerUp(P(10 + Moves % 200, FracturingFog.Input.PointerButton.None));
        }
    }

    [Theory]
    [InlineData(Case.Mandelbrot2DPan)]
    [InlineData(Case.MathPlane3DOrbit)]
    [InlineData(Case.Mandelbrot2DSlow)]
    [InlineData(Case.Mandelbulb3DProgressive)]
    public void AFastMouseDrag_IsPacedToTheFramesShown(Case c)
    {
        // Previews are low-res stand-ins, not settled frames: that case checks pacing only.
        string lastSettled = "";
        var settled = Progressive(c) ? new HashSet<string>() : Settled(c, out lastSettled);
        var free = Drag(c, paced: false);
        var paced = Drag(c, paced: true);
        _out.WriteLine($"{c} unpaced: {free.Triggers} triggers, {free.CallerMsPerMove:0.000} ms/move, {free.Images} images ({free.Fps:0}/s), max gap {free.MaxGapMs:0} ms");
        _out.WriteLine($"{c} paced:   {paced.Triggers} triggers, {paced.CallerMsPerMove:0.000} ms/move, {paced.Images} images ({paced.Fps:0}/s), max gap {paced.MaxGapMs:0} ms");

        // Renders started: about one per frame shown, not one per move.
        Assert.True(paced.Triggers <= paced.Images * 2 + 5, $"{paced.Triggers} Triggers for {paced.Images} images");
        Assert.True(paced.Triggers * 3 < free.Triggers, $"paced {paced.Triggers} vs unpaced {free.Triggers} Triggers");
        // The caller (the UI thread) spends less per move.
        Assert.True(paced.CallerMsPerMove < free.CallerMsPerMove, $"paced {paced.CallerMsPerMove:0.000} vs {free.CallerMsPerMove:0.000} ms/move");
        // No worse on screen.
        Assert.True(paced.Fps >= free.Fps * 0.85, $"paced {paced.Fps:0}/s vs unpaced {free.Fps:0}/s");
        Assert.True(paced.MaxGapMs <= free.MaxGapMs + 25, $"paced gap {paced.MaxGapMs:0} vs {free.MaxGapMs:0} ms");
        // The last move is the image left on screen, and no image is torn.
        if (Progressive(c)) return;
        Assert.Equal(lastSettled, paced.LastShown);
        foreach (var f in paced.AllShown)
            Assert.True(settled.Contains(Key(f)), $"{c}: a shown image is not the settled frame of any dragged view");
    }
}
