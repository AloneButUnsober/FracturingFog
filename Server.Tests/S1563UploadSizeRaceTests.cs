// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1563 — a calculator's Resize sets Width/Height before it reallocates ColorBuffer,
// so the final-frame upload (a thread-pool thread DrainCalc does not wait for) could
// read a new, larger size with the old, shorter buffer. Array.Copy then threw on the
// thread pool and killed the process at startup. The upload now drops such a frame.
// Mutant: removing the length guard makes the first test throw ArgumentException.

using System;
using System.Reflection;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public class S1563UploadSizeRaceTests
{
    private const int W = 32, H = 16;

    private sealed class CountingRenderer : FracturingFog.IFractalRenderer
    {
        public int Uploads, LastW, LastH;
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { Uploads++; LastW = width; LastH = height; }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "count";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static readonly MethodInfo Upload = typeof(FractalRenderHost).GetMethod(
        "UploadProcessedBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FractalRenderHost NewHost(CountingRenderer r)
    {
        var state = new FractalViewState { FractalType = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1.0 };
        var host = new FractalRenderHost(r, state, W, H, ColorPalette.BuiltIns[0]);
        // Let the constructor's first frame settle so it doesn't race the counts below.
        using var done = new ManualResetEventSlim();
        host.FrameCompleted += (_, _) => done.Set();
        host.Trigger(progressive: false);
        done.Wait(TimeSpan.FromSeconds(30));
        return host;
    }

    [Fact]
    public void A_Buffer_Shorter_Than_The_Size_Is_Dropped_Not_Thrown()
    {
        var r = new CountingRenderer();
        using var host = NewHost(r);
        int before = r.Uploads;
        // Old 32x16 buffer read with the new 64x32 size: the startup race.
        var ex = Record.Exception(() => Upload.Invoke(host, [new uint[W * H], W * 2, H * 2, false]));
        Assert.Null(ex);
        Assert.Equal(before, r.Uploads);
    }

    [Fact]
    public void A_Matching_Buffer_Still_Uploads()
    {
        var r = new CountingRenderer();
        using var host = NewHost(r);
        int before = r.Uploads;
        Upload.Invoke(host, [new uint[W * H], W, H, false]);
        Assert.True(r.Uploads > before, "precondition: a matching frame reaches the renderer");
        Assert.Equal((W, H), (r.LastW, r.LastH));
    }
}
