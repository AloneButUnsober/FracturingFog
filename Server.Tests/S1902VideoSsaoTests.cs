// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1902 — the live video engine (single-shot zoom, video slideshow, travel) rendered each
// Mandelbrot frame without the flat 2D SSAO, while the live view, batch and scene video
// run it (#1448). RenderVideoFrame now runs it after the adaptive contrast / band dither,
// before the temporal blend. Checked on a real FractalRenderHost: a video at a fixed view
// (Standard quality, no AA, temporal smoothing 0) ends on a frame equal to the export of
// that view with the SSAO on, and the export without it differs (precondition). Mutant (by
// hand): the call removed — caught (the frame is the SSAO-free export).

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1902VideoSsaoTests
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
    private const double Zoom = 1e12;   // the Standard tier: no MSAA / TAA, as video frames

    [Fact]
    public void A_Video_Frame_Runs_The_Ssao_As_The_Export_Does()
    {
        var state = new FractalViewState
        {
            FractalType = FractalType.Mandelbrot, Zoom = Zoom, Quality = QualityPreset.Standard, PreferredIterations = 3000,
            CenterX = S1448View.Cx.X0, CenterXLo = S1448View.Cx.X1, CenterY = S1448View.Cy.X0, CenterYLo = S1448View.Cy.X1,
        };
        state.FractalParameters.Lighting = S1448View.Params(true).Lighting;
        using var host = new FractalRenderHost(new NullRenderer(), state, W, H, ColorPalette.BuiltIns[0]);
        var stopped = new ManualResetEventSlim(false);   // not disposed: a late event may still Set it
        host.Stopped += (_, _) => stopped.Set();
        host.StartVideo(new VideoZoomRequest
        {
            StartFromCurrentView = true,
            TargetCXHi = S1448View.Cx.X0, TargetCXLo = S1448View.Cx.X1,
            TargetCYHi = S1448View.Cy.X0, TargetCYLo = S1448View.Cy.X1,
            TargetZoom = Zoom, Seconds = 0.3, TaaSmoothing = 0,
        });
        Assert.True(stopped.Wait(TimeSpan.FromSeconds(120)), "the video did not finish");
        var calc = host.Mandelbrot;
        Assert.Equal(QualityPreset.Standard.Name, calc.Quality!.Name);   // precondition: no AA tier
        var frame = calc.ColorBuffer.Take(W * H).ToArray();

        PosterRequest Req(bool ssao) => S1448View.Req(ssao, QualityPreset.Standard, calc.MaxIterations, calc.ColorMap!, temporal: false)
            with { Zoom = Zoom };
        var with = S1448View.Export(Req(true));
        var without = S1448View.Export(Req(false));
        Assert.True(S1448View.AssertDarkened(without, with) > W * H / 20, "precondition: SSAO changes the frame");
        Assert.True(frame.SequenceEqual(with), "the video frame differs from the export with SSAO"
            + (frame.SequenceEqual(without) ? " (it is the frame without SSAO)" : ""));
    }
}
