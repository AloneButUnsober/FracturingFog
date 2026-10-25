// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1380 — the quality presets' temporal AA (High 8 / Ultra 16 / Extreme 32
// samples) used to be live-only: a --batch or exported still matched the live
// view's first frame, not the one it settles to. Now a still (PosterRequest.
// TemporalAa, on for batch images and exports, --no-taa off) runs the same
// samples through the accumulator the live host uses. Locks, each against an
// independent reference (fresh calculators, our own Halton sequence):
//   • the offline pass is the mean of the MSAA seed and every Halton-jittered
//     sample (shallow, deep, with adaptive contrast equalising each sample);
//   • the LIVE host's settled frame equals the exported still, byte for byte;
//   • TemporalAa off, and the presets without TAA, are unchanged;
//   • a cluster tile's anti-aliasing equals the same pixels of the whole image
//     (the jitter is one IMAGE pixel; it used the tile's — fixed here, MSAA too);
//   • --no-taa parses, is catalogued for images, and the builder no longer flags
//     the TAA presets.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

internal static class S1380Reference
{
    public static double Halton(int index, int b)
    {
        double f = 1.0, r = 0.0;
        for (int i = index; i > 0; i /= b) { f /= b; r += f * (i % b); }
        return r;
    }

    public static int Ch(uint c, int k) => (int)((c >> (8 * k)) & 0xFF);

