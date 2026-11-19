// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1448 — the flat Mandelbrot view's 2D SSAO (Phase 8b) used to run in the live view
// only; export, --batch and scene video silently left it out although the settings
// reach them (Lighting & FX fields, --lighting-preset). And in the live view the
// first TAA sample dropped it: the running mean averages SSAO-free samples. Locks,
// each against an independent reference:
//   • SSAO only darkens: every RGB channel of a frame with it is at most the same
//     frame's without it, alpha unchanged, many pixels darker (export, scene video);
//   • the LIVE host's settled frame equals the exported still with SSAO on —
//     Standard (MSAA only) and High (MSAA + TAA), with and without adaptive contrast;
//     and differs from the still without it (the TAA drop is gone);
//   • SSAO off: the export is the AA pass exactly, as before;
//   • a scene video frame equals the export of the same view (no TAA) with SSAO on;
//   • cluster tiles assembled equal the whole image with it on, byte for byte (#1815:
//     a tile renders grown by the pass's reach and runs it in the frame's geometry), and
//     with it off; without the halo the tiles' borders would differ;
//   • a live recolour (adaptive contrast, theme) keeps it: the recoloured frame equals the
//     export at the new setting (#1816);
//   • the Command builder carries the settings through --lighting-preset and reports
//     them as a gap without one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using FracturingFog.Cli;
using FracturingFog.Export;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

internal static class S1448View
{
    public const int W = 96, H = 64;
    public const double Zoom = 1e15;   // deep: the CPU perturbation path (as in S1380)
    public static readonly OD Cx = new(-1.1726999042772253, 8.9529605787776783E-17, 0, 0, 0, 0, 0, 0);
    public static readonly OD Cy = new(-0.2968356710071185, -2.3536240906562374E-18, 0, 0, 0, 0, 0, 0);

    public static FractalParameters Params(bool ssao)
    {
        var p = new FractalParameters();
        var fx = p.Lighting;
        if (ssao) { fx.SsaoSamples = 16; fx.SsaoRadius = 0.05; fx.SsaoStrength = 1.0; }
        p.Lighting = fx;
        return p;
    }

    public static PosterRequest Req(bool ssao, QualityPreset q, int maxIter, IColorMap theme, int heq = 0, bool temporal = true) => new()
    {
        FractalType = FractalType.Mandelbrot, Width = W, Height = H,
        CenterX = Cx.X0, CenterXLo = Cx.X1, CenterY = Cy.X0, CenterYLo = Cy.X1, Zoom = Zoom,
        MaxIterations = maxIter, Quality = q, ColorMap = theme,
        FractalParameters = Params(ssao), HistogramEq = heq, TemporalAa = temporal, Path = string.Empty,
    };

    public static uint[] Export(PosterRequest req) =>
        PosterRenderer.RenderToPixels(req, CancellationToken.None, out _, out _).Take(W * H).ToArray();

    /// <summary>Asserts <paramref name="on"/> is <paramref name="off"/> darkened: no RGB channel
    /// brighter, alpha equal; returns the pixels made darker.</summary>
    public static int AssertDarkened(uint[] off, uint[] on)
    {
        int darker = 0;
        for (int i = 0; i < off.Length; i++)
        {
            Assert.Equal(off[i] >> 24, on[i] >> 24);
            bool d = false;
            for (int k = 0; k < 24; k += 8)
            {
                uint a = (off[i] >> k) & 0xFF, b = (on[i] >> k) & 0xFF;
                Assert.True(b <= a, $"pixel {i} channel {k / 8}: {b} > {a}");
                d |= b < a;
            }
            if (d) darker++;
        }
        return darker;
    }
}

public sealed class S1448Ssao2DParityTests
{
    private const int W = S1448View.W, H = S1448View.H, Iter = 3000;
    private static readonly IColorMap Theme = ColorPalette.BuiltIns[0];

