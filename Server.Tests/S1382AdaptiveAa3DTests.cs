// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1382 — adaptive anti-aliasing for the ten 3D raymarchers (Mandelbrot's is #1344).
// Invariants, each against an independent full-supersampling or single-sample render:
//   • a masked frame computes every marked pixel exactly as a full frame does, on
//     every family (the CPU march honours the mask);
//   • adaptive with every pixel marked and no early stop is full supersampling, byte
//     for byte; with the defaults, unmarked pixels are exactly the one-sample render,
//     fewer pixel-samples are taken and the error against full stays small;
//   • a frame with a pass that reads neighbours (SSAO, …) or a cross-frame cache
//     falls back to full supersampling — every such condition is checked;
//   • batch parity: the region snapshot / --param / Command builder carry the
//     settings for the 3D families, the poster honours them like the live
//     calculator, the live host takes the adaptive pass.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1382AdaptiveAa3DTests(ITestOutputHelper output)
{
    private const int W = 96, H = 64;

    public enum Family { Mandelbulb, Mandelbox, Menger, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex, Coquaternion, DualOrbitVolume, UserBulb }

    public static TheoryData<Family> Families()
    {
        var d = new TheoryData<Family>();
        foreach (var f in Enum.GetValues<Family>()) d.Add(f);
        return d;
    }

    private delegate void FxEdit(ref LightingFxData fx);   // LightingFxData is a struct

    private static FractalParameters Params(bool adaptive, double threshold = AdaptiveAa.DefaultThreshold,
        double tolerance = AdaptiveAa.DefaultTolerance, FxEdit? edit = null)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = false;
        edit?.Invoke(ref fx);
        return new FractalParameters
        {
            Lighting = fx, UserBulbSource = "z^8 + c",
            AdaptiveAa = adaptive, AdaptiveAaThreshold = threshold, AdaptiveAaTolerance = tolerance,
        };
    }

    private static IFractalCalculator Calc(Family fam, FractalParameters fp, int w = W, int h = H)
    {
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

    private static uint[] Pixels(IFractalCalculator c) => c.ColorBuffer.Take(c.Width * c.Height).ToArray();

    private static uint[] Single(Family fam, FractalParameters fp)
    {
        var c = Calc(fam, fp);
        c.Calculate(CancellationToken.None);
        return Pixels(c);
    }

    // Supersample a fresh calculator with aaSamples samples; returns the image and stats.
    private static (uint[] Px, SupersampleStats St) Super(Family fam, FractalParameters fp, int aaSamples)
    {
        var c = Calc(fam, fp);
        c.Calculate(CancellationToken.None);
        var st = Supersampling.AccumulateAlt(c, aaSamples, CancellationToken.None, histogramEq: 0);
        return (Pixels(c), st);
    }

    private static int MaxDiff(uint a, uint b)
    {
        int m = 0;
        for (int s = 0; s < 32; s += 8) m = Math.Max(m, Math.Abs((int)((a >> s) & 0xFF) - (int)((b >> s) & 0xFF)));
        return m;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_Masked_Frame_Computes_Every_Marked_Pixel_As_A_Full_Frame_Does(Family fam)
    {
        var full = Single(fam, Params(false));
        var c = Calc(fam, Params(true));
        var ad = Assert.IsAssignableFrom<IAdaptiveAaCalculator>(c);
        Assert.True(ad.MaskExact, "precondition: the default lighting is per-pixel");
        var mask = new byte[W * H];
        var rng = new Random(1382);
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(rng.Next(3) == 0 ? 1 : 0);
        ad.PixelMask = mask;
        c.Calculate(CancellationToken.None);
        ad.PixelMask = null;
        var px = Pixels(c);
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] != 0) Assert.True(px[i] == full[i], $"{fam}: marked pixel {i} {px[i]:X8} vs {full[i]:X8}");
        Assert.Contains(Enumerable.Range(0, mask.Length), i => mask[i] == 0 && px[i] != full[i]);   // precondition: the mask skipped pixels
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Adaptive_With_Every_Pixel_And_Every_Sample_Is_Full_Supersampling(Family fam)
    {
        var (full, fst) = Super(fam, Params(false), 4);
        var single = Single(fam, Params(false));
        Assert.NotEqual(single, full);   // precondition: the jitter changes the image
        var (every, est) = Super(fam, Params(true, threshold: -1, tolerance: 0), 4);
        Assert.Equal(full, every);
        Assert.Equal(W * H, est.Marked);
        Assert.Equal(fst.PixelSamples, est.PixelSamples);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Adaptive_Keeps_Unmarked_Pixels_At_One_Sample_Takes_Fewer_Samples_And_Stays_Close(Family fam)
    {
        var single = Single(fam, Params(true));
        var (full, _) = Super(fam, Params(false), 16);
        var (ad, st) = Super(fam, Params(true), 16);
        // The mask, independently: the colour test on the one-sample frame.
        var mask = new byte[W * H];
        int marked = Supersampling.BuildMask(single, null, 0, W, H, AdaptiveAa.DefaultThreshold, mask);
        Assert.Equal(marked, st.Marked);
        Assert.True(marked > 0 && marked < W * H, $"precondition: some pixels marked ({marked})");
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] == 0) Assert.Equal(single[i], ad[i]);
        Assert.True(st.PixelSamples < st.FullPixelSamples);
        double mean = Enumerable.Range(0, W * H).Average(i => MaxDiff(ad[i], full[i]));
        output.WriteLine($"{fam}: marked {marked * 100.0 / (W * H):F1}%, samples {st.PixelSamples * 100.0 / st.FullPixelSamples:F1}% of full, mean error {mean:F2}");
        Assert.True(mean <= 3.0, $"mean error {mean:F2} levels");
    }

    public static TheoryData<string> NonLocal() => new()
    {
        "ssao", "edge ink", "screen dof", "froxel", "bloom", "vignette", "oidn", "debug hud", "depth stereo",
    };

    private static void Make(string what, ref LightingFxData fx)
    {
        switch (what)
        {
            case "ssao": fx.SsaoSamples = 8; break;
            case "edge ink": fx.EdgeStrength = 0.5; break;
            case "screen dof": fx.DofAperture = 0.05; fx.DofSamples = 8; fx.DofThinLens = false; break;
            case "froxel": fx.Froxel3D = true; break;
            case "bloom": fx.BloomStrength = 0.5; break;
            case "vignette": fx.Vignette = 0.5; break;
            case "oidn": fx.OidnDenoise = true; break;
            case "debug hud": fx.DebugHudFlags = 1; break;
            case "depth stereo": fx.StereoMode = StereoMode.Autostereogram; break;
        }
    }

    [Theory]
    [MemberData(nameof(NonLocal))]
    public void A_Pass_That_Reads_Neighbours_Falls_Back_To_Full_Supersampling(string what)
    {
        var baseFx = Params(true).Lighting;
        Assert.True(ScreenSpacePost.PixelLocal3D(in baseFx));   // precondition
        var fx = baseFx; Make(what, ref fx);
        Assert.False(ScreenSpacePost.PixelLocal3D(in fx), what);
        if (what is "oidn" or "debug hud" or "depth stereo") return;   // render-equality below for the cheap ones
        var (full, _) = Super(Family.Mandelbulb, Params(false, edit: (ref LightingFxData l) => Make(what, ref l)), 4);
        var (ad, st) = Super(Family.Mandelbulb, Params(true, edit: (ref LightingFxData l) => Make(what, ref l)), 4);
        Assert.Equal(W * H, st.Marked);
        Assert.Equal(full, ad);
    }

    [Fact]
    public void Tone_Mapping_And_Thin_Lens_Dof_Stay_Per_Pixel_And_The_Caches_Do_Not()
    {
        var fx = Params(true).Lighting;
        fx.ToneMap = ToneMapOperator.Aces;
        fx.DofThinLens = true; fx.DofAperture = 0.05; fx.DofSamples = 4;
        Assert.True(ScreenSpacePost.PixelLocal3D(in fx));
        var c = Calc(Family.Mandelbulb, Params(true, edit: (ref LightingFxData l) => { l.ToneMap = ToneMapOperator.Aces; l.DofThinLens = true; l.DofAperture = 0.05; l.DofSamples = 4; }));
        Assert.True(((IAdaptiveAaCalculator)c).MaskExact);
        using (DistanceFieldCache.Use(new DistanceFieldCache()))
            Assert.False(ScreenSpacePost.PixelLocal3D(in fx));
    }

    // ── Batch parity ───────────────────────────────────────────────────────────

    [Fact]
    public void The_Snapshot_Param_And_Command_Builder_Carry_It_For_The_3D_Families()
    {
        var stock = RegionFractalParams.Snapshot(FractalType.Mandelbulb, new FractalParameters());   // omitted at the defaults
        Assert.True(stock?.AdaptiveAa == null && stock?.AdaptiveAaThreshold == null && stock?.AdaptiveAaTolerance == null);
        var p = new FractalParameters { AdaptiveAa = true, AdaptiveAaThreshold = 8, AdaptiveAaTolerance = 0.5 };
        foreach (var t in new[] { FractalType.Mandelbulb, FractalType.Kleinian, FractalType.Julibrot, FractalType.UserBulb })
        {
            Assert.True(AdaptiveAaCapabilities.IsRaymarch3D(t));
            var snap = RegionFractalParams.Snapshot(t, p)!;
            var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
            var q = new FractalParameters();
            back.ApplyTo(q);
            Assert.True(q.AdaptiveAa);
            Assert.Equal(8, q.AdaptiveAaThreshold);
            Assert.Equal(0.5, q.AdaptiveAaTolerance);
            RegionFractalParams.ResetFamilyToDefaults(t, q);
            Assert.False(q.AdaptiveAa);
            Assert.Equal(AdaptiveAa.DefaultThreshold, q.AdaptiveAaThreshold);
        }
        Assert.False(AdaptiveAaCapabilities.IsRaymarch3D(FractalType.Julia));

        var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbulb, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.Mandelbulb, p)!.ToKeyValues(),
        };
        string cmd = FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap);
        Assert.Contains("AdaptiveAa=", cmd);
        var args = new List<string> { "--fractal", "Mandelbulb", "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
        var overlay = RegionFractalParams.FromKeyValues(opts.Params, FractalType.Mandelbulb, out var error);
        Assert.True(overlay != null, error);
        var fresh = new FractalParameters();
        overlay!.ApplyTo(fresh);
        Assert.True(fresh.AdaptiveAa);
        Assert.Equal(8, fresh.AdaptiveAaThreshold);
    }

    [Fact]
    public void The_Poster_Honours_It_Like_The_Live_Calculator()
    {
        FracturingFog.Imaging.PosterRequest Req(bool adaptive) => new()
        {
            FractalType = FractalType.Mandelbulb, Width = W, Height = H, Zoom = 1.0, MaxIterations = 256,
            Quality = QualityPreset.Ultra, ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = Params(adaptive), Path = string.Empty,
        };
        var (live, _) = Super(Family.Mandelbulb, Params(true), QualityPreset.Ultra.AaSamples);
        var (liveFull, _) = Super(Family.Mandelbulb, Params(false), QualityPreset.Ultra.AaSamples);
        Assert.NotEqual(liveFull, live);   // precondition: adaptive differs from full
        Assert.Equal(live, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(true), CancellationToken.None, out _, out _));
        Assert.Equal(liveFull, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(false), CancellationToken.None, out _, out _));
    }

    [Fact]
    public void The_View_Model_Shows_The_Controls_On_The_3D_Families()
    {
        Assert.True(new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(FractalType.Mandelbulb, new FractalParameters()).IsRaymarch3DAdaptiveAa);
        Assert.False(new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(FractalType.Mandelbrot, new FractalParameters()).IsRaymarch3DAdaptiveAa);
    }
}