    /// <summary>The settled still by definition: the seed (MSAA, already
    /// tested by #1344 / #1381) plus samples 1 … taa − 1, each a fresh calculator
    /// at the Halton (index + 2) jitter of one image pixel, equalised with sample
    /// 0's CDF when <paramref name="heq"/> &gt; 0; the running sums averaged and
    /// rounded half up.</summary>
    public static uint[] Settled(Func<MandelbrotCalculator> make, int heq)
    {
        var c0 = make();
        int n = c0.Width * c0.Height;
        c0.Calculate(CancellationToken.None);
        (double[] Cdf, int Bins, int Src)? cdf = null;
        if (heq > 0)
        {
            Assert.True(c0.BuildHistogramCdf(out var cd, out int bins, out int src));
            cdf = (cd!, bins, src);
        }
        var seedCalc = make();
        seedCalc.Calculate(CancellationToken.None);
        var st = Supersampling.AccumulateMandelbrot(seedCalc, seedCalc.Quality!.AaSamples, AdaptiveAa.Off, CancellationToken.None, heq);
        if (heq > 0 && !st.Equalized)
            seedCalc.ApplyHistogramEqualizationWithCdf(cdf!.Value.Cdf, cdf.Value.Bins, cdf.Value.Src, heq / 100.0);
        var sums = new long[n * 4];
        for (int i = 0; i < n; i++) for (int k = 0; k < 4; k++) sums[i * 4 + k] = Ch(seedCalc.ColorBuffer[i], k);
        int count = 1;
        int iw = c0.ImageWidth > 0 ? c0.ImageWidth : c0.Width, ih = c0.ImageHeight > 0 ? c0.ImageHeight : c0.Height;
        double scale = (3.5 / Math.Max(iw, ih)) / c0.Zoom;
        for (int s = 1; s < c0.Quality!.TaaMaxSamples; s++)
        {
            var c = make();
            var orig = CentreLimbs.Capture(c);
            CentreJitter.Apply(c, in orig, Halton(s + 2, 2) - 0.5, Halton(s + 2, 3) - 0.5, scale);
            c.Calculate(CancellationToken.None);
            if (cdf is { } e) c.ApplyHistogramEqualizationWithCdf(e.Cdf, e.Bins, e.Src, heq / 100.0);
            for (int i = 0; i < n; i++) for (int k = 0; k < 4; k++) sums[i * 4 + k] += Ch(c.ColorBuffer[i], k);
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
}

public sealed class S1380OfflineTaaTests
{
    private const int W = 120, H = 80;

    private static MandelbrotCalculator Calc(double cx, double cy, double zoom, int maxIter, QualityPreset q) => new(W, H)
    {
        CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter, Quality = q, ColorMap = new HsvPalette(),
    };

    private static MandelbrotCalculator Seahorse(QualityPreset q) => Calc(-0.7436, 0.1318, 300, 800, q);
    private static MandelbrotCalculator Spiral(QualityPreset q) => Calc(-0.743643887037151, 0.13182590420533, 1e14, 3000, q);

    private static uint[] Offline(MandelbrotCalculator c, int heq, bool temporal, out SupersampleStats st)
    {
        c.Calculate(CancellationToken.None);
        st = Supersampling.AccumulateQuality(c, c.Quality, AdaptiveAa.Off, heq, temporal, CancellationToken.None);
        Supersampling.FinishHistogramEq(c, in st, heq);
        return c.ColorBuffer.Take(W * H).ToArray();
    }

    private static int Differ(uint[] a, uint[] b) => Enumerable.Range(0, a.Length).Count(i => a[i] != b[i]);

    [Theory]
    [InlineData("High", 0, false)]
    [InlineData("Ultra", 0, false)]
    [InlineData("High", 100, false)]
    [InlineData("High", 0, true)]
    [InlineData("High", 60, true)]
    public void The_Offline_Pass_Is_The_Mean_Of_The_Seed_And_Every_Halton_Sample(string quality, int heq, bool deep)
    {
        var q = QualityPreset.FromName(quality);
        Func<MandelbrotCalculator> make = deep ? () => Spiral(q) : () => Seahorse(q);
        var expected = S1380Reference.Settled(make, heq);
        var got = Offline(make(), heq, temporal: true, out var st);
        Assert.Equal(expected, got);
        Assert.Equal(q.TaaMaxSamples, st.TemporalSamples);
        Assert.Equal(heq > 0, st.Equalized);

        // Precondition: the temporal samples change the frame (vs MSAA only).
        var msaa = Offline(make(), heq, temporal: false, out var st0);
        Assert.Equal(0, st0.TemporalSamples);
        Assert.True(Differ(msaa, got) > W * H / 50, $"only {Differ(msaa, got)} pixels changed");
    }

    [Theory]
    [InlineData("Draft")]
    [InlineData("Standard")]
    public void Presets_Without_Taa_Are_Unchanged(string quality)
    {
        var q = QualityPreset.FromName(quality);
        Assert.True(q.TaaMaxSamples <= 1, "precondition");
        Assert.Equal(Offline(Seahorse(q), 50, false, out _), Offline(Seahorse(q), 50, true, out var st));
        Assert.Equal(0, st.TemporalSamples);
    }

    [Fact]
    public void Taa_Without_Msaa_Still_Equalises_Every_Sample_With_Sample_0s_Cdf()
    {
        // No shipped preset has TAA without MSAA, but a custom one may: the seed is
        // then equalised here, not by the MSAA pass.
        var q = new QualityPreset { Name = "TaaOnly", AaSamples = 1, TaaMaxSamples = 6 };
        var expected = S1380Reference.Settled(() => Seahorse(q), 80);
        var got = Offline(Seahorse(q), 80, temporal: true, out var st);
        Assert.Equal(expected, got);
        Assert.True(st.Equalized);
        Assert.Equal(6, st.TemporalSamples);
    }

    [Fact]
    public void Temporal_Off_Is_The_Msaa_Pass_Exactly()
    {
        var plain = Seahorse(QualityPreset.Ultra);
        plain.Calculate(CancellationToken.None);
        var ss = Supersampling.AccumulateMandelbrot(plain, 16, AdaptiveAa.Off, CancellationToken.None, 0);
        Supersampling.FinishHistogramEq(plain, in ss, 0);
        Assert.Equal(plain.ColorBuffer.Take(W * H).ToArray(), Offline(Seahorse(QualityPreset.Ultra), 0, false, out _));
    }

    [Fact]
    public void The_Offset_Sequence_Is_Halton_2_3_From_Index_2()
    {
        for (int k = 1; k < 32; k++)
        {
            var (jx, jy) = Supersampling.TaaOffset(k);
            Assert.Equal(S1380Reference.Halton(k + 2, 2) - 0.5, jx);
            Assert.Equal(S1380Reference.Halton(k + 2, 3) - 0.5, jy);
            Assert.InRange(jx, -0.5, 0.5);
            Assert.InRange(jy, -0.5, 0.5);
        }
        // Sample 1 = index 3: base 2 "11" -> 0.75, base 3 "10" -> 1/9.
        Assert.Equal(0.25, Supersampling.TaaOffset(1).Jx);
        Assert.Equal(1.0 / 9 - 0.5, Supersampling.TaaOffset(1).Jy, 15);
    }

    [Fact]
    public void A_Cancelled_Pass_Restores_The_Centre()
    {
        var c = Spiral(QualityPreset.Extreme);
        c.CenterXLo = 1.25e-17;
        c.Calculate(CancellationToken.None);
        var before = CentreLimbs.Capture(c);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int n = Supersampling.AccumulateTemporal(c, 32, null, cts.Token);
        Assert.Equal(1, n);   // the seed only
        Assert.Equal(before, CentreLimbs.Capture(c));
    }

    [Fact]
    public void A_Finished_Pass_Restores_Every_Centre_Limb()
    {
        var c = Spiral(QualityPreset.High);
        c.CenterXLo = 1.25e-17; c.CenterYLo = -3.5e-18;
        c.Calculate(CancellationToken.None);
        var before = CentreLimbs.Capture(c);
        Assert.Equal(8, Supersampling.AccumulateTemporal(c, 8, null, CancellationToken.None));
        Assert.Equal(before, CentreLimbs.Capture(c));
    }

    // ── Cluster tiles ─────────────────────────────────────────────────────

    private static MandelbrotCalculator Tile(QualityPreset q, int ox, int oy, int tw, int th) => new(tw, th)
    {
        CenterX = -0.743643887037151, CenterY = 0.13182590420533, Zoom = 1e14, MaxIterations = 3000,
        Quality = q, ColorMap = new HsvPalette(),
        ImageWidth = W, ImageHeight = H, SubRectOffsetX = ox, SubRectOffsetY = oy,
    };

    private static uint[] Crop(uint[] full, int ox, int oy, int tw, int th)
    {
        var r = new uint[tw * th];
        for (int y = 0; y < th; y++) Array.Copy(full, (oy + y) * W + ox, r, y * tw, tw);
        return r;
    }

    [Fact]
    public void A_Cluster_Tiles_Anti_Aliasing_Equals_The_Same_Pixels_Of_The_Whole_Image()
    {
        // The window of the spiral where AA changes the most (a flat one would prove nothing).
        const int ox = 64, oy = 44, tw = 40, th = 36;
        // Precondition: without AA the tile is exactly those pixels of the image.
        var full0 = Offline(Spiral(QualityPreset.Standard), 0, false, out _);
        var t0 = Tile(QualityPreset.Standard, ox, oy, tw, th);
        t0.Calculate(CancellationToken.None);
        var crop0 = Crop(full0, ox, oy, tw, th);
        Assert.Equal(crop0, t0.ColorBuffer.Take(tw * th).ToArray());
        Assert.True(crop0.Distinct().Count() > 50, $"precondition: a detailed window ({crop0.Distinct().Count()} colours)");

        foreach (bool temporal in new[] { false, true })
        {
            var full = Offline(Spiral(QualityPreset.High), 0, temporal, out _);
            var t = Tile(QualityPreset.High, ox, oy, tw, th);
            t.Calculate(CancellationToken.None);
            var st = Supersampling.AccumulateQuality(t, t.Quality, AdaptiveAa.Off, 0, temporal, CancellationToken.None);
            var crop = Crop(full, ox, oy, tw, th);
            Assert.True(Differ(crop, crop0) > tw * th / 10, $"precondition: AA changes the window ({Differ(crop, crop0)} px)");
            Assert.Equal(crop, t.ColorBuffer.Take(tw * th).ToArray());
            Assert.Equal(temporal ? 8 : 0, st.TemporalSamples);
        }
    }

    // ── Batch parity ──────────────────────────────────────────────────────

    [Fact]
    public void No_Taa_Parses_And_Is_Catalogued_For_Images()
    {
        string[] Args(params string[] extra) =>
            new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png" }.Concat(extra).ToArray();
        Assert.True(BatchOptions.TryParse(Args(), 2, out var on, out var err), err);
        Assert.True(on.TemporalAa);
        Assert.True(BatchOptions.TryParse(Args(BatchFlags.NoTaa), 2, out var off, out err), err);
        Assert.False(off.TemporalAa);
        var spec = BatchFlagCatalog.All.Single(f => f.Name == BatchFlags.NoTaa);
        Assert.Equal(BatchFlagKind.Switch, spec.Kind);
        Assert.Equal(BatchModes.Image, spec.Modes);
    }

    [Fact]
    public void The_Live_Seed_Never_Emits_No_Taa()
    {
        // The live view always runs the preset's TAA, so its command keeps the default.
        var snap = new BatchCommandSnapshot { Fractal = FractalType.Mandelbrot, Parameters = new FractalParameters(), QualityName = "Extreme" };
        Assert.DoesNotContain(BatchFlags.NoTaa, BatchCommandBuilder.Build(snap));
    }
}

/// <summary>#1380 — the headline: the live view's settled frame IS the exported still.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1380LiveEqualsExportTests
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
    private const double Zoom = 1e15;   // deep: the CPU perturbation path (as in S1330 / S1381)
    private static readonly OD Cx = new(-1.1726999042772253, 8.9529605787776783E-17, 0, 0, 0, 0, 0, 0);
    private static readonly OD Cy = new(-0.2968356710071185, -2.3536240906562374E-18, 0, 0, 0, 0, 0, 0);

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void The_Settled_Live_Frame_Equals_The_Exported_Still(int heq)
    {
        var state = new FractalViewState
        {
            FractalType = FractalType.Mandelbrot, Zoom = Zoom, Quality = QualityPreset.High, PreferredIterations = 3000,
            CenterX = Cx.X0, CenterXLo = Cx.X1, CenterY = Cy.X0, CenterYLo = Cy.X1, HistogramEq = heq,
        };
        using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var done = new ManualResetEventSlim(false);   // not disposed: a late event may still Set it
        host.RefineProgress += (_, e) => { if (e.Done) done.Set(); };
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(120)), "the TAA chain never settled");
        Thread.Sleep(200);
        var calc = host.Mandelbrot;
        var live = calc.ColorBuffer.Take(W * H).ToArray();

