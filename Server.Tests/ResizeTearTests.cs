// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1768 (interactivity epic #1756) — dragging a window edge flashed and showed horizontal bands.
// The live trace (#1762) showed frames calculated at the old size being presented after the swap
// chain was resized, and uploads still reading the calculator's buffers when a resize swapped them.
// Here the real FractalRenderHost is resized in a fast burst between sizes of different aspect,
// with a renderer stub that blocks in Render() as a vsync'd Present does. Independent checks, from
// what reached the stub:
//   - after the stub is resized to a size, every image handed to it until the next resize has that
//     size (no old-size frame stretched into the new window);
//   - every image is byte-equal to the settled frame of its size, rendered on a fresh host (no torn
//     or banded image);
//   - the last image is the settled frame of the last size.

using System;
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

[Collection("MandelbrotGpuStatics")]
public sealed class ResizeTearTests
{
    private readonly ITestOutputHelper _out;
    public ResizeTearTests(ITestOutputHelper output) => _out = output;

    private sealed class Ev
    {
        public bool IsResize;
        public int W, H;
        public uint[]? Frame;
    }

    private sealed class VsyncRenderer : IFractalRenderer
    {
        public readonly List<Ev> Log = new();
        private uint[]? _tex; private int _tw, _th;
        public void UpdateTexture(uint[] c, int w, int h)
        {
            lock (Log) { _tex = c[..(w * h)]; _tw = w; _th = h; }
        }
        public void Render()
        {
            Thread.Sleep(14);   // a vsync'd Present
            lock (Log)
                if (_tex != null) Log.Add(new Ev { W = _tw, H = _th, Frame = _tex });
        }
        public void Resize(int w, int h) { lock (Log) Log.Add(new Ev { IsResize = true, W = w, H = h }); }
        public string RendererDescription => "vsync stub";
        public bool VSync { get; set; } = true;
        public void Dispose() { }
    }

    private static readonly (int W, int H)[] Sizes = { (320, 200), (256, 240), (300, 150), (288, 216) };

    // Relief: the 2D relief runs in the upload, between its cancel check and its Present, which
    // widens the window in which a resize can meet an upload still reading the old buffers.
    // Hud: the perf HUD composite runs in the upload's post pipeline, after its cancel check and
    // before its Present: a resize landing there met an upload that presented the old size after
    // the swap chain was resized. Its text changes per frame, so it checks sizes, not pixels.
    public enum Case { Plain, Relief, Hud }

    private static FractalViewState View(Case c)
    {
        var vs = new FractalViewState
        {
            Quality = QualityPreset.All.First(q => q.Name == "Draft"),
            FractalType = FractalType.Mandelbrot, CenterX = -0.75, CenterY = 0.1, Zoom = 4,
        };
        if (c == Case.Relief) { vs.FractalParameters.Relief2DEnabled = true; vs.FractalParameters.Relief2DRaymarch = true; }
        return vs;
    }

    private static string Key(uint[] f)
    {
        var b = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
    }

    private static int SettleMs(Case c) => c == Case.Relief ? 1500 : 150;

    // Every image a fresh host shows at this size for this view (relief refines over a few
    // frames), and the last one.
    private static (HashSet<string> All, string Last) SettledAt(Case c, (int W, int H) s)
    {
        var r = new VsyncRenderer();
        using var host = new FractalRenderHost(r, View(c), s.W, s.H, ColorPalette.BuiltIns[0]);
        var done = new AutoResetEvent(false);
        host.FrameCompleted += (_, _) => done.Set();
        host.Trigger();
        Assert.True(done.WaitOne(20000));
        Thread.Sleep(SettleMs(c));
        lock (r.Log)
        {
            var keys = r.Log.Where(e => !e.IsResize).Select(e => Key(e.Frame!)).ToList();
            return (keys.ToHashSet(), keys[^1]);
        }
    }

    [Theory]
    [InlineData(Case.Plain)]
    [InlineData(Case.Relief)]
    [InlineData(Case.Hud)]
    public void AResizeBurst_ShowsOnlyWholeFramesOfTheCurrentSize(Case c)
    {
        var settled = Sizes.ToDictionary(s => s, s => SettledAt(c == Case.Hud ? Case.Plain : c, s));
        var r = new VsyncRenderer();
        int steps = 0;
        using (var host = new FractalRenderHost(r, View(c), Sizes[0].W, Sizes[0].H, ColorPalette.BuiltIns[0]))
        {
            host.ShowPerfHud = c == Case.Hud;
            var done = new AutoResetEvent(false);
            host.FrameCompleted += (_, _) => done.Set();
            host.Trigger();
            Assert.True(done.WaitOne(20000));
            Thread.Sleep(SettleMs(c));   // relief refines over several frames: let it finish
            lock (r.Log) r.Log.Clear();

            // A window-edge drag: resize steps 15-45 ms apart (the live trace's median was 67 ms), so
            // some frames land between steps and some steps meet a frame or its upload in flight.
            long next = Stopwatch.GetTimestamp();
            for (int i = 1; i <= 48; i++)
            {
                var s = Sizes[i % Sizes.Length];
                host.Resize(s.W, s.H);
                steps++;
                next += Stopwatch.Frequency * (15 + (i * 7) % 31) / 1000;
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(50);
            }
            Thread.Sleep(c == Case.Relief ? 1500 : 800);   // the last size settles
        }

        List<Ev> log;
        lock (r.Log) log = r.Log.ToList();
        // The one image allowed at another size: the host's re-present of the image already on screen,
        // straight after it resizes the swap chain (it covers the new back buffer until a frame of
        // the new size lands).
        (int W, int H)? current = null;
        bool rePresentDue = false;
        int images = 0, wrongSize = 0, torn = 0;
        foreach (var e in log)
        {
            if (e.IsResize) { current = (e.W, e.H); rePresentDue = true; continue; }
            images++;
            bool rePresent = rePresentDue && (e.W, e.H) != current;
            rePresentDue = false;
            // Relief renders interactive-quality frames while frames are cut short and converges on
            // the settled one (checked last), so only the plain view checks every image.
            if (c == Case.Plain && (!settled.TryGetValue((e.W, e.H), out var want) || !want.All.Contains(Key(e.Frame!))))
                torn++;
            if (current is { } cur && (e.W, e.H) != cur && !rePresent) wrongSize++;
        }
        var last = log.Last(e => !e.IsResize);
        _out.WriteLine($"{c}: {steps} resizes, {log.Count(e => e.IsResize)} reached the renderer; {images} images: " +
                       $"{wrongSize} of the previous size, {torn} not a settled frame");
        Assert.Equal(0, wrongSize);
        Assert.Equal(0, torn);
        Assert.Equal(Sizes[48 % Sizes.Length], (last.W, last.H));
        if (c != Case.Hud) Assert.Equal(settled[Sizes[48 % Sizes.Length]].Last, Key(last.Frame!));
    }
}