    [Theory]
    [InlineData("Standard", 0)]
    [InlineData("High", 0)]
    [InlineData("High", 100)]
    public void The_Export_Runs_The_Ssao_Which_Only_Darkens(string quality, int heq)
    {
        var q = QualityPreset.FromName(quality);
        var off = S1448View.Export(S1448View.Req(false, q, Iter, Theme, heq));
        var on = S1448View.Export(S1448View.Req(true, q, Iter, Theme, heq));
        int darker = S1448View.AssertDarkened(off, on);
        Assert.True(darker > W * H / 20, $"precondition: SSAO darkened only {darker} pixels");
    }

    [Fact]
    public void Ssao_Off_Is_The_Aa_Pass_Exactly()
    {
        // The reference: the shared AA pipeline by hand, no SSAO step.
        var c = new MandelbrotCalculator(W, H)
        {
            CenterX = S1448View.Cx.X0, CenterXLo = S1448View.Cx.X1, CenterY = S1448View.Cy.X0, CenterYLo = S1448View.Cy.X1,
            Zoom = S1448View.Zoom, MaxIterations = Iter, Quality = QualityPreset.High, ColorMap = Theme,
        };
        c.Calculate(CancellationToken.None);
        var st = Supersampling.AccumulateQuality(c, c.Quality, AdaptiveAa.Off, 0, true, CancellationToken.None);
        Supersampling.FinishHistogramEq(c, in st, 0);
        Assert.Equal(c.ColorBuffer.Take(W * H).ToArray(), S1448View.Export(S1448View.Req(false, QualityPreset.High, Iter, Theme)));
        Assert.False(ScreenSpacePost.ApplySsao2D(c, S1448View.Params(false)));
        Assert.False(ScreenSpacePost.ApplySsao2D(c, null));
    }

    // #1815 — the cluster path: the image cut into tiles (sub-rects of the whole frame, as
    // TilePlanner sends them), each rendered alone, then assembled. Uneven tiles so the seams
    // fall inside the pass's reach of the frame's middle. Mutants (by hand): no halo (the
    // tile renders bare) — caught, at the seams; the hash in tile coordinates — caught;
    // the radius from the tile's size — caught.
    [Theory]
    [InlineData(true, "Standard")]
    [InlineData(true, "High")]
    [InlineData(false, "Standard")]
    public void Cluster_Tiles_Assemble_To_The_Whole_Image(bool ssao, string quality)
    {
        var q = QualityPreset.FromName(quality);
        var whole = S1448View.Req(ssao, q, Iter, Theme);
        var want = PosterRenderer.RenderDisplayPixels(whole, CancellationToken.None, out _, out _);
        int[] xs = [0, 37, W], ys = [0, 21, H];
        var got = new uint[W * H];
        for (int ty = 0; ty < 2; ty++)
            for (int tx = 0; tx < 2; tx++)
            {
                int x0 = xs[tx], y0 = ys[ty], tw = xs[tx + 1] - x0, th = ys[ty + 1] - y0;
                var tile = whole with { Width = tw, Height = th, ImageWidth = W, ImageHeight = H, SubRectOffsetX = x0, SubRectOffsetY = y0 };
                var px = PosterRenderer.RenderDisplayPixels(tile, CancellationToken.None, out int rw, out int rh);
                Assert.Equal((tw, th), (rw, rh));
                for (int y = 0; y < th; y++) Array.Copy(px, y * tw, got, (y0 + y) * W + x0, tw);
            }
        if (ssao)   // precondition: the pass changes the frame
        {
            var off = PosterRenderer.RenderDisplayPixels(S1448View.Req(false, q, Iter, Theme), CancellationToken.None, out _, out _);
            Assert.True(S1448View.AssertDarkened(off, want) > W * H / 20, "precondition: SSAO darkens the whole frame");
        }
        int differ = Enumerable.Range(0, W * H).Count(i => got[i] != want[i]);
        Assert.True(differ == 0, $"{differ} pixels of the assembled tiles differ from the whole image");
    }

