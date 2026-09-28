// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
using System.Threading;
using FracturingFog.Calculators;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1011 (S4 of #1014) — the autostereogram wired into the live view, the
// poster / batch still, Relief, presets and the Lighting & FX hints.
public class AutostereoWiringTests
{
    private const int W = 96, H = 48;
    private const uint Black = 0xFF000000u, White = 0xFFFFFFFFu;

    private sealed class CapturingRenderer : FracturingFog.IFractalRenderer
    {
        public int LastW, LastH;
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { LastW = width; LastH = height; }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "capture";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private static LightingFxData AutoFx(AutostereoPattern pattern = AutostereoPattern.RandomDots)
    {
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.Autostereogram;
        fx.StereoAutoPattern = pattern;
        return fx;
    }

    private static (uint[] Mono, float[] Depth) BulbFrame(int w = W, int h = H)
    {
        var calc = new MandelbulbCalculator(w, h) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
        calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = AutoFx() };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), calc.DepthBuffer!);
    }

    [Fact]
    public void Autostereogram_AsksForDepth_WithoutAnEyeSeparation()
    {
        var fx = AutoFx();
        fx.StereoEyeSeparation = 0.0;   // the side-by-side setting is not used
        Assert.True(ScreenSpacePost.WantsDepthOutput(in fx));
        Assert.True(ReliefScreenSpacePost.WantsStereo(in fx));
        Assert.False(StereoRender.WantsTrueStereo(true, in fx));
    }

    [Theory]
    [InlineData(AutostereoPattern.RandomDots)]
    [InlineData(AutostereoPattern.ThemeDots)]
    [InlineData(AutostereoPattern.FractalTexture)]
    public void FromLighting_KeepsTheFrameSize_AndDrawsFromThePatternSource(AutostereoPattern pattern)
    {
        var (mono, depth) = BulbFrame();
        var fx = AutoFx(pattern);
        fx.StereoAutoGuideDots = false;
        var img = Autostereogram.FromLighting(mono, depth, W, H, in fx);
        Assert.Equal(W * H, img.Length);
        var allowed = pattern == AutostereoPattern.RandomDots
            ? new[] { Black, White }
            : mono.Select(p => p | 0xFF000000u).Distinct().ToArray();
        Assert.All(img, p => Assert.Contains(p, allowed));
    }

    // An older preset (or default(LightingFxData)) has zero autostereo fields:
    // FromLighting falls back to the defaults instead of throwing.
    [Fact]
    public void FromLighting_ZeroSettings_FallBackToDefaults()
    {
        var (mono, depth) = BulbFrame();
        var fx = default(LightingFxData);
        fx.StereoMode = StereoMode.Autostereogram;
        Assert.Equal(W * H, Autostereogram.FromLighting(mono, depth, W, H, in fx).Length);
    }

    [Fact]
    public void ApplyDepthStereo_ChoosesAutostereogramOrSbsByMode()
    {
        var (mono, depth) = BulbFrame();
        var auto = AutoFx();
        Assert.NotNull(ScreenSpacePost.ApplyDepthStereo(mono, depth, W, H, in auto, out int aw, out int ah));
        Assert.Equal((W, H), (aw, ah));

        var fake = LightingFxData.CreateDefault();
        fake.StereoMode = StereoMode.Fake; fake.StereoEyeSeparation = 0.1;
        Assert.NotNull(ScreenSpacePost.ApplyDepthStereo(mono, depth, W, H, in fake, out int fw, out int fh));
        Assert.Equal((W * 2, H), (fw, fh));

        var off = LightingFxData.CreateDefault();
        Assert.Null(ScreenSpacePost.ApplyDepthStereo(mono, depth, W, H, in off, out _, out _));
    }

    [Fact]
    public void Relief_Autostereogram_FromTheCapturedDepth()
    {
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(W, H);
        for (int i = 0; i < W * H; i++) aov.Depth[i] = (i % W) < W / 2 ? 2f : 1e6f;   // half terrain, half sky sentinel
        var dst = Enumerable.Repeat(0xFF336699u, W * H).ToArray();
        var fx = AutoFx();
        var img = ReliefScreenSpacePost.ApplyStereo(dst, aov, W, H, in fx, out int ow, out int oh);
        Assert.NotNull(img);
        Assert.Equal((W, H), (ow, oh));
        Assert.All(img!, p => Assert.True(p == Black || p == White));
    }

    // End to end through the live host: the window and the snapshot get the
    // autostereogram at the mono frame size (no side-by-side doubling).
    [Fact]
    public void Live_Autostereogram_On3D()
    {
        var renderer = new CapturingRenderer();
        var fp = new FractalParameters { BulbCameraDistance = 2.6, Lighting = AutoFx() };
        var state = new FractalViewState { FractalType = FractalType.Mandelbulb, FractalParameters = fp, CenterX = 0, CenterY = 0, Zoom = 1.0 };
        using var host = new FractalRenderHost(renderer, state, W, H, ColorPalette.BuiltIns[0]);
        using var done = new ManualResetEventSlim();
        host.FrameCompleted += (_, _) => done.Set();
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "no frame completed");

        Assert.Equal((W, H), (renderer.LastW, renderer.LastH));
        var snap = host.SnapshotFrame(out int sw, out int sh);
        Assert.Equal((W, H), (sw, sh));
        Assert.All(snap, p => Assert.True((p | 0xFF000000u) is Black or White, $"not a random-dot pixel: {p:X8}"));
    }

    [Fact]
    public void Poster_Autostereogram_On3D_KeepsTheSize()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-auto-{Guid.NewGuid():N}.png");
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
                FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = AutoFx() },
                Path = path,
                Format = ImageFileFormat.Png,
            };
            var result = PosterRenderer.RenderToFile(req, CancellationToken.None);
            Assert.Equal((W, H), (result.SavedWidth, result.SavedHeight));
            Assert.True(File.Exists(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Preset_RoundTrips_TheAutostereoSettings()
    {
        var fx = AutoFx(AutostereoPattern.FractalTexture);
        fx.StereoAutoEyeSep = 0.2; fx.StereoAutoDepthOfField = 0.4; fx.StereoAutoCrossEyed = true;
        fx.StereoAutoGuideDots = false; fx.StereoAutoSeed = 42; fx.StereoAutoBlur = 7; fx.StereoAutoLevels = 3;
        var back = LightingFxPresetData.FromFx(fx).ToFx();
        Assert.Equal(StereoMode.Autostereogram, back.StereoMode);
        Assert.Equal(AutostereoPattern.FractalTexture, back.StereoAutoPattern);
        Assert.Equal((0.2, 0.4, true, false, 42, 7, 3),
            (back.StereoAutoEyeSep, back.StereoAutoDepthOfField, back.StereoAutoCrossEyed,
             back.StereoAutoGuideDots, back.StereoAutoSeed, back.StereoAutoBlur, back.StereoAutoLevels));
    }

    // A preset saved before #1011 has no StereoAuto* keys: it loads with the defaults.
    [Fact]
    public void OlderPreset_LoadsTheAutostereoDefaults()
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<LightingFxPresetData>("{\"StereoMode\":0}")!;
        var fx = dto.ToFx();
        var d = LightingFxData.CreateDefault();
        Assert.Equal((d.StereoAutoEyeSep, d.StereoAutoDepthOfField, d.StereoAutoGuideDots, d.StereoAutoBlur, d.StereoAutoLevels),
            (fx.StereoAutoEyeSep, fx.StereoAutoDepthOfField, fx.StereoAutoGuideDots, fx.StereoAutoBlur, fx.StereoAutoLevels));
    }

    [Theory]
    [InlineData(true, false, false, false, "")]            // eye sep 0 is fine for an autostereogram
    [InlineData(true, false, true, false, "thin-lens")]
    [InlineData(true, false, false, true, "CPU")]
    [InlineData(false, true, false, false, "")]            // Relief: depth from the relief G-buffer
    public void StereoHint_Autostereogram(bool is3D, bool relief, bool thinLens, bool gpu, string contains)
    {
        var fx = AutoFx();
        fx.StereoEyeSeparation = 0.0;
        if (thinLens) { fx.DofThinLens = true; fx.DofAperture = 0.2; fx.DofSamples = 8; }
        string hint = FractalParamsViewModel.StereoHintFor(is3D, relief, in fx, gpu);
        if (contains.Length == 0) Assert.Equal("", hint);
        else { Assert.Contains(contains, hint); Assert.Contains("autostereogram", hint); }
    }

    [Fact]
    public void ThemeColors_AreSpreadByBrightness_AndFallBackForAFlatImage()
    {
        var ramp = Enumerable.Range(0, 256).Select(i => 0xFF000000u | (uint)(i * 0x010101)).ToArray();
        var colors = Autostereogram.ThemeColors(ramp, ramp.Length);
        Assert.Equal(16, colors.Length);
        Assert.Equal(0xFF000000u, colors[0]);
        Assert.Equal(0xFFFFFFFFu, colors[^1]);
        Assert.Equal(new[] { Black, White }, Autostereogram.ThemeColors(new uint[64], 64));
    }

    // A small fractal on a big sky (e.g. a Quaternion Julia): the texture tile is
    // cut from the fractal itself, so no black sky rows end up in the pattern.
    private static (uint[] Mono, float[] Depth) SmallObjectFrame(int w, int h)
    {
        var mono = new uint[w * h];
        var depth = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                bool hit = y >= h * 2 / 5 && y < h * 3 / 5 && x >= w / 4 && x < w * 3 / 4;
                mono[i] = hit ? 0xFF000000u | (uint)((x * 7 + y * 13) & 0xFF) << 8 | 0x40u : Black;
                depth[i] = hit ? 2f + 0.2f * MathF.Sin(x * 0.2f) : float.PositiveInfinity;
            }
        return (mono, depth);
    }

    // The fractal's box edge rows may miss the strip's columns entirely (the
    // fractal is off to one side there): those rows borrow a neighbour, so the
    // tile has no black seam.
    [Fact]
    public void CutStrip_RowsMissingTheStrip_BorrowANeighbour()
    {
        const int w = 100, h = 20;
        var mono = new uint[w * h];
        var depth = Enumerable.Repeat(float.PositiveInfinity, w * h).ToArray();
        for (int y = 5; y < 15; y++)
            for (int x = 30; x < 70; x++) { mono[y * w + x] = 0xFF4080C0u; depth[y * w + x] = 2f; }
        depth[4 * w + 5] = 2f; mono[4 * w + 5] = 0xFF4080C0u;   // a stray hit far left widens the box up a row
        var tile = Autostereogram.CutStrip(mono, w, h, 10, depth);
        Assert.Equal(11, tile.Height);                          // rows 4..14
        Assert.DoesNotContain(Black, tile.Pixels);
    }

    [Fact]
    public void FractalTexture_OnASmallFractal_FillsTheFrame()
    {
        const int w = 240, h = 160;
        var (mono, depth) = SmallObjectFrame(w, h);
        var fx = AutoFx(AutostereoPattern.FractalTexture);
        fx.StereoAutoGuideDots = false;
        var img = Autostereogram.FromLighting(mono, depth, w, h, in fx);
        double black = img.Count(p => p == Black) / (double)img.Length;
        Assert.True(black < 0.01, $"{black:P1} of the stereogram is black sky");
    }

    [Fact]
    public void CutStrip_WithDepth_CropsToTheFractal_AndFillsGaps()
    {
        const int w = 240, h = 160;
        var (mono, depth) = SmallObjectFrame(w, h);
        var tile = Autostereogram.CutStrip(mono, w, h, 40, depth);
        Assert.Equal((40, h * 3 / 5 - h * 2 / 5), (tile.Width, tile.Height));
        Assert.DoesNotContain(Black, tile.Pixels);
    }

    [Fact]
    public void ThemeColors_WithDepth_IgnoreTheSky()
    {
        const int w = 240, h = 160;
        var (mono, depth) = SmallObjectFrame(w, h);
        Assert.DoesNotContain(Black, Autostereogram.ThemeColors(mono, w * h, depth));
    }
}
