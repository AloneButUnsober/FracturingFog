// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1381 — adaptive contrast (histogram equalisation) recolours a frame from its
// iteration / smooth buffers, which after a supersample pass hold only the last
// jittered sample, so equalising the finished average threw the anti-aliasing
// away (live, --batch and export alike). Now every sample is equalised with the
// CDF of sample 0 before the average. Each test checks the result against an
// independent reference: a fresh calculator per sample (the unjittered frame
// plus each jitter offset), every one equalised with sample 0's CDF, averaged
// per channel and rounded half up.
//   • full supersampling (shallow SP and deep perturbation, both strengths) is
//     exactly that mean, and differs from the old result, which was the last
//     sample alone;
//   • adaptive AA: an unmarked pixel is equalised sample 0, a marked one the full
//     mean, and the mask is built from the equalised colours;
//   • the escape-time alt calculators (Julia) the same;
//   • off (0 %), one sample, and a view with no escaped pixel keep the old path;
//   • export (PosterRenderer) runs it, Mandelbrot and alt;
//   • the live host: a High (MSAA + TAA) frame settles to the mean of every
//     equalised sample (the TAA continuations used to show the last one alone),
//     and moving the slider on such a frame re-renders it at the new strength.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

internal static class S1381Reference
{
    public static int Ch(uint c, int k) => (int)((c >> (8 * k)) & 0xFF);

    /// <summary>Per-channel mean of <paramref name="frames"/>, rounded half up.</summary>
    public static uint[] Mean(IReadOnlyList<uint[]> frames, int pixels)
    {
        var outB = new uint[pixels];
        int n = frames.Count;
        for (int i = 0; i < pixels; i++)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++)
            {
                int sum = 0;
                foreach (var f in frames) sum += Ch(f[i], k);
                v |= (uint)((sum + n / 2) / n) << (8 * k);
            }
            outB[i] = v;
        }
        return outB;
    }

    /// <summary>Sample 0's CDF: (cdf, bins, sourceMaxIter).</summary>
    public static (double[] Cdf, int Bins, int Src) Cdf(ISupportsHistogramEq he)
    {
        Assert.True(he.BuildHistogramCdf(out var cdf, out int bins, out int src), "precondition: pixels escape");
        return (cdf!, bins, src);
    }

    public static uint[] Equalised(ISupportsHistogramEq he, uint[] color, (double[] Cdf, int Bins, int Src) c, double strength)
    {
        he.ApplyHistogramEqualizationWithCdf(c.Cdf, c.Bins, c.Src, strength);
        return (uint[])color.Clone();
    }

    /// <summary>Sample 0 then the grid's jitter offsets (row-major, the centre cell
    /// being sample 0), each a fresh calculator equalised with sample 0's CDF.</summary>
    public static List<uint[]> MandelSamples(Func<MandelbrotCalculator> make, int side, double strength,
        out uint[] single, out int[] iter0)
    {
        var c0 = make();
        c0.Calculate(CancellationToken.None);
        var cdf = Cdf(c0);
        single = Equalised(c0, c0.ColorBuffer, cdf, strength);
        iter0 = (int[])c0.IterationBuffer.Clone();
        var frames = new List<uint[]> { single };
        double scale = (3.5 / Math.Max(c0.Width, c0.Height)) / c0.Zoom;
        foreach (var (jx, jy) in Supersampling.GridOffsets(side))
        {
            var c = make();
            var orig = CentreLimbs.Capture(c);
            CentreJitter.Apply(c, in orig, jx, jy, scale);
            c.Calculate(CancellationToken.None);
            frames.Add(Equalised(c, c.ColorBuffer, cdf, strength));
        }
        return frames;
    }

    /// <summary>The alt twin: the jitter moves CenterX / CenterY (AccumulateAlt).</summary>
    public static List<uint[]> AltSamples(Func<IFractalCalculator> make, int side, double strength)
    {
        var c0 = make();
        c0.Calculate(CancellationToken.None);
        var he0 = (ISupportsHistogramEq)c0;
        var cdf = Cdf(he0);
        var frames = new List<uint[]> { Equalised(he0, c0.ColorBuffer, cdf, strength) };
        double scale = (3.5 / Math.Max(c0.Width, c0.Height)) / c0.Zoom;
        foreach (var (jx, jy) in Supersampling.GridOffsets(side))
        {
            var c = make();
            c.CenterX += jx * scale;
            c.CenterY += jy * scale;
            c.Calculate(CancellationToken.None);
            frames.Add(Equalised((ISupportsHistogramEq)c, c.ColorBuffer, cdf, strength));
        }
        return frames;
    }
}