    // ── scene video ──────────────────────────────────────────────────────────

    private static readonly Type ResolvedShotType =
        typeof(SceneVideoRenderer).GetNestedType("ResolvedShot", BindingFlags.NonPublic)!;
    private static readonly MethodInfo RenderShotFrame =
        typeof(SceneVideoRenderer).GetMethod("RenderShotFrame", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static uint[] SceneFrame(bool ssao, QualityPreset q)
    {
        object shot = Activator.CreateInstance(ResolvedShotType, nonPublic: true)!;
        void Set(string f, object v) => ResolvedShotType.GetField(f)!.SetValue(shot, v);
        Set("RenderType", FractalType.Mandelbrot);
        Set("CenterX", S1448View.Cx.X0); Set("CenterXLo", S1448View.Cx.X1);
        Set("CenterY", S1448View.Cy.X0); Set("CenterYLo", S1448View.Cy.X1);
        Set("Zoom", S1448View.Zoom); Set("MaxIterations", Iter); Set("Quality", q);
        Set("Themes", new IColorMap[] { Theme });
        Set("BaseParams", S1448View.Params(ssao));
        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(int), ResolvedShotType);
        var cache = (System.Collections.IDictionary)Activator.CreateInstance(dictType)!;
        cache[0] = shot;
        var args = RenderShotFrame.GetParameters()
            .Select((p, i) => i switch { 0 => cache, 1 => 0, 2 => 0.0, 3 => W, 4 => H, 5 => (object)CancellationToken.None, _ => Type.Missing })
            .ToArray();
        return ((uint[])RenderShotFrame.Invoke(null, args)!).Take(W * H).ToArray();
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("High")]
    public void A_Scene_Video_Frame_Runs_It_As_The_Export_Does(string quality)
    {
        var q = QualityPreset.FromName(quality);
        var off = SceneFrame(false, q);
        var on = SceneFrame(true, q);
        Assert.True(S1448View.AssertDarkened(off, on) > W * H / 20, "precondition: SSAO darkened the frame");
        // A video frame takes the MSAA pass only (no TAA), like an export with TAA off.
        Assert.Equal(S1448View.Export(S1448View.Req(true, q, Iter, Theme, temporal: false)), on);
        Assert.Equal(S1448View.Export(S1448View.Req(false, q, Iter, Theme, temporal: false)), off);
    }

    // ── batch parity: the Command builder ────────────────────────────────────

    [Fact]
    public void The_Command_Builder_Carries_It_Through_The_Lighting_Preset()
    {
        var p = S1448View.Params(true);
        var preset = LightingFxPresetData.FromFx(p.Lighting);
        var back = preset.ToFx();
        Assert.Equal(16, back.SsaoSamples);
        Assert.Equal(0.05, back.SsaoRadius);
        Assert.Equal(1.0, back.SsaoStrength);

        var withPreset = new BatchCommandSnapshot { Fractal = FractalType.Mandelbrot, Parameters = p, LightingPresetName = "ao" };
        string cmd = BatchCommandBuilder.Build(withPreset);
        Assert.Contains("--lighting-preset", cmd);
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(withPreset), g => g.Contains("Ssao", StringComparison.OrdinalIgnoreCase));

