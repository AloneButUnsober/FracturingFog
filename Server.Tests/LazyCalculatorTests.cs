// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1772 (interactivity epic #1756) — the render host made all ~50 alt calculators at full size in
// its constructor and resized them all on every Resize: 634 MB allocated each time at 1280×744
// (21-94 ms of UI-thread time per window-resize step in the live trace), though one draws the
// view. They are now made on first use. Independent checks:
//   - bytes allocated by the constructor and by a Resize (GC counters), against the old 634 MB;
//   - a Mandelbrot start makes no alt calculator;
//   - a type first used after a Resize and a colour-map change renders byte-equal to a fresh host
//     made at that size with that colour map (the lazy calculator got both);
//   - a type used, then recoloured and resized, then rendered again also matches a fresh host.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection("MandelbrotGpuStatics")]
public sealed class LazyCalculatorTests
{
    private readonly ITestOutputHelper _out;
    public LazyCalculatorTests(ITestOutputHelper output) => _out = output;

    private sealed class LastFrameRenderer : IFractalRenderer
    {
        private readonly object _gate = new();
        private uint[]? _tex; private int _w, _h;
        public (uint[]? Frame, int W, int H) Last { get { lock (_gate) return (_tex, _w, _h); } }
        public void UpdateTexture(uint[] c, int w, int h) { lock (_gate) { _tex = c[..(w * h)]; _w = w; _h = h; } }
        public void Render() { }
        public void Resize(int w, int h) { }
        public string RendererDescription => "stub";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static string Key(uint[] f)
    {
        var b = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
    }

    private static FractalViewState View(FractalType t)
    {
        var vs = new FractalViewState { Quality = QualityPreset.All.First(q => q.Name == "Draft") };
        vs.FractalType = t;
        switch (t)
        {
            case FractalType.Mandelbrot: vs.CenterX = -0.75; vs.CenterY = 0.1; vs.Zoom = 4; break;
            case FractalType.Julia: vs.Zoom = 1; break;
            case FractalType.Mandelbulb: vs.Zoom = 1; break;
            case FractalType.MathPlane3D: MathPlane3DPresets.Find("Parametric torus")!.Apply(vs.FractalParameters); break;
        }
        return vs;
    }

    private static void RenderAndSettle(FractalRenderHost host)
    {
        var done = new AutoResetEvent(false);
        EventHandler<RenderFrameInfo> h = (_, _) => done.Set();
        host.FrameCompleted += h;
        host.Trigger();
        Assert.True(done.WaitOne(30000), "no frame completed");
        Thread.Sleep(150);
        host.FrameCompleted -= h;
    }

    private static readonly IColorMap Theme = ColorPalette.BuiltIns[3];

    // The settled frame of a fresh host made at this size and type, with Theme.
    private static (string Key, int W, int H) Fresh(FractalType t, int w, int h)
    {
        var r = new LastFrameRenderer();
        using var host = new FractalRenderHost(r, View(t), w, h, Theme);
        RenderAndSettle(host);
        var (f, fw, fh) = r.Last;
        return (Key(f!), fw, fh);
    }

    [Fact]
    public void TheConstructorAndAResize_AllocateForTheMandelbrotPathOnly()
    {
        const int W = 1280, H = 744;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long a0 = GC.GetTotalAllocatedBytes(true);
        using var host = new FractalRenderHost(new LastFrameRenderer(), View(FractalType.Mandelbrot), W, H, Theme);
        long ctorMb = (GC.GetTotalAllocatedBytes(true) - a0) >> 20;
        Assert.Equal(0, host.CreatedAltCalculatorCount);

        RenderAndSettle(host);
        Assert.Equal(0, host.CreatedAltCalculatorCount);   // a Mandelbrot view touches no alt calculator

        long a1 = GC.GetTotalAllocatedBytes(true);
        host.Resize(W + 1, H);
        long resizeMb = (GC.GetTotalAllocatedBytes(true) - a1) >> 20;
        _out.WriteLine($"constructor {ctorMb} MB, resize {resizeMb} MB (all calculators: 634 MB each)");
        // The Mandelbrot calculator and its preview sidecars (and a first frame's caches) remain;
        // the alt calculators were ~90% of the old 634 MB.
        Assert.True(ctorMb < 200, $"constructor allocated {ctorMb} MB");
        Assert.True(resizeMb < 200, $"resize allocated {resizeMb} MB");
    }

    [Theory]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.Mandelbulb)]
    [InlineData(FractalType.MathPlane3D)]
    public void ATypeFirstUsedAfterAResize_RendersLikeAFreshHost(FractalType t)
    {
        const int W0 = 320, H0 = 200, W1 = 288, H1 = 216;
        var r = new LastFrameRenderer();
        var vs = View(t);
        vs.FractalType = FractalType.Mandelbrot;   // start on another type: t's calculator is not made yet
        using (var host = new FractalRenderHost(r, vs, W0, H0, ColorPalette.BuiltIns[0]))
        {
            RenderAndSettle(host);
            int before = host.CreatedAltCalculatorCount;
            host.Resize(W1, H1);
            RenderAndSettle(host);
            host.ColorMap = Theme;                 // a colour change before t's calculator exists
            vs.FractalType = t;                    // first use: made now, at W1×H1, with Theme
            RenderAndSettle(host);
            Assert.True(host.CreatedAltCalculatorCount > before, "the type's calculator was not made lazily");
        }
        var (f, fw, fh) = r.Last;
        var fresh = Fresh(t, W1, H1);
        Assert.Equal((fresh.W, fresh.H), (fw, fh));
        Assert.Equal(fresh.Key, Key(f!));
    }

    [Theory]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.Mandelbulb)]
    public void AMadeCalculator_FollowsLaterResizes(FractalType t)
    {
        const int W0 = 320, H0 = 200, W1 = 256, H1 = 240;
        var r = new LastFrameRenderer();
        using (var host = new FractalRenderHost(r, View(t), W0, H0, ColorPalette.BuiltIns[0]))
        {
            RenderAndSettle(host);                 // t's calculator is made at W0×H0
            host.ColorMap = Theme;                 // a colour change reaches the made calculator
            host.Resize(W1, H1);                   // and so does this resize
            RenderAndSettle(host);
        }
        var (f, fw, fh) = r.Last;
        var fresh = Fresh(t, W1, H1);
        Assert.Equal((fresh.W, fresh.H), (fw, fh));
        Assert.Equal(fresh.Key, Key(f!));
    }
}
