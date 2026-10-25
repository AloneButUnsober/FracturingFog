// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1392 (part 1) — the live view denoises a settled frame with Intel Open Image
// Denoise in the background instead of holding it back. Invariants, each against an
// independent synchronous OIDN call on the same input:
//   • a session leaves a new frame raw, then answers that exact frame with the
//     synchronous result; a newer frame supersedes (the older job never reports), a
//     cancel drops the job, a re-present that misses denoises synchronously;
//   • the denoiser's cancel predicate stops a filter (false, input untouched);
//   • without a session (batch, export) and with the debug HUD on, the pass stays
//     synchronous;
//   • through a real host: a 3D frame and a relief frame are shown raw at once, then
//     re-presented as exactly the synchronous denoise of that raw frame; the 3D
//     calculator's own buffer stays raw; a supersampled 3D frame keeps the
//     synchronous pass; a save waits for the denoised frame.
// Needs the OIDN runtime (FF_OIDN_DIR or the in-app install); skips without it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>Tests that use the OIDN runtime or flip <see cref="OidnDenoiser.Disabled"/>.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OidnRuntimeCollection { public const string Name = "OidnRuntime"; }

[Collection(OidnRuntimeCollection.Name)]
public sealed class S1392OidnAsyncTests(ITestOutputHelper output)
{
    private static bool Oidn => OidnDenoiser.IsAvailable;

