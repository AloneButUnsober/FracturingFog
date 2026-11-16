// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1424 — a superseded frame's cancel (or late completion) must not reach the status
// bar. Trigger raises "Calculating…" and owns the bar until its own frame completes or
// is itself cancelled; before the fix the previous Trigger's job raised RenderCancelled
// when it noticed the cancel, and MainViewModel replayed the old frame's info as done.
// Checked on the real FractalRenderHost: the event order the status bar sees.
//   • Trigger A, then Trigger B while A is still calculating: between B's
//     "Calculating…" and B's FrameCompleted there is no RenderCancelled and no other
//     FrameCompleted; that FrameCompleted is B's view (its zoom).
//   • A cancel with no newer Trigger (a synchronous render of another region, as the
//     slideshow does) still raises RenderCancelled, so the bar is not left on
//     "Calculating…".

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection("MandelbrotGpuStatics")]
public sealed class S1424StaleCancelStatusTests(ITestOutputHelper output)
{
    private const int W = 640, H = 360;

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] c, int w, int h) { }
        public void Render() { }
        public void Resize(int w, int h) { }
        public string RendererDescription => "null stub";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private sealed record Ev(string Kind, double Zoom, long Ticks);

    // A slow CPU frame: the bench-kit p953 minibrot (fractadyne bench-kit, WindySnowOwl,
    // from rith's FF 0.7.1 report; see the bibliography), centre limbs as
    // S1626LaEvaluatorTests, at Extreme with its 4x4 anti-aliasing and no TAA (16
    // cancellable samples a frame). Zooming in keeps the centre, so the reference orbit is
    // reused and each frame is pixel work.
    private const double Zoom0 = 1.342765e53 * 3.5 * H / (4.0 * W);   // #1615 conversion

    private static (FractalRenderHost Host, FractalViewState Vs) NewHost()
    {
        double[] X = "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.203746728154813e-51|-4.269596053996866e-68|1.9389949392385534e-84|6.25819023760954e-101|-2.6646795788290983e-117"
            .Split('|').Select(double.Parse).ToArray();
        double[] Y = "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3771597857226995e-50|-5.482812918750436e-67|4.692321383846679e-84|-2.313052339214723e-100|2.1139231852108862e-116"
            .Split('|').Select(double.Parse).ToArray();
        var vs = new FractalViewState
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3], CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
            CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3], CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
            Zoom = Zoom0, IterLocked = true, LockedIterations = 28_590,
        };
        var x = QualityPreset.Extreme;
        vs.Quality = new QualityPreset
        {
            Tier = x.Tier, Name = "Extreme (no TAA)", Description = x.Description, ZoomMin = x.ZoomMin, ZoomMax = x.ZoomMax,
            WheelZoomFactor = x.WheelZoomFactor, IterBase = x.IterBase, IterMax = x.IterMax, IterPerDecade = x.IterPerDecade,
            AllowHighPrecision = true, HPZoomThreshold = x.HPZoomThreshold, AaSamples = 16, TaaMaxSamples = 0,
        };
        var r = new NullRenderer();
        return (new FractalRenderHost(r, vs, W, H, ColorPalette.BuiltIns[0]), vs);
    }

    private static ConcurrentQueue<Ev> Record(FractalRenderHost host, FractalViewState vs)
    {
        var q = new ConcurrentQueue<Ev>();
        host.StatusRequested += (_, t) => q.Enqueue(new Ev("status:" + t, vs.Zoom, Stopwatch.GetTimestamp()));
        host.RenderCancelled += (_, _) => q.Enqueue(new Ev("cancelled", double.NaN, Stopwatch.GetTimestamp()));
        host.FrameCompleted += (_, f) => q.Enqueue(new Ev("completed", f.Zoom, Stopwatch.GetTimestamp()));
        return q;
    }

    private static bool WaitFor(ConcurrentQueue<Ev> q, Func<Ev[], bool> done, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (done(q.ToArray())) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    [Fact]
    public void A_Superseded_Frame_Never_Reaches_The_Status_Bar()
    {
        var (host, vs) = NewHost();
        using (host)
        {
            var q = Record(host, vs);
            // Precondition: a frame takes long enough to be superseded mid-calculation.
            var sw = Stopwatch.StartNew();
            host.Trigger();
            Assert.True(WaitFor(q, e => e.Any(x => x.Kind == "completed"), 60000), "first frame never completed");
            double frameMs = sw.Elapsed.TotalMilliseconds;
            output.WriteLine($"one frame: {frameMs:F0} ms");
            Assert.True(frameMs > 150, $"frame too fast ({frameMs:F0} ms) to supersede");
            Thread.Sleep(300);

            for (int round = 0; round < 3; round++)
            {
                while (q.TryDequeue(out _)) { }
                double zoomA = Zoom0 * (1 + 0.25 * round), zoomB = zoomA * 1.1;
                vs.Zoom = zoomA;
                host.Trigger();                                   // A
                Thread.Sleep((int)Math.Min(60, frameMs / 4));     // A is calculating
                vs.Zoom = zoomB;
                host.Trigger();                                   // B supersedes A
                Assert.True(WaitFor(q, e => e.Any(x => x.Kind == "completed" && x.Zoom == zoomB), 60000), "B never completed");
                Thread.Sleep(200);   // a late stale event would land here

                var ev = q.ToArray();
                output.WriteLine($"round {round}: " + string.Join(", ", ev.Select(x => x.Kind + (double.IsNaN(x.Zoom) ? "" : $"@{x.Zoom:0}"))));
                int bStatus = Array.FindLastIndex(ev, x => x.Kind.StartsWith("status:") && x.Zoom == zoomB);
                int bDone = Array.FindIndex(ev, x => x.Kind == "completed" && x.Zoom == zoomB);
                Assert.True(bStatus >= 0 && bDone > bStatus);
                // Nothing but B's own completion settles the bar after B's "Calculating…".
                for (int i = bStatus + 1; i < ev.Length; i++)
                {
                    Assert.NotEqual("cancelled", ev[i].Kind);
                    if (ev[i].Kind == "completed") Assert.Equal(zoomB, ev[i].Zoom);
                }
                Assert.Equal(1, ev.Skip(bStatus + 1).Count(x => x.Kind == "completed"));
            }
        }
    }

    [Fact]
    public void A_Cancel_With_No_Newer_Trigger_Still_Clears_The_Status()
    {
        var (host, vs) = NewHost();
        using (host)
        {
            var q = Record(host, vs);
            host.Trigger();
            Assert.True(WaitFor(q, e => e.Any(x => x.Kind == "completed"), 60000));
            Thread.Sleep(300);
            while (q.TryDequeue(out _)) { }

            vs.Zoom = Zoom0 * 1.2;
            host.Trigger();
            Thread.Sleep(40);
            // A synchronous render of another region (the slideshow's) cancels the frame in
            // flight and starts no Trigger: the latest Trigger's own job is cancelled.
            var buf = host.RenderRegionToBuffer(new FractalRegion { FractalType = FractalType.Julia, Zoom = 1, Iterations = 64 },
                ColorPalette.BuiltIns[1], 64, 36);
            Assert.NotNull(buf);
            bool settled = WaitFor(q, e => e.Any(x => x.Kind == "cancelled"), 60000);
            Thread.Sleep(200);
            var ev = q.ToArray();
            output.WriteLine(string.Join(", ", ev.Select(x => x.Kind)));
            Assert.Equal(1, ev.Count(x => x.Kind.StartsWith("status:")));   // no Trigger of its own
            Assert.True(settled, "the status bar was left on Calculating… (no RenderCancelled)");
            Assert.DoesNotContain(ev, x => x.Kind == "completed");
        }
    }

    // The progressive chain (preview stages, then the final) is one Trigger: its final
    // frame still reaches the status bar, superseding a frame in flight or not.
    [Fact]
    public void A_Progressive_Trigger_Still_Completes_Through_Its_Preview_Stages()
    {
        var (host, vs) = NewHost();
        using (host)
        {
            var q = Record(host, vs);
            for (int round = 0; round < 2; round++)
            {
                while (q.TryDequeue(out _)) { }
                double zoom = Zoom0 * (1 + 0.3 * round);
                vs.Zoom = zoom;
                host.Trigger(progressive: true);
                Assert.True(WaitFor(q, e => e.Any(x => x.Kind == "completed" && x.Zoom == zoom), 60000),
                    $"round {round}: the progressive Trigger never completed");
                Thread.Sleep(100);
                var ev = q.ToArray();
                output.WriteLine($"round {round}: " + string.Join(", ", ev.Select(x => x.Kind)));
                Assert.DoesNotContain(ev, x => x.Kind == "cancelled");
            }
        }
    }
}