public sealed class S1381HistogramEqAaTests
{
    private const int W = 120, H = 80;

    private static MandelbrotCalculator Calc(double cx, double cy, double zoom, int maxIter, QualityPreset q) => new(W, H)
    {
        CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter, Quality = q, ColorMap = new HsvPalette(),
    };

    private static MandelbrotCalculator Seahorse(QualityPreset q) => Calc(-0.7436, 0.1318, 300, 800, q);
    private static MandelbrotCalculator Spiral(QualityPreset q) => Calc(-0.743643887037151, 0.13182590420533, 1e14, 3000, q);

    private static QualityPreset For(int n) => n == 4 ? QualityPreset.High : QualityPreset.Ultra;

    private static uint[] Rendered(MandelbrotCalculator c, int n, AdaptiveAa aa, int heq, out SupersampleStats st)
    {
        c.Calculate(CancellationToken.None);
        st = Supersampling.AccumulateMandelbrot(c, n, aa, CancellationToken.None, heq);
        Supersampling.FinishHistogramEq(c, in st, heq);
        return (uint[])c.ColorBuffer.Clone();
    }

    private static int Differ(uint[] a, uint[] b) => Enumerable.Range(0, W * H).Count(i => a[i] != b[i]);

    [Theory]
    [InlineData(4, 100, false)]
    [InlineData(16, 100, false)]
    [InlineData(4, 40, false)]
    [InlineData(4, 100, true)]
    public void Full_Supersampling_Is_The_Mean_Of_The_Equalised_Samples(int n, int heq, bool deep)
    {
        var q = For(n);
        Func<MandelbrotCalculator> make = deep ? () => Spiral(q) : () => Seahorse(q);
        var frames = S1381Reference.MandelSamples(make, (int)Math.Sqrt(n), heq / 100.0, out var single, out _);
        var expected = S1381Reference.Mean(frames, W * H);

        var got = Rendered(make(), n, AdaptiveAa.Off, heq, out var st);
        Assert.Equal(expected, got);
        Assert.True(st.Equalized);
        Assert.Equal(heq / 100.0, st.Equalizer!.Strength);

        // The old order (average, then equalise from the buffers) left the last
        // sample alone: the precondition that this view tells the two apart.
        var old = make();
        old.Calculate(CancellationToken.None);
        Supersampling.AccumulateMandelbrot(old, n, AdaptiveAa.Off, CancellationToken.None);
        old.ApplyHistogramEqualization(heq / 100.0);
        Assert.True(Differ(old.ColorBuffer, got) > W * H / 50, $"precondition: only {Differ(old.ColorBuffer, got)} pixels differ from the old result");
        // And the AA is there: the frame differs from the one-sample frame along edges.
        Assert.True(Differ(single, got) > W * H / 50, $"only {Differ(single, got)} pixels differ from one sample");
    }

