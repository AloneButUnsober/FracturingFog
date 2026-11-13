// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1754 — a camera drag showed 2-6 of ~60 frames a second (the #86 trace of a live session):
//   1. a redundant stale-hold Present before every calculation claimed the new sequence number,
//      so the frame still finishing was refused by the newest-wins gate;
//   2. on a 3D view every Trigger cancelled the frame being calculated, and a drag moves faster
//      than a frame takes;
//   3. (seen in the second live trace) a kept 3D preview's upload queued its chain's next stage
//      with a blocking Add on the bounded(1) queue while the calc thread waited on that upload's
//      barrier: one-second stalls on a Mandelbulb right-drag (the progressive preview path).
// The renderer here blocks in Render() as a vsync'd Present does, and each case drags with one
// Trigger per move at a fixed interval. Independent checks: new images keep reaching the screen
// (rate and longest gap), and every image shown is byte-equal to the settled frame of one of the
// dragged views — presenting kept frames never shows a torn buffer. (The progressive case shows
// low-res previews, which are not settled frames, so it checks rate and gaps only.)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection("MandelbrotGpuStatics")]
public sealed class DragFrameStarvationTests
{
    private readonly ITestOutputHelper _out;
    public DragFrameStarvationTests(ITestOutputHelper output) => _out = output;

    private sealed class VsyncRenderer : IFractalRenderer
    {
        public readonly ConcurrentQueue<(long Ticks, uint[] Frame)> Shown = new();
        private readonly int _w, _h;
        private uint[]? _texture;
        public VsyncRenderer(int w, int h) { _w = w; _h = h; }
        public void UpdateTexture(uint[] c, int w, int h)
            => _texture = w == _w && h == _h ? c[..(w * h)] : null;   // previews are not "new images"
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

    public enum Case { MathPlane3DCheap, MathPlane3DHeavy, Mandelbrot2DPan, Mandelbulb3DProgressive }

    private static (int W, int H, int MoveMs, int Moves) Setup(Case c) => c switch
    {
        Case.MathPlane3DCheap => (320, 200, 10, 100),
        Case.MathPlane3DHeavy => (960, 540, 5, 120),
        Case.Mandelbulb3DProgressive => (1280, 744, 20, 100),   // the third live trace: window, ~48 moves/s
        _ => (320, 200, 10, 100),
    };

    private static (FractalRenderHost Host, VsyncRenderer R, FractalViewState Vs) NewHost(Case c)
    {
        var (w, h, _, _) = Setup(c);
        var vs = new FractalViewState();
        vs.Quality = QualityPreset.All.First(q => q.Name == "Draft");
        if (c == Case.Mandelbrot2DPan)
        {
            vs.FractalType = FractalType.Mandelbrot;
            vs.CenterX = -0.75; vs.CenterY = 0.1; vs.Zoom = 4;
        }
        else if (c == Case.Mandelbulb3DProgressive)
        {
            vs.FractalType = FractalType.Mandelbulb;
            vs.Zoom = 1;   // the view state's default 0.1 leaves the bulb out of sight (every frame alike)
        }
        else
        {
            vs.FractalType = FractalType.MathPlane3D;
            MathPlane3DPresets.Find(c == Case.MathPlane3DCheap ? "Parametric torus" : "Mandelbulb: an escaping orbit")!
                .Apply(vs.FractalParameters);
        }
        var r = new VsyncRenderer(w, h);
        return (new FractalRenderHost(r, vs, w, h, ColorPalette.BuiltIns[0]), r, vs);
    }

    // Move i of the drag: the camera turns (3D) or the centre pans (2D).
    private static void Move(Case c, FractalViewState vs, int i)
    {
        if (c == Case.Mandelbrot2DPan) vs.CenterX = -0.75 + 0.002 * i;
        else if (c == Case.Mandelbulb3DProgressive) vs.FractalParameters.BulbCameraTheta = 0.9 + 0.01 * i;
        else vs.FractalParameters.MathPlane3DCameraTheta = 0.9 + 0.01 * i;
    }

    [Theory]
    [InlineData(Case.MathPlane3DCheap)]
    [InlineData(Case.MathPlane3DHeavy)]
    [InlineData(Case.Mandelbrot2DPan)]
    [InlineData(Case.Mandelbulb3DProgressive)]
    public void ADrag_KeepsShowingWholeFrames(Case c)
    {
        var (_, _, moveMs, moves) = Setup(c);
        bool progressive = c == Case.Mandelbulb3DProgressive;   // a right-drag's RenderHint.Fast

        // The settled frame of every view the drag passes through.
        var settled = new HashSet<string>();
        if (!progressive)
        {
            var (host, r, vs) = NewHost(c);
            using (host)
            {
                var done = new AutoResetEvent(false);
                host.FrameCompleted += (_, _) => done.Set();
                for (int i = 0; i <= moves; i++)
                {
                    Move(c, vs, i);
                    host.Trigger();
                    Assert.True(done.WaitOne(20000));
                    Thread.Sleep(30);   // let the present land
                }
                foreach (var s in r.Shown) settled.Add(Key(s.Frame));
            }
        }
        if (!progressive) Assert.True(settled.Count > moves / 2, $"only {settled.Count} distinct settled frames");

        // The drag.
        var (h2, r2, vs2) = NewHost(c);
        using (h2)
        {
            var first = new AutoResetEvent(false);
            h2.FrameCompleted += (_, _) => first.Set();
            Move(c, vs2, 0);
            h2.Trigger();
            Assert.True(first.WaitOne(20000));
            Thread.Sleep(100);
            while (r2.Shown.TryDequeue(out _)) { }

            long start = Stopwatch.GetTimestamp(), tick = Stopwatch.Frequency * moveMs / 1000;
            for (int i = 1; i <= moves; i++)
            {
                Move(c, vs2, i);
                h2.Trigger(progressive);
                long next = start + i * tick;
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);   // Sleep(n) rounds up to ~15 ms on Windows
            }
            long end = Stopwatch.GetTimestamp();
            Thread.Sleep(500);

            // New images only: a Present of the image already on screen shows nothing new.
            var fresh = new List<long>();
            string? prev = null;
            foreach (var s in r2.Shown.Where(s => s.Ticks <= end))
            {
                string k = Key(s.Frame);
                if (k != prev) fresh.Add(s.Ticks);
                prev = k;
            }
            double dragMs = (end - start) * 1000.0 / Stopwatch.Frequency;
            var times = new[] { start }.Concat(fresh).Append(end).ToList();
            double maxGap = times.Zip(times.Skip(1), (a, b) => (b - a) * 1000.0 / Stopwatch.Frequency).Max();
            double fps = fresh.Count * 1000.0 / dragMs;
            _out.WriteLine($"DRAG {c}: {dragMs:0} ms, {moves} moves: {fresh.Count} new images ({fps:0}/s), max gap {maxGap:0} ms");

            // New images keep reaching the screen while the pointer moves.
            Assert.True(fps >= 15, $"{c}: {fresh.Count} new images in {dragMs:0} ms of dragging ({fps:0}/s)");
            Assert.True(maxGap < 150, $"{c}: a {maxGap:0} ms gap with no new image during the drag ({fresh.Count} shown)");

            // Every image shown is a whole frame of one of the dragged views, never a torn buffer.
            if (!progressive)
            foreach (var s in r2.Shown)
                Assert.True(settled.Contains(Key(s.Frame)), $"{c}: a shown image is not the settled frame of any dragged view");
        }
    }
}
