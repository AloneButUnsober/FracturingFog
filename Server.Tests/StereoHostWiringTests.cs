// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FracturingFog.Calculators;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1008 — True side-by-side stereo end to end. The #107 live gate required the
// Mandelbrot calculator while every 3D type renders through the alt calculator,
// so the live window (and its screenshot snapshot) stayed mono. These drive the
// real render host with a stub renderer and check what reaches the texture.
public class StereoHostWiringTests
{
    private const int W = 48, H = 32;

    private sealed class CapturingRenderer : IFractalRenderer
    {
        public int LastW, LastH;
        public uint[]? Last;
        public void UpdateTexture(uint[] colorBuffer, int width, int height)
        {
            LastW = width; LastH = height;
            Last = colorBuffer.Take(width * height).ToArray();
        }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "capture";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static FractalParameters BulbParams(StereoMode mode, StereoLayout layout = StereoLayout.FullSbs)
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = mode;
        fx.StereoEyeSeparation = 0.12;
        fx.StereoLayout = layout;
        return new FractalParameters
        {
            BulbPower = 8,
            BulbIterations = 8,
            BulbCameraDistance = 2.6,
            Lighting = fx,
        };
    }

    // One settled (non-progressive) frame through the live host; returns what
    // the renderer was last handed.
    private static CapturingRenderer RenderLive(FractalParameters fp)
    {
        var renderer = new CapturingRenderer();
        // A 3D view (the Mandelbrot defaults, centre -0.5 / zoom 0.13, frame the
        // bulb off-screen).
        var state = new FractalViewState
        {
            FractalType = FractalType.Mandelbulb, FractalParameters = fp,
            CenterX = 0, CenterY = 0, Zoom = 1.0,
        };
        using var host = new FractalRenderHost(renderer, state, W, H, ColorPalette.BuiltIns[0]);
        using var done = new ManualResetEventSlim();
        host.FrameCompleted += (_, _) => done.Set();
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame completed");
        Assert.True(renderer.Last!.Distinct().Count() > 8, "blank frame — test view does not frame the bulb");
        return renderer;
    }

    [Fact]
    public void Live_TrueStereo_On3D_PresentsFullSbs()
    {
        var fp = BulbParams(StereoMode.True);
        var r = RenderLive(fp);
        Assert.Equal((W * 2, H), (r.LastW, r.LastH));

        // The two halves are different viewpoints, not a duplicated mono frame.
        int diff = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (r.Last![y * 2 * W + x] != r.Last[y * 2 * W + W + x]) diff++;
        Assert.True(diff > W * H / 50, $"eyes nearly identical ({diff} px differ)");
    }

    [Fact]
    public void Live_TrueStereo_HalfSbs_KeepsMonoDims()
    {
        var r = RenderLive(BulbParams(StereoMode.True, StereoLayout.HalfSbs));
        Assert.Equal((W, H), (r.LastW, r.LastH));
    }

    [Fact]
    public void Live_StereoOff_StaysMono()
    {
        var r = RenderLive(BulbParams(StereoMode.Off));
        Assert.Equal((W, H), (r.LastW, r.LastH));
    }

    // Guard: every 3D type's live calculator can render a stereo eye. A new 3D
    // type added to FractalViewState.IsThreeD without IStereoEyeCamera fails here
    // instead of silently rendering mono in True mode.
    [Fact]
    public void Every3DType_LiveCalculator_IsStereoEyeCamera()
    {
        var state = new FractalViewState();
        using var host = new FractalRenderHost(new CapturingRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var select = typeof(FractalRenderHost).GetMethod("SelectAltCalculatorByType",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var threeD = Enum.GetValues<FractalType>().Where(FractalViewState.IsThreeD).ToList();
        Assert.NotEmpty(threeD);
        foreach (var t in threeD)
        {
            var calc = select.Invoke(host, new object[] { t });
            Assert.True(calc is IStereoEyeCamera, $"{t} → {calc?.GetType().Name ?? "null"} is not IStereoEyeCamera");
        }
    }

    // #1008 — the eyes render with the calculator's own offset; the shared
    // params are never written, and the calculator is mono again afterwards.
    [Fact]
    public void RenderTrueStereo_OnMandelbulb_LeavesParamsAndCalculatorMono()
    {
        var fp = BulbParams(StereoMode.True);
        var calc = new MandelbulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], FractalParameters = fp, Zoom = 1.0 };
        calc.Calculate(default);
        var monoBefore = (uint[])calc.ColorBuffer.Clone();
        var lightingBefore = fp.Lighting;

        var sbs = StereoRender.RenderTrueStereo(calc, fp.Lighting, CancellationToken.None);

        Assert.NotNull(sbs);
        Assert.Equal(W * 2 * H, sbs!.Length);
        Assert.Equal(0.0, calc.StereoEyeOffset);
        Assert.Equal(lightingBefore.GetHashCode(), fp.Lighting.GetHashCode());
        calc.Calculate(default);
        Assert.Equal(monoBefore, calc.ColorBuffer);   // no offset leaked into the next frame
    }

    [Fact]
    public void RenderTrueStereo_OnNon3DCalculator_ReturnsNull()
    {
        var fx = BulbParams(StereoMode.True).Lighting;
        var calc = new EscapeTimeCalculator(8, 8);
        Assert.Null(StereoRender.RenderTrueStereo(calc, fx, CancellationToken.None));
    }

    // #1008 — a recording started while the view is about to present SBS must
    // not lock to the stale mono frame on screen: the host passes the stereo
    // size and the mono frame is resampled into it, the SBS frames pass exactly.
    [Fact]
    public void Recorder_LockSize_OverridesFirstFrame()
    {
        int fw = 32, fh = 20;
        uint color = 0xFF102030u;
        string dir = Path.Combine(Path.GetTempPath(), $"ff-live-{Guid.NewGuid():N}");
        var rec = new LiveFrameRecorder(dir, 30, (out int w, out int h) =>
        {
            w = fw; h = fh;
            return Enumerable.Repeat(color, fw * fh).ToArray();
        }, lockSize: (64, 20));
        try
        {
            rec.SampleAt(0);                       // mono frame still on screen
            fw = 64; color = 0xFF405060u; rec.NotifyFrameChanged();
            rec.SampleAt(TimeSpan.TicksPerSecond / 2);   // first SBS frame
            var result = rec.StopAt(TimeSpan.TicksPerSecond)!;
            Assert.Equal((64, 20), (result.Width, result.Height));
            Assert.Equal(2, result.Frames.Count);
        }
        finally
        {
            rec.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