    /// <summary>The #1344 mask by definition, pixel by pixel.</summary>
    private static bool ShouldMark(uint[] c, int[] it, int maxIter, int x, int y, double thr)
    {
        int i = y * W + x;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int j = ny * W + nx;
                if ((it[i] >= maxIter) != (it[j] >= maxIter)) return true;
                for (int k = 0; k < 4; k++)
                    if (Math.Abs(S1381Reference.Ch(c[i], k) - S1381Reference.Ch(c[j], k)) > thr) return true;
            }
        return false;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void Adaptive_Aa_Keeps_Equalised_Sample_0_Unmarked_And_The_Full_Mean_Marked(int n)
    {
        var q = For(n);
        var frames = S1381Reference.MandelSamples(() => Seahorse(q), (int)Math.Sqrt(n), 1.0, out var single, out var it);
        var full = S1381Reference.Mean(frames, W * H);
        // No early stop: a marked pixel takes every sample.
        var got = Rendered(Seahorse(q), n, new AdaptiveAa(true, 12, 0), 100, out var st);
        int marked = 0, markedChanged = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                bool m = ShouldMark(single, it, 800, x, y, 12);   // from the EQUALISED sample 0
                marked += m ? 1 : 0;
                if (m && full[i] != single[i]) markedChanged++;
                Assert.Equal(m ? full[i] : single[i], got[i]);
            }
        Assert.Equal(marked, st.Marked);
        Assert.True(st.Equalized);
        Assert.InRange(marked, W * H / 20, W * H * 9 / 10);   // precondition: a real mix
        Assert.True(markedChanged > marked / 4, "precondition: AA changes the marked pixels");
    }

    private static EscapeTimeCalculator Julia(QualityPreset q) => new(W, H)
    {
        FractalType = FractalType.Julia, FractalParameters = new FractalParameters(),
        CenterX = 0.0, CenterY = 0.0, Zoom = 0.6, MaxIterations = 256, ColorMap = new HsvPalette(), Quality = q,
    };

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void An_Alt_Calculator_Averages_Its_Equalised_Samples(int n)
    {
        var q = For(n);
        var frames = S1381Reference.AltSamples(() => Julia(q), (int)Math.Sqrt(n), 1.0);
        var expected = S1381Reference.Mean(frames, W * H);

        var c = Julia(q);
        c.Calculate(CancellationToken.None);
        var st = Supersampling.AccumulateAlt(c, n, CancellationToken.None, 100);
        Supersampling.FinishHistogramEq(c, in st, 100);
        Assert.True(st.Equalized);
        Assert.Equal(expected, c.ColorBuffer.Take(W * H).ToArray());

        var old = Julia(q);
        old.Calculate(CancellationToken.None);
        Supersampling.AccumulateAlt(old, n, CancellationToken.None);
        old.ApplyHistogramEqualization(1.0);
        Assert.True(Differ(old.ColorBuffer, c.ColorBuffer) > W * H / 50, "precondition: the old result differs");
        Assert.True(Differ(frames[0], c.ColorBuffer) > W * H / 50, "the AA is missing");
    }

    [Fact]
    public void Off_Is_The_Plain_Supersample_And_Finishing_Does_Nothing()
    {
        var plain = Seahorse(QualityPreset.High);
        plain.Calculate(CancellationToken.None);
        Supersampling.AccumulateMandelbrot(plain, 4, AdaptiveAa.Off, CancellationToken.None);

        var got = Rendered(Seahorse(QualityPreset.High), 4, AdaptiveAa.Off, 0, out var st);
        Assert.False(st.Equalized);
        Assert.Equal(plain.ColorBuffer, got);
    }

    [Fact]
    public void One_Sample_Equalises_After_The_Pass_As_Before()
    {
        var old = Seahorse(QualityPreset.Standard);
        old.Calculate(CancellationToken.None);
        old.ApplyHistogramEqualization(1.0);

        var got = Rendered(Seahorse(QualityPreset.Standard), 1, AdaptiveAa.Off, 100, out var st);
        Assert.False(st.Equalized);
        Assert.Equal(old.ColorBuffer, got);
        // Precondition: equalisation changed the frame.
        var raw = Seahorse(QualityPreset.Standard);
        raw.Calculate(CancellationToken.None);
        Assert.True(Differ(raw.ColorBuffer, got) > W * H / 10, "precondition: equalisation is visible");
    }

    [Fact]
    public void A_Frame_With_No_Escaped_Pixel_Keeps_The_Old_Path()
    {
        // Inside the main cardioid: nothing escapes, so there is no CDF.
        Func<MandelbrotCalculator> make = () => Calc(-0.1, 0.05, 50, 500, QualityPreset.High);
        var old = make();
        old.Calculate(CancellationToken.None);
        Supersampling.AccumulateMandelbrot(old, 4, AdaptiveAa.Off, CancellationToken.None);
        old.ApplyHistogramEqualization(1.0);

        var got = Rendered(make(), 4, AdaptiveAa.Off, 100, out var st);
        Assert.False(st.Equalized);
        Assert.Equal(old.ColorBuffer, got);
    }

    [Fact]
    public void Finishing_An_Equalised_Pass_Leaves_Its_Colour_Alone()
    {
        var c = Seahorse(QualityPreset.High);
        c.Calculate(CancellationToken.None);
        var st = Supersampling.AccumulateMandelbrot(c, 4, AdaptiveAa.Off, CancellationToken.None, 100);
        var before = (uint[])c.ColorBuffer.Clone();
        Supersampling.FinishHistogramEq(c, in st, 100);
        Assert.Equal(before, c.ColorBuffer);
    }

    // ── Export ────────────────────────────────────────────────────────────

    private static FracturingFog.Imaging.PosterRequest Req(FractalType type, QualityPreset q, int heq) => type == FractalType.Mandelbrot
        ? new()
        {
            FractalType = type, Width = W, Height = H, CenterX = -0.7436, CenterY = 0.1318, Zoom = 300, MaxIterations = 800,
            Quality = q, ColorMap = new HsvPalette(), FractalParameters = new FractalParameters(), Path = string.Empty, HistogramEq = heq,
        }
        : new()
        {
            FractalType = type, Width = W, Height = H, CenterX = 0, CenterY = 0, Zoom = 0.6, MaxIterations = 256,
            Quality = q, ColorMap = new HsvPalette(), FractalParameters = new FractalParameters(), Path = string.Empty, HistogramEq = heq,
        };

    private static uint[] Poster(FracturingFog.Imaging.PosterRequest r) =>
        FracturingFog.Imaging.PosterRenderer.RenderToPixels(r, CancellationToken.None, out _, out _);

    [Fact]
    public void Export_Averages_The_Equalised_Samples()
    {
        var m = S1381Reference.Mean(S1381Reference.MandelSamples(() => Seahorse(QualityPreset.High), 2, 0.7, out _, out _), W * H);
        Assert.Equal(m, Poster(Req(FractalType.Mandelbrot, QualityPreset.High, 70)));

        var jReq = Req(FractalType.Julia, QualityPreset.High, 70);
        var j = S1381Reference.Mean(S1381Reference.AltSamples(() => FracturingFog.Imaging.PosterRenderer.BuildCaptureCalculator(jReq)!, 2, 0.7), W * H);
        Assert.Equal(j, Poster(jReq));

        // One sample: equalised after the frame as before.
        var one = Seahorse(QualityPreset.Standard);
        one.Calculate(CancellationToken.None);
        one.ApplyHistogramEqualization(0.7);
        Assert.Equal(one.ColorBuffer, Poster(Req(FractalType.Mandelbrot, QualityPreset.Standard, 70)));
    }
}

