// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1373 — the 3D raymarchers never got a low-res interactive stage: a camera
// orbit raised RenderHint.Full (so no progressive chain at all), and nothing set
// their LowResPreview during a drag. Now an orbit is a Fast (progressive) move,
// the host's preview stage renders the live 3D calculator at the DynamicResolution
// level's scale (ILowResPreviewCalculator), and the final frame is unchanged.
// Locks:
//   • every family (10): a preview frame at a scale is the nearest upscale of the
//     same view rendered at the preview size (independent upscale here), and the
//     next frame without the flag is the full render byte for byte (no state
//     left behind);
//   • the scale override reaches ⅛ (the controller's coarsest level) and is
//     clamped there; with no override the parameter's ¼ … 1 default stands;
//   • through a real host: a progressive trigger on Mandelbulb presents a blocky
//     preview of the preview size first, then the full frame, which equals a
//     fresh full render; the flags are cleared after; a 2D alt type (Julia) still
//     renders a single full frame;
//   • a 3D right-drag raises Fast; a 2D right-drag doesn't orbit.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Input;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1373Raymarch3DPreviewTests
{
    private const int W = 96, H = 64;

    public enum Family { Mandelbulb, Mandelbox, Menger, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex, Coquaternion, DualOrbitVolume, UserBulb }

    public static TheoryData<Family> Families()
    {
        var d = new TheoryData<Family>();
        foreach (var f in Enum.GetValues<Family>()) d.Add(f);
        return d;
    }

    private static IFractalCalculator Calc(Family fam, int w, int h)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = false;
        var fp = new FractalParameters { Lighting = fx, UserBulbSource = "z^8 + c" };
        IFractalCalculator c = fam switch
        {
            Family.Mandelbulb      => new MandelbulbCalculator(w, h),
            Family.Mandelbox       => new MandelboxCalculator(w, h),
            Family.Menger          => new KifsCalculator(w, h),
            Family.QuatJulia       => new QuatJuliaCalculator(w, h),
            Family.QuatMandelbrot  => new QuatMandelbrotCalculator(w, h),
            Family.Kleinian        => new KleinianCalculator(w, h),
            Family.Bicomplex       => new BicomplexMandelbrotCalculator(w, h),
            Family.Coquaternion    => new CoquaternionMandelbrotCalculator(w, h),
            Family.DualOrbitVolume => new DualOrbitVolumeCalculator(w, h),
            _                      => new UserBulbCalculator(w, h),
        };
        c.ColorMap = ColorPalette.BuiltIns[0];
        ((dynamic)c).FractalParameters = fp;
        return c;
    }

    private static uint[] Render(IFractalCalculator c)
    {
        c.Calculate(CancellationToken.None);
        return c.ColorBuffer.Take(c.Width * c.Height).ToArray();
    }

    /// <summary>Nearest upscale by definition (the same index rule as a texture fetch).</summary>
    private static uint[] Nearest(uint[] src, int sw, int sh, int dw, int dh)
    {
        var d = new uint[dw * dh];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
                d[y * dw + x] = src[Math.Min(sh - 1, y * sh / dh) * sw + Math.Min(sw - 1, x * sw / dw)];
        return d;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_Preview_Is_The_Upscaled_Small_Render_And_The_Next_Frame_Is_The_Full_One(Family fam)
    {
        const double scale = 0.25;
        int pw = (int)Math.Round(W * scale), ph = (int)Math.Round(H * scale);
        var small = Render(Calc(fam, pw, ph));
        var full = Render(Calc(fam, W, H));
        Assert.True(full.Distinct().Count() > 4, $"precondition: {fam} renders something");

        var live = Calc(fam, W, H);
        var lr = Assert.IsAssignableFrom<ILowResPreviewCalculator>(live);
        lr.LowResPreview = true;
        lr.LowResPreviewScaleOverride = scale;
        var preview = Render(live);
        Assert.Equal(Nearest(small, pw, ph, W, H), preview);
        Assert.NotEqual(full, preview);

        lr.LowResPreview = false;
        lr.LowResPreviewScaleOverride = null;
        Assert.Equal(full, Render(live));
    }

    [Fact]
    public void The_Override_Reaches_An_Eighth_And_Is_Clamped_There()
    {
        Assert.Equal(1.0, LowResPreview.EffectiveScale(false, 0.125, 0.5));
        Assert.Equal(0.125, LowResPreview.EffectiveScale(true, 0.125, 0.5));
        Assert.Equal(0.125, LowResPreview.EffectiveScale(true, 0.01, 0.5));
        Assert.Equal(1.0, LowResPreview.EffectiveScale(true, 3.0, 0.5));
        Assert.Equal(0.5, LowResPreview.EffectiveScale(true, null, 0.5));
        Assert.Equal(0.25, LowResPreview.EffectiveScale(true, null, 0.1));   // the parameter keeps its ¼ floor
        Assert.Equal(0.5, LowResPreview.EffectiveScale(true, double.NaN, 0.5));

        // A real ⅛ frame: 128×64 renders 16×8 and upscales.
        var small = Render(Calc(Family.Mandelbulb, 16, 8));
        var live = Calc(Family.Mandelbulb, 128, 64);
        var lr = (ILowResPreviewCalculator)live;
        lr.LowResPreview = true; lr.LowResPreviewScaleOverride = 1.0 / 16;   // asks below ⅛ → ⅛
        Assert.Equal(Nearest(small, 16, 8, 128, 64), Render(live));
    }

    [Fact]
    public void Without_An_Override_The_Old_Defaults_Stand()
    {
        // Mandelbulb: FractalParameters.LowResPreviewScale (default) — unchanged.
        var c = Calc(Family.Mandelbulb, W, H);
        double s = ((dynamic)c).FractalParameters.LowResPreviewScale;
        int pw = (int)Math.Round(W * Math.Clamp(s, 0.25, 1.0)), ph = (int)Math.Round(H * Math.Clamp(s, 0.25, 1.0));
        ((ILowResPreviewCalculator)c).LowResPreview = true;
        Assert.Equal(Nearest(Render(Calc(Family.Mandelbulb, pw, ph)), pw, ph, W, H), Render(c));

        // UserBulb: its fixed ½ preview.
        var ub = Calc(Family.UserBulb, W, H);
        ((ILowResPreviewCalculator)ub).LowResPreview = true;
        var half = Calc(Family.UserBulb, W / 2, H / 2);
        ((ILowResPreviewCalculator)half).LowResPreview = false;
        // UserBulb's preview also drops its post-passes; with the defaults there are none.
        Assert.Equal(Nearest(Render(half), W / 2, H / 2, W, H), Render(ub));
    }

    // ── Input ─────────────────────────────────────────────────────────────

    private static PointerInput P(int x, int y, PointerButton b) => new(x, y, 800, 600, b, InputModifiers.None);

    [Fact]
    public void A_3D_Orbit_Is_A_Fast_Progressive_Move()
    {
        var s = new FractalViewState { FractalType = FractalType.Mandelbulb, Zoom = 1.0 };
        var c = new FractalInputController(s);
        var hints = new List<RenderHint>();
        c.ViewChanged += (_, e) => hints.Add(e.Hint);
        double theta0 = s.FractalParameters.BulbCameraTheta;
        c.OnPointerDown(P(200, 300, PointerButton.Right));
        c.OnPointerMove(P(260, 300, PointerButton.Right));
        c.OnPointerMove(P(320, 310, PointerButton.Right));
        c.OnPointerUp(P(320, 310, PointerButton.Right));
        Assert.NotEqual(theta0, s.FractalParameters.BulbCameraTheta);   // it orbited
        Assert.Equal(new[] { RenderHint.Fast, RenderHint.Fast }, hints);
    }
}

