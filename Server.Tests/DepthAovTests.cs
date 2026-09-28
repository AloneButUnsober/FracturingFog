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
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1009 (S2 of #1014) — the 3D raymarchers publish their depth for post-frame
// passes; the depth-parallax (Fake) stereo warp now works on 3D; the G-buffer is
// no longer SSAO-only (edge ink / screen-space DoF were silent no-ops without it).
public class DepthAovTests
{
    private const int W = 48, H = 32;

    private sealed class CapturingRenderer : FracturingFog.IFractalRenderer
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

    private static LightingFxData Fx(StereoMode mode = StereoMode.Off, double sep = 0.0)
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = mode;
        fx.StereoEyeSeparation = sep;
        return fx;
    }

    // Every 3D type's live calculator, configured the way the host would (the
    // host's own type → calculator map, so a new 3D type cannot be missed).
    private static (FractalType Type, IFractalCalculator Calc)[] Live3DCalculators(FractalRenderHost host)
    {
        var select = typeof(FractalRenderHost).GetMethod("SelectAltCalculatorByType",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Enum.GetValues<FractalType>().Where(FractalViewState.IsThreeD)
            .Select(t => (t, (IFractalCalculator)select.Invoke(host, new object[] { t })!))
            .ToArray();
    }

    private static void Configure(IFractalCalculator c, LightingFxData fx)
    {
        c.Resize(W, H);
        c.CenterX = 0; c.CenterY = 0; c.Zoom = 1.0; c.MaxIterations = 64;
        c.ColorMap = ColorPalette.BuiltIns[0];
        // UserBulb renders nothing without an equation (the example bulb).
        var fp = new FractalParameters { Lighting = fx, UserBulbSource = "z^8 + c" };
        c.GetType().GetProperty("FractalParameters")!.SetValue(c, fp);
    }

    [Fact]
    public void Every3DType_PublishesDepth_OnlyWhenFakeStereoWantsIt()
    {
        using var host = new FractalRenderHost(new CapturingRenderer(), new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
        foreach (var (type, calc) in Live3DCalculators(host))
        {
            Assert.True(calc is IDepthAovSource, $"{type} is not IDepthAovSource");
            var ds = (IDepthAovSource)calc;

            Configure(calc, Fx(StereoMode.Fake, 0.1));
            calc.Calculate(default);
            Assert.True(ds.DepthBuffer is { } d && d.Length == W * H, $"{type}: no depth published for Fake stereo");

            Configure(calc, Fx());
            calc.Calculate(default);
            Assert.Null(ds.DepthBuffer);   // not wanted → not published (and reset, not stale)
        }
    }

    // Capturing depth must not change the image: the Fake-stereo G-buffer only
    // records, it drives no pass on its own.
    [Fact]
    public void DepthCapture_LeavesTheMonoRenderByteIdentical()
    {
        using var host = new FractalRenderHost(new CapturingRenderer(), new FractalViewState(), W, H, ColorPalette.BuiltIns[0]);
        foreach (var (type, calc) in Live3DCalculators(host))
        {
            Configure(calc, Fx());
            calc.Calculate(default);
            var mono = (uint[])calc.ColorBuffer.Clone();
            Configure(calc, Fx(StereoMode.Fake, 0.1));
            calc.Calculate(default);
            Assert.True(mono.SequenceEqual(calc.ColorBuffer), $"{type}: depth capture changed the colour buffer");
        }
    }

    [Fact]
    public void Mandelbulb_Depth_IsRayDistance_WithSkyAsMiss()
    {
        var calc = new MandelbulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
        calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = Fx(StereoMode.Fake, 0.1) };
        calc.Calculate(default);
        var d = calc.DepthBuffer!;
        int hits = d.Count(v => !float.IsPositiveInfinity(v));
        Assert.InRange(hits, W * H / 10, W * H - 1);                // bulb + sky both present
        // Every hit lies between the camera and the far side of the bulb.
        Assert.All(d.Where(v => !float.IsPositiveInfinity(v)), v => Assert.InRange(v, 0.5f, 4.0f));
        // The frame centre looks straight at the bulb: nearer than the camera distance.
        Assert.True(d[(H / 2) * W + W / 2] < 2.6f);
    }

    [Fact]
    public void LowResPreview_PublishesDepthAtFullDims()
    {
        var calc = new MandelbulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0, LowResPreview = true };
        calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, LowResPreviewScale = 0.5, Lighting = Fx(StereoMode.Fake, 0.1) };
        calc.Calculate(default);
        Assert.Equal(W * H, calc.DepthBuffer!.Length);
    }

    // Before #1009 the G-buffer existed only with SSAO on, so edge ink alone did nothing.
    [Fact]
    public void EdgeInk_WorksWithoutSsao()
    {
        uint[] Render(double edge)
        {
            var fx = Fx();
            fx.SsaoSamples = 0;
            fx.EdgeStrength = edge;
            var calc = new MandelbulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
            calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = fx };
            calc.Calculate(default);
            return (uint[])calc.ColorBuffer.Clone();
        }
        Assert.False(Render(0.0).SequenceEqual(Render(1.0)), "edge ink had no effect without SSAO");
    }

    [Fact]
    public void PublishDepth_ResamplesToOutputDims_AndHonoursTheGates()
    {
        var fake = Fx(StereoMode.Fake, 0.1);
        var src = new float[] { 1, 2, 3, 4 };   // 2 × 2
        var up = ScreenSpacePost.PublishDepth(src, 2, 2, 4, 2, in fake)!;
        Assert.Equal(new float[] { 1, 1, 2, 2, 3, 3, 4, 4 }, up);
        Assert.Same(src, ScreenSpacePost.PublishDepth(src, 2, 2, 2, 2, in fake));
        Assert.Null(ScreenSpacePost.PublishDepth(src, 2, 2, 2, 2, in fake, valid: false));
        var off = Fx();
        Assert.Null(ScreenSpacePost.PublishDepth(src, 2, 2, 2, 2, in off));
    }

    [Theory]
    [InlineData(0, 0.0, 0.0, false, false)]
    [InlineData(4, 0.0, 0.0, false, true)]    // SSAO
    [InlineData(0, 0.5, 0.0, false, true)]    // edge ink (was a no-op without SSAO)
    [InlineData(0, 0.0, 0.2, false, true)]    // screen-space DoF
    [InlineData(0, 0.0, 0.2, true, false)]    // thin-lens DoF writes no G-buffer
    public void WantsGBuffer_Gate(int ssao, double edge, double aperture, bool thinLens, bool expected)
    {
        var fx = Fx();
        fx.SsaoSamples = ssao; fx.EdgeStrength = edge; fx.DofAperture = aperture;
        fx.DofThinLens = thinLens; fx.DofSamples = 8;
        Assert.Equal(expected, ScreenSpacePost.WantsGBuffer(in fx));
    }

    // End to end: Fake stereo on a 3D type now reaches the window as SBS.
    [Fact]
    public void Live_FakeStereo_On3D_PresentsSbs()
    {
        var renderer = new CapturingRenderer();
        var fp = new FractalParameters { BulbCameraDistance = 2.6, Lighting = Fx(StereoMode.Fake, 0.12) };
        var state = new FractalViewState { FractalType = FractalType.Mandelbulb, FractalParameters = fp, CenterX = 0, CenterY = 0, Zoom = 1.0 };
        using var host = new FractalRenderHost(renderer, state, W, H, ColorPalette.BuiltIns[0]);
        using var done = new ManualResetEventSlim();
        host.FrameCompleted += (_, _) => done.Set();
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame completed");

        var snap = host.SnapshotFrame(out int sw, out int sh);
        Assert.Equal((W * 2, H), (sw, sh));
        int diff = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (snap[y * 2 * W + x] != snap[y * 2 * W + W + x]) diff++;
        Assert.True(diff > W * H / 50, $"right eye is not warped ({diff} px differ)");
    }

    [Fact]
    public void Poster_FakeStereo_On3D_WritesSbs()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-depth-{Guid.NewGuid():N}.png");
        try
        {
            var req = new PosterRequest
            {
                FractalType = FractalType.Mandelbulb,
                CenterX = 0, CenterY = 0, Zoom = 1.0,
                MaxIterations = 64,
                Width = W, Height = H,
                ColorMap = ColorPalette.BuiltIns[0],
                Quality = QualityPreset.Standard,
                FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = Fx(StereoMode.Fake, 0.12) },
                Path = path,
                Format = ImageFileFormat.Png,
            };
            var result = PosterRenderer.RenderToFile(req, CancellationToken.None);
            Assert.Equal((W * 2, H), (result.SavedWidth, result.SavedHeight));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Theory]
    [InlineData(true, false, StereoMode.Off, 0.1, false, false, "")]
    [InlineData(true, false, StereoMode.True, 0.0, false, false, "eye sep")]
    [InlineData(false, true, StereoMode.True, 0.1, false, false, "one camera")]
    [InlineData(true, false, StereoMode.Fake, 0.1, true, false, "thin-lens")]
    [InlineData(true, false, StereoMode.Fake, 0.1, false, true, "CPU")]
    [InlineData(true, false, StereoMode.Fake, 0.1, false, false, "")]
    [InlineData(true, false, StereoMode.True, 0.1, false, true, "")]      // True renders two eyes on the GPU fine
    [InlineData(false, false, StereoMode.Fake, 0.1, false, false, "")]    // flat 2D: the dialog is not shown
    public void StereoHint_Rules(bool is3D, bool relief, StereoMode mode, double sep, bool thinLens, bool gpu, string contains)
    {
        var fx = Fx(mode, sep);
        if (thinLens) { fx.DofThinLens = true; fx.DofAperture = 0.2; fx.DofSamples = 8; }
        string hint = FractalParamsViewModel.StereoHintFor(is3D, relief, in fx, gpu);
        if (contains.Length == 0) Assert.Equal("", hint);
        else Assert.Contains(contains, hint);
    }
}