/// <summary>#1381 — the live host: the calc thread equalises every MSAA / TAA sample
/// with sample 0's CDF; the slider re-renders a supersampled frame.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1381HistogramEqHostTests
{
    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private const int W = 96, H = 64;
    private const double Zoom = 1e15;   // deep: the CPU perturbation path (as in S1330)
    private static readonly OD Cx = new(-1.1726999042772253, 8.9529605787776783E-17, 0, 0, 0, 0, 0, 0);
    private static readonly OD Cy = new(-0.2968356710071185, -2.3536240906562374E-18, 0, 0, 0, 0, 0, 0);

    private static FractalViewState State(int heq) => new()
    {
        FractalType = FractalType.Mandelbrot, Zoom = Zoom, Quality = QualityPreset.High, PreferredIterations = 3000,
        CenterX = Cx.X0, CenterXLo = Cx.X1, CenterY = Cy.X0, CenterYLo = Cy.X1, HistogramEq = heq,
    };

    private static double Halton(int index, int b)
    {
        double f = 1.0, r = 0.0;
        for (int i = index; i > 0; i /= b) { f /= b; r += f * (i % b); }
        return r;
    }

    /// <summary>The settled High frame by definition: the 2×2 MSAA mean of equalised
    /// samples (the TAA seed, rounded), then each Halton TAA sample equalised with
    /// the same CDF, all averaged and rounded half up.</summary>
    private static uint[] Expected(MandelbrotCalculator live, double strength)
    {
        MandelbrotCalculator Make() => new(W, H)
        {
            Quality = live.Quality, MaxIterations = live.MaxIterations, Zoom = live.Zoom, ColorMap = live.ColorMap,
            CenterX = Cx.X0, CenterXLo = Cx.X1, CenterY = Cy.X0, CenterYLo = Cy.X1,
        };
        int n = W * H;
        var msaa = S1381Reference.MandelSamples(Make, 2, strength, out _, out _);
        var seed = S1381Reference.Mean(msaa, n);
        var c0 = Make();
        c0.Calculate(CancellationToken.None);
        var cdf = S1381Reference.Cdf(c0);
        var sums = new long[n * 4];
        for (int i = 0; i < n; i++) for (int k = 0; k < 4; k++) sums[i * 4 + k] = S1381Reference.Ch(seed[i], k);
        int count = 1;
        double scale = (3.5 / Math.Max(W, H)) / Zoom;
        for (int s = 1; s < live.Quality!.TaaMaxSamples; s++)
        {
            var c = Make();
            var orig = CentreLimbs.Capture(c);
            CentreJitter.Apply(c, in orig, Halton(s + 2, 2) - 0.5, Halton(s + 2, 3) - 0.5, scale);
            c.Calculate(CancellationToken.None);
            var f = S1381Reference.Equalised(c, c.ColorBuffer, cdf, strength);
            for (int i = 0; i < n; i++) for (int k = 0; k < 4; k++) sums[i * 4 + k] += S1381Reference.Ch(f[i], k);
            count++;
        }
        var outB = new uint[n];
        for (int i = 0; i < n; i++)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++) v |= (uint)((sums[i * 4 + k] + count / 2) / count) << (8 * k);
            outB[i] = v;
        }
        return outB;
    }

    private static ManualResetEventSlim Settled(FractalRenderHost host)
    {
        var done = new ManualResetEventSlim(false);   // not disposed: a late event may still Set it
        host.RefineProgress += (_, e) => { if (e.Done) done.Set(); };
        return done;
    }

    [Fact]
    public void A_Taa_Frame_Settles_To_The_Mean_Of_Every_Equalised_Sample_And_The_Slider_Re_Renders_It()
    {
        var state = State(100);
        using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var done = Settled(host);
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(120)), "the TAA chain never settled");
        Thread.Sleep(200);
        var calc = host.Mandelbrot;
        Assert.Equal(8, QualityPreset.High.TaaMaxSamples);
        Assert.False(calc.UseGpuCompute && !calc.IsHighPrecisionActive, "precondition: the CPU path");
        var got = calc.ColorBuffer.Take(W * H).ToArray();
        Assert.Equal(Expected(calc, 1.0), got);

        // The slider on a supersampled frame: the cheap recolour shows at once, then a
        // full render settles at the new strength.
        done.Reset();
        state.HistogramEq = 40;
        host.RepaintWithAdaptive();
        Assert.True(done.Wait(TimeSpan.FromSeconds(120)), "the slider never re-rendered the frame");
        Thread.Sleep(200);
        var again = calc.ColorBuffer.Take(W * H).ToArray();
        Assert.Equal(Expected(calc, 0.4), again);
        Assert.NotEqual(got, again);
    }
}