    // A frame with real per-pixel noise (OIDN changes it visibly).
    private static uint[] Noisy(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var f = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int r = Math.Clamp(40 + 160 * x / w + rng.Next(-60, 60), 0, 255);
                int g = Math.Clamp(60 + 120 * y / h + rng.Next(-60, 60), 0, 255);
                int b = Math.Clamp(128 + rng.Next(-60, 60), 0, 255);
                f[y * w + x] = 0xFF000000u | (uint)r << 16 | (uint)g << 8 | (uint)b;
            }
        return f;
    }

    private static uint[] SyncDenoise(uint[] frame, int w, int h)
    {
        var o = (uint[])frame.Clone();
        Assert.True(OidnDenoiser.TryDenoiseBgra(o, null, null, w, h, out var err), err);
        return o;
    }

    private static bool WaitFor(Func<bool> cond, int ms = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(10); }
        return true;
    }

    [Fact]
    public void A_Session_Leaves_A_New_Frame_Raw_Then_Answers_It_With_The_Synchronous_Result()
    {
        if (!Oidn) { output.WriteLine("OIDN not installed: " + OidnDenoiser.LoadError); return; }
        const int w = 160, h = 90;
        var raw = Noisy(w, h, 1);
        var expected = SyncDenoise(raw, w, h);
        Assert.NotEqual(raw, expected);   // precondition: OIDN changes it

        using var s = new OidnAsync();
        var tags = new List<long>();
        s.Completed += t => { lock (tags) tags.Add(t); };
        var frame = (uint[])raw.Clone();
        using (s.Enter(7)) Assert.Equal(OidnAsync.Outcome.Deferred, s.Resolve(frame, w, h));
        Assert.Equal(raw, frame);                                  // left raw
        Assert.True(WaitFor(() => !s.Pending), "the job never finished");
        Assert.Equal(new long[] { 7 }, tags);
        Assert.Equal(expected, s.TryGetResult(raw, w, h));         // the exact synchronous result
        using (s.Enter(8)) Assert.Equal(OidnAsync.Outcome.Denoised, s.Resolve(frame, w, h));
        Assert.Equal(expected, frame);
        Assert.Equal(1, s.JobsStarted);                            // the hit started nothing
        Assert.Null(s.TryGetResult(Noisy(w, h, 2), w, h));         // another frame: no result
    }

    [Fact]
    public void A_Newer_Frame_Supersedes_A_Cancel_Drops_The_Job_And_A_Replay_Miss_Runs_Now()
    {
        if (!Oidn) { output.WriteLine("OIDN not installed"); return; }
        const int w = 640, h = 360;
        var a = Noisy(w, h, 3);
        var b = Noisy(w, h, 4);
        using var s = new OidnAsync();
        var tags = new List<long>();
        s.Completed += t => { lock (tags) tags.Add(t); };
        using (s.Enter(1)) s.Resolve((uint[])a.Clone(), w, h);
        using (s.Enter(2)) s.Resolve((uint[])b.Clone(), w, h);
        Assert.True(WaitFor(() => !s.Pending));
        Assert.Equal(new long[] { 2 }, tags);                      // a's job never reports
        Assert.Null(s.TryGetResult(a, w, h));
        Assert.Equal(SyncDenoise(b, w, h), s.TryGetResult(b, w, h));

        var c = Noisy(w, h, 5);
        using (s.Enter(3)) s.Resolve((uint[])c.Clone(), w, h);
        s.Cancel();
        Thread.Sleep(3000);                                         // longer than the denoise
        Assert.False(s.Pending);
        Assert.Equal(new long[] { 2 }, tags);
        Assert.Null(s.TryGetResult(c, w, h));
        Assert.NotNull(s.TryGetResult(b, w, h));                   // a finished result survives a cancel

        using (s.Enter(4, replay: true)) Assert.Equal(OidnAsync.Outcome.RunNow, s.Resolve((uint[])c.Clone(), w, h));
        Assert.Equal(1, s.ReplayMisses);
    }

    [Fact]
    public void The_Cancel_Predicate_Stops_The_Filter_And_Leaves_The_Input()
    {
        if (!Oidn) { output.WriteLine("OIDN not installed"); return; }
        Assert.True(OidnDenoiser.SupportsCancel);
        const int w = 640, h = 360;
        var raw = Noisy(w, h, 6);
        var f = (uint[])raw.Clone();
        Assert.False(OidnDenoiser.TryDenoiseBgra(f, null, null, w, h, out var err, cancel: () => true));
        Assert.Equal("cancelled", err);
        Assert.Equal(raw, f);
        // Cancelled mid-run, from the progress monitor: count its polls.
        int polls = 0;
        Assert.False(OidnDenoiser.TryDenoiseBgra(f, null, null, w, h, out err, cancel: () => Interlocked.Increment(ref polls) > 2));
        Assert.Equal("cancelled", err);
        Assert.True(polls > 2, $"the monitor polled {polls} times");
        Assert.Equal(raw, f);
        // And a later uncancelled call on the same cached filter still denoises.
        Assert.True(OidnDenoiser.TryDenoiseBgra(f, null, null, w, h, out err), err);
        Assert.NotEqual(raw, f);
    }

    [Fact]
    public void Without_A_Session_Or_With_The_Debug_Hud_The_Pass_Stays_Synchronous()
    {
        if (!Oidn) { output.WriteLine("OIDN not installed"); return; }
        const int w = 160, h = 90;
        var raw = Noisy(w, h, 7);
        var expected = SyncDenoise(raw, w, h);
        var fx = new FractalParameters().Lighting;
        fx.OidnDenoise = true;

        var f = (uint[])raw.Clone();
        Assert.True(ScreenSpacePost.ApplyOidn(f, w, h, in fx));    // no session: now
        Assert.Equal(expected, f);

        using var s = new OidnAsync();
        f = (uint[])raw.Clone();
        using (s.Enter(1)) Assert.False(ScreenSpacePost.ApplyOidn(f, w, h, in fx));
        Assert.Equal(raw, f);                                       // a session: deferred
        Assert.True(WaitFor(() => !s.Pending));

        var hud = fx; hud.DebugHudFlags = 1;
        f = (uint[])Noisy(w, h, 8).Clone();
        var hudExpected = SyncDenoise(f, w, h);
        using (s.Enter(2)) Assert.True(ScreenSpacePost.ApplyOidn(f, w, h, in hud));
        Assert.Equal(hudExpected, f);                               // the HUD: now
        Assert.Equal(1, s.JobsStarted);
    }
}