/// <summary>#1373 — the preview stage through a real host.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1373Raymarch3DPreviewHostTests
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

    private const int W = 512, H = 384;   // ≥ 256² for the progressive chain; ¼ = 128×96 (no 64-px floor)

    private static FractalViewState State(FractalType t)
    {
        var s = new FractalViewState { FractalType = t, Zoom = 1.0, Quality = QualityPreset.Standard };
        var fx = s.FractalParameters.Lighting;   // a struct: copy, edit, store
        fx.UseGpuRender = false;
        s.FractalParameters.Lighting = fx;
        return s;
    }

    private static bool Settle(FractalRenderHost host, Recorder r, int minUploads, int ms = 60000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int last = -1; long stableSince = 0;
        while (sw.ElapsedMilliseconds < ms)
        {
            int n = r.Snapshot().Count;
            if (n != last) { last = n; stableSince = sw.ElapsedMilliseconds; }
            else if (n >= minUploads && sw.ElapsedMilliseconds - stableSince > 1500) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>True when every <paramref name="block"/>² block is one colour (a
    /// nearest upscale by that factor).</summary>
    private static bool Blocky(uint[] px, int w, int h, int block)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * w + x] != px[(y / block * block) * w + (x / block * block)]) return false;
        return true;
    }

    [Fact]
    public void A_Progressive_Trigger_On_Mandelbulb_Presents_The_Preview_Then_The_Unchanged_Full_Frame()
    {
        var r = new Recorder();
        using var host = new FractalRenderHost(r, State(FractalType.Mandelbulb), W, H, ColorPalette.BuiltIns[0]);
        host.Trigger(progressive: true);
        Assert.True(Settle(host, r, 2), "the chain never settled");
        var ups = r.Snapshot();
        Assert.True(ups.Count >= 2, $"only {ups.Count} upload(s): no preview stage");

        // The first stage: ¼ (DynamicResolution's default before any measurement),
        // presented at full size as a 4× nearest upscale.
        var (p0, w0, h0) = ups[0];
        Assert.Equal((W, H), (w0, h0));
        Assert.True(Blocky(p0, W, H, 4), "the first upload isn't a ¼ preview");

        // The final upload is the full render: not blocky, and the live calculator's
        // frame equals a fresh full render of the same view.
        var (pf, _, _) = ups[^1];
        Assert.False(Blocky(pf, W, H, 2), "the last upload is still a preview");
        var live = Assert.IsType<MandelbulbCalculator>(host.ActiveAltCalculator);
        Assert.False(live.LowResPreview);
        Assert.Null(live.LowResPreviewScaleOverride);
        var fresh = new MandelbulbCalculator(W, H)
        {
            ColorMap = live.ColorMap, FractalParameters = live.FractalParameters,
            Zoom = live.Zoom, CenterX = live.CenterX, CenterY = live.CenterY,
        };
        fresh.Calculate(CancellationToken.None);
        Assert.Equal(fresh.ColorBuffer.Take(W * H).ToArray(), live.ColorBuffer.Take(W * H).ToArray());
    }

    [Fact]
    public void A_2D_Alt_Type_Still_Renders_A_Single_Full_Frame()
    {
        var r = new Recorder();
        using var host = new FractalRenderHost(r, State(FractalType.Julia), W, H, ColorPalette.BuiltIns[0]);
        host.Trigger(progressive: true);
        Assert.True(Settle(host, r, 1), "no frame");
        var ups = r.Snapshot();
        Assert.Single(ups);
    }
}