        var req = new FracturingFog.Imaging.PosterRequest
        {
            FractalType = FractalType.Mandelbrot, Width = W, Height = H,
            CenterX = Cx.X0, CenterXLo = Cx.X1, CenterY = Cy.X0, CenterYLo = Cy.X1, Zoom = Zoom,
            MaxIterations = calc.MaxIterations, Quality = QualityPreset.High, ColorMap = calc.ColorMap!,
            FractalParameters = new FractalParameters(), HistogramEq = heq, TemporalAa = true, Path = string.Empty,
        };
        var still = FracturingFog.Imaging.PosterRenderer.RenderToPixels(req, CancellationToken.None, out _, out _);
        Assert.Equal(live, still.Take(W * H).ToArray());

        // The UI's exports start from the host's request, which asks for the TAA.
        Assert.True(host.CreatePosterRequest(W, H, rotate: false, string.Empty, FracturingFog.Imaging.ImageFileFormat.Png).TemporalAa);

        // Precondition: without its temporal samples the export would differ.
        var first = FracturingFog.Imaging.PosterRenderer.RenderToPixels(req with { TemporalAa = false }, CancellationToken.None, out _, out _);
        Assert.True(Enumerable.Range(0, W * H).Count(i => first[i] != live[i]) > W * H / 50, "precondition: TAA changes the frame");
    }
}
