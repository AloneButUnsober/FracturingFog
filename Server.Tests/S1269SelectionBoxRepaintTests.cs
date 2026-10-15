// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1269 (SM-4) — the right-drag selection-box outline redrew slowly at deep zoom.
// SetSelectionBox repainted synchronously on the UI thread, and the repaint must
// present, which needs the D3D gate; a deep GPU perturbation frame holds that
// gate for its whole dispatch (up to a 3 s budget), and TAA continuations queue
// more such frames. The fix: the repaint runs on its own coalescing worker, and
// TAA continuations are held while a box is on screen, then resumed.
//
// A renderer whose UpdateTexture blocks stands in for the held D3D gate. Checks:
//   • SetSelectionBox returns at once while presentation is blocked, and the box
//     then reaches the screen at the requested place;
//   • a burst of moves while blocked ends with the LAST box on screen;
//   • TAA does not advance while a box is shown, and on release the image still
//     settles with every sample exactly once.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1269SelectionBoxRepaintTests
{
    private const int W = 160, H = 120;

    private sealed class GatedRenderer : IFractalRenderer
    {
        public readonly ManualResetEventSlim Open = new(true);
        private readonly object _lock = new();
        private uint[]? _last;
        private int _uploads;
        public void UpdateTexture(uint[] colorBuffer, int width, int height)
        {
            Open.Wait();
            lock (_lock) { _last = colorBuffer.Take(width * height).ToArray(); _uploads++; }
        }
        public (uint[]? Last, int Uploads) Seen { get { lock (_lock) return (_last, _uploads); } }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "gated";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static FractalViewState State(QualityPreset q) => new()
    {
        FractalType = FractalType.Mandelbrot,
        CenterX = -0.7453, CenterY = 0.1127, Zoom = 1e3, Quality = q,
    };

    private static void FirstFrame(FractalRenderHost host)
    {
        using var done = new ManualResetEventSlim(false);
        EventHandler<RenderFrameInfo> h = (_, _) => done.Set();
        host.FrameCompleted += h;
        try
        {
            host.Trigger(progressive: false);
            Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame");
        }
        finally { host.FrameCompleted -= h; }
    }

    private static bool WaitFor(Func<bool> cond, int ms = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    /// <summary>Pixels that differ from the base frame, as (x, y).</summary>
    private static List<(int X, int Y)> Diff(uint[] a, uint[] b)
    {
        var d = new List<(int, int)>();
        for (int i = 0; i < W * H; i++) if (a[i] != b[i]) d.Add((i % W, i / W));
        return d;
    }

    [Fact]
    public void SetSelectionBox_Does_Not_Wait_For_A_Blocked_Present_And_The_Box_Lands()
    {
        var r = new GatedRenderer();
        using var host = new FractalRenderHost(r, State(QualityPreset.Standard), W, H, ColorPalette.BuiltIns[0]);
        FirstFrame(host);
        Assert.True(WaitFor(() => r.Seen.Last != null));
        var baseFrame = r.Seen.Last!;

        r.Open.Reset();   // presentation blocked, as behind a deep GPU frame
        try
        {
            var call = Task.Run(() => host.SetSelectionBox(40, 30, 60, 50));
            Assert.True(call.Wait(TimeSpan.FromSeconds(2)), "SetSelectionBox waited on the blocked present");
        }
        finally { r.Open.Set(); }

        // Once presentation resumes, the outline reaches the screen, and only
        // around the requested rectangle (40,30)-(100,80).
        Assert.True(WaitFor(() => r.Seen.Last is { } l && Diff(baseFrame, l).Count > 0), "the box never reached the screen");
        var changed = Diff(baseFrame, r.Seen.Last!);
        Assert.All(changed, p => Assert.True(p.X >= 37 && p.X <= 103 && p.Y >= 27 && p.Y <= 83,
            $"pixel ({p.X},{p.Y}) changed outside the box"));
        Assert.Contains(changed, p => Math.Abs(p.X - 40) <= 2);
        Assert.Contains(changed, p => Math.Abs(p.X - 100) <= 2);

        // Clearing restores the frame.
        host.SetSelectionBox(null, null, null, null);
        Assert.True(WaitFor(() => r.Seen.Last is { } l && Diff(baseFrame, l).Count == 0), "clearing left the box on screen");
        Assert.False(host.SelectionBoxActive);
    }

    [Fact]
    public void A_Burst_Of_Moves_While_Blocked_Ends_On_The_Last_Box()
    {
        var r = new GatedRenderer();
        using var host = new FractalRenderHost(r, State(QualityPreset.Standard), W, H, ColorPalette.BuiltIns[0]);
        FirstFrame(host);
        Assert.True(WaitFor(() => r.Seen.Last != null));
        var baseFrame = r.Seen.Last!;
        int uploadsBefore = r.Seen.Uploads;

        r.Open.Reset();
        try
        {
            for (int i = 0; i < 30; i++) host.SetSelectionBox(10, 10, 20 + i, 20 + i);
        }
        finally { r.Open.Set(); }

        // The last box is (10,10) + 49x49, so its far edge is x = y = 59: the
        // outline reaches it (every earlier box is smaller and stops sooner), and
        // nothing changes beyond it.
        Assert.True(WaitFor(() => r.Seen.Last is { } l && Diff(baseFrame, l).Any(p => p.X >= 57)), "the last box never landed");
        Thread.Sleep(200);
        var changed = Diff(baseFrame, r.Seen.Last!);
        Assert.Contains(changed, p => p.X >= 57 && p.X <= 61);
        Assert.Contains(changed, p => p.Y >= 57 && p.Y <= 61);
        Assert.All(changed, p => Assert.True(p.X <= 62 && p.Y <= 62 && p.X >= 7 && p.Y >= 7, $"({p.X},{p.Y}) outside the last box"));
        // Coalesced: far fewer presents than moves.
        Assert.True(r.Seen.Uploads - uploadsBefore < 10, $"{r.Seen.Uploads - uploadsBefore} presents for 30 moves");
        host.SetSelectionBox(null, null, null, null);
    }

    [Fact]
    public void Taa_Is_Held_While_A_Box_Is_Shown_And_Settles_After_Release()
    {
        var q = QualityPreset.High;   // TaaMaxSamples = 8
        var r = new GatedRenderer();
        using var host = new FractalRenderHost(r, State(q), W, H, ColorPalette.BuiltIns[0]);
        var samples = new List<RefineProgressInfo>();
        var settled = new ManualResetEventSlim(false);   // not disposed: late events may still Set it
        host.RefineProgress += (_, e) => { lock (samples) samples.Add(e); if (e.Done) settled.Set(); };

        host.SetSelectionBox(5, 5, 30, 30);   // the box is up before the frame lands
        FirstFrame(host);
        Thread.Sleep(1500);
        int duringHold;
        lock (samples) duringHold = samples.Count;
        // Sample 1 is the FrameCompleted frame; at most the one continuation that
        // was already queued may land, then nothing until release.
        Assert.True(duringHold <= 2, $"{duringHold} refine steps while the box was shown");
        Assert.False(settled.IsSet, "settled while held");

        host.SetSelectionBox(null, null, null, null);
        Assert.True(settled.Wait(TimeSpan.FromSeconds(60)), "TAA never resumed after the box was released");
        Thread.Sleep(200);
        lock (samples)
        {
            Assert.Single(samples, s => s.Done);
            Assert.Equal(q.TaaMaxSamples, samples[^1].Sample);
            // Every sample index exactly once (none lost or repeated by the hold).
            Assert.Equal(Enumerable.Range(samples[0].Sample, samples.Count), samples.Select(s => s.Sample));
        }
    }
}