/// <summary>#1382 — the live host takes the adaptive pass on a 3D family.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1382AdaptiveAa3DHostTests
{
    private sealed class Recorder : IFractalRenderer
    {
        private int _n;
        public int Count => Volatile.Read(ref _n);
        public void UpdateTexture(uint[] colorBuffer, int width, int height) => Interlocked.Increment(ref _n);
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "recorder";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Host_Supersamples_Adaptively_When_The_View_Asks(bool adaptive)
    {
        var s = new FractalViewState { FractalType = FractalType.Mandelbulb, Zoom = 1.0, Quality = QualityPreset.High };
        var fx = s.FractalParameters.Lighting; fx.UseGpuRender = false; s.FractalParameters.Lighting = fx;
        s.FractalParameters.AdaptiveAa = adaptive;
        var r = new Recorder();
        using var host = new FractalRenderHost(r, s, 256, 192, ColorPalette.BuiltIns[0]);
        int done = 0;
        host.FrameCompleted += (_, _) => Interlocked.Increment(ref done);
        host.Trigger(progressive: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref done) == 0 && sw.ElapsedMilliseconds < 60000) Thread.Sleep(10);
        var st = Assert.NotNull(host.LastSupersample);
        Assert.Equal(4, st.Samples);
        if (adaptive) Assert.True(st.Marked < st.Pixels, $"marked {st.Marked} of {st.Pixels}");
        else Assert.Equal(st.Pixels, st.Marked);
    }
}