        var noPreset = new BatchCommandSnapshot { Fractal = FractalType.Mandelbrot, Parameters = p };
        Assert.Contains(BatchCommandBuilder.DetectGaps(noPreset), g => g.Contains("Ssao", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>#1448 — the live view's frame with SSAO IS the exported still.</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1448LiveEqualsExportTests
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

    private const int W = S1448View.W, H = S1448View.H;

    [Theory]
    [InlineData("Standard", 0)]
    [InlineData("Standard", 100)]
    [InlineData("High", 0)]
    [InlineData("High", 100)]
    public void The_Settled_Live_Frame_Equals_The_Exported_Still_With_Ssao(string quality, int heq)
    {
        var q = QualityPreset.FromName(quality);
        var state = new FractalViewState
        {
            FractalType = FractalType.Mandelbrot, Zoom = S1448View.Zoom, Quality = q, PreferredIterations = 3000,
            CenterX = S1448View.Cx.X0, CenterXLo = S1448View.Cx.X1, CenterY = S1448View.Cy.X0, CenterYLo = S1448View.Cy.X1,
            HistogramEq = heq,
        };
        state.FractalParameters.Lighting = S1448View.Params(true).Lighting;
        using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var done = new ManualResetEventSlim(false);   // not disposed: a late event may still Set it
        if (q.TaaMaxSamples > 1) host.RefineProgress += (_, e) => { if (e.Done) done.Set(); };
        else host.FrameCompleted += (_, _) => done.Set();
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(120)), "the frame never settled");
        Thread.Sleep(200);
        var calc = host.Mandelbrot;
        var live = calc.ColorBuffer.Take(W * H).ToArray();

        var with = S1448View.Export(S1448View.Req(true, q, calc.MaxIterations, calc.ColorMap!, heq));
        var without = S1448View.Export(S1448View.Req(false, q, calc.MaxIterations, calc.ColorMap!, heq));
        Assert.True(S1448View.AssertDarkened(without, with) > W * H / 20, "precondition: SSAO changes the still");
        Assert.Equal(with, live);
    }

    // #1816 — the live view's cheap recolours (the adaptive-contrast slider, a theme
    // change) rewrite the colour from the cached buffers; they dropped the SSAO until the
    // next full render, which at Standard quality the slider never arms. At Standard (one
    // sample, so the recolour is the whole frame) the recoloured frame must equal the export
    // at the new setting. Read straight after the call, before the theme's settle render.
    // Mutants (by hand): no SSAO after the slider's recolour — caught; none after the theme
    // recolour — caught.
    [Theory]
    [InlineData("adaptive")]
    [InlineData("theme")]
    public void A_Live_Recolour_Keeps_The_Ssao(string what)
    {
        var q = QualityPreset.Standard;
        var state = new FractalViewState
        {
            FractalType = FractalType.Mandelbrot, Zoom = S1448View.Zoom, Quality = q, PreferredIterations = 3000,
            CenterX = S1448View.Cx.X0, CenterXLo = S1448View.Cx.X1, CenterY = S1448View.Cy.X0, CenterYLo = S1448View.Cy.X1,
        };
        state.FractalParameters.Lighting = S1448View.Params(true).Lighting;
        using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var done = new ManualResetEventSlim(false);   // not disposed: a late event may still Set it
        host.FrameCompleted += (_, _) => done.Set();
        host.Trigger(progressive: false);
        Assert.True(done.Wait(TimeSpan.FromSeconds(120)), "the frame never settled");
        Thread.Sleep(200);
        var calc = host.Mandelbrot;

        int heq = 0;
        var theme = calc.ColorMap!;
        if (what == "adaptive")
        {
            heq = 100;
            host.ViewState.HistogramEq = heq;
            host.RepaintWithAdaptive();
        }
        else
        {
            theme = ColorPalette.BuiltIns[1];
            host.ApplyColorMap(theme);
        }
        var live = calc.ColorBuffer.Take(W * H).ToArray();

        var with = S1448View.Export(S1448View.Req(true, q, calc.MaxIterations, theme, heq));
        var without = S1448View.Export(S1448View.Req(false, q, calc.MaxIterations, theme, heq));
        Assert.True(S1448View.AssertDarkened(without, with) > W * H / 20, "precondition: SSAO changes the still");
        Assert.True(live.SequenceEqual(with), $"{what}: the recoloured frame differs from the export"
            + (live.SequenceEqual(without) ? " (it is the frame without SSAO)" : ""));
    }
}