/// <summary>#1392 — through a real host.</summary>
[Collection(OidnRuntimeCollection.Name)]
public sealed class S1392OidnAsyncHostTests(ITestOutputHelper output)
{
    private sealed class Recorder : IFractalRenderer
    {
        private readonly object _lock = new();
        public readonly List<(uint[] Px, int W, int H)> Uploads = new();
        public void UpdateTexture(uint[] colorBuffer, int width, int height)
        {
            lock (_lock) Uploads.Add((colorBuffer.Take(width * height).ToArray(), width, height));
        }
        public List<(uint[] Px, int W, int H)> Snapshot() { lock (_lock) return Uploads.ToList(); }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "recorder";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private const int W = 320, H = 240;

    private static bool Settle(FractalRenderHost host, Recorder r, int minUploads, int ms = 90000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int last = -1; long stableSince = 0;
        while (sw.ElapsedMilliseconds < ms)
        {
            int n = r.Snapshot().Count;
            if (n != last) { last = n; stableSince = sw.ElapsedMilliseconds; }
            else if (n >= minUploads && !host.LiveDenoise.Pending && sw.ElapsedMilliseconds - stableSince > 1500) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static uint[] SyncDenoise(uint[] frame, int w = W, int h = H)
    {
        var o = (uint[])frame.Clone();
        Assert.True(OidnDenoiser.TryDenoiseBgra(o, null, null, w, h, out var err), err);
        return o;
    }

    private static FractalViewState State3D(QualityPreset q)
    {
        var s = new FractalViewState { FractalType = FractalType.Mandelbulb, Zoom = 1.0, Quality = q };
        var fx = s.FractalParameters.Lighting;   // a struct: copy, edit, store
        fx.UseGpuRender = false;
        fx.OidnDenoise = true;
        s.FractalParameters.Lighting = fx;
        return s;
    }

    [Fact]
    public void A_3D_Frame_Is_Shown_Raw_Then_As_The_Synchronous_Denoise_Of_It()
    {
        if (!OidnDenoiser.IsAvailable) { output.WriteLine("OIDN not installed"); return; }
        var r = new Recorder();
        using var host = new FractalRenderHost(r, State3D(QualityPreset.Standard), W, H, ColorPalette.BuiltIns[0]);
        host.Trigger(progressive: false);
        Assert.True(Settle(host, r, 2), "the frame never settled");
        var ups = r.Snapshot().Where(u => u.W == W && u.H == H).ToList();

        // The calculator's buffer stays raw: it equals a fresh render with OIDN off.
        var live = Assert.IsType<MandelbulbCalculator>(host.ActiveAltCalculator);
        var fp = live.FractalParameters.Clone();
        var off = fp.Lighting; off.OidnDenoise = false; fp.Lighting = off;
        var fresh = new MandelbulbCalculator(W, H) { ColorMap = live.ColorMap, FractalParameters = fp, Zoom = live.Zoom, CenterX = live.CenterX, CenterY = live.CenterY };
        fresh.Calculate(CancellationToken.None);
        var raw = fresh.ColorBuffer.Take(W * H).ToArray();
        Assert.Equal(raw, live.ColorBuffer.Take(W * H).ToArray());

        var denoised = SyncDenoise(raw);
        Assert.NotEqual(raw, denoised);   // precondition
        output.WriteLine($"{ups.Count} uploads, {host.LiveDenoise.JobsStarted} job(s)");
        Assert.Equal(raw, ups[0].Px);                   // shown at once, raw
        Assert.Equal(denoised, ups[^1].Px);             // then the exact synchronous denoise
        Assert.Equal(1, host.LiveDenoise.JobsStarted);
        Assert.Equal(0, host.LiveDenoise.ReplayMisses);
    }

    [Fact]
    public void A_Relief_Frame_Is_Shown_Raw_Then_As_The_Synchronous_Denoise_Of_It()
    {
        if (!OidnDenoiser.IsAvailable) { output.WriteLine("OIDN not installed"); return; }
        var s = new FractalViewState { FractalType = FractalType.Mandelbrot, CenterX = -0.75, Zoom = 1.0, Quality = QualityPreset.Standard };
        var fp = s.FractalParameters;
        fp.Relief2DEnabled = true;
        fp.Relief2DRaymarch = true;
        fp.Relief2DGpuRaymarch = false;
        var fx = fp.Lighting; fx.OidnDenoise = true; fp.Lighting = fx;
        var r = new Recorder();
        using var host = new FractalRenderHost(r, s, W, H, ColorPalette.BuiltIns[0]);
        host.UseGpuCompute = false;
        host.Trigger(progressive: false);
        Assert.True(Settle(host, r, 2), "the frame never settled");
        var ups = r.Snapshot().Where(u => u.W == W && u.H == H).ToList();
        output.WriteLine($"{ups.Count} uploads, {host.LiveDenoise.JobsStarted} job(s), {host.LiveDenoise.ReplayMisses} replay misses");
        var denoised = SyncDenoise(ups[0].Px);
        Assert.NotEqual(ups[0].Px, denoised);           // precondition: the first upload is raw
        Assert.Equal(denoised, ups[^1].Px);
        Assert.Equal(1, host.LiveDenoise.JobsStarted);
        Assert.Equal(0, host.LiveDenoise.ReplayMisses); // the re-run raymarch found its result
    }

    [Fact]
    public void A_Supersampled_3D_Frame_Keeps_The_Synchronous_Pass()
    {
        if (!OidnDenoiser.IsAvailable) { output.WriteLine("OIDN not installed"); return; }
        var r = new Recorder();
        using var host = new FractalRenderHost(r, State3D(QualityPreset.High), W, H, ColorPalette.BuiltIns[0]);
        Assert.True(QualityPreset.High.AaSamples > 1);   // precondition
        host.Trigger(progressive: false);
        Assert.True(Settle(host, r, 1), "no frame");
        Assert.Equal(0, host.LiveDenoise.JobsStarted);
        Assert.Single(r.Snapshot().Where(u => u.W == W && u.H == H));
    }

    [Fact]
    public void A_New_Trigger_Cancels_The_Running_Denoise()
    {
        if (!OidnDenoiser.IsAvailable) { output.WriteLine("OIDN not installed"); return; }
        var r = new Recorder();
        var s = State3D(QualityPreset.Standard);
        using var host = new FractalRenderHost(r, s, 960, 540, ColorPalette.BuiltIns[0]);
        int done = 0;
        host.FrameCompleted += (_, _) => Interlocked.Increment(ref done);
        host.Trigger(progressive: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref done) == 0 && sw.ElapsedMilliseconds < 60000) Thread.Sleep(1);
        Assert.True(host.LiveDenoise.Pending, "precondition: the raw frame is up and its denoise running");
        host.ViewState.Zoom = 1.05;
        host.Trigger(progressive: false);
        Assert.False(host.LiveDenoise.Pending);   // dropped at once, before the new frame asks
    }

    [Fact]
    public void A_Save_Waits_For_The_Denoised_Frame()
    {
        if (!OidnDenoiser.IsAvailable) { output.WriteLine("OIDN not installed"); return; }
        // 960×540: the denoise (~0.3 s) outlasts the save itself, so only a wait shows it.
        const int w = 960, h = 540;
        var r = new Recorder();
        using var host = new FractalRenderHost(r, State3D(QualityPreset.Standard), w, h, ColorPalette.BuiltIns[0]);
        int done = 0;
        host.FrameCompleted += (_, _) => Interlocked.Increment(ref done);
        host.Trigger(progressive: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref done) == 0 && sw.ElapsedMilliseconds < 60000) Thread.Sleep(1);
        Assert.True(host.LiveDenoise.Pending, "precondition: the raw frame is up and the denoise still running");
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s1392-{Guid.NewGuid():N}.png");
        try
        {
            host.SaveLastFrameToPng(path);
            Assert.False(host.LiveDenoise.Pending);
            var ups = r.Snapshot();
            Assert.True(ups.Count >= 2, "the denoised frame was not shown before the save");
            Assert.Equal(SyncDenoise(ups[0].Px, w, h), ups[^1].Px);
            Assert.True(System.IO.File.Exists(path));
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
    }
}
