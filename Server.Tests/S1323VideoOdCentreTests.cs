// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1323 — the video engine interpolated the camera centre in QD (four limbs per axis)
// and wrote only CenterX..CenterX3 / CenterY..CenterY3 each frame, so CenterX4..7 /
// CenterY4..7 kept whatever the view held before: the start view's (or an earlier
// region's) OD tail rode along on every interpolated centre and stayed in the view
// state after the run. (Video zooms clamp to the Ultra cap, 5e27, and skip Extreme
// regions, so the frames themselves never reached the depth where the tail moves the
// picture; the stale tail did, once the user zoomed on from the end view.) The centre is
// now interpolated in all eight OD limbs, with exact ends.
// Checked on a real FractalRenderHost: a single-shot video from the current view (a
// centre with a full OD tail) to a target given in four limbs ends with every one of the
// sixteen limbs equal to the target's, in the frame and in the view state after the run,
// and the first frame carries the start's tail; for a zoom-in and for a pan only. Mutants
// (by hand): the frame writing four limbs again (ApplyVideoFrameState) — caught by both;
// the lerp without exact ends — caught by the pan (a zoom-in's last frame is the zoom
// phase, at the target itself).

using System;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1323VideoOdCentreTests
{
    // Records the view's centre limbs at each frame upload (the video loop applies the
    // frame state, renders, then uploads).
    private sealed class LimbRenderer(Func<FractalViewState> state) : IFractalRenderer
    {
        public double[]? First, Last;
        public int Frames;
        public void UpdateTexture(uint[] colorBuffer, int width, int height)
        {
            var s = state();
            var l = new[] { s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3, s.CenterX4, s.CenterX5, s.CenterX6, s.CenterX7,
                            s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3, s.CenterY4, s.CenterY5, s.CenterY6, s.CenterY7 };
            First ??= l;
            Last = l;
            Frames++;
        }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "limbs";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    // The start: the period-1808 minibrot centre near "Will this work" (S1320), all eight limbs.
    private static readonly double[] StartX = [-1.9918151296901943, -7.82198368112659E-17, -5.6237669196087764E-33, 2.6876672348091084E-49,
        8.344815422720153E-68, 5.62021278812849E-84, 3.758605147229201E-100, 3.74170214755604E-117];
    private static readonly double[] StartY = [-5.524041575397243E-06, -2.9567164202839686E-22, -2.2585741366702907E-38, 6.439806134954378E-55,
        1.8890258519870084E-71, 1.8268317099722925E-87, -9.199248922388955E-104, 5.069913980636591E-120];

    // targetZoom: a zoom-in (pan, then zoom at the target) and a pan only (its last frame
    // is the lerp at t = 1).
    [Theory]
    [InlineData(1e21)]
    [InlineData(1e20)]
    public void A_Video_Ends_On_All_Sixteen_Limbs_Of_Its_Target(double targetZoom)
    {
        var state = new FractalViewState
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = StartX[0], CenterXLo = StartX[1], CenterX2 = StartX[2], CenterX3 = StartX[3],
            CenterX4 = StartX[4], CenterX5 = StartX[5], CenterX6 = StartX[6], CenterX7 = StartX[7],
            CenterY = StartY[0], CenterYLo = StartY[1], CenterY2 = StartY[2], CenterY3 = StartY[3],
            CenterY4 = StartY[4], CenterY5 = StartY[5], CenterY6 = StartY[6], CenterY7 = StartY[7],
            Zoom = 1e20, Quality = QualityPreset.High,
        };
        FractalRenderHost? hostRef = null;
        var renderer = new LimbRenderer(() => hostRef!.ViewState);
        using var host = new FractalRenderHost(renderer, state, 48, 32, ColorPalette.BuiltIns[0]);
        hostRef = host;

        // A target a few views off the start, as the dialog sends it: four limbs. Its third
        // limb is far below the start's, so a_i + (b_i − a_i)·1 rounds to 0, not b_i.
        double[] tx = [StartX[0], StartX[1], 1e-50, 1.25e-66];
        double[] ty = [StartY[0], StartY[1], -3e-51, -7.5e-67];
        var stopped = new ManualResetEventSlim(false);   // not disposed: a late event must not Set a disposed handle
        host.Stopped += (_, _) => stopped.Set();
        host.StartVideo(new VideoZoomRequest
        {
            StartFromCurrentView = true,
            TargetCXHi = tx[0], TargetCXLo = tx[1], TargetCX2 = tx[2], TargetCX3 = tx[3],
            TargetCYHi = ty[0], TargetCYLo = ty[1], TargetCY2 = ty[2], TargetCY3 = ty[3],
            TargetZoom = targetZoom, Seconds = 0.4, TaaSmoothing = 0,
        });
        Assert.True(stopped.Wait(TimeSpan.FromSeconds(120)), "the video did not finish");

        Assert.True(renderer.Frames >= 2, $"precondition: frames rendered ({renderer.Frames})");
        // The first frame (a few ms into the leg) is about the start view, its OD tail included.
        for (int i = 0; i < 16; i++)
        {
            double s0 = i < 8 ? StartX[i] : StartY[i - 8];
            Assert.True(Math.Abs(renderer.First![i] - s0) <= 1e-2 * Math.Abs(s0), $"first frame limb {i}: {renderer.First[i]:R}, start {s0:R}");
        }
        // The last frame and the view state after the run: the target's sixteen limbs.
        double[] want = [.. tx, 0, 0, 0, 0, .. ty, 0, 0, 0, 0];
        Assert.Equal(want, renderer.Last!);
        var s = host.ViewState;
        Assert.Equal(want, new[] { s.CenterX, s.CenterXLo, s.CenterX2, s.CenterX3, s.CenterX4, s.CenterX5, s.CenterX6, s.CenterX7,
                                   s.CenterY, s.CenterYLo, s.CenterY2, s.CenterY3, s.CenterY4, s.CenterY5, s.CenterY6, s.CenterY7 });
    }
}
