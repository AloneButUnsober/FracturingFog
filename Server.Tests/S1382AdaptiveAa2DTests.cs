// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1382 — adaptive anti-aliasing for the 2D alt calculators: EscapeTimeCalculator's
// families (Julia, Burning Ship, Tricorn, Multibrot, Phoenix, Magnet, Glynn, Spider;
// SIMD, scalar, the domain warp), the root finders (Newton, Nova, Halley, Secant),
// the transcendental Julia sets, Lyapunov and user equations. Mandelbrot's is #1344,
// the 3D raymarchers' S1382AdaptiveAa3DTests. Invariants, each against an
// independent full-supersampling or single-sample render:
//   • a masked frame computes every marked pixel exactly as a full frame does and
//     leaves every other pixel untouched;
//   • adaptive with every pixel marked and no early stop is full supersampling, byte
//     for byte (also with adaptive contrast, and with solid-region skipping on);
//   • with the defaults, unmarked pixels are exactly the one-sample render, the mask
//     is the set boundary plus the colour steps, fewer pixel-samples are taken and
//     the error against full stays small;
//   • the set boundary is marked where no colour step is (the escape counts reach the
//     mask);
//   • a frame on the GPU kernel hands it the mask and gives the same result;
//   • batch parity: the region snapshot / --param / Command builder carry the
//     settings, the poster honours them like the live calculator, the live host takes
//     the adaptive pass.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1382AdaptiveAa2DTests(ITestOutputHelper output)
{
    private const int W = 101, H = 64, MaxIter = 150;   // 101: the SIMD cores' tail lanes

    public enum Family
    {
        Julia, JuliaWarp, BurningShip, Tricorn, Multibrot3, Multibrot7, Phoenix, Magnet1, Magnet1Escape, Magnet2,
        Glynn, Spider, Newton, Nova, Halley, Secant, Transcendental, Lyapunov, UserEquation,
    }

    public static TheoryData<Family> Families()
    {
        var d = new TheoryData<Family>();
        foreach (var f in Enum.GetValues<Family>()) d.Add(f);
        return d;
    }

    private static FractalType TypeOf(Family f) => f switch
    {
        Family.Julia or Family.JuliaWarp => FractalType.Julia,
        Family.BurningShip => FractalType.BurningShip,
        Family.Tricorn => FractalType.Tricorn,
        Family.Multibrot3 or Family.Multibrot7 => FractalType.Multibrot,
        Family.Phoenix => FractalType.Phoenix,
        Family.Magnet1 or Family.Magnet1Escape => FractalType.Magnet1,
        Family.Magnet2 => FractalType.Magnet2,
        Family.Glynn => FractalType.Glynn,
        Family.Spider => FractalType.Spider,
        Family.Newton => FractalType.Newton,
        Family.Nova => FractalType.Nova,
        Family.Halley => FractalType.Halley,
        Family.Secant => FractalType.Secant,
        Family.Transcendental => FractalType.TranscendentalJulia,
        Family.Lyapunov => FractalType.Lyapunov,
        _ => FractalType.UserEquation,
    };

    private static FractalParameters Params(Family f, bool adaptive, double threshold = AdaptiveAa.DefaultThreshold,
        double tolerance = AdaptiveAa.DefaultTolerance)
    {
        var p = new FractalParameters
        {
            UserEquationSource = "z*z*z + c",
            AdaptiveAa = adaptive, AdaptiveAaThreshold = threshold, AdaptiveAaTolerance = tolerance,
        };
        switch (f)
        {
            case Family.JuliaWarp: p.DomainWarpEnabled = true; p.DomainWarpStrength = 0.3; break;
            case Family.Multibrot7: p.MultibrotExponent = 7; break;
            case Family.Magnet1Escape: p.MagnetConvergence = false; break;
        }
        return p;
    }

    private static IFractalCalculator Calc(Family f, FractalParameters fp, int w = W, int h = H)
    {
        var t = TypeOf(f);
        IFractalCalculator c = f switch
        {
            Family.Newton or Family.Nova => new NewtonCalculator(w, h) { FractalType = t },
            Family.Halley => new HalleyCalculator(w, h),
            Family.Secant => new SecantCalculator(w, h),
            Family.Transcendental => new TranscendentalJuliaCalculator(w, h),
            Family.Lyapunov => new LyapunovCalculator(w, h),   // its own default view
            Family.UserEquation => new UserEquationCalculator(w, h),
            _ => new EscapeTimeCalculator(w, h) { FractalType = t },
        };
        c.ColorMap = ColorPalette.BuiltIns[0];
        c.MaxIterations = MaxIter;
        if (f == Family.BurningShip) { c.CenterX = -0.5; c.CenterY = -0.5; }
        if (f is Family.Magnet1 or Family.Magnet1Escape or Family.Magnet2) c.CenterX = 1.0;
        ((dynamic)c).FractalParameters = fp;
        return c;
    }

    private static uint[] Pixels(IFractalCalculator c) => c.ColorBuffer.Take(c.Width * c.Height).ToArray();

    private static uint[] Single(Family f, FractalParameters fp)
    {
        var c = Calc(f, fp);
        c.Calculate(CancellationToken.None);
        return Pixels(c);
    }

    private static (uint[] Px, SupersampleStats St) Super(Family f, FractalParameters fp, int aaSamples, int heq = 0,
        int w = W, int h = H)
    {
        var c = Calc(f, fp, w, h);
        c.Calculate(CancellationToken.None);
        var st = Supersampling.AccumulateAlt(c, aaSamples, CancellationToken.None, heq);
        Supersampling.FinishHistogramEq(c, in st, heq);
        return (Pixels(c), st);
    }

    private static int MaxDiff(uint a, uint b)
    {
        int m = 0;
        for (int s = 0; s < 32; s += 8) m = Math.Max(m, Math.Abs((int)((a >> s) & 0xFF) - (int)((b >> s) & 0xFF)));
        return m;
    }

    // Every per-pixel buffer the calculator publishes (public arrays of the frame's size).
    private static List<(string Name, Array A)> Buffers(IFractalCalculator c)
    {
        var list = new List<(string, Array)>();
        foreach (var p in c.GetType().GetProperties())
        {
            if (!p.PropertyType.IsArray || p.GetIndexParameters().Length != 0) continue;
            if (p.GetValue(c) is Array a && a.Length == c.Width * c.Height) list.Add((p.Name, a));
        }
        return list;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_Masked_Frame_Computes_Marked_Pixels_As_A_Full_Frame_Does_And_No_Other(Family f)
    {
        // A sentinel in every buffer, to see which elements a frame wrote (a buffer the
        // family never writes keeps it in both).
        static void Sentinel(IFractalCalculator calc)
        {
            foreach (var (_, a) in Buffers(calc))
            {
                if (a is uint[] u) Array.Fill(u, 0xDEADBEEFu);
                else if (a is int[] n) Array.Fill(n, -77);
                else if (a is float[] fl) Array.Fill(fl, -1234.5f);
            }
        }
        var full = Calc(f, Params(f, false));
        Sentinel(full);
        full.Calculate(CancellationToken.None);
        Assert.DoesNotContain(0xDEADBEEFu, Pixels(full));   // precondition: a full frame writes every pixel
        var c = Calc(f, Params(f, true));
        var ad = Assert.IsAssignableFrom<IAdaptiveAaCalculator>(c);
        Assert.True(ad.MaskExact);
        Assert.True(AdaptiveAaCapabilities.IsEscapeTime2D(TypeOf(f)) && AdaptiveAaCapabilities.Supports(TypeOf(f)));
        Sentinel(c);
        var before = Buffers(c).ToDictionary(b => b.Name, b => (Array)b.A.Clone());
        var mask = new byte[W * H];
        var rng = new Random(1382);
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(rng.Next(3) == 0 ? 1 : 0);
        for (int i = 0; i < W; i++) mask[5 * W + i] = 0;   // a row with no marked pixel
        ad.PixelMask = mask;
        c.Calculate(CancellationToken.None);
        ad.PixelMask = null;
        var fullBufs = Buffers(full).ToDictionary(b => b.Name, b => b.A);
        Assert.Contains("ColorBuffer", fullBufs.Keys);
        // The SIMD cores write whole batches (a batch with a marked lane), each lane a
        // full frame's value; every other core writes the marked pixels alone.
        bool batches = f is Family.Julia or Family.BurningShip or Family.Tricorn or Family.Multibrot3;
        int untouched = 0;
        foreach (var (name, a) in Buffers(c))
        {
            var fa = fullBufs[name];
            var ba = before[name];
            for (int i = 0; i < mask.Length; i++)
            {
                object v = a.GetValue(i)!, fv = fa.GetValue(i)!;
                if (mask[i] != 0) Assert.True(v.Equals(fv), $"{f}: {name}[{i}] marked {v} vs full {fv}");
                else if (v.Equals(ba.GetValue(i))) { if (name == "ColorBuffer") untouched++; }
                else Assert.True(batches && v.Equals(fv), $"{f}: {name}[{i}] unmarked, written {v} (full {fv})");
            }
        }
        Assert.True(untouched > W, $"{f}: precondition, the mask skipped pixels ({untouched})");
        if (!batches) Assert.Equal(mask.Count(b => b == 0), untouched);

        // With the mask removed the next frame is a whole one again.
        c.Calculate(CancellationToken.None);
        foreach (var (name, a) in Buffers(c))
            for (int i = 0; i < mask.Length; i++)
                Assert.True(a.GetValue(i)!.Equals(fullBufs[name].GetValue(i)), $"{f}: {name}[{i}] after the mask was removed");
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Adaptive_With_Every_Pixel_And_Every_Sample_Is_Full_Supersampling(Family f)
    {
        var (full, fst) = Super(f, Params(f, false), 4);
        var single = Single(f, Params(f, false));
        Assert.NotEqual(single, full);   // precondition: the jitter changes the image
        var (every, est) = Super(f, Params(f, true, threshold: -1, tolerance: 0), 4);
        Assert.Equal(full, every);
        Assert.Equal(W * H, est.Marked);
        Assert.Equal(fst.PixelSamples, est.PixelSamples);
    }

    [Theory]
    [InlineData(Family.Julia)]
    [InlineData(Family.Phoenix)]
    [InlineData(Family.Newton)]
    [InlineData(Family.UserEquation)]
    public void Adaptive_Contrast_Equalises_Every_Sample_As_Full_Supersampling_Does(Family f)
    {
        var (plain, _) = Super(f, Params(f, false), 4);
        var (full, fst) = Super(f, Params(f, false), 4, heq: 80);
        Assert.True(fst.Equalized, "precondition: the pass equalised");
        Assert.NotEqual(plain, full);
        var (every, _) = Super(f, Params(f, true, threshold: -1, tolerance: 0), 4, heq: 80);
        Assert.Equal(full, every);
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Adaptive_Keeps_Unmarked_Pixels_At_One_Sample_Takes_Fewer_Samples_And_Stays_Close(Family f)
    {
        var one = Calc(f, Params(f, true));
        one.Calculate(CancellationToken.None);
        var single = Pixels(one);
        var (full, _) = Super(f, Params(f, false), 16);
        var (ad, st) = Super(f, Params(f, true), 16);
        // The mask, independently: the set boundary (where the family has escape
        // counts) and the colour test, on the one-sample frame.
        int[]? iter = one switch
        {
            EscapeTimeCalculator e => e.IterationBuffer,
            TranscendentalJuliaCalculator t => t.IterationBuffer,
            UserEquationCalculator u => u.IterationBuffer,
            _ => null,
        };
        var mask = new byte[W * H];
        int marked = Supersampling.BuildMask(single, iter, MaxIter, W, H, AdaptiveAa.DefaultThreshold, mask);
        Assert.Equal(marked, st.Marked);
        Assert.True(marked > 0 && marked < W * H, $"precondition: some pixels marked ({marked})");
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] == 0) Assert.Equal(single[i], ad[i]);
        Assert.True(st.PixelSamples < st.FullPixelSamples);
        double mean = Enumerable.Range(0, W * H).Average(i => MaxDiff(ad[i], full[i]));
        output.WriteLine($"{f}: marked {marked * 100.0 / (W * H):F1}%, samples {st.PixelSamples * 100.0 / st.FullPixelSamples:F1}% of full, mean error {mean:F2}");
        Assert.True(mean <= 3.0, $"mean error {mean:F2} levels");
    }

    [Theory]
    [InlineData(Family.Julia)]
    [InlineData(Family.Phoenix)]
    [InlineData(Family.Transcendental)]
    [InlineData(Family.UserEquation)]
    public void The_Set_Boundary_Is_Marked_Where_No_Colour_Step_Is(Family f)
    {
        // Threshold 255: no colour difference marks a pixel, so only the boundary does.
        var one = Calc(f, Params(f, true));
        one.Calculate(CancellationToken.None);
        int[] iter = (int[])((dynamic)one).IterationBuffer;
        int inSet = iter.Take(W * H).Count(i => i >= MaxIter);
        Assert.True(inSet > 20 && inSet < W * H - 20, $"precondition: {inSet} in-set pixels");
        var mask = new byte[W * H];
        int marked = Supersampling.BuildMask(Pixels(one), iter, MaxIter, W, H, 255, mask);
        Assert.True(marked > 0);
        Assert.Equal(0, Supersampling.BuildMask(Pixels(one), null, 0, W, H, 255, new byte[W * H]));
        var (_, st) = Super(f, Params(f, true, threshold: 255), 4);
        Assert.Equal(marked, st.Marked);
    }

    [Fact]
    public void The_Root_Finders_And_Lyapunov_Have_No_Escape_Counts_For_The_Mask()
    {
        foreach (var f in new[] { Family.Newton, Family.Nova, Family.Halley, Family.Secant, Family.Lyapunov })
            Assert.Null(((IAdaptiveAaCalculator)Calc(f, Params(f, true))).AdaptiveAaIterations);
        foreach (var f in new[] { Family.Julia, Family.Transcendental, Family.UserEquation })
            Assert.NotNull(((IAdaptiveAaCalculator)Calc(f, Params(f, true))).AdaptiveAaIterations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Solid_Region_Skipping_Leaves_The_Adaptive_Result_Unchanged(bool everyPixel)
    {
        // The rabbit: a filled Julia set with large interiors, at a size the subdivision takes.
        const int w = 192, h = 160;
        FractalParameters P(bool skip) => new()
        {
            JuliaC = new Complex(-0.123, 0.745), SolidRegionSkip = skip, SolidRegionAuto = false,
            AdaptiveAa = true, AdaptiveAaThreshold = everyPixel ? -1 : AdaptiveAa.DefaultThreshold,
            AdaptiveAaTolerance = everyPixel ? 0 : AdaptiveAa.DefaultTolerance,
        };
        var probe = (EscapeTimeCalculator)Calc(Family.Julia, P(true), w, h);
        probe.Calculate(CancellationToken.None);
        Assert.True(probe.LastFrameFilledPixels > 0, "precondition: the one-sample frame subdivided");
        var (off, ost) = Super(Family.Julia, P(false), 4, w: w, h: h);
        var c = (EscapeTimeCalculator)Calc(Family.Julia, P(true), w, h);
        c.Calculate(CancellationToken.None);
        var st = Supersampling.AccumulateAlt(c, 4, CancellationToken.None, histogramEq: 0);
        Assert.Equal(off, Pixels(c));
        Assert.Equal(ost.Marked, st.Marked);
        Assert.Equal(0, c.LastFrameSolidPasses);   // a masked sample does not subdivide
        Assert.Null(c.PixelMask);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Frame_On_The_Gpu_Kernel_Hands_It_The_Mask_And_Gives_The_Same_Result(bool kernelMasks)
    {
        EscapeTimeCalculator Gpu(IGpuKernel k, double threshold, double tolerance, bool adaptive = true)
        {
            var c = (EscapeTimeCalculator)Calc(Family.Julia, Params(Family.Julia, adaptive, threshold, tolerance));
            c.ColorMap = new HsvPalette();
            c.UseGpuCompute = true; c.GpuKernel = k;
            return c;
        }
        var kf = new S1394GpuSolidRegionTests.SoftMaskKernel();
        var full = Gpu(kf, 0, 0, adaptive: false);
        full.Calculate(CancellationToken.None);
        Assert.Equal(1, kf.Dispatches);   // precondition: the frame ran on the kernel
        var one = Pixels(full);
        var oneIter = (int[])full.IterationBuffer.Clone();
        var fst = Supersampling.AccumulateAlt(full, 4, CancellationToken.None, histogramEq: 0);
        Assert.Empty(kf.MaskCounts);   // full supersampling: no mask
        var fpx = Pixels(full);
        Assert.NotEqual(one, fpx);

        // Every pixel, every sample: the full pass.
        var k = new S1394GpuSolidRegionTests.SoftMaskKernel { HonourMask = kernelMasks };
        var every = Gpu(k, -1, 0);
        every.Calculate(CancellationToken.None);
        var est = Supersampling.AccumulateAlt(every, 4, CancellationToken.None, histogramEq: 0);
        Assert.Equal(fpx, Pixels(every));
        Assert.Equal(fst.PixelSamples, est.PixelSamples);
        Assert.Null(k.PixelMask);   // cleared after the pass

        // The default mask, every sample (tolerance 0): unmarked pixels are the
        // one-sample frame, marked ones the full pass's mean.
        var k2 = new S1394GpuSolidRegionTests.SoftMaskKernel { HonourMask = kernelMasks };
        var ad = Gpu(k2, AdaptiveAa.DefaultThreshold, 0);
        ad.Calculate(CancellationToken.None);
        var st = Supersampling.AccumulateAlt(ad, 4, CancellationToken.None, histogramEq: 0);
        var mask = new byte[W * H];
        int marked = Supersampling.BuildMask(one, oneIter, MaxIter, W, H, AdaptiveAa.DefaultThreshold, mask);
        Assert.True(marked > 0 && marked < W * H, $"precondition: {marked} marked");
        Assert.Equal(marked, st.Marked);
        Assert.Equal(4, k2.Dispatches);
        if (kernelMasks) Assert.Equal(new long[] { marked, marked, marked }, k2.MaskCounts);
        else Assert.Empty(k2.MaskCounts);
        Assert.Null(k2.PixelMask);
        var px = Pixels(ad);
        for (int i = 0; i < mask.Length; i++) Assert.Equal(mask[i] != 0 ? fpx[i] : one[i], px[i]);
    }

    // ── Batch parity ───────────────────────────────────────────────────────────

    [Fact]
    public void The_Snapshot_Param_And_Command_Builder_Carry_It_For_The_2D_Families()
    {
        foreach (var f in Enum.GetValues<Family>())
        {
            var t = TypeOf(f);
            var stock = RegionFractalParams.Snapshot(t, new FractalParameters());   // omitted at the defaults
            Assert.True(stock?.AdaptiveAa == null && stock?.AdaptiveAaThreshold == null && stock?.AdaptiveAaTolerance == null, t.ToString());
            var p = new FractalParameters { AdaptiveAa = true, AdaptiveAaThreshold = 8, AdaptiveAaTolerance = 0.5 };
            var snap = RegionFractalParams.Snapshot(t, p)!;
            var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
            var q = new FractalParameters();
            back.ApplyTo(q);
            Assert.True(q.AdaptiveAa, t.ToString());
            Assert.Equal(8, q.AdaptiveAaThreshold);
            Assert.Equal(0.5, q.AdaptiveAaTolerance);
            RegionFractalParams.ResetFamilyToDefaults(t, q);
            Assert.False(q.AdaptiveAa, t.ToString());
            Assert.Equal(AdaptiveAa.DefaultThreshold, q.AdaptiveAaThreshold);
            Assert.Equal(AdaptiveAa.DefaultTolerance, q.AdaptiveAaTolerance);

            var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot
            {
                Fractal = t, Parameters = p, FamilyParams = snap.ToKeyValues(),
            };
            Assert.Contains("AdaptiveAa=", FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap));
            var args = new List<string> { "--fractal", t.ToString(), "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
            foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
            Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
            var overlay = RegionFractalParams.FromKeyValues(opts.Params, t, out var error);
            Assert.True(overlay != null, error);
            var fresh = new FractalParameters();
            overlay!.ApplyTo(fresh);
            Assert.True(fresh.AdaptiveAa, t.ToString());
            Assert.Equal(8, fresh.AdaptiveAaThreshold);
            Assert.Equal(0.5, fresh.AdaptiveAaTolerance);
        }
        // A family with no adaptive pass does not carry it.
        Assert.False(AdaptiveAaCapabilities.Supports(FractalType.Plasma));
        Assert.Null(RegionFractalParams.Snapshot(FractalType.Plasma, new FractalParameters { AdaptiveAa = true })?.AdaptiveAa);
    }

    [Theory]
    [InlineData(Family.Julia)]
    [InlineData(Family.Newton)]
    [InlineData(Family.UserEquation)]
    public void The_Poster_Honours_It_Like_The_Live_Calculator(Family f)
    {
        FracturingFog.Imaging.PosterRequest Req(bool adaptive) => new()
        {
            FractalType = TypeOf(f), Width = W, Height = H, Zoom = 1.0, MaxIterations = MaxIter,
            Quality = QualityPreset.Ultra, ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = Params(f, adaptive), Path = string.Empty,
        };
        var (live, _) = Super(f, Params(f, true), QualityPreset.Ultra.AaSamples);
        var (liveFull, _) = Super(f, Params(f, false), QualityPreset.Ultra.AaSamples);
        Assert.NotEqual(liveFull, live);   // precondition: adaptive differs from full
        Assert.Equal(live, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(true), CancellationToken.None, out _, out _));
        Assert.Equal(liveFull, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(false), CancellationToken.None, out _, out _));
    }

    [Fact]
    public void The_View_Model_Shows_The_Controls_On_The_2D_Families()
    {
        static FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel Vm(FractalType t) => new(t, new FractalParameters());
        foreach (var f in Enum.GetValues<Family>())
        {
            Assert.True(Vm(TypeOf(f)).IsAdaptiveAa2D, f.ToString());
            Assert.False(Vm(TypeOf(f)).HasNoParams, f.ToString());
        }
        Assert.True(Vm(FractalType.Mandelbrot).IsAdaptiveAa2D);
        Assert.False(Vm(FractalType.Mandelbulb).IsAdaptiveAa2D);
        Assert.False(Vm(FractalType.Plasma).IsAdaptiveAa2D);
    }
}

/// <summary>#1382 — the live host takes the adaptive pass on a 2D alt family.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1382AdaptiveAa2DHostTests
{
    private sealed class Recorder : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "recorder";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(FractalType.Julia, true)]
    [InlineData(FractalType.Julia, false)]
    [InlineData(FractalType.Newton, true)]
    [InlineData(FractalType.UserEquation, true)]
    public void The_Host_Supersamples_Adaptively_When_The_View_Asks(FractalType type, bool adaptive)
    {
        var s = new FractalViewState { FractalType = type, Zoom = 1.0, Quality = QualityPreset.High };
        s.FractalParameters.UserEquationSource = "z*z*z + c";
        s.FractalParameters.AdaptiveAa = adaptive;
        using var host = new FractalRenderHost(new Recorder(), s, 256, 192, ColorPalette.BuiltIns[0]);
        int done = 0;
        host.FrameCompleted += (_, _) => Interlocked.Increment(ref done);
        host.Trigger(progressive: false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref done) == 0 && sw.ElapsedMilliseconds < 60000) Thread.Sleep(10);
        var st = Assert.NotNull(host.LastSupersample);
        Assert.Equal(4, st.Samples);
        if (adaptive) Assert.True(st.Marked > 0 && st.Marked < st.Pixels, $"marked {st.Marked} of {st.Pixels}");
        else Assert.Equal(st.Pixels, st.Marked);
    }
}
